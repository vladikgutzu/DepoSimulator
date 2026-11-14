using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Server-authoritative state for every box that is not simply lying where the scene put it.
///
/// **State, not events.** This used to be four independent messages per carry — an entry added to
/// a grab list, a stream of positions, a release, a resting place — and each of them repaired
/// nothing if it went missing, arrived out of order, or named a box the receiver resolved
/// differently. Both failures were stable: a carry that never ended left the box frozen in the air
/// with its colliders off, unpickable by anyone for the rest of the session; a carry that never
/// started left the box on the floor while its carrier walked around holding nothing. Neither had
/// anything to correct it, because an event says what happened, not what is.
///
/// So the server keeps one row per interesting box — who holds it, what it is doing, where it is
/// and how fast — and every machine brings itself to that row: on each change, and again on a
/// sweep once a second. A dropped message now costs a second, not a session, and a client that
/// joins in the middle gets the warehouse as it stands rather than as it started.
///
/// Rows exist only for boxes that have been touched. The hundreds sitting untouched on shelves say
/// nothing and cost nothing.
///
/// Identity comes from BoxPhysicsInteractable.NetBoxId — a NetworkObjectId where there is one, and
/// a hash of the scene path where there is not (24 boxes per pallet pack carry no NetworkObject of
/// their own, which is why this manager exists at all rather than NetworkTransform doing the job).
///
/// Single-player: all paths are gated on NetworkManager.Singleton?.IsListening.
public class NetworkPhysBoxManager : NetworkBehaviour
{
    public static NetworkPhysBoxManager Instance { get; private set; }

    NetworkList<BoxNetState> _boxes;
    NetworkList<CartCargoState> _cartCargo;

    const float GrabRange = 4f;
    const float CarryPoseRange = 3.5f;
    const float MaxReleaseSpeed = 20f;
    const double MinCarryReportSeconds = 1d / 30d;
    const double CarryLeaseSeconds = 3d;
    const float MotionSnapshotInterval = 1f / 20f;
    const float ReliableMotionKeyframeInterval = 0.25f;
    const int MotionSnapshotsPerRpc = 16;

    readonly Dictionary<ulong, double> _nextCarryReportByBox = new();
    readonly Dictionary<ulong, double> _lastCarryReportByBox = new();
    readonly Dictionary<ulong, uint> _lastMotionSequenceByBox = new();
    readonly List<BoxMotionSnapshot> _motionSnapshots = new(MotionSnapshotsPerRpc);
    float _nextMotionSnapshot;
    float _nextReliableMotionKeyframe;
    uint _motionSequence;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;

        _boxes = new NetworkList<BoxNetState>(
            new List<BoxNetState>(),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        _cartCargo = new NetworkList<CartCargoState>(
            new List<CartCargoState>(),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn()
    {
        // Claimed here, not only in Awake. Awake runs once per scene load, this is cleared in
        // OnNetworkDespawn, and the co-op flow deliberately reuses the scene between rooms — so
        // from the second room onwards the static stayed null on the machine that hosted the first
        // one, and every `Instance?.` call through it did nothing at all. That reads in-game as
        // "nobody can pick anything up", because a grab is exactly such a call.
        if (Instance == null) Instance = this;

        _boxes.OnListChanged += OnBoxStateChanged;
        _cartCargo.OnListChanged += OnCartCargoChanged;

        // A joining client inherits the whole warehouse, not just what changes after it arrives.
        if (!IsServer)
        {
            // The client is a visual replica for world physics. Freeze every authored Rigidbody
            // before applying persistent rows so a distant host camera or local PhysX timing can
            // never create a second warehouse layout.
            BoxPhysicsInteractable.PrepareAllAsNetworkPhysicsReplicas();
            foreach (var state in _boxes)
                ApplyState(state);
            foreach (var state in _cartCargo)
                ApplyCartCargoState(state);
        }

        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;

        // Not reported straight away: the numbering pass runs on sceneLoaded, and a joining
        // client is spawned somewhere in the middle of synchronisation -- sometimes before its
        // own scene has finished loading. A second is far longer than that and costs nothing,
        // and a pass that runs later reports itself anyway (see ReportSceneIds).
        if (!IsServer) StartCoroutine(ReportSceneIdsSoon());
    }

    System.Collections.IEnumerator ReportSceneIdsSoon()
    {
        yield return new WaitForSeconds(1f);
        ReportSceneIds();
    }

    // ── Scene-box numbering check ─────────────────────────────────────────────────

    /// Tells the server which scene boxes this client numbered, so the two machines disagreeing
    /// about what is in the warehouse is something you are told rather than something you deduce
    /// from the symptoms three sessions later.
    ///
    /// Boxes with no NetworkObject of their own are identified by an id each peer derives on its
    /// own from the scene (BoxPhysicsInteractable.AssignSceneNetworkIds). Since that id is a hash
    /// of the box's own path, a set difference no longer breaks the boxes both peers do have --
    /// which it did while ids were list indices. What a difference still means is that one side
    /// has a box the other has not, so messages about it land nowhere.
    ///
    /// Called after every numbering pass, and once a second after a client spawns for the pass
    /// that ran before this object existed.
    public static void ReportSceneIds()
    {
        var inst = Instance;
        if (inst == null || !inst.IsSpawned) return;

        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || nm.IsServer) return;   // the server is the reference

        inst.VerifySceneIdsServerRpc(BoxPhysicsInteractable.SceneIdCount,
                                     BoxPhysicsInteractable.SceneIdHash);
    }

    [ServerRpc(RequireOwnership = false)]
    void VerifySceneIdsServerRpc(int count, ulong hash, ServerRpcParams rpcParams = default)
    {
        int   serverCount = BoxPhysicsInteractable.SceneIdCount;
        ulong serverHash  = BoxPhysicsInteractable.SceneIdHash;

        if (count == serverCount && hash == serverHash) return;

        string msg =
            $"[NetworkPhysBoxManager] Scene boxes differ from client " +
            $"{rpcParams.Receive.SenderClientId}: server numbered {serverCount} (digest " +
            $"{serverHash:X16}), client numbered {count} ({hash:X16}). " +
            "Box identities differ: carry, position and state updates may not resolve to a box. " +
            "Check matching builds, scene hierarchy and registration timing on both peers.";

        Debug.LogWarning(msg);
        SceneIdMismatchClientRpc(msg, SingleClient(rpcParams.Receive.SenderClientId));
    }

    // Logged on the client too: with two builds running there are two consoles, and the one being
    // read is as often the client's as the host's.
    [ClientRpc]
    void SceneIdMismatchClientRpc(string message, ClientRpcParams _ = default) =>
        Debug.LogWarning(message);

    public override void OnNetworkDespawn()
    {
        _boxes.OnListChanged -= OnBoxStateChanged;
        _cartCargo.OnListChanged -= OnCartCargoChanged;
        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        _nextCarryReportByBox.Clear();
        _lastCarryReportByBox.Clear();
        _lastMotionSequenceByBox.Clear();
        if (Instance == this) Instance = null;
    }

    /// Server-only: a carrier who disconnects mid-carry would otherwise leave his box hanging in
    /// the air forever, frozen and uncollidable, with his hand IK still locked to it on every
    /// other screen. Nothing else would ever end that carry — the box is not destroyed on this
    /// path the way BoxWithPart is, so no destroyed-reference guard saves it either.
    ///
    /// Dropped rather than deleted: the box stays in the world, in his last known pose, and simply
    /// stops being held.
    void OnClientDisconnected(ulong clientId)
    {
        for (int i = _boxes.Count - 1; i >= 0; i--)
        {
            var s = _boxes[i];
            if (s.Phase != (byte)BoxPhase.Held || s.Holder != clientId) continue;

            s.Phase   = (byte)BoxPhase.Resting;
            s.Holder  = ulong.MaxValue;   // nobody: this rest belongs to no machine's announcement
            s.Vel     = Vector3.zero;
            s.Version = s.Version + 1;
            _boxes[i] = s;
            _nextCarryReportByBox.Remove(s.BoxId);
            _lastCarryReportByBox.Remove(s.BoxId);
        }
    }

    // ── State changed ─────────────────────────────────────────────────────────────

    void OnBoxStateChanged(NetworkListEvent<BoxNetState> evt)
    {
        switch (evt.Type)
        {
            case NetworkListEvent<BoxNetState>.EventType.Add:
            case NetworkListEvent<BoxNetState>.EventType.Value:
            case NetworkListEvent<BoxNetState>.EventType.Insert:
                // One box's problem stays one box's problem. An exception thrown in here escapes
                // into Netcode's own dispatch and takes the rest of the batch with it, so a single
                // odd crate stopped every other box in the warehouse from syncing — with nothing
                // on screen to connect the two.
                try { ApplyState(evt.Value); }
                catch (System.Exception e)
                {
                    Debug.LogError($"[NetworkPhysBoxManager] box {evt.Value.BoxId:X} threw while " +
                                   $"applying phase {(BoxPhase)evt.Value.Phase}: {e}");
                }
                break;

            case NetworkListEvent<BoxNetState>.EventType.Remove:
            case NetworkListEvent<BoxNetState>.EventType.RemoveAt:
                // The row is gone: the box was consumed, or handed to a pallet or a cart, which
                // own its pose from here. Only the carry needs undoing.
                Forget(evt.Value.BoxId);
                break;
        }
    }

    void Forget(ulong boxId)
    {
        var box = BoxPhysicsInteractable.FindByNetId(boxId);
        if (box != null && box.IsRemotelyGrabbed) box.SetRemoteGrabbed(false, box.RemoteGrabberId);
    }

    // ── Reconciliation ────────────────────────────────────────────────────────────

    /// How often each machine checks what it believes against what the server says.
    const float ReconcileInterval = 1f;

    float _nextReconcile;

    void Update()
    {
        if (!IsSpawned || _boxes == null) return;

        // Free-body physics has one authority: the listen server (the host). Clients receive a
        // small, unreliable stream for visual motion and the reliable Resting row at the end.
        // Static, carried and container-bound boxes are not part of this stream.
        if (IsServer && Time.unscaledTime >= _nextMotionSnapshot)
        {
            _nextMotionSnapshot = Time.unscaledTime + MotionSnapshotInterval;
            BroadcastMotionSnapshots();
        }

        if (Time.time < _nextReconcile) return;

        _nextReconcile = Time.time + ReconcileInterval;
        if (IsServer) ExpireStaleCarries();
        Reconcile();
    }

    /// A Held row is a lease, not permanent ownership. The carrier refreshes it with the same
    /// pose reports observers already consume. If a grab confirmation could not be applied, the
    /// carrier object disappeared, or a client stopped reporting without disconnecting cleanly,
    /// the server releases the ghost claim instead of leaving an unpickable box on the floor.
    void ExpireStaleCarries()
    {
        double now = NetworkManager.ServerTime.Time;
        for (int i = _boxes.Count - 1; i >= 0; i--)
        {
            BoxNetState row = _boxes[i];
            if (row.Phase != (byte)BoxPhase.Held) continue;

            if (!_lastCarryReportByBox.TryGetValue(row.BoxId, out double last))
            {
                _lastCarryReportByBox[row.BoxId] = now;
                continue;
            }
            if (now - last <= CarryLeaseSeconds) continue;

            row.Holder = ulong.MaxValue;
            row.Phase = (byte)BoxPhase.Resting;
            row.Vel = Vector3.zero;
            row.Version++;
            _boxes[i] = row;
            _nextCarryReportByBox.Remove(row.BoxId);
            _lastCarryReportByBox.Remove(row.BoxId);
        }
    }

    /// Walks every row and brings this machine to it.
    ///
    /// The per-change path above is the fast one and does the same work; this is the floor under
    /// it. A change that was missed, arrived before the box existed locally, or was applied to a
    /// box that has since been swapped out gets caught here within a second instead of standing
    /// for the rest of the session.
    void Reconcile()
    {
        // Temporary, with the throw trace: one box can only have one row. Two rows for the same id
        // means every sweep applies both, and the older one drags the box back to wherever it was
        // when that row was last written.
        if (BoxPhysicsInteractable.TraceThrows)
        {
            var seen = new HashSet<ulong>();
            foreach (var state in _boxes)
                if (!seen.Add(state.BoxId))
                    Debug.LogWarning($"[BoxThrow] DUPLICATE ROW for box id={state.BoxId:X} " +
                                     $"(phase={state.Phase} ver={state.Version} pos={state.Pos:F2})");
        }

        foreach (var state in _boxes)
        {
            // Same reason as in OnBoxStateChanged: this sweep runs once a second over every box in
            // the warehouse, and one throwing here used to abort the whole pass — so the sweep that
            // exists to repair desyncs became the thing that stopped repairing them.
            try { ApplyState(state); }
            catch (System.Exception e)
            {
                Debug.LogError($"[NetworkPhysBoxManager] reconcile: box {state.BoxId:X} threw: {e}");
            }
        }

        // Cart cargo is persistent state too. This also repairs states that arrived before the
        // cart or the box had spawned on a late-joining client.
        foreach (var state in _cartCargo)
            ApplyCartCargoState(state);

        // Held here, held by nobody according to the server. The row may have been removed while
        // this machine was not listening, and a carry only ends when something ends it.
        var held = BoxPhysicsInteractable.RemotelyHeld;
        for (int i = held.Count - 1; i >= 0; i--)
        {
            var box = held[i];
            if (box == null) continue;
            if (IsHeldOnServer(box.NetBoxId)) continue;

            if (BoxPhysicsInteractable.TraceThrows)
                Debug.Log($"[BoxThrow] RECONCILE freeing stuck carry on '{box.name}' id={box.NetBoxId:X}");

            // Only the server announces where it lands: several machines noticing the same stuck
            // box would otherwise each broadcast their own answer and drag it between them.
            box.ReleaseStuckRemote(IsServer);
        }
    }

    bool IsHeldOnServer(ulong boxId)
    {
        int i = IndexOf(boxId);
        return i >= 0 && _boxes[i].Phase == (byte)BoxPhase.Held;
    }

    /// Brings one box to one row. The whole client side of this manager is this method.
    ///
    /// Held and the pose are continuous — applied every time they arrive. A throw and a landing
    /// are one-shot events inside a state, so they carry a version: applying the same throw twice
    /// would relaunch a box that has already come to rest, and never applying it would leave the
    /// box hanging. The peer named in Holder is the one that produced the event and has already
    /// lived it, so it only records the version.
    void ApplyState(BoxNetState s)
    {
        var box = BoxPhysicsInteractable.FindByNetId(s.BoxId);
        if (box == null) return;   // not spawned here yet — the sweep will catch it

        ulong local  = NetworkManager.Singleton.LocalClientId;
        bool  isMine = s.Holder == local;

        if (box.IsTraced)
            Debug.Log($"[BoxThrow] STATE row phase={(BoxPhase)s.Phase} holder={s.Holder} " +
                      $"ver={s.Version} isMine={isMine} pos={s.Pos:F2} -> '{box.name}' " +
                      $"id={s.BoxId:X} standing at {box.transform.position:F2} " +
                      $"(locallyGrabbed={box.IsLocallyGrabbed} remotelyGrabbed={box.IsRemotelyGrabbed} " +
                      $"netVer={box.NetVersion})");

        switch ((BoxPhase)s.Phase)
        {
            case BoxPhase.Held:
                if (isMine)
                {
                    // Mine. I drive it; all this row can do is contradict me, so it does not —
                    // except to notice I am not actually holding what the server says I am. That
                    // happens when the confirmation went missing: everyone else has the box frozen
                    // in my hands while my hands are empty. Take it, and the two views agree.
                    if (box.IsRemotelyGrabbed) box.SetRemoteGrabbed(false, s.Holder);
                    box.StoredInCart?.SyncBoxRemoved(box);
                    box.StoredOnPallet?.ApplyRemoteCargoDeparture(box);

                    // Only for a grab this box has not already lived. Version changes when the box
                    // changes hands, not when its carried pose is reported — so the rows arriving
                    // between "I let go" and the server publishing the throw all still say Held by
                    // me, with the version of the grab that is already over. Taking them at face
                    // value put the box straight back in the carrier's hands a moment after he
                    // dropped it, while everyone else watched it fall: the drop that "sometimes
                    // snaps back".
                    if (!box.IsLocallyGrabbed && box.NetVersion != s.Version) box.ExecuteConfirmedGrab();

                    box.NoteNetVersion(s.Version);
                    return;
                }

                // The server says it is in someone else's hands and I am still holding it: the box
                // was taken from me and the notice never arrived. Let go without throwing it, the
                // same way ForceRelease would have.
                if (box.IsLocallyGrabbed) box.SilentRelease();

                if (!box.IsRemotelyGrabbed)
                {
                    // Out of the cart first, so the visual is right before it freezes.
                    box.StoredInCart?.SyncBoxRemoved(box);
                    box.StoredOnPallet?.ApplyRemoteCargoDeparture(box);
                    box.SetRemoteGrabbed(true, s.Holder);
                }

                box.ApplyRemotePosition(s.Pos, s.Rot);
                box.NoteNetVersion(s.Version);
                return;

            case BoxPhase.Flying:
                if (box.NetVersion == s.Version) return;   // this throw has already been lived
                box.NoteNetVersion(s.Version);

                if (box.IsLocallyGrabbed) return;           // I have caught it since

                if (IsServer)
                {
                    // The host already started its own throw locally. A remote client's throw is
                    // instantiated here and from now on only this Rigidbody owns the result.
                    if (!isMine) box.ApplyRemoteRelease(s.Pos, s.Rot, s.Vel);
                }
                else
                {
                    // Clients never run a competing PhysX simulation for a world box. This also
                    // replaces the thrower's short local prediction once the server accepts it.
                    box.ApplyAuthoritativeMotionSnapshot(
                        s.Pos, s.Rot, s.Vel, Vector3.zero, snapImmediately: true);
                }
                return;

            case BoxPhase.Resting:
                if (box.NetVersion == s.Version) return;
                box.NoteNetVersion(s.Version);

                // Only hands outrank a landing now. "isMine" used to skip this as well, because the
                // peer that threw the box was the peer that announced where it stopped — its own
                // answer coming back. The server announces every landing now, so for a client this
                // row is somebody else's answer about his box, and taking it is the whole point:
                // it is what puts the two warehouses back on the same layout.
                if (box.IsLocallyGrabbed) return;
                if (isMine && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                    return;                                 // my own answer, coming back to me

                // Below the guard, not above it: this line is about an answer that is *going* to be
                // applied, and reporting one on the machine that wrote it named a move that was
                // never going to happen. A long distance here is either two simulations that
                // genuinely parted company or a row that does not belong to this box — and the
                // row's own id and version are the only things that tell those apart.
                if (BoxPhysicsInteractable.TraceThrows &&
                    Vector3.Distance(box.transform.position, s.Pos) > 2f)
                    Debug.Log($"[BoxThrow] REST ROW id={s.BoxId:X} ver={s.Version} holder={s.Holder} " +
                              $"pos={s.Pos:F2} -> box '{box.name}'#{box.GetInstanceID()} " +
                              $"(NetBoxId={box.NetBoxId:X}) standing at {box.transform.position:F2}");
                box.ApplyRemoteRest(s.Pos, s.Rot);
                return;

            case BoxPhase.Consumed:
                // Consumption is persistent state, not a fire-and-forget destroy message. A
                // late joiner receives this row and removes the same box even if it missed the
                // original bench/shipping transaction entirely.
                if (box.IsLocallyGrabbed) box.SilentRelease();
                if (box.IsRemotelyGrabbed) box.SetRemoteGrabbed(false, box.RemoteGrabberId);
                box.NoteNetVersion(s.Version);
                Object.Destroy(box.gameObject);
                return;
        }
    }

    // ── Grab ServerRpc ────────────────────────────────────────────────────────────

    /// Client requests grab of a box. If it's free, first caller wins as usual. If someone else
    /// is already carrying it, this transfers the grab instead of rejecting — lets a teammate take
    /// a box out of another player's hands (matches the same "grab it away" pattern already used
    /// for the ladder). The previous holder is told to release locally via SilentRelease(), which
    /// clears their carry state without the normal throw/drop physics, so the box stays frozen in
    /// its carried pose until the new grabber's Grab() picks it up again a moment later.
    [ServerRpc(RequireOwnership = false)]
    public void RequestGrabServerRpc(ulong boxId, ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        int at = IndexOf(boxId);
        if (at >= 0 && _boxes[at].Phase == (byte)BoxPhase.Consumed)
        {
            GrabDeniedClientRpc(
                "Pickup unavailable", "This part has already been used",
                SingleClient(sender));
            return;
        }

        var box = BoxPhysicsInteractable.FindByNetId(boxId);
        if (box == null)
        {
            string message = $"[NetworkPhysBoxManager] Grab rejected: client {sender}, unknown box ID {boxId:X}. Check scene identity mismatch logs.";
            Debug.LogWarning(message);
            SceneIdMismatchClientRpc(message, SingleClient(sender));
            return;
        }

        var  row  = at >= 0 ? _boxes[at] : NewRow(boxId);
        bool held = at >= 0 && row.Phase == (byte)BoxPhase.Held;

        Vector3 authoritativePosition = held ? row.Pos : box.transform.position;
        if (!TryGetActivePlayer(sender, out var player))
        {
            string body = player != null && player.IsRagdolledNet.Value
                ? "Stand up before picking up a box"
                : "Your player state is still synchronizing";
            GrabDeniedClientRpc("Pickup unavailable", body, SingleClient(sender));
            return;
        }

        if (!Finite(authoritativePosition) ||
            Vector3.SqrMagnitude(player.transform.position - authoritativePosition) >
            GrabRange * GrabRange)
        {
            GrabDeniedClientRpc(
                "Pickup failed", "Move closer to the box and try again",
                SingleClient(sender));
            return;
        }

        if (held && row.Holder == sender)
        {
            // The authoritative row may have arrived before the local delivery box existed,
            // or the targeted confirmation may have been lost. Re-sending is idempotent and
            // repairs the exact case where the server says Held while the owner's hands are
            // visibly empty.
            _lastCarryReportByBox[boxId] = NetworkManager.ServerTime.Time;
            ConfirmGrabClientRpc(boxId, row.Version, SingleClient(sender));
            return;
        }

        // Direct world grabs use the same one-client/one-box invariant as cart and pallet
        // transactions. Check before touching the old holder so a rejected request cannot force
        // somebody else to release their box.
        if (ClientAlreadyHoldsBox(sender))
        {
            GrabDeniedClientRpc(
                "Hands state updating", "Set down the current box and try again",
                SingleClient(sender));
            return;
        }

        if (held)
        {

            // Vehicle delivery parts are transactional inventory. They cannot be stolen out of
            // a teammate's hands: first confirmed grab owns the part until it is dropped or the
            // assembly bench consumes it.
            if (box.GetComponent<VehiclePartBox>() != null)
            {
                GrabDeniedClientRpc(
                    "Part already taken", "A teammate is carrying this delivery part",
                    SingleClient(sender));
                return;
            }

            // Taken out of a team-mate's hands, the same "grab it away" the ladder allows. He is
            // told to let go without the throw physics, so the box stays in its carried pose until
            // the new grabber's Grab() takes over a moment later.
            ForceReleaseClientRpc(boxId, SingleClient(row.Holder));
        }

        // A rejected remote request must never unload a cart. Container state is transferred only
        // after the server has accepted a living, nearby player as the new holder.
        RemoveCartCargo(boxId);

        row.Holder  = sender;
        row.Phase   = (byte)BoxPhase.Held;
        row.Vel     = Vector3.zero;
        row.Version = row.Version + 1;

        // The server's own copy is the best guess at where the box is until the new carrier's
        // first report arrives a twentieth of a second later.
        row.Pos = box.transform.position;
        row.Rot = box.transform.rotation;

        Write(at, row);
        _nextCarryReportByBox.Remove(boxId);
        _lastCarryReportByBox[boxId] = NetworkManager.ServerTime.Time;
        ConfirmGrabClientRpc(boxId, row.Version, SingleClient(sender));
    }

    [ClientRpc]
    void GrabDeniedClientRpc(string title, string body, ClientRpcParams _ = default)
    {
        HUDController.Instance?.AddNotification(
            title, body, HUDController.NotifType.Warning);
    }

    /// Server-side ownership validation for transactional destinations.
    public bool ServerIsHeldBy(ulong boxId, ulong clientId)
    {
        if (!IsServer) return false;
        int at = IndexOf(boxId);
        if (at < 0) return false;
        BoxNetState row = _boxes[at];
        return row.Phase == (byte)BoxPhase.Held && row.Holder == clientId;
    }

    /// Server-side optimistic-concurrency check used by transactional destinations. The sender
    /// must still own the exact grab generation it acted on; an old request cannot consume a box
    /// that was dropped, transferred, and picked up again later.
    public bool ServerTryGetHeldBox(ulong boxId, ulong clientId, uint expectedVersion,
        out BoxPhysicsInteractable box, out BoxNetState state)
    {
        box = null;
        state = default;
        if (!IsServer) return false;

        int at = IndexOf(boxId);
        if (at < 0) return false;

        state = _boxes[at];
        if (state.Phase != (byte)BoxPhase.Held || state.Holder != clientId ||
            state.Version != expectedVersion)
            return false;

        box = BoxPhysicsInteractable.FindByNetId(boxId);
        return box != null;
    }

    /// Atomically changes a currently held box into a persistent consumed tombstone.
    public bool ServerTryConsumeHeldBox(ulong boxId, ulong clientId, uint expectedVersion,
        out BoxPhysicsInteractable box)
    {
        if (!ServerTryGetHeldBox(boxId, clientId, expectedVersion, out box, out var state))
            return false;

        RemoveCartCargo(boxId);
        int at = IndexOf(boxId);
        state.Holder = ulong.MaxValue;
        state.Phase = (byte)BoxPhase.Consumed;
        state.Vel = Vector3.zero;
        state.Version++;
        Write(at, state);
        _nextCarryReportByBox.Remove(boxId);
        _lastCarryReportByBox.Remove(boxId);
        return true;
    }

    /// Server-only destination path for a box physically dropped/thrown into a trigger. The
    /// server's overlap is the authority; a client-side trigger can never call this directly.
    public bool ServerTryConsumeUnheldBox(ulong boxId, out BoxPhysicsInteractable box)
    {
        box = null;
        if (!IsServer) return false;

        int at = IndexOf(boxId);
        var state = at >= 0 ? _boxes[at] : NewRow(boxId);
        if (at >= 0 && (state.Phase == (byte)BoxPhase.Held ||
                        state.Phase == (byte)BoxPhase.Consumed))
            return false;

        box = BoxPhysicsInteractable.FindByNetId(boxId);
        if (box == null) return false;

        RemoveCartCargo(boxId);
        state.Holder = ulong.MaxValue;
        state.Phase = (byte)BoxPhase.Consumed;
        state.Pos = box.transform.position;
        state.Rot = box.transform.rotation;
        state.Vel = Vector3.zero;
        state.Version++;
        Write(at, state);
        return true;
    }

    // Only the winner client receives this.
    [ClientRpc]
    void ConfirmGrabClientRpc(ulong boxId, uint version, ClientRpcParams _ = default)
    {
        var box = BoxPhysicsInteractable.FindByNetId(boxId);
        if (box == null) return;
        box.NoteNetVersion(version);
        box.ExecuteConfirmedGrab();
    }

    // Only the player who just had this box taken from them receives this.
    [ClientRpc]
    void ForceReleaseClientRpc(ulong boxId, ClientRpcParams _ = default)
    {
        var box = BoxPhysicsInteractable.FindByNetId(boxId);
        box?.SilentRelease();
        HUDController.Instance?.AddNotification(
            "Box taken", "A teammate took the box from your hands",
            HUDController.NotifType.Warning);
    }

    static ClientRpcParams SingleClient(ulong clientId) => new()
    {
        Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
    };

    // ── Consume (destination absorbs the box permanently) ─────────────────────────

    /// Compatibility path for event destinations that have not moved to a typed transaction yet.
    /// It still requires the sender to be the current holder; consumption is written as persistent
    /// state so a late joiner cannot resurrect the box.
    [ServerRpc(RequireOwnership = false)]
    public void ConsumeBoxServerRpc(ulong boxId, ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        int at = IndexOf(boxId);
        if (at < 0) return;

        BoxNetState state = _boxes[at];
        if (!TryGetActivePlayer(sender, out var player) || !Finite(state.Pos) ||
            Vector3.SqrMagnitude(player.transform.position - state.Pos) > GrabRange * GrabRange ||
            !ServerTryConsumeHeldBox(boxId, sender, state.Version, out _))
            SendConsumeRejectedClientRpc(SingleClient(sender));
    }

    [ClientRpc]
    void SendConsumeRejectedClientRpc(ClientRpcParams _ = default)
    {
        HUDController.Instance?.AddNotification(
            "Action rejected", "The box is no longer held by you",
            HUDController.NotifType.Warning);
    }

    // ── Carry ─────────────────────────────────────────────────────────────────────

    /// The carrier reports where the box is, twenty times a second. No version bump: a pose is
    /// continuous, not an event, and every peer simply follows the latest one it has.
    [ServerRpc(RequireOwnership = false)]
    public void ReportCarryServerRpc(ulong boxId, Vector3 pos, Quaternion rot,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        int at = IndexOf(boxId);
        if (at < 0) return;

        var row = _boxes[at];

        // Only from the hands that hold it. A report arriving from a player the server has already
        // moved on from — the box was taken from him a moment ago — must not drag it back.
        if (row.Phase != (byte)BoxPhase.Held || row.Holder != sender ||
            !TryGetActivePlayer(sender, out var player) || !Finite(pos) ||
            !TryNormalize(rot, out rot) ||
            Vector3.SqrMagnitude(pos - player.transform.position) > CarryPoseRange * CarryPoseRange)
            return;

        double now = NetworkManager.ServerTime.Time;
        _lastCarryReportByBox[boxId] = now;
        if (_nextCarryReportByBox.TryGetValue(boxId, out double next) && now < next) return;
        _nextCarryReportByBox[boxId] = now + MinCarryReportSeconds;

        row.Pos   = pos;
        row.Rot   = rot;
        _boxes[at] = row;
    }

    // ── Release ───────────────────────────────────────────────────────────────────

    /// A throw or a drop: the box stops being held and starts flying, from this pose at this
    /// velocity. Every peer runs the same throw from those three numbers — the bargain this
    /// codebase already makes for a collapsing stack, and cheaper than streaming a flight.
    [ServerRpc(RequireOwnership = false)]
    public void ReleaseBoxServerRpc(ulong boxId, uint expectedVersion, Vector3 finalPos,
        Quaternion finalRot, Vector3 finalVel,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!ServerTryGetHeldBox(boxId, sender, expectedVersion, out _, out var row))
        {
            int currentAt = IndexOf(boxId);
            bool restore = currentAt >= 0 && _boxes[currentAt].Phase == (byte)BoxPhase.Held &&
                           _boxes[currentAt].Holder == sender;
            uint currentVersion = currentAt >= 0 ? _boxes[currentAt].Version : 0;
            ReleaseRejectedClientRpc(boxId, restore, currentVersion, SingleClient(sender));
            return;
        }

        // Invalid data is rejected rather than written into the persistent NetworkList. A single
        // NaN position or rotation would otherwise poison reconciliation for every peer.
        if (!TryGetActivePlayer(sender, out var player) || !Finite(finalPos) ||
            !Finite(finalVel) || !TryNormalize(finalRot, out finalRot) ||
            Vector3.SqrMagnitude(finalPos - player.transform.position) >
            CarryPoseRange * CarryPoseRange)
        {
            ReleaseRejectedClientRpc(boxId, true, row.Version, SingleClient(sender));
            return;
        }

        finalVel = Vector3.ClampMagnitude(finalVel, MaxReleaseSpeed);

        int at = IndexOf(boxId);

        row.Holder  = sender;   // whose throw this is; he has lived it
        row.Phase   = (byte)BoxPhase.Flying;
        row.Pos     = finalPos;
        row.Rot     = finalRot;
        row.Vel     = finalVel;
        row.Version = row.Version + 1;

        Write(at, row);
        _nextCarryReportByBox.Remove(boxId);
        _lastCarryReportByBox.Remove(boxId);
    }

    [ClientRpc]
    void ReleaseRejectedClientRpc(ulong boxId, bool restoreCarry, uint authoritativeVersion,
        ClientRpcParams _ = default)
    {
        var box = BoxPhysicsInteractable.FindByNetId(boxId);
        if (box == null) return;
        box.NoteNetVersion(authoritativeVersion);
        if (restoreCarry) box.ExecuteConfirmedGrab();
    }

    bool TryGetClientTransform(ulong clientId, out Transform player)
    {
        player = null;
        var manager = NetworkManager.Singleton;
        if (manager == null ||
            !manager.ConnectedClients.TryGetValue(clientId, out var client) ||
            client.PlayerObject == null)
            return false;

        player = client.PlayerObject.transform;
        return true;
    }

    bool TryGetActivePlayer(ulong clientId, out NetworkPlayer player)
    {
        player = null;
        var manager = NetworkManager.Singleton;
        if (manager == null ||
            !manager.ConnectedClients.TryGetValue(clientId, out var client) ||
            client.PlayerObject == null)
            return false;

        player = client.PlayerObject.GetComponent<NetworkPlayer>();
        return player != null && !player.IsDeadNet.Value && !player.IsRagdolledNet.Value;
    }

    static bool Finite(Vector3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    static bool TryNormalize(Quaternion value, out Quaternion normalized)
    {
        normalized = Quaternion.identity;
        if (!float.IsFinite(value.x) || !float.IsFinite(value.y) ||
            !float.IsFinite(value.z) || !float.IsFinite(value.w))
            return false;

        float sqrMagnitude = value.x * value.x + value.y * value.y +
                             value.z * value.z + value.w * value.w;
        if (!float.IsFinite(sqrMagnitude) || sqrMagnitude < 0.000001f) return false;

        float inverse = 1f / Mathf.Sqrt(sqrMagnitude);
        normalized = new Quaternion(value.x * inverse, value.y * inverse,
            value.z * inverse, value.w * inverse);
        return true;
    }

    // ── Atomic cart transactions ─────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void RequestPlaceBoxOnCartServerRpc(ulong boxId, uint expectedBoxVersion,
        ulong cartNetObjId, ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!ServerTryGetHeldBox(boxId, sender, expectedBoxVersion, out var box, out var held) ||
            !TryResolveCart(cartNetObjId, out var cart) ||
            !TryGetClientTransform(sender, out var player) ||
            Vector3.SqrMagnitude(player.position - cart.transform.position) > 25f ||
            !cart.TryGetAuthoritativePlacement(box, out int slot, out Vector3 localPos,
                out Quaternion localRot))
        {
            CartCommandRejectedClientRpc("Could not load that box onto the cart",
                SingleClient(sender));
            return;
        }

        // The manifest becomes the only owner of the pose. The client-supplied command contains
        // no slot or offset; those are computed from the server's current cart contents.
        RemoveRow(boxId);
        var cargo = new CartCargoState
        {
            BoxId = boxId,
            CartNetworkObjectId = cartNetObjId,
            SlotIndex = slot,
            LocalPos = localPos,
            LocalRot = localRot,
            Version = held.Version + 1,
        };
        int at = CartCargoIndexOf(boxId);
        if (at >= 0) _cartCargo[at] = cargo;
        else _cartCargo.Add(cargo);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestTakeBoxFromCartServerRpc(ulong boxId, uint expectedCargoVersion,
        ulong cartNetObjId, ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        int at = CartCargoIndexOf(boxId);
        if (at < 0 || _cartCargo[at].CartNetworkObjectId != cartNetObjId ||
            _cartCargo[at].Version != expectedCargoVersion ||
            !TryResolveCart(cartNetObjId, out var cart) ||
            !TryGetClientTransform(sender, out var player) ||
            Vector3.SqrMagnitude(player.position - cart.transform.position) > 25f ||
            !ServerCanGrantGrabFromContainer(boxId, sender))
        {
            CartCommandRejectedClientRpc("That cart load changed before you could take it",
                SingleClient(sender));
            return;
        }

        CartCargoState cargo = _cartCargo[at];
        RemoveCartCargo(boxId);
        ServerGrantGrabFromContainer(boxId, sender, cargo.Version + 1);
    }

    [ClientRpc]
    void CartCommandRejectedClientRpc(string body, ClientRpcParams _ = default)
    {
        HUDController.Instance?.AddNotification(
            "Cart", body, HUDController.NotifType.Warning);
    }

    bool TryResolveCart(ulong cartNetObjId, out CartStorageInteractable cart)
    {
        cart = null;
        var manager = NetworkManager.Singleton;
        if (manager == null || !manager.SpawnManager.SpawnedObjects.TryGetValue(
                cartNetObjId, out var cartObject))
            return false;
        cart = cartObject.GetComponent<CartStorageInteractable>();
        return cart != null;
    }

    bool ClientAlreadyHoldsBox(ulong clientId)
    {
        for (int i = 0; i < _boxes.Count; i++)
            if (_boxes[i].Phase == (byte)BoxPhase.Held && _boxes[i].Holder == clientId)
                return true;
        return false;
    }

    /// Shared by cart and pallet transactions after their manifest row has been validated.
    public bool ServerGrantGrabFromContainer(ulong boxId, ulong clientId, uint version)
    {
        if (!ServerCanGrantGrabFromContainer(boxId, clientId)) return false;
        var box = BoxPhysicsInteractable.FindByNetId(boxId);

        int at = IndexOf(boxId);
        var row = at >= 0 ? _boxes[at] : NewRow(boxId);
        if (at >= 0 && (row.Phase == (byte)BoxPhase.Held ||
                        row.Phase == (byte)BoxPhase.Consumed))
            return false;

        row.Holder = clientId;
        row.Phase = (byte)BoxPhase.Held;
        row.Pos = box.transform.position;
        row.Rot = box.transform.rotation;
        row.Vel = Vector3.zero;
        uint minimumNext = row.Version + 1;
        row.Version = version > minimumNext ? version : minimumNext;
        Write(at, row);
        _nextCarryReportByBox.Remove(boxId);
        _lastCarryReportByBox[boxId] = NetworkManager.ServerTime.Time;
        return true;
    }

    public bool ServerCanGrantGrabFromContainer(ulong boxId, ulong clientId)
    {
        if (!IsServer || ClientAlreadyHoldsBox(clientId) ||
            BoxPhysicsInteractable.FindByNetId(boxId) == null)
            return false;

        int at = IndexOf(boxId);
        return at < 0 || (_boxes[at].Phase != (byte)BoxPhase.Held &&
                          _boxes[at].Phase != (byte)BoxPhase.Consumed);
    }

    public bool TryGetCartCargoVersion(ulong boxId, out uint version)
    {
        int at = CartCargoIndexOf(boxId);
        version = at >= 0 ? _cartCargo[at].Version : 0;
        return at >= 0;
    }

    void OnCartCargoChanged(NetworkListEvent<CartCargoState> evt)
    {
        switch (evt.Type)
        {
            case NetworkListEvent<CartCargoState>.EventType.Add:
            case NetworkListEvent<CartCargoState>.EventType.Value:
            case NetworkListEvent<CartCargoState>.EventType.Insert:
                ApplyCartCargoState(evt.Value);
                break;
            case NetworkListEvent<CartCargoState>.EventType.Remove:
            case NetworkListEvent<CartCargoState>.EventType.RemoveAt:
                RemoveCartCargoLocally(evt.Value);
                break;
        }
    }

    void ApplyCartCargoState(CartCargoState state)
    {
        var box = BoxPhysicsInteractable.FindByNetId(state.BoxId);
        if (box == null || NetworkManager.Singleton == null) return;
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(
                state.CartNetworkObjectId, out var cartNetObj)) return;

        var cart = cartNetObj.GetComponent<CartStorageInteractable>();
        if (cart == null) return;
        if (box.StoredInCart != null && box.StoredInCart != cart)
            box.StoredInCart.SyncBoxRemoved(box);
        box.NoteNetVersion(state.Version);
        cart.SyncBoxPlaced(box, state.SlotIndex, state.LocalPos, state.LocalRot);
    }

    void RemoveCartCargoLocally(CartCargoState state)
    {
        var box = BoxPhysicsInteractable.FindByNetId(state.BoxId);
        if (box == null || box.StoredInCart == null) return;
        box.StoredInCart.SyncBoxRemoved(box);
    }

    int CartCargoIndexOf(ulong boxId)
    {
        for (int i = 0; i < _cartCargo.Count; i++)
            if (_cartCargo[i].BoxId == boxId) return i;
        return -1;
    }

    void RemoveCartCargo(ulong boxId)
    {
        int at = CartCargoIndexOf(boxId);
        if (at < 0) return;
        ulong cartId = _cartCargo[at].CartNetworkObjectId;
        _cartCargo.RemoveAt(at);
        RefreshCartLayout(cartId);
    }

    void RefreshCartLayout(ulong cartId)
    {
        // Removing a middle box restacks the remaining cargo locally. Persist those resulting
        // poses as part of the authoritative manifest so the reconciliation sweep cannot restore
        // the old gap a second later.
        for (int i = 0; i < _cartCargo.Count; i++)
        {
            var state = _cartCargo[i];
            if (state.CartNetworkObjectId != cartId) continue;
            var box = BoxPhysicsInteractable.FindByNetId(state.BoxId);
            if (box == null || box.StoredInCart == null) continue;
            int slot = box.StoredInCart.GetStoredIndex(box);
            if (slot < 0) continue;
            state.SlotIndex = slot;
            state.LocalPos = box.FollowLocalPos;
            state.LocalRot = box.FollowLocalRot;
            _cartCargo[i] = state;
        }
    }

    // ── Server-only helper for NetworkPalletManager ───────────────────────────────

    /// Server-side: forget a box entirely after an atomic container transaction transfers
    /// ownership of its pose to the pallet or cart manifest.
    public void ServerRemoveGrabState(ulong boxId)
    {
        if (!IsServer) return;
        RemoveRow(boxId);
    }

    /// Moves a non-held world box into a persistent container manifest and returns the next
    /// lifecycle version. Used only by the server's physical auto-snap trigger.
    public bool ServerMoveWorldBoxIntoContainer(ulong boxId, out uint containerVersion)
    {
        containerVersion = 0;
        if (!IsServer || BoxPhysicsInteractable.FindByNetId(boxId) == null) return false;

        int at = IndexOf(boxId);
        if (at >= 0 && (_boxes[at].Phase == (byte)BoxPhase.Held ||
                        _boxes[at].Phase == (byte)BoxPhase.Consumed))
            return false;

        containerVersion = at >= 0 ? _boxes[at].Version + 1 : 1;
        RemoveCartCargo(boxId);
        RemoveRow(boxId);
        return true;
    }

    /// Server-side world motion (thief shove, shelf collapse, pallet blast). Publishes the
    /// starting pose and velocity through the same persistent state table used by throws, so
    /// every peer simulates the same box and the server's later Resting row reconciles the result.
    public bool ServerCanMoveBox(ulong boxId)
    {
        if (!IsServer) return false;
        int at = IndexOf(boxId);
        return at < 0 || (_boxes[at].Phase != (byte)BoxPhase.Held &&
                          _boxes[at].Phase != (byte)BoxPhase.Consumed);
    }

    /// True once the server has published any authoritative lifecycle state for this box.
    /// Runtime delivery objects can be instantiated before this manager is spawned; callers use
    /// this to retry the initial publication without relaunching an already-settled body.
    public bool ServerHasBoxState(ulong boxId) => IsServer && IndexOf(boxId) >= 0;

    /// Starts authoritative physics for a box moved by a real server-side collision. Returns true
    /// only for the transition into Flying, allowing the caller to start exactly one settle watcher.
    public bool ServerBeginWorldMotion(ulong boxId, Vector3 pos, Quaternion rot, Vector3 velocity)
    {
        if (!IsServer || !Finite(pos) || !Finite(velocity) || !TryNormalize(rot, out rot))
            return false;

        int at = IndexOf(boxId);
        if (at >= 0)
        {
            BoxPhase phase = (BoxPhase)_boxes[at].Phase;
            if (phase == BoxPhase.Held || phase == BoxPhase.Consumed || phase == BoxPhase.Flying)
                return false;
        }

        ServerPublishWorldMotion(boxId, pos, rot, velocity);
        return true;
    }

    public void ServerPublishWorldMotion(ulong boxId, Vector3 pos, Quaternion rot, Vector3 velocity,
        uint minimumVersion = 0)
    {
        if (!IsServer || !Finite(pos) || !Finite(velocity) || !TryNormalize(rot, out rot)) return;

        int at = IndexOf(boxId);
        var row = at >= 0 ? _boxes[at] : NewRow(boxId);
        if (at >= 0 && (row.Phase == (byte)BoxPhase.Held ||
                        row.Phase == (byte)BoxPhase.Consumed)) return;

        row.Holder  = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.LocalClientId
            : NetworkManager.ServerClientId;
        row.Phase   = (byte)BoxPhase.Flying;
        row.Pos     = pos;
        row.Rot     = rot;
        row.Vel     = velocity;
        uint nextVersion = row.Version + 1;
        row.Version = nextVersion < minimumVersion ? minimumVersion : nextVersion;
        Write(at, row);
    }

    // ── Host-authoritative free-body snapshots ───────────────────────────────────

    void BroadcastMotionSnapshots()
    {
        _motionSnapshots.Clear();
        uint sequence = ++_motionSequence;
        bool sendReliableKeyframe = Time.unscaledTime >= _nextReliableMotionKeyframe;
        if (sendReliableKeyframe)
            _nextReliableMotionKeyframe = Time.unscaledTime + ReliableMotionKeyframeInterval;

        for (int i = 0; i < _boxes.Count; i++)
        {
            BoxNetState row = _boxes[i];
            if (row.Phase != (byte)BoxPhase.Flying) continue;

            var box = BoxPhysicsInteractable.FindByNetId(row.BoxId);
            var body = box != null ? box.GetComponent<Rigidbody>() : null;
            if (box == null || body == null) continue;

            _motionSnapshots.Add(new BoxMotionSnapshot
            {
                BoxId = row.BoxId,
                Version = row.Version,
                Sequence = sequence,
                Position = box.transform.position,
                Rotation = box.transform.rotation,
                LinearVelocity = body.isKinematic ? Vector3.zero : body.linearVelocity,
                AngularVelocity = body.isKinematic ? Vector3.zero : body.angularVelocity,
            });

            if (_motionSnapshots.Count < MotionSnapshotsPerRpc) continue;
            SendMotionSnapshotBatch(sendReliableKeyframe);
            _motionSnapshots.Clear();
        }

        if (_motionSnapshots.Count > 0)
            SendMotionSnapshotBatch(sendReliableKeyframe);
    }

    void SendMotionSnapshotBatch(bool sendReliableKeyframe)
    {
        BoxMotionSnapshot[] batch = _motionSnapshots.ToArray();
        MotionSnapshotsClientRpc(batch);
        if (sendReliableKeyframe) MotionKeyframesClientRpc(batch);
    }

    [ClientRpc(Delivery = RpcDelivery.Unreliable)]
    void MotionSnapshotsClientRpc(BoxMotionSnapshot[] snapshots)
    {
        // A host receives its own ClientRpc too. Its Rigidbody is the authority and must never be
        // steered by the visual proxy intended for remote clients.
        if (IsServer || snapshots == null) return;

        ApplyMotionSnapshots(snapshots);
    }

    // Unreliable motion is the smooth path. This periodic reliable copy is the recovery path: a
    // busy Relay/client can lose several transient packets, but may never leave a kinematic proxy
    // frozen in mid-air waiting for the final Resting row.
    [ClientRpc]
    void MotionKeyframesClientRpc(BoxMotionSnapshot[] snapshots)
    {
        if (IsServer || snapshots == null) return;
        ApplyMotionSnapshots(snapshots);
    }

    void ApplyMotionSnapshots(BoxMotionSnapshot[] snapshots)
    {
        for (int i = 0; i < snapshots.Length; i++)
        {
            BoxMotionSnapshot snapshot = snapshots[i];
            var box = BoxPhysicsInteractable.FindByNetId(snapshot.BoxId);
            if (box == null || box.IsLocallyGrabbed || snapshot.Version < box.NetVersion) continue;

            if (_lastMotionSequenceByBox.TryGetValue(snapshot.BoxId, out uint lastSequence) &&
                snapshot.Sequence <= lastSequence)
                continue;
            _lastMotionSequenceByBox[snapshot.BoxId] = snapshot.Sequence;

            box.NoteNetVersion(snapshot.Version);
            box.ApplyAuthoritativeMotionSnapshot(
                snapshot.Position, snapshot.Rotation,
                snapshot.LinearVelocity, snapshot.AngularVelocity,
                snapImmediately: false);
        }
    }

    // ── The table ─────────────────────────────────────────────────────────────────

    int IndexOf(ulong boxId)
    {
        for (int i = 0; i < _boxes.Count; i++)
            if (_boxes[i].BoxId == boxId) return i;

        return -1;
    }

    static BoxNetState NewRow(ulong boxId) => new()
    {
        BoxId  = boxId,
        Holder = ulong.MaxValue,      // nobody
        Phase  = (byte)BoxPhase.Resting,
        Rot    = Quaternion.identity,
    };

    /// Writes a row in place, or appends it. Server only — the list refuses anything else.
    void Write(int at, BoxNetState row)
    {
        if (at >= 0) _boxes[at] = row;
        else         _boxes.Add(row);
    }

    void RemoveRow(ulong boxId)
    {
        int at = IndexOf(boxId);
        if (at >= 0) _boxes.RemoveAt(at);
        _nextCarryReportByBox.Remove(boxId);
        _lastCarryReportByBox.Remove(boxId);
    }

    // ── Rest ──────────────────────────────────────────────────────────────────────

    /// Commits the server physics result. This deliberately is not an RPC: clients simulate for
    /// responsiveness, but only the server is allowed to choose the persistent resting pose.
    public void ServerConfirmBoxRest(ulong boxId, Vector3 restPos, Quaternion restRot)
    {
        if (!IsServer || BoxPhysicsInteractable.FindByNetId(boxId) == null ||
            !Finite(restPos) || !TryNormalize(restRot, out restRot))
            return;

        int at  = IndexOf(boxId);
        var row = at >= 0 ? _boxes[at] : NewRow(boxId);

        // A landing cannot overrule a carry: by the time this arrives somebody may have caught
        // the box, and his hands outrank where it would have fallen.
        if (at >= 0 && (row.Phase == (byte)BoxPhase.Held ||
                       row.Phase == (byte)BoxPhase.Consumed)) return;

        row.Holder  = NetworkManager.ServerClientId;
        row.Phase   = (byte)BoxPhase.Resting;
        row.Pos     = restPos;
        row.Rot     = restRot;
        row.Vel     = Vector3.zero;
        row.Version = row.Version + 1;

        Write(at, row);
        _nextCarryReportByBox.Remove(boxId);
        _lastCarryReportByBox.Remove(boxId);
    }

}

/// One transient host-physics sample. These are deliberately unreliable: a newer sample replaces
/// an older one, while lifecycle changes and the final resting pose remain reliable NetworkList
/// state. Sixteen samples fit comfortably below the transport MTU.
public struct BoxMotionSnapshot : INetworkSerializable
{
    public ulong BoxId;
    public uint Version;
    public uint Sequence;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 LinearVelocity;
    public Vector3 AngularVelocity;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref BoxId);
        serializer.SerializeValue(ref Version);
        serializer.SerializeValue(ref Sequence);
        serializer.SerializeValue(ref Position);
        serializer.SerializeValue(ref Rotation);
        serializer.SerializeValue(ref LinearVelocity);
        serializer.SerializeValue(ref AngularVelocity);
    }
}

/// What a box can be doing. Anything not in this table is lying where the scene put it.
public enum BoxPhase : byte
{
    Held    = 0,   // in somebody's hands; Pos/Rot follow them at 20 Hz
    Flying  = 1,   // let go at Pos/Rot with Vel; every peer runs the same throw
    Resting = 2,   // came to rest at Pos/Rot, and that is where it is on every machine
    Consumed = 3,  // installed/delivered; persistent tombstone for late joiners
}

/// Everything the network knows about one box.
///
/// One row, one truth. Pos/Rot are continuous — the latest is simply the latest. Version marks the
/// one-shot moments inside that state, a throw and a landing: without it a peer re-reading the
/// table would relaunch a box that had already come to rest, and a peer that missed the change
/// would never launch it at all. Holder names the machine that produced the current phase — the
/// carrier while Held, the thrower while Flying, whoever announced the landing while Resting — so
/// that machine can recognise its own doing and not apply it twice.
public struct BoxNetState : INetworkSerializable, System.IEquatable<BoxNetState>
{
    public ulong      BoxId;
    public ulong      Holder;    // ulong.MaxValue = nobody
    public byte       Phase;
    public uint       Version;
    public Vector3    Pos;
    public Quaternion Rot;
    public Vector3    Vel;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref BoxId);
        serializer.SerializeValue(ref Holder);
        serializer.SerializeValue(ref Phase);
        serializer.SerializeValue(ref Version);
        serializer.SerializeValue(ref Pos);
        serializer.SerializeValue(ref Rot);
        serializer.SerializeValue(ref Vel);
    }

    // NetworkList compares rows to decide what changed, so this has to cover every field a change
    // can land in — a pose that moved with an unchanged id and phase is still a change.
    public bool Equals(BoxNetState other) =>
        BoxId   == other.BoxId   && Holder == other.Holder &&
        Phase   == other.Phase   && Version == other.Version &&
        Pos     == other.Pos     && Rot    == other.Rot &&
        Vel     == other.Vel;

    public override bool Equals(object obj) => obj is BoxNetState other && Equals(other);

    public override int GetHashCode() => BoxId.GetHashCode() * 397 ^ (int)Version;
}

/// One authoritative row for one box loaded on a cart. Unlike the old one-shot placement RPC,
/// this survives late joins and carries enough information to reproduce order and offsets.
public struct CartCargoState : INetworkSerializable, System.IEquatable<CartCargoState>
{
    public ulong BoxId;
    public ulong CartNetworkObjectId;
    public int SlotIndex;
    public Vector3 LocalPos;
    public Quaternion LocalRot;
    public uint Version;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref BoxId);
        serializer.SerializeValue(ref CartNetworkObjectId);
        serializer.SerializeValue(ref SlotIndex);
        serializer.SerializeValue(ref LocalPos);
        serializer.SerializeValue(ref LocalRot);
        serializer.SerializeValue(ref Version);
    }

    public bool Equals(CartCargoState other) =>
        BoxId == other.BoxId && CartNetworkObjectId == other.CartNetworkObjectId &&
        SlotIndex == other.SlotIndex && LocalPos == other.LocalPos && LocalRot == other.LocalRot &&
        Version == other.Version;

    public override bool Equals(object obj) => obj is CartCargoState other && Equals(other);
    public override int GetHashCode() => BoxId.GetHashCode() * 397 ^ SlotIndex;
}
