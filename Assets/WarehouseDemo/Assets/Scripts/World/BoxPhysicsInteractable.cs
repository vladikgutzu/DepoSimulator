using UnityEngine;
using Unity.Collections;
using Unity.Netcode;

// Multiplayer: NetBoxId is the NetworkObjectId, so it names the same box on every peer.
// Grab/release are server-validated via NetworkPhysBoxManager.
// Single-player: all network code is gated on NetworkManager.Singleton?.IsListening.
// Never Transform.SetParent this object at runtime — it has a NetworkObject component and
// Netcode reverts (+ error-logs "Only the server can re-parent NetworkObjects") any parent
// change made by a client without authority. Use BeginFollow/EndFollow instead, which track
// a moving parent (pallet, cart) by re-deriving world position every frame — same pattern
// PalletBehavior.Attach/SyncToFork already uses for riding the forklift.
[RequireComponent(typeof(Rigidbody))]
public class BoxPhysicsInteractable : MonoBehaviour, IInteractable, ICanInteractCheck
{
    static BoxPhysicsInteractable _grabbed;
    static readonly int IsCarryingBoxHash = Animator.StringToHash("IsCarryingBox");
    static PhysicsMaterial s_cargoPhysicsMaterial;

    public static PhysicsMaterial CargoPhysicsMaterial
    {
        get
        {
            if (s_cargoPhysicsMaterial == null)
            {
                s_cargoPhysicsMaterial = new PhysicsMaterial("PalletCargo")
                {
                    hideFlags       = HideFlags.HideAndDontSave,
                    dynamicFriction = 0.60f,
                    staticFriction  = 0.85f,
                    bounciness      = 0f,
                    frictionCombine = PhysicsMaterialCombine.Average,
                    bounceCombine   = PhysicsMaterialCombine.Minimum,
                };
            }
            return s_cargoPhysicsMaterial;
        }
    }

    public static BoxPhysicsInteractable GrabbedBox => _grabbed;

    // Set by CartStorageInteractable when this box is stored on a cart
    public CartStorageInteractable StoredInCart   { get; set; }
    // Set by PalletStackZone when this box is placed on a pallet
    public PalletStackZone         StoredOnPallet { get; set; }

    // A box that was violently knocked loose — by a thief wrecking a pallet, a shelf
    // collapse, a pallet drop — is supposed to STAY on the floor as mess. Without this it
    // settles on whatever pallet it happened to land next to and SettleAndFreeze tidies it
    // straight back into a stack, undoing the vandalism (and handing the thief the same box
    // to shove off again). Cleared the moment a player picks it up: putting boxes away by
    // hand is exactly what auto-snap is for.
    const float ScatterNoSnapTime = 30f;
    float _noAutoSnapUntil;
    bool _persistentPhysics;
    PalletStackZone _persistentSupportPallet;
    uint _settleGeneration;
    float _nextImpactAudioTime;

    // ── Multiplayer: stable per-box integer ID ────────────────────────────────────

    // Identity comes from NGO: a network-spawned box has the same NetworkObjectId on every
    // peer, by definition. The previous scheme -- a static counter incremented in Awake --
    // relied on Awake order matching across processes (Unity guarantees no such thing) and
    // broke outright the moment BoxPhysicsInteractable was added at runtime, which
    // PartSpawner.EnsureBoxPhysics and NetworkBoxManager.ApplyAssignmentLocally both do:
    // from that point each side numbered its boxes differently, so a grab request named a
    // different box on the receiver.
    //
    // Packed order boxes are the exception -- CoopGameManager.SpawnPackedBoxClientRpc builds
    // a separate local twin on each client rather than network-spawning one, so they have no
    // NetworkObjectId to share. Those keep an explicit server-issued id via ForceNetId, held
    // in their own registry so they can never collide with (or evict) a real NetworkObjectId.
    ulong _forcedId;
    NetworkObject _netObj;

    // ── Who owns a box's transform, and when ─────────────────────────────────────

    /// The stock NetworkTransform some box prefabs carry, and the reason one player could not
    /// throw while the other could.
    ///
    /// It is server-authoritative (AuthorityMode 0). On the server that costs nothing: the
    /// authority reads the transform and replicates it. On a client it is the opposite — NGO
    /// writes the transform every PreLateUpdate from the server's interpolated pose
    /// (NetworkTransform.OnUpdate → ApplyAuthoritativeState), so a client's copy cannot go
    /// anywhere the server has not already been.
    ///
    /// A throw is exactly that case. The client applies 32 m/s locally; the server's copy is
    /// still a hundred-odd milliseconds behind, holding the box in the thrower's hands. Physics
    /// carries the box out, the network drags it back into the man who threw it, and it ends up
    /// at his feet. Measured, full charge: the box reached 1.43 m, was back at 0.72 m sixty
    /// milliseconds later, hit the thrower's own rig four times with **zero impulse** each — the
    /// signature of a transform being written rather than struck — and finished 0.66 m out having
    /// travelled a path of 2.99 m. The host, being the authority, saw none of it.
    ///
    /// So the component is switched off for the length of a flight and back on when the box
    /// settles. Nothing is lost while it is off: the release RPC carries position, rotation and
    /// velocity, every peer simulates the same throw from them, and ConfirmBoxRest pulls the
    /// copies back together at the end — the bargain this file already documents in Unsupport.
    /// It is back on at rest, where it is useful again, replicating the server-only pushes a box
    /// takes from a thief's bat, a collapsing stack or a forklift.
    ///
    /// NGO gates the call on `enabled` itself (NetworkManager's PreLateUpdate loop), so this is
    /// the supported switch rather than a trick.
    Unity.Netcode.Components.NetworkTransform _netTransform;

    /// True while replication may drive this box's transform.
    ///
    /// Off from the moment a pair of hands takes it until the moment it comes to rest: through
    /// that stretch the box belongs to whoever is carrying it and then to physics, and the network
    /// carries the intent — hands, throw, landing — through NetworkPhysBoxManager's table rather
    /// than through a stream of poses. Two owners of one transform is what produced every version
    /// of "the box will not move where I put it".
    ///
    /// On again at rest, where it is useful and uncontested: it is how everyone else sees the
    /// pushes a still box takes from a thief's bat, a collapsing stack, or a forklift.
    void FlightBelongsToPhysics(bool flying)
    {
        if (_netTransform == null) return;
        if (_netTransform.enabled != flying) return;   // already where it should be

        // Where the box actually is, read **before** the component is touched.
        //
        // This is the whole trick and the first attempt got it wrong. Enabling a NetworkTransform
        // that has been off for the length of a flight applies the pose it was switched off at —
        // the box's place before the throw — and it does so synchronously, inside the assignment
        // below. A reading taken afterwards is therefore already the stale one, and publishing it
        // as "where the box is" pins the mistake instead of correcting it. Measured: a box that
        // announced its rest at (4.68, 0.00, -0.63) was, one line later, standing at
        // (3.04, 0.00, -2.26) — exactly where it had been picked up.
        Vector3    here = transform.position;
        Quaternion how  = transform.rotation;

        _netTransform.enabled = !flying;

        if (!flying) RestoreAndPublish(here, how);
    }

    /// Undoes the snap that enabling the component just did, and tells the network the truth.
    ///
    /// The local half always runs: the box goes back where the physics left it, whatever this peer
    /// is allowed to say about it. The published half only runs on the side that may speak —
    /// Teleport throws outright on a peer that is not the authority, and which side that is depends
    /// on the component: a plain NetworkTransform answers to the server, a ClientNetworkTransform
    /// to the owner. Everyone else gets the answer from whoever does, which is the arrangement
    /// working as intended.
    void RestoreAndPublish(Vector3 pos, Quaternion rot)
    {
        transform.SetPositionAndRotation(pos, rot);

        if (_rb != null) { _rb.position = pos; _rb.rotation = rot; }

        if (_netObj == null || !_netObj.IsSpawned || !_netTransform.enabled) return;

        bool mineToSay = _netTransform.IsServerAuthoritative()
            ? NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer
            : _netObj.IsOwner;

        if (mineToSay) _netTransform.Teleport(pos, rot, transform.localScale);
    }

    public ulong NetBoxId =>
        _forcedId != 0UL                        ? _forcedId
      : _netObj != null && _netObj.IsSpawned    ? _netObj.NetworkObjectId
      : 0UL;

    static readonly System.Collections.Generic.Dictionary<ulong, BoxPhysicsInteractable>
        s_forcedById = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetNetworkIds() => s_forcedById.Clear();

    // Scene boxes that sit under a pallet pack are the second exception, and the one that went
    // unnoticed longest: pallet_box_pack_prefab carries a single NetworkObject on its root and
    // 24 grabbable boxes underneath it, none of which has one of its own. GetComponent (rather
    // than GetComponentInParent) is deliberate -- reaching up to the pallet would hand all 24
    // boxes the same NetworkObjectId, so a grab request would name whichever of them the
    // receiver happened to look up first. Without an id of their own they used to fall through
    // to the local grab path in Interact(), which is why a box carried off a pallet was visible
    // only to the player carrying it.
    //
    // They get their own id space, derived rather than exchanged: both peers deserialize the same
    // scene, so both can arrive at the same numbering without saying a word to each other -- see
    // SceneNetIds.
    //
    // The id is a hash of the box's own scene path, and that "own" is the whole point. It used to
    // be the box's index in a list sorted by authored position, and an index depends on everything
    // else in the list: this component is added at runtime in two places (PartSpawner
    // .EnsureBoxPhysics, NetworkBoxManager.ApplyAssignmentLocally), and the pass numbers whatever
    // carries one at the moment it runs. One such box present for one peer's pass and absent for
    // the other's -- easy, since a client's pass runs when it loads the scene, long after the
    // host's -- shifted every id after it by one. From then on every grab, carry, throw and rest
    // message named a different box on the receiver: the observer froze somebody else's box and
    // switched its colliders off, making it unpickable for everyone while the real one sat
    // untouched. Nothing logged a word. A path hash cannot do that: what a box is called does not
    // depend on which other boxes exist.
    //
    // Base sits above NGO's NetworkObjectId range and above CoopGameManager's packed-order-box
    // range (1 << 32), so the three id spaces can never overlap in s_forcedById. The hash is
    // masked to 40 bits to stay inside that range -- with a few thousand boxes the odds of two
    // colliding are about one in a million, and the pass says so out loud if it ever happens.
    const ulong SceneIdBase = 1UL << 40;
    const ulong SceneIdMask = (1UL << 40) - 1UL;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void InstallSceneIdPass() => SceneNetIds.RunEveryLoad(AssignSceneNetworkIds);

    /// The id a box derives from where it sits in the scene. Both peers deserialize the same scene,
    /// so both arrive at the same number without exchanging a word — see SceneNetIds.HierarchyPath.
    static ulong SceneIdFor(Transform t) =>
        SceneIdBase + (Fnv1a(SceneNetIds.BoxHierarchyPath(t)) & SceneIdMask);

    /// Names a **scene** box whose physics component was added after the numbering pass had run.
    ///
    /// The boxes carry no NetworkObject any more, so an id is the only thing that makes one the
    /// same box on two machines, and the pass only sees what exists at scene load. PartSpawner
    /// giving a crate its physics when an order is accepted, and NetworkBoxManager doing the same
    /// on a client, both land after that — and a box with no id is one nobody else can be told
    /// about: grabbable locally, invisible everywhere else.
    ///
    /// Deliberately not done in Awake for every box. A box *instantiated* at runtime — a vehicle
    /// delivery crate, a packed order — has a path that exists only on the machine that made it,
    /// and two peers need not agree on it. Those either get an explicit id from the server
    /// (ForceNetId) or stay local on purpose, which is what they did before and still do.
    public void AdoptSceneId()
    {
        if (_netObj != null || _forcedId != 0UL) return;
        ForceNetId(SceneIdFor(transform));
    }

    static void AssignSceneNetworkIds()
    {
        // Entries left by a scene that has just been unloaded -- every box in it is destroyed,
        // and NGO reloads the scene on a joining client, so this is the common case rather than
        // the exotic one. Dropping them keeps a stale id from resolving to a dead component.
        var dead = new System.Collections.Generic.List<ulong>();
        foreach (var kv in s_forcedById)
            if (kv.Value == null) dead.Add(kv.Key);
        foreach (var id in dead) s_forcedById.Remove(id);

        // The pass lands after every Awake, so _netObj is already cached on everything it sees.
        var unowned = new System.Collections.Generic.List<BoxPhysicsInteractable>();
        foreach (var box in FindObjectsByType<BoxPhysicsInteractable>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            // A box with its own NetworkObject already has an id both sides agree on, and a packed
            // order box was handed one by the server. Neither needs numbering here.
            //
            // A scene box that named itself in Awake does, though: it is in this pass's set, and
            // leaving it out would make the digest describe a fraction of the warehouse — the one
            // number that tells two machines whether they hold the same boxes at all.
            if (box == null || box._netObj != null) continue;
            if (box._forcedId != 0UL && (box._forcedId < SceneIdBase || box._forcedId > SceneIdBase + SceneIdMask))
                continue;   // a packed box's server-issued id, not a scene name
            unowned.Add(box);
        }

        // Sorted before numbering even though the id no longer depends on the order: it keeps the
        // pass deterministic to read and to log, and it makes a collision report name the same
        // pair on both machines.
        SceneNetIds.SortDeterministically(unowned);

        var takenBy = new System.Collections.Generic.Dictionary<ulong, string>(unowned.Count);

        foreach (var box in unowned)
        {
            if (box == null) continue;

            string path = SceneNetIds.BoxHierarchyPath(box.transform);
            // Loading another scene must not rename an already registered box.
            ulong  id   = box._forcedId != 0UL ? box._forcedId : SceneIdFor(box.transform);

            // Two boxes hashing to one id would silently become one box as far as every RPC is
            // concerned -- grabbing one would freeze the other. Vanishingly unlikely, and named
            // out loud rather than trusted to stay unlikely.
            if (takenBy.TryGetValue(id, out string owner))
            {
                Debug.LogError($"[BoxPhysicsInteractable] Scene id collision: '{path}' and " +
                               $"'{owner}' both hash to {id:X}. Rename one of them — until then " +
                               "the two are one box to every network message.");
                continue;
            }

            takenBy[id] = path;
            box.ForceNetId(id);
        }

        SceneIdCount = takenBy.Count;
        SceneIdHash  = HashNumbering(unowned);
        NetworkPhysBoxManager.ReportSceneIds();
    }

    // ── Proving both peers numbered the same boxes ────────────────────────────────

    /// What the last numbering pass produced: how many boxes it named, and a digest of which.
    ///
    /// A difference indicates incompatible registered identities, even if box counts match.
    /// Such a mismatch can prevent carry and position updates from resolving on another peer.
    /// See NetworkPhysBoxManager.ReportSceneIds.
    public static int   SceneIdCount { get; private set; }
    public static ulong SceneIdHash  { get; private set; }

    /// Digest of the SET of registered IDs — a sum, so the order they were walked
    /// in cannot change it. Positions are deliberately not hashed: two peers agree on the authored
    /// scene, not on where physics has since pushed things, and a digest that drifted with the
    /// boxes would cry wolf every session.
    ///
    /// It used to be a chain, which made it a digest of the order as well as of the set: the
    /// numbering is sorted by quantised position, so two peers whose floats land either side of a
    /// centimetre boundary walk the same boxes in a different order and reported a difference that
    /// was not there. A warning that fires when nothing is wrong teaches you to ignore it, and this
    /// one is the only thing that can tell "the two machines hold different warehouses" apart from
    /// every other symptom it produces.
    static ulong HashNumbering(System.Collections.Generic.List<BoxPhysicsInteractable> ordered)
    {
        ulong h = 0;

        unchecked
        {
            foreach (var box in ordered)
            {
                if (box == null) continue;
                h += Fnv1a(box._forcedId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return h;
    }

    /// FNV-1a, seeded so it can be chained across several strings.
    static ulong Fnv1a(string s, ulong seed = 14695981039346656037UL)
    {
        const ulong prime = 1099511628211UL;

        ulong h = seed;
        foreach (char c in s) { h ^= c; h *= prime; }

        // Separator, so "ab" + "c" and "a" + "bc" are not the same chain.
        h ^= (ulong)'|';
        h *= prime;

        return h;
    }

    /// Find a box by its stable network ID. Returns null if not found.
    public static BoxPhysicsInteractable FindByNetId(ulong id)
    {
        if (id == 0UL) return null;

        if (s_forcedById.TryGetValue(id, out var forced) && forced != null) return forced;

        var nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null) return null;
        return nm.SpawnManager.SpawnedObjects.TryGetValue(id, out var netObj) && netObj != null
            ? netObj.GetComponent<BoxPhysicsInteractable>()
            : null;
    }

    /// True only when this box has an identity both sides agree on. Locally-instantiated packed
    /// orders and vehicle-delivery crates receive deterministic forced IDs from their authoritative
    /// spawn record; authored scene boxes receive a path-derived ID in the pass above.
    bool IsNetworked => NetworkManager.Singleton != null
                     && NetworkManager.Singleton.IsListening
                     && NetBoxId != 0UL;

    /// Assign a server-issued id to a box that has no NetworkObject of its own -- packed order
    /// boxes, which each client instantiates locally from the same ClientRpc. Ids come from a
    /// range far above anything NGO hands out, so the two id spaces never meet.
    public void ForceNetId(ulong id)
    {
        if (id == 0UL)
        {
            Debug.LogError($"[BoxPhysicsInteractable] Refused zero forced ID on '{name}'.");
            return;
        }
        if (s_forcedById.TryGetValue(id, out var collision) && collision != null &&
            collision != this)
        {
            Debug.LogError($"[BoxPhysicsInteractable] Forced ID collision {id:X}: " +
                           $"'{collision.name}' and '{name}'. The new box was not registered.");
            return;
        }
        if (_forcedId != 0UL && s_forcedById.TryGetValue(_forcedId, out var previous) &&
            previous == this)
            s_forcedById.Remove(_forcedId);
        _forcedId = id;
        s_forcedById[id] = this;
    }

    // ── Serialized fields ─────────────────────────────────────────────────────────

    [Header("Carry anchor (player-local, fallback if bounds fail)")]
    [SerializeField] float   _handHeight    = 0.85f;
    [SerializeField] float   _holdDistance  = 0.05f;
    [SerializeField] Vector3 _carryRotation = Vector3.zero;

    [Header("Physics thresholds")]
    [SerializeField] float _heavyLimit       = 30f;
    [SerializeField] float _linearDamping    = 2.5f;
    [SerializeField] float _angularDamping   = 4.0f;

    [Header("Impact ragdoll")]
    [Tooltip("Minimum collision impulse (N·s) required to trigger player ragdoll. " +
             "Heavy boxes at speed will easily exceed 12. Light tap ~2-4.")]
    [SerializeField] float _ragdollImpulseThreshold = 12f;
    // Minimum closing speed (m/s) for a box to knock anyone down, on top of the impulse threshold.
    const float MinKnockdownSpeed = 3f;

    Rigidbody   _rb;
    Rigidbody[] _childRbs;
    Collider[]  _ownColliders;
    Collider[]  _childRbColliders; // colliders belonging to child RBs — kept off when parent is dynamic
    Transform   _playerTransform;
    Animator    _playerAnimator;
    Vector3     _carryOffset;
    Vector3     _carryVisualSize;
    bool        _bulkyPackedCarry;
    float       _packedCarryPitch;
    Transform   _packedGripRight;
    Transform   _packedGripLeft;
    float       _releaseY;
    Coroutine   _physicsReleaseRoutine;

    // Multiplayer: last time a position update was sent (throttle to 20 Hz)
    float _lastNetUpdate;

    // "Follow" state for boxes resting on a pallet or cart — see BeginFollow/EndFollow.
    Transform  _followParent;
    Vector3    _followLocalPos;
    Quaternion _followLocalRot;
    bool       _followMotionActive;

    // Remote-carry smoothing: the carrier's pose arrives at 20 Hz (see TickUpdate) — hard-setting
    // the transform straight from each packet reads as a stutter on every receiving client, since
    // 50 ms gaps plus jitter are well above one visual frame. So the pose is chased every frame
    // instead of snapped to.
    //
    // The rate is a lag the observer pays on top of the network's own. As an exponential chase it
    // covers 63% of the gap in 1/speed seconds: at 15 that is 67 ms, and the last of the gap takes
    // twice as long again — a carried box visibly trailing its carrier's hands, then catching up
    // when he stops. At 30 the same chase is inside a couple of frames and still absorbs the gap
    // between packets, which is all it was ever for.
    const float RemotePosSmoothSpeed = 30f;

    /// How often the carrier tells the server where his box is. See the carry report in Update.
    const float CarryReportInterval = 0.1f;

    /// The carrier's own puppet, while somebody else's box is in view.
    ///
    /// An observed box used to chase its replicated world position and nothing else, on a stream of
    /// its own — so it agreed with the man carrying it only as far as the two streams agreed with
    /// each other. Lose or delay either and the box drifts off the hands that hold it, which is the
    /// "box flying through the air by itself" every player in a session has seen. Riding the
    /// carrier's transform instead makes the two one object: whatever the puppet does, late,
    /// interpolated or teleported, the box does with it. The replicated pose is then only used to
    /// keep the offset honest — and a few packets of it a second are enough for that.
    Transform  _remoteCarrier;
    Vector3    _remoteLocalPos;
    Quaternion _remoteLocalRot;
    bool       _haveRemoteLocal;
    float      _nextCarrierLook;

    Vector3    _remoteTargetPos;
    Quaternion _remoteTargetRot;
    bool       _remoteSmoothing;

    // Free boxes are simulated only by the listen server. Every non-server peer keeps a
    // kinematic visual proxy which chases the latest unreliable server snapshot. Lifecycle and
    // final rest still arrive through NetworkPhysBoxManager's reliable state table.
    const float AuthoritativeMotionSmoothSpeed = 24f;
    // Longer than the 250 ms reliable keyframe interval. A lost transient packet therefore keeps
    // following a plausible ballistic arc instead of visibly stopping after 120 ms.
    const float AuthoritativeMotionMaxPrediction = 0.6f;
    bool       _authoritativeMotionProxy;
    Vector3    _authoritativeTargetPos;
    Quaternion _authoritativeTargetRot;
    Vector3    _authoritativeLinearVelocity;
    Vector3    _authoritativeAngularVelocity;
    float      _authoritativeSnapshotTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetStatics() { _grabbed = null; s_remoteHeld.Clear(); }

    // Called from PlayerInteraction when E is pressed and nothing is in crosshair.
    public static void ReleaseGrabbed(float throwForce = 3f, Vector3? overrideDir = null) =>
        _grabbed?.Release(throwForce, overrideDir);

    void Awake()
    {
        // Cached, not read per call: NetBoxId is hit from the carry hot path every frame.
        // Null here is fine and expected for packed boxes -- they use ForceNetId instead.
        _netObj = GetComponent<NetworkObject>();

        // May be absent, and on most boxes is: only the ones with a NetworkObject of their own
        // carry one. See FlightBelongsToPhysics.
        _netTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();

        _rb = GetComponent<Rigidbody>();

        // A box with no body at all. Every authored box has one and every runtime path adds one
        // before this component, so this is the case nobody planned for — and it threw a
        // NullReferenceException out of Unsupport, from inside a network message handler, which
        // takes the rest of that batch down with it: one odd box and *no* box syncs any more.
        // Given one anyway, configured like PartSpawner does it, and said out loud so the box that
        // needs fixing in the scene can be named rather than guessed at.
        if (_rb == null)
        {
            Debug.LogWarning($"[BoxPhysicsInteractable] '{name}' had no Rigidbody — added one. " +
                             $"Path: {SceneNetIds.HierarchyPath(transform)}");
            _rb = gameObject.AddComponent<Rigidbody>();
            _rb.mass           = 8f;
            _rb.linearDamping  = 0.4f;
            _rb.angularDamping = 2f;
            _rb.isKinematic    = true;
            _rb.useGravity     = false;
        }

        _rb.interpolation          = RigidbodyInterpolation.None;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        _ownColliders  = GetComponentsInChildren<Collider>(true);


        // What each piece sits on while it is furniture, so it can be put back there. Captured
        // rather than assumed: a box is not always plain Default — see CameraBlind.
        _restLayers = new int[_ownColliders.Length];
        for (int i = 0; i < _ownColliders.Length; i++)
            _restLayers[i] = _ownColliders[i] != null ? _ownColliders[i].gameObject.layer : 0;

        // Collect child Rigidbodies (excluding self).
        // AddRigidbodyToBoxes adds Rigidbody to every nested "box" mesh,
        // so these must be made kinematic together with the parent on grab.
        var all = GetComponentsInChildren<Rigidbody>(true);
        var list = new System.Collections.Generic.List<Rigidbody>(all.Length);
        foreach (var rb in all)
            if (rb != _rb) list.Add(rb);
        _childRbs = list.ToArray();

        // Pre-collect colliders on child-RB GameObjects so we can disable them
        // after Release. Kinematic child colliders + dynamic parent RB = jitter.
        var childCols = new System.Collections.Generic.List<Collider>();
        foreach (var rb in _childRbs)
            childCols.AddRange(rb.GetComponentsInChildren<Collider>(true));
        _childRbColliders = childCols.ToArray();

        // Runtime-spawned boxes can appear after NetworkPhysBoxManager's initial client pass.
        // Freeze them immediately on a non-server peer so only the host ever advances PhysX.
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening &&
            !NetworkManager.Singleton.IsServer)
            PrepareAsNetworkPhysicsReplica();
    }

    public static void PrepareAllAsNetworkPhysicsReplicas()
    {
        foreach (var box in FindObjectsByType<BoxPhysicsInteractable>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            box?.PrepareAsNetworkPhysicsReplica();
    }

    void PrepareAsNetworkPhysicsReplica()
    {
        if (_rb == null || _grabbed == this) return;

        if (!_rb.isKinematic)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
        _rb.isKinematic = true;
        _rb.useGravity = false;

        foreach (var child in _childRbs)
        {
            if (child == null) continue;
            if (!child.isKinematic)
            {
                child.linearVelocity = Vector3.zero;
                child.angularVelocity = Vector3.zero;
            }
            child.isKinematic = true;
            child.useGravity = false;
            child.detectCollisions = false;
        }
    }

    void OnDestroy()
    {
        if (_forcedId != 0UL && s_forcedById.TryGetValue(_forcedId, out var registered) &&
            registered == this)
            s_forcedById.Remove(_forcedId);
        if (_grabbed == this) _grabbed = null;
        BoxPhysicsTickDriver.Unregister(this);
    }

    // ── Player reference ─────────────────────────────────────────────────────────

    static Transform s_cachedPlayer;
    static Animator  s_cachedAnimator;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetPlayerCache() { s_cachedPlayer = null; s_cachedAnimator = null; }

    void FindPlayer()
    {
        if (s_cachedPlayer == null)
        {
            var go = GameObject.FindGameObjectWithTag("Player");
            if (go == null) return;
            s_cachedPlayer   = go.transform;
            s_cachedAnimator = go.GetComponentInChildren<Animator>();
        }
        _playerTransform = s_cachedPlayer;
        _playerAnimator  = s_cachedAnimator;
    }

    // ── Bounds-based carry offset ─────────────────────────────────────────────────

    // Returns a player-local position where the box CENTER should sit so that:
    //   • the BOTTOM of the box is at _handHeight (hand level)
    //   • the BACK of the box is at _holdDistance from the player's origin
    Vector3 ComputeCarryOffset()
    {
        if (GetComponent<PackedOrderBox>() != null && TryGetRootLocalVisualBounds(out Bounds local))
        {
            // Packed prefabs do not guarantee that their root pivot is at the geometric centre.
            // Position the actual underside support point on the character's fixed palms instead
            // of treating transform.position as the box centre. CarryRotation is included here so
            // the same point stays on the hands after a tall package tilts onto the torso.
            // Rest the centre of the near lower edge in the palms. This is the edge the carrier
            // actually reaches under, not the centre of the whole bottom face; using centre.z
            // buried both hands halfway beneath a deep generated carton.
            Vector3 supportLocal = new(local.center.x, local.min.y, local.min.z);
            Vector3 scaledSupport = Vector3.Scale(supportLocal, transform.localScale);
            Vector3 palmsLocal    = PlayerHandIK.PackedBoxSupportLocalPosition;
            return palmsLocal - CarryRotation() * scaledSupport;
        }

        var rends = GetComponentsInChildren<Renderer>(false);
        if (rends.Length == 0) return new Vector3(0f, _handHeight, _holdDistance);

        var b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);

        float halfH = b.extents.y;
        float halfD = b.extents.z;
        // Packed products keep their real dimensions and sit against the carrier's torso. Their
        // lower edge therefore belongs around waist height; the dynamically generated hand grips
        // then land around the chest instead of pulling both arms above the robot's head.
        float bottomHeight = GetComponent<PackedOrderBox>() != null ? 0.95f : _handHeight;

        return new Vector3(
            0f,
            bottomHeight  + halfH,   // centre Y = bottom target + half-height
            _holdDistance + halfD    // centre Z = hold dist + half-depth → back face at hold dist
        );
    }

    // ── IInteractable ────────────────────────────────────────────────────────────

    public bool IsHeavyBox => _rb != null && _rb.mass > _heavyLimit;
    public bool HasBulkyCarryDebuff => _grabbed == this && _bulkyPackedCarry;
    public bool CanBeCaught
    {
        get
        {
            float speed = _authoritativeMotionProxy
                ? _authoritativeLinearVelocity.magnitude
                : (_rb != null ? _rb.linearVelocity.magnitude : 0f);
            if (_rb == null || (!_authoritativeMotionProxy && _rb.isKinematic) || speed < 1.5f)
                return false;
            if (_grabbed != null || _remotelyGrabbed || StoredInCart != null) return false;
            return GetComponent<PackedOrderBox>() == null || PackedOrderHeight() <= 1.35f;
        }
    }

    public string InteractPrompt
    {
        get
        {
            if (StoredInCart != null) return $"Take from cart ({_rb.mass:F0} kg)  {InputHints.E}";
            if (_grabbed == this) return $"Set down {InputHints.E}";
            if (_remotelyGrabbed)
            {
                string holder = null;
                foreach (var np in NetworkPlayer.All)
                    if (np.OwnerClientId == _remoteGrabberId) { holder = np.PlayerName; break; }
                return $"Take from {(string.IsNullOrEmpty(holder) ? "player" : holder)}  {InputHints.E}";
            }
            if (CanBeCaught) return $"Catch box  {InputHints.E}";
            var partTag = GetComponent<BoxWithPart>();
            if (partTag != null && partTag.Part != null)
                return $"{partTag.Part.PartName}  {InputHints.E}  (in box)";
            var vehiclePartTag = GetComponent<VehiclePartBox>();
            if (vehiclePartTag != null && vehiclePartTag.Part != null)
                return $"{vehiclePartTag.Part.PartName}  {InputHints.E}  (vehicle part — take to bench)";
            if (IsHeavyBox) return $"[HEAVY] box ({_rb.mass:F0} kg)  {InputHints.E}";
            return $"Pick up box ({_rb.mass:F0} kg)  {InputHints.E}";
        }
    }

    public bool CanInteract(GameObject interactor)
    {
        if (StoredInCart != null) return _grabbed == null; // take only if hands free
        if (_grabbed == this) return true;
        return true;
    }

    public void Interact(GameObject interactor)
    {
        // Box is stored on cart — delegate removal to cart
        if (StoredInCart != null)
        {
            if (_grabbed != null)
            {
                HUDController.Instance?.AddNotification(
                    "Hands full", "Set down the box first [E]",
                    HUDController.NotifType.Warning);
                return;
            }
            StoredInCart.TakeBox(this, interactor);
            return;
        }

        // Pallet cargo is a container transaction in multiplayer: the server removes the cargo
        // manifest row and grants the hand state atomically. Grab() must not locally dismantle the
        // stack first, otherwise two players can both take the same carton.
        if (StoredOnPallet != null && IsNetworked)
        {
            if (_grabbed != null)
            {
                HUDController.Instance?.AddNotification(
                    "Hands full", "Set down the box first [E]",
                    HUDController.NotifType.Warning);
                return;
            }
            StoredOnPallet.RequestTakeBox(this);
            return;
        }

        if (_grabbed == this) { Release(); return; }

        // Block if already carrying something
        if (_grabbed != null)
        {
            HUDController.Instance?.AddNotification(
                "Hands full",
                "Drop the box first [E]",
                HUDController.NotifType.Warning);
            return;
        }

        // Arm injury prevents lifting heavy boxes.
        if (IsHeavyBox && InjurySystem.Instance?.CanCarryHeavy == false)
        {
            HUDController.Instance?.AddNotification(
                "Arm injury",
                "Can't lift heavy things",
                HUDController.NotifType.Danger);
            return;
        }

        // Multiplayer: ask server for permission — Grab() runs after server confirms.
        // A box with no shared id falls through to the local grab path below.
        if (IsNetworked)
        {
            NetworkPhysBoxManager.Instance?.RequestGrabServerRpc(NetBoxId);
            return;
        }

        Grab();
    }

    // Called when the box is destroyed while grabbed (e.g. installed at bench).
    // Clears grabbed state and resets player without touching this transform
    // or starting coroutines — safe to call before Destroy().
    public void SilentRelease()
    {
        if (_grabbed != this) return;
        _grabbed = null;
        BoxPhysicsTickDriver.Unregister(this);
        _playerAnimator?.SetBool(IsCarryingBoxHash, false);
        PlayerHandIK.Instance?.ClearTargets();
    }

    /// Called by PalletStackZone to snap this box onto the pallet.
    /// Snaps position for 1 FixedUpdate (as trigger) to avoid pushing the player,
    /// then releases to full physics — no kinematic locking.
    public void PlaceOnPallet(PalletStackZone zone, Transform parent, Vector3 localPosition, Quaternion localRot)
    {
        if (_grabbed != this) return;
        _grabbed = null;

        BeginFollow(parent, localPosition, localRot);

        _rb.linearVelocity  = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.isKinematic     = true; // temporary — cleared in ReleaseToPhysics after 1 FixedUpdate
        _rb.useGravity      = false;

        // Enable as TRIGGERS — CharacterController ignores triggers so it won't be
        // pushed even if the box snaps into the player's capsule for this one frame.
        bool[] wasTrigger = new bool[_ownColliders.Length];
        for (int i = 0; i < _ownColliders.Length; i++)
        {
            wasTrigger[i]              = _ownColliders[i].isTrigger;
            _ownColliders[i].enabled   = true;
            _ownColliders[i].isTrigger = true;
        }

        // Child RBs stay kinematic (no detectCollisions) same as after a normal Release —
        // they follow the parent transform; the root RB drives all physics.
        foreach (var rb in _childRbs)
        {
            rb.linearVelocity   = Vector3.zero;
            rb.angularVelocity  = Vector3.zero;
            rb.isKinematic      = true;
            rb.useGravity       = false;
            rb.detectCollisions = false;
        }

        StoredOnPallet = zone;

        _playerAnimator?.SetBool(IsCarryingBoxHash, false);
        PlayerHandIK.Instance?.ClearTargets();
        // Deliberate grid placement is a soft set-down, not a falling-box impact.
        AudioManager.Instance?.PlayReturnPart();
        _nextImpactAudioTime = Time.unscaledTime + 0.6f;

        _physicsReleaseRoutine = StartCoroutine(ReleaseToPhysics(wasTrigger));
    }

    System.Collections.IEnumerator ReleaseToPhysics(bool[] wasTrigger)
    {
        // Wait 1 FixedUpdate while colliders are triggers — CharacterController clears.
        yield return new WaitForFixedUpdate();
        _physicsReleaseRoutine = null;
        if (StoredOnPallet == null) yield break; // grabbed back before this fired

        EndFollow(); // box goes fully dynamic below — no longer needs to be pinned to the pallet

        // Restore solid colliders; disable child-RB ones (same pattern as Release()).
        for (int i = 0; i < _ownColliders.Length; i++)
            _ownColliders[i].isTrigger = wasTrigger[i];
        foreach (var col in _childRbColliders) col.enabled = false;

        // Release main RB to physics — box falls and settles naturally, no kinematic.
        _rb.isKinematic            = false;
        _rb.linearVelocity         = Vector3.zero;
        _rb.angularVelocity        = Vector3.zero;
        _rb.useGravity             = true;
        _rb.detectCollisions       = true;
        _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        _rb.linearDamping          = _linearDamping;
        _rb.angularDamping         = _angularDamping;
        _rb.constraints            = RigidbodyConstraints.None;
    }

    // Called by CartStorageInteractable to cleanly transfer from hands to cart.
    // Colliders are NOT re-enabled here — CartStorageInteractable calls
    // EnableCollidersOnCart() after positioning the box to avoid briefly
    // having solid colliders at the carry position (which pushes the player up).
    public void PlaceOnCart(CartStorageInteractable cart)
    {
        if (_grabbed != this) return;

        _grabbed = null;
        // Child RBs stay kinematic — they follow the cart via BeginFollow, set up next
        // by CartStorageInteractable.PlaceBox(). No need to activate gravity; the root
        // RB is made kinematic by PlaceBox.

        _playerAnimator?.SetBool(IsCarryingBoxHash, false);
        PlayerHandIK.Instance?.ClearTargets();
    }

    // Called by CartStorageInteractable after the box has been positioned on the cart.
    // Mirrors the Release() pattern: enable as triggers first so CharacterController
    // passes through for one FixedUpdate (giving PhysX time to sync the kinematic
    // body's position), then restore to solid.
    public void EnableCollidersOnCart()
    {
        StartCoroutine(EnableCollidersOnCartRoutine());
    }

    System.Collections.IEnumerator EnableCollidersOnCartRoutine()
    {
        foreach (var col in _ownColliders) { col.enabled = true; col.isTrigger = true; }
        yield return new WaitForFixedUpdate();
        if (StoredInCart != null)   // still on cart (not grabbed back)
            foreach (var col in _ownColliders) col.isTrigger = false;
    }

    // ── Grab / Release ───────────────────────────────────────────────────────────

    /// Takes the box out of whatever it was standing in, the moment it is picked up.
    ///
    /// The warehouse's boxes are authored as children of their pallet, and nothing ever moved them
    /// out: a box carried off and dropped across the room stayed a child of the pallet it came
    /// from. Mostly that is only untidy — until a forklift drives that pallet away and the box
    /// lying on the floor goes with it.
    ///
    /// The file header's rule against SetParent is why this needs the first line. A NetworkObject
    /// polices its own parent: OnTransformParentChanged puts the old one back and logs an error
    /// whenever the NetworkManager is not listening, which in singleplayer is always. Clearing
    /// AutoObjectParentSync is Netcode's own way of saying "this object's parent is not yours to
    /// manage", and it is honest here — these boxes are never parented over the network. They ride
    /// pallets and carts through BeginFollow, which is what the header's rule was steering towards
    /// in the first place.
    ///
    /// Done locally on every peer rather than replicated, for the same reason: with parenting out
    /// of Netcode's hands, each copy has to leave the pallet on its own, and each one does it at
    /// the moment it learns the box was picked up.
    public void DetachFromParent()
    {
        if (transform.parent == null) return;

        var netObj = GetComponent<NetworkObject>();
        if (netObj != null) netObj.AutoObjectParentSync = false;

        transform.SetParent(null, true);
    }

    // ── Pallet/cart follow (replaces Transform.SetParent — see file header) ───────

    /// Snap immediately to parent-local pose and track it every LateUpdate while kinematic.
    public void BeginFollow(Transform parent, Vector3 localPos, Quaternion localRot)
    {
        StopAuthoritativeMotionProxy();

        // Temporary, with the throw trace. Landing exactly in a stack slot can only happen through
        // here, so when a box that has just been thrown turns up on a pallet, this is the one line
        // that names who put it there. The stack frame is cheap at this call rate — a box is pinned
        // a handful of times a shift, not per frame.
        if (TraceThrows)
        {
            var caller = new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod();

            Debug.Log($"[BoxThrow] FOLLOW '{name}'#{GetInstanceID()} id={NetBoxId:X} pinned to " +
                      $"'{parent.name}' at local {localPos:F2} (world " +
                      $"{parent.TransformPoint(localPos):F2}) by " +
                      $"{caller?.DeclaringType?.Name}.{caller?.Name}");
        }

        _followParent   = parent;
        _followLocalPos = localPos;
        _followLocalRot = localRot;
        var pallet = parent.GetComponentInParent<PalletBehavior>();
        _followMotionActive = pallet == null || pallet.IsLifted;
        SyncFollowPose();

        if (_followMotionActive)
            BoxPhysicsTickDriver.Register(this);
        else if (_grabbed != this && !_remoteSmoothing && !_authoritativeMotionProxy)
            BoxPhysicsTickDriver.Unregister(this);
    }

    public void EndFollow()
    {
        _followParent = null;
        _followMotionActive = false;

        // Following a pallet is only one of three reasons a box is on the tick driver — being
        // carried and being remote-smoothed are the others — so leaving the follow must not take
        // the box off it while one of those is still true.
        //
        // This is what put a team-mate's box in the pallet with his hands stretching towards it.
        // Taking a box off a container changes two persistent manifests: the container row is
        // removed and the Held row is added. Either callback may run first locally, so leaving
        // follow mode must not unregister an already-started remote carry smoother.
        if (_grabbed == this || _remoteSmoothing || _authoritativeMotionProxy) return;

        BoxPhysicsTickDriver.Unregister(this);
    }

    /// Parked pallet cargo keeps its saved local pose without joining the global per-frame tick.
    /// A lifted pallet enables following for its own cargo only, then disables it again on release.
    public void SetFollowMotionActive(bool active)
    {
        if (_followParent == null) return;

        _followMotionActive = active;
        if (active)
        {
            SyncFollowPose();
            BoxPhysicsTickDriver.Register(this);
        }
        else if (_grabbed != this && !_remoteSmoothing && !_authoritativeMotionProxy)
        {
            BoxPhysicsTickDriver.Unregister(this);
        }
    }

    void SyncFollowPose()
    {
        if (_followParent == null) return;

        transform.position = _followParent.TransformPoint(_followLocalPos);
        transform.rotation = _followParent.rotation * _followLocalRot;
        if (_rb != null)
        {
            _rb.position = transform.position;
            _rb.rotation = transform.rotation;
        }
    }

    public Vector3    FollowLocalPos => _followLocalPos;
    public Quaternion FollowLocalRot => _followLocalRot;

    /// True when this box already tracks the exact persistent container pose. Network manifest
    /// reconciliation uses this to repair missed/out-of-order state without reapplying hundreds
    /// of unchanged pallet rows every second on clients.
    public bool IsFollowing(Transform parent, Vector3 localPos, Quaternion localRot) =>
        _followParent == parent
        && Vector3.SqrMagnitude(_followLocalPos - localPos) < 0.000001f
        && Quaternion.Angle(_followLocalRot, localRot) < 0.01f;

    void Grab()
    {
        StopAuthoritativeMotionProxy();
        LeavePersistentSupportPallet();

        // Cancel pending physics-release if box is grabbed before it fires
        if (_physicsReleaseRoutine != null)
        {
            StopCoroutine(_physicsReleaseRoutine);
            _physicsReleaseRoutine = null;
        }

        // Notify pallet zone so it can repack remaining boxes
        if (StoredOnPallet != null)
        {
            StoredOnPallet.OnBoxPickedUp(this);
            StoredOnPallet = null;
        }

        EndFollow();
        DetachFromParent();

        // Back in human hands — from here on it is being put away, not scattered.
        _noAutoSnapUntil = 0f;

        FindPlayer();
        _grabbed = this;
        _carryVisualSize  = MeasureVisualSize();
        _bulkyPackedCarry = GetComponent<PackedOrderBox>() != null
                         && PackedOrderHeight() > 1.35f;
        _packedCarryPitch = GetComponent<PackedOrderBox>() != null
                          ? PackedCarryPitch(PackedOrderHeight()) : 0f;
        BoxPhysicsTickDriver.Register(this);

        // In hand and about to be thrown: from here until it settles, it is not scenery.
        CameraBlind(true);

        // In hands: the carrier owns the pose and reports it. Replication stays out until it rests.
        FlightBelongsToPhysics(true);

        _carryOffset = ComputeCarryOffset();
        if (GetComponent<PackedOrderBox>() != null)
            EnsurePackedGripTargets();

        _rb.isKinematic = true;
        _rb.useGravity  = false;

        // Isolate child Rigidbodies from simulation during carry — detectCollisions=false
        // removes them from collision detection; combined with isKinematic they won't
        // accumulate velocity from parent movement and won't explode on release.
        foreach (var rb in _childRbs)
        {
            rb.isKinematic      = true;
            rb.useGravity       = false;
            rb.detectCollisions = false;
        }

        foreach (var col in _ownColliders) col.enabled = false;

        // Snap to world-space carry position and follow the player each LateUpdate while kinematic.
        if (_playerTransform != null)
        {
            transform.position = _playerTransform.TransformPoint(_carryOffset);
            transform.rotation = _playerTransform.rotation * CarryRotation();
            _rb.position = transform.position;
            _rb.rotation = transform.rotation;
        }

        _playerAnimator?.SetBool(IsCarryingBoxHash, true);

        bool packedOrder = GetComponent<PackedOrderBox>() != null;
        if (packedOrder)
        {
            BindPackedCarryIK(PlayerHandIK.Instance);
            PlayerHandIK.Instance?.SetCarryLean(
                _bulkyPackedCarry ? CarryLeanForHeight(PackedOrderHeight()) : 0f,
                immediate: true);
        }
        else
        {
            var anchors = GetComponent<BoxHandAnchors>();
            if (anchors != null)
                PlayerHandIK.Instance?.SetTargets(anchors.RightHandTarget, anchors.LeftHandTarget,
                                                  refresh: SyncCarryPose);
            PlayerHandIK.Instance?.SetCarryLean(anchors != null ? anchors.CarryLean : 0f,
                                                immediate: true);
        }

        AudioManager.Instance?.PlayPickup();
    }

    void Release(float throwForce = 3f, Vector3? overrideDir = null)
    {
        if (_grabbed != this) return;
        _grabbed = null;
        BoxPhysicsTickDriver.Unregister(this);

        Vector3 throwDir;
        if (overrideDir.HasValue)
        {
            // Explicit direction (e.g. ragdoll knocking the box out of hand) — skip the
            // camera-aim behavior below, which is only meant for a deliberate player throw.
            throwDir = overrideDir.Value;
        }
        else
        {
            var cam = Camera.main;
            throwDir = cam != null ? cam.transform.forward
                : (_playerTransform != null ? _playerTransform.forward : Vector3.forward);
        }

        // Release from the pose that was already visible in the carrier's hands. Moving the box
        // to a second, player-relative launch point here made every throw look like two separate
        // shoves: first a 0.6 m transform jump, then the actual Rigidbody flight.
        if (throwDir.sqrMagnitude < 0.0001f)
            throwDir = _playerTransform != null ? _playerTransform.forward : Vector3.forward;
        throwDir.Normalize();

        Vector3 finalPos       = transform.position;
        Vector3 releaseVelocity = throwDir * throwForce;

        // Multiplayer: notify other clients of the release with the final position and velocity.
        if (IsNetworked)
            NetworkPhysBoxManager.Instance?.ReleaseBoxServerRpc(
                NetBoxId, NetVersion, finalPos, transform.rotation, releaseVelocity);

        // From here the throw is physics, not replication — see FlightBelongsToPhysics.
        FlightBelongsToPhysics(true);

        // Enable as TRIGGERS first — CharacterController ignores triggers so it
        // won't be pushed this frame. Made solid on the next FixedUpdate.
        bool[] wasTrigger = new bool[_ownColliders.Length];
        for (int i = 0; i < _ownColliders.Length; i++)
        {
            wasTrigger[i]              = _ownColliders[i].isTrigger;
            _ownColliders[i].enabled   = true;
            _ownColliders[i].isTrigger = true;
        }
        foreach (var col in _childRbColliders) col.enabled = false;

        _releaseY = transform.position.y;

        // Dynamic first: a velocity written while the body is still kinematic is refused, and
        // Unity says so in the console on every single drop.
        _rb.isKinematic            = false;
        _rb.position               = finalPos;
        _rb.linearVelocity         = releaseVelocity;
        _rb.angularVelocity        = Vector3.zero;
        _rb.useGravity             = true;
        _rb.detectCollisions       = true;
        _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        _rb.linearDamping          = _linearDamping;
        _rb.angularDamping         = _angularDamping;
        _rb.constraints            = RigidbodyConstraints.None;

        StartCoroutine(RestoreColliders(wasTrigger));

        // Child RBs stay kinematic — they follow the parent Transform and don't
        // need independent simulation. Only the root RB drives the physics.
        foreach (var rb in _childRbs)
        {
            rb.linearVelocity   = Vector3.zero;
            rb.angularVelocity  = Vector3.zero;
            rb.detectCollisions = false;
            rb.isKinematic      = true;
            rb.useGravity       = false;
        }

        _playerAnimator?.SetBool(IsCarryingBoxHash, false);
        PlayerHandIK.Instance?.ClearTargets();
        // The release itself is silent. The first meaningful collision below selects the
        // light/heavy impact cue, preventing one throw from sounding like two impacts.

        // Don't let the throw hit the man who threw it — see PassThrough. His rig and, in a
        // session, his own network puppet both stand in the box's first half metre.
        PassThrough(_playerTransform);
        if (NetworkPlayer.Local != null) PassThrough(NetworkPlayer.Local.transform);

        _traceThrower = _playerTransform;

        TraceThrow("local", releaseVelocity);

        if (IsNetworked && NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
        {
            // The throw is responsive immediately, but the client does not keep a competing
            // Rigidbody simulation. The accepted server flight and its snapshots take over this
            // same proxy; if the request is rejected, the reliable Held row restores the carry.
            ApplyAuthoritativeMotionSnapshot(
                finalPos, transform.rotation, releaseVelocity, Vector3.zero,
                snapImmediately: true);
        }
        else
        {
            StartCoroutine(SettleAndFreeze());
        }
    }

    // ── Temporary: watching a throw live or die ───────────────────────────────────
    // Two lines per throw per machine: what it left with, and what it still had a third of a
    // second later. A throw that leaves at 32 and is down to 2 by the next sample was stopped by
    // something; one that leaves at 3 was never charged. Delete this block once the answer is in.

    // Off. Everything below is gated on it — the throw trace, the contacts, the resting
    // handshake, the state-table watch and PlayerInteraction's wind-up lines — so this one flag
    // is the whole switch. Left in place rather than deleted because it is what found the two
    // faults in this file that no screenshot could: a box that went home to its shelf the moment
    // replication was handed back, and a wind-up whose meter was told 5% four hundred times.
    //
    // Turn it on before reproducing anything about throwing, and off again afterwards: it logs
    // per contact and per state change, which is a lot of lines on a floor with four thousand
    // boxes.
    public static bool TraceThrows = false;

    /// The box whose throw is being watched, and until when. One at a time and one second at a
    /// time: the state table is walked for thousands of boxes a second, and a log of all of it is
    /// not a trace, it is a denial of service. Temporary, with the rest of this block.
    public static ulong TracedBoxId;
    public static float TracedUntil;

    /// True while this box is the one being watched.
    public bool IsTraced => TraceThrows && NetBoxId == TracedBoxId && Time.time <= TracedUntil;

    float     _traceUntil;    // contacts are logged only while a throw is young
    int       _traceHits;     // …and only the first few, so a box rolling to rest is not a novel
    Vector3   _traceFrom;     // where the throw left, to say how far each contact happened
    Transform _traceThrower;  // whose throw it was, to tell his body from anyone else's

    /// Says where a box went when it stopped being where it landed. Same switch as the throw
    /// trace, and to be deleted with it.
    void TraceVanish(string why)
    {
        if (!TraceThrows) return;

        Debug.Log($"[BoxThrow] GONE '{name}' id={NetBoxId:X} — {why}. " +
                  $"at={transform.position} releaseY={_releaseY:0.00}");
    }

    void TraceThrow(string what, Vector3 vel)
    {
        if (!TraceThrows) return;

        var  cam   = Camera.main;
        var  dir   = vel.sqrMagnitude > 0.0001f ? vel.normalized : Vector3.zero;
        // Signed pitch of the throw: positive is upward. A camera framing the player from behind
        // and slightly above hands its forward vector a downward tilt, and a box let go at hand
        // height with a few degrees of down on it is in the floor within two metres.
        float pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;

        Debug.Log($"[BoxThrow] {what} '{name}' id={NetBoxId:X} networked={IsNetworked} " +
                  $"speed={vel.magnitude:0.0} pitch={pitch:0.0}° dir={dir} " +
                  $"from={transform.position} cam={(cam != null ? cam.name : "none")}");

        _traceUntil = Time.time + 1f;
        _traceHits  = 0;
        _traceFrom  = transform.position;

        TracedBoxId = NetBoxId;
        TracedUntil = Time.time + 2f;

        StartCoroutine(TraceAfter(what));
    }

    /// Names everything the throw runs into while it is still young.
    ///
    /// This logged the first contact only, and that hid the answer rather than showing it: a
    /// throw's first touch is often a graze that costs it nothing — one measured at impulse 4.3
    /// with the box still doing 26 m/s afterwards — and whatever actually stopped it came next,
    /// off the end of the log. So: every contact for a second, with how far out it happened and
    /// whose body it was, because "a player" and "which player" are different answers.
    void TraceContact(Collision col)
    {
        const int maxHits = 6;

        if (!TraceThrows || Time.time > _traceUntil || _traceHits >= maxHits) return;
        _traceHits++;

        var other = col.collider != null ? col.collider.gameObject : null;
        if (other == null) return;

        Vector3 at    = col.contactCount > 0 ? col.GetContact(0).point : transform.position;
        string  whose = Whose(other);

        Debug.Log($"[BoxThrow] hit #{_traceHits} '{name}' → '{other.name}'{whose} " +
                  $"(layer {LayerMask.LayerToName(other.layer)}, tag {other.tag}) " +
                  $"{(at - _traceFrom).magnitude:0.00}m out" +
                  $"{(_traceThrower != null ? $", {(at - _traceThrower.position).magnitude:0.00}m from thrower" : "")} " +
                  $"impulse={col.impulse.magnitude:0.0} speed now={_rb.linearVelocity.magnitude:0.0}");
    }

    /// Whose body a collider belongs to, when it belongs to one at all. "Which player" is the
    /// question the previous log could not answer: a puppet capsule and a team-mate's puppet
    /// capsule print the same object name, and they mean opposite things.
    static string Whose(GameObject go)
    {
        var np = go.GetComponentInParent<NetworkPlayer>();
        if (np != null)
            return $" [puppet of client {np.OwnerClientId}{(np.IsOwner ? ", MINE" : "")}]";

        if (go.CompareTag("Player")) return " [this machine's own scene rig]";

        return "";
    }

    /// Samples the flight rather than only its end, because the end alone cannot tell the two
    /// remaining suspects apart. A box slowed by an impact reads as a ramp that flattens: each
    /// sample further out than the last, the steps shrinking. A box whose transform is being
    /// written from somewhere else — a server-authoritative NetworkTransform pulling a client's
    /// copy back to a pose a hundred milliseconds stale — reads as steps that stall or go
    /// backwards while the rigidbody still claims speed. The last field says exactly that: real
    /// distance covered against the distance its own velocity should have carried it.
    System.Collections.IEnumerator TraceAfter(string what)
    {
        Vector3 from  = transform.position;
        Vector3 last  = from;
        float   crawl = 0f;                    // path length actually walked, corners and all
        var     steps = new System.Text.StringBuilder();

        for (int i = 0; i < 5; i++)
        {
            yield return new WaitForSeconds(0.06f);
            if (_rb == null) yield break;

            Vector3 now = transform.position;
            crawl += (now - last).magnitude;
            last   = now;

            steps.Append($"{(now - from).magnitude:0.00} ");
        }

        Debug.Log($"[BoxThrow] {what} +0.3s '{name}' id={NetBoxId:X} " +
                  $"speed={_rb.linearVelocity.magnitude:0.0} " +
                  $"moved={(transform.position - from).magnitude:0.00}m " +
                  $"path={crawl:0.00}m steps=[{steps.ToString().TrimEnd()}] " +
                  $"kinematic={_rb.isKinematic} grabbed={_grabbed == this} " +
                  $"remote={_remotelyGrabbed} smoothing={_remoteSmoothing}");
    }

    // ── Letting a throw out of the thrower ────────────────────────────────────────

    /// Lets this box through one player's colliders until it is clear of him.
    ///
    /// A throw leaves from the player's centre plus 0.6 metres, at hand height. The bodies
    /// standing in that spot are wider than the gap: the rig's capsule is 0.28 of radius, the box
    /// about 0.3 of half-width, and in a session two more colliders occupy the same place — the
    /// owner's own network puppet and its ragdoll bones. So the box was being born in contact with
    /// its thrower and stopped by him in the first physics steps. Measured, at full charge: a
    /// 32 m/s throw hit 'Player' 0.38 m out and was down to 13.8 m/s having covered 0.74 m. It
    /// read in the game as "I cannot throw" — and, since a session puts more bodies in that spot
    /// than single player does, as a multiplayer bug.
    ///
    /// The old guard — colliders switched to triggers for one FixedUpdate — was written for a
    /// different problem (not shoving the CharacterController on release) and covers one physics
    /// step of the rig alone. It is kept; this covers the rest.
    ///
    /// Ends on distance rather than on a timer, so a gentle drop is let go of as soon as it has
    /// left the man's own space instead of falling through him for a fixed third of a second. The
    /// timeout is only there for a box that never gets clear — wedged in a corner, or dropped
    /// straight down at his feet.
    void PassThrough(Transform body)
    {
        if (body == null) return;
        StartCoroutine(PassThroughRoutine(body));
    }

    System.Collections.IEnumerator PassThroughRoutine(Transform body)
    {
        const float clearDistance = 1.1f;   // past any capsule radius plus the box's own reach
        const float timeout       = 0.6f;

        var pairs = new System.Collections.Generic.List<(Collider Mine, Collider Theirs)>();

        foreach (var theirs in body.GetComponentsInChildren<Collider>(true))
        {
            // A carried limb, a weapon in the fist, a box in the arms: children of the player
            // that are not the player. Ignoring them for half a second costs nothing, but the
            // box's own colliders must never be handed to IgnoreCollision against themselves.
            if (theirs == null || theirs.transform.IsChildOf(transform)) continue;

            foreach (var mine in _ownColliders)
            {
                if (mine == null) continue;
                Physics.IgnoreCollision(mine, theirs, true);
                pairs.Add((mine, theirs));
            }
        }

        if (pairs.Count == 0) yield break;

        float until = Time.time + timeout;
        while (Time.time < until && body != null && _grabbed != this)
        {
            if ((transform.position - body.position).sqrMagnitude > clearDistance * clearDistance)
                break;

            yield return null;
        }

        foreach (var (mine, theirs) in pairs)
            if (mine != null && theirs != null) Physics.IgnoreCollision(mine, theirs, false);
    }

    // After dropping: wait for the box to genuinely rest, then freeze it kinematic.
    // Destroys the box if it falls through the floor.
    System.Collections.IEnumerator RestoreColliders(bool[] wasTrigger)
    {
        yield return new WaitForFixedUpdate();
        for (int i = 0; i < _ownColliders.Length; i++)
            if (_ownColliders[i] != null)
                _ownColliders[i].isTrigger = wasTrigger[i];
    }

    System.Collections.IEnumerator SettleAndFreeze()
    {
        uint settleGeneration = _settleGeneration;

        // Read and cleared here, at the start of the fall it belongs to, rather than at the end.
        // Every path out of this coroutine before the announcement -- grabbed again mid-flight,
        // fallen through the floor, auto-snapped onto a pallet -- used to leave the flag set, and
        // the next fall of this box, a genuine local throw included, inherited the silence.
        bool announce = !_skipRestBroadcast;
        _skipRestBroadcast = false;

        // In a session the server's warehouse is the real one, and it says where every box it
        // settles came to rest — whoever set that box moving.
        //
        // Before this, only the machine that *started* a motion announced it, which covered a
        // throw and nothing else. Everything physics does on its own — a flying crate ploughing
        // into a stack, a stack losing the box underneath it, a forklift clipping a pile — was
        // simulated independently on every peer and announced by nobody, so each machine kept its
        // own version of where those boxes ended up. Those differences never healed: a box at rest
        // is kinematic and silent until somebody picks it up again. Two warehouses drifting apart
        // one collapse at a time is what that looks like from the floor.
        //
        // A client still settles locally, so letting go of a box feels immediate, but it no longer
        // announces anything: the server's answer arrives a moment later and it takes that.
        if (IsNetworked && NetworkManager.Singleton != null)
            announce = NetworkManager.Singleton.IsServer;

        // Every flight in this file comes through here — a throw, a remote release, a shelf
        // collapse, a stack losing its bottom box — which makes it the one place that has to say
        // the box is in motion. Put back at the two exits that mean it has come to rest; the exits
        // that mean "grabbed again" deliberately leave it set, because it is still in motion.
        CameraBlind(true);

        yield return new WaitForFixedUpdate();

        const float fallLimit     = 6f;   // metres below release = fell through world
        const float watchdogTime  = 8f;   // max seconds before force-freeze
        const float restRequired  = 0.5f; // seconds of stillness before freeze
        const float restThreshold = 0.005f * 0.005f; // sqrMagnitude

        float elapsed  = 0f;
        float restTime = 0f;

        while (_persistentPhysics || elapsed < watchdogTime)
        {
            if (settleGeneration != _settleGeneration) yield break;

            // Grabbed again in mid-air. Replication stays off: a carried box is driven by its
            // carrier and put back under the network by whichever release ends that carry.
            if (_grabbed == this || _remotelyGrabbed || _authoritativeMotionProxy) yield break;

            // Fall-through safety — destroy rather than freeze underground
            if (transform.position.y < _releaseY - fallLimit)
            {
                TraceVanish($"fell through the world: {_releaseY - transform.position.y:0.0} m " +
                            $"below where it was let go");
                Destroy(gameObject);
                yield break;
            }

            bool atRest = _rb.linearVelocity.sqrMagnitude  < restThreshold
                       && _rb.angularVelocity.sqrMagnitude < restThreshold;

            if (atRest)
            {
                restTime += Time.deltaTime;
                if (restTime >= restRequired) break; // confirmed at rest
            }
            else
            {
                restTime = 0f;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (settleGeneration != _settleGeneration || _grabbed == this ||
            _remotelyGrabbed || _authoritativeMotionProxy) yield break;

        // One last fall-through check before freezing
        if (transform.position.y < _releaseY - fallLimit)
        {
            TraceVanish($"fell through the world after settling: " +
                        $"{_releaseY - transform.position.y:0.0} m below where it was let go");
            Destroy(gameObject);
            yield break;
        }

        // A thrown or dropped box stays where physics put it. Pallet membership is an explicit
        // player action through PalletStackZone.Interact; merely landing over a pallet must not
        // teleport the box into a computed slot or turn it into kinematic pallet cargo.

        // Multiplayer: broadcast final resting position before freezing so all clients
        // snap to the same location, eliminating per-client physics divergence after a throw.
        //
        // Suppressed for a fall this machine only heard about — see Unsupport and
        // ApplyRemoteRelease, and `announce` at the top of this coroutine. Otherwise every peer
        // that ran the same collapse would announce its own resting place and the box would be
        // dragged between them.
        if (announce && IsNetworked && NetworkPhysBoxManager.Instance != null)
        {
            if (TraceThrows)
                Debug.Log($"[BoxThrow] REST SENT '{name}'#{GetInstanceID()} id={NetBoxId:X} " +
                          $"at {transform.position:F2}");

            NetworkPhysBoxManager.Instance.ServerConfirmBoxRest(
                NetBoxId, transform.position, transform.rotation);
        }
        else if (TraceThrows && IsNetworked)
        {
            // The half of the story that was never told out loud: this copy has come to rest and is
            // saying nothing about it, so whatever it is standing on is where it stays until
            // somebody else's answer arrives — or, if nobody's does, for the rest of the shift.
            Debug.Log($"[BoxThrow] REST SILENT '{name}'#{GetInstanceID()} id={NetBoxId:X} " +
                      $"at {transform.position:F2} (this flight belongs to another machine)");
        }

        // Come to rest, so it is furniture again and blocks the camera like the rest of the room.
        CameraBlind(false);

        // In host/client play only the host keeps real world physics. Leave its touched boxes as
        // sleeping dynamic bodies so a forklift, pallet or another carton can wake and push them
        // naturally later. Clients keep the reliable resting pose kinematic and receive a new
        // Flying generation as soon as this body is disturbed again.
        if (IsNetworked && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.constraints = RigidbodyConstraints.None;
            _rb.isKinematic = false;
            _rb.useGravity = true;
            _rb.detectCollisions = true;
            _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _rb.Sleep();
            FlightBelongsToPhysics(false);
            yield break;
        }

        if (_persistentPhysics)
        {
            _rb.constraints            = RigidbodyConstraints.None;
            _rb.isKinematic            = false;
            _rb.useGravity             = true;
            _rb.detectCollisions       = true;
            _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            FlightBelongsToPhysics(false);
            yield break;
        }

        _rb.linearVelocity         = Vector3.zero;
        _rb.angularVelocity        = Vector3.zero;
        _rb.constraints            = RigidbodyConstraints.None;
        _rb.isKinematic            = true;
        _rb.useGravity             = false;
        _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

        foreach (var rb in _childRbs)
        {
            rb.linearVelocity  = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            // already kinematic — no other changes needed
        }

        // Come to rest: hand the transform back to the network, which is where a still box wants
        // to be — it is how everyone else sees the pushes this box takes from a thief's bat, a
        // collapsing stack or a forklift.
        FlightBelongsToPhysics(false);
    }

    // Called by PalletStackZone.TryAutoSnapFreeBox when box settles on a pallet.
    internal void ApplyAutoSnap(PalletStackZone zone, Transform parent, Vector3 localPos)
    {
        BeginFollow(parent, localPos, Quaternion.identity);

        _rb.linearVelocity         = Vector3.zero;
        _rb.angularVelocity        = Vector3.zero;
        _rb.constraints            = RigidbodyConstraints.None;
        _rb.isKinematic            = true;
        _rb.useGravity             = false;
        _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

        foreach (var col in _ownColliders) { col.enabled = true; col.isTrigger = false; }
        foreach (var rb in _childRbs)
        {
            rb.linearVelocity   = Vector3.zero;
            rb.angularVelocity  = Vector3.zero;
            rb.isKinematic      = true;
            rb.useGravity       = false;
            rb.detectCollisions = false;
        }

        StoredOnPallet = zone;
    }

    // ── Multiplayer: networked grab confirmation and remote sync ─────────────────

    /// Called by NetworkPhysBoxManager.ConfirmGrabClientRpc on the winning client.
    /// Safety: if the local player already grabbed something else before this arrived, no-op.
    public void ExecuteConfirmedGrab()
    {
        if (IsTraced)
            Debug.Log($"[BoxThrow] STATE ExecuteConfirmedGrab on '{name}'#{GetInstanceID()} " +
                      $"id={NetBoxId:X} at {transform.position:F2} — back into the hands");

        if (_grabbed == this) return;                     // state row arrived before the RPC
        if (_grabbed != null) return;                     // stale confirmation
        Grab();
    }

    /// Freeze or unfreeze a box held by another client (remote grab state), and apply/clear that
    /// carrier's hand IK on their CharacterVisual so observers see hands actually holding the box
    /// (the box itself doesn't get parented under the carrier like BoxWithPart does — it stays in
    /// world space, position-synced via UpdateBoxPositionServerRpc — so BoxHandAnchors' targets,
    /// being children of the box, already track the synced position and just need to be handed to
    /// the carrier's CharacterVisualIK).
    /// SetRemoteGrabbed(true): make kinematic, disable colliders, shrink to carry scale and clear
    ///   any pallet/cart follow so the box is visually frozen (and correctly sized) in the other
    ///   player's hands instead of snapping back to its old parent-relative pose.
    /// SetRemoteGrabbed(false): physics/position is handled by ApplyRemoteRelease or
    ///   ApplyRemoteStack ClientRpc — this only restores scale and clears the carrier's hand IK.
    bool  _remotelyGrabbed;
    ulong _remoteGrabberId;

    /// Every box this machine currently believes is in somebody else's hands.
    ///
    /// Kept so the belief can be checked against the server's, which is the only cure for the
    /// failure this design keeps producing: the box freezes kinematic with its colliders off when
    /// a carry starts, and nothing but a second message ever undoes that. Miss the message and the
    /// box hangs in the air in a hand that let go long ago, unpickable by anyone, for the rest of
    /// the session. See NetworkPhysBoxManager.Reconcile.
    static readonly System.Collections.Generic.List<BoxPhysicsInteractable> s_remoteHeld = new();

    public static System.Collections.Generic.IReadOnlyList<BoxPhysicsInteractable> RemotelyHeld =>
        s_remoteHeld;

    public bool  IsRemotelyGrabbed => _remotelyGrabbed;
    public bool  IsLocallyGrabbed  => _grabbed == this;
    public ulong RemoteGrabberId   => _remoteGrabberId;

    /// Which version of this box's network row this machine has already lived through.
    ///
    /// A row is read repeatedly — on every change and on the sweep — but a throw and a landing
    /// happen once. Without this, re-reading the same row would relaunch a box that had already
    /// come to rest. See NetworkPhysBoxManager.ApplyState.
    public uint NetVersion { get; private set; }

    public void NoteNetVersion(uint version) => NetVersion = version;

    public bool IsAuthoritativeMotionProxy => _authoritativeMotionProxy;

    /// Applies one host-physics sample on a non-server peer. The local Rigidbody remains
    /// kinematic: it is a visual/interactable proxy, never a second physics authority.
    public void ApplyAuthoritativeMotionSnapshot(Vector3 pos, Quaternion rot,
        Vector3 linearVelocity, Vector3 angularVelocity, bool snapImmediately)
    {
        if (_grabbed == this) return;

        if (_remotelyGrabbed) SetRemoteGrabbed(false, _remoteGrabberId);
        // While a pallet is lifted its local pose is a better visual source than delayed box
        // packets: the pallet already follows the smoothed/predicted fork on this peer. Keep the
        // authoritative sample for the moment the box leaves the pallet, but render supported
        // cargo from the pallet-local pose so lifting cannot make the stack trail or shudder.
        // A large disagreement means the server physics really knocked the box off; in that case
        // the snapshot immediately takes visual ownership again.
        bool followLiftedPallet = _followMotionActive && _followParent != null
                               && _rb != null && _rb.isKinematic;
        if (followLiftedPallet)
        {
            Vector3 expectedPosition = _followParent.TransformPoint(_followLocalPos);
            const float FollowBreakDistance = 0.9f;
            if ((pos - expectedPosition).sqrMagnitude
                > FollowBreakDistance * FollowBreakDistance)
            {
                EndFollow();
                followLiftedPallet = false;
            }
        }
        else
        {
            EndFollow();
        }
        DetachFromParent();
        FlightBelongsToPhysics(true);

        bool starting = !_authoritativeMotionProxy && !followLiftedPallet;
        if (followLiftedPallet) snapImmediately = false;
        _authoritativeMotionProxy = true;
        _authoritativeTargetPos = pos;
        _authoritativeTargetRot = rot;
        _authoritativeLinearVelocity = linearVelocity;
        _authoritativeAngularVelocity = angularVelocity;
        _authoritativeSnapshotTime = Time.unscaledTime;

        if (_rb != null)
        {
            if (!_rb.isKinematic)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.detectCollisions = true;
            _rb.constraints = RigidbodyConstraints.None;
        }

        foreach (var col in _ownColliders)
            if (col != null) col.enabled = true;
        foreach (var col in _childRbColliders)
            if (col != null) col.enabled = false;

        if (starting || snapImmediately || Vector3.SqrMagnitude(transform.position - pos) > 9f)
        {
            transform.SetPositionAndRotation(pos, rot);
            if (_rb != null) { _rb.position = pos; _rb.rotation = rot; }
        }

        CameraBlind(true);
        BoxPhysicsTickDriver.Register(this);
    }

    void StopAuthoritativeMotionProxy()
    {
        if (!_authoritativeMotionProxy) return;

        _authoritativeMotionProxy = false;
        _authoritativeLinearVelocity = Vector3.zero;
        _authoritativeAngularVelocity = Vector3.zero;
        CameraBlind(false);

        if (_grabbed != this && !_remoteSmoothing &&
            (_followParent == null || !_followMotionActive))
            BoxPhysicsTickDriver.Unregister(this);
    }

    /// Puts the box where the network says it came to rest, and stops it there.
    ///
    /// The end of a throw every peer simulated for itself: the arcs drift a few centimetres apart
    /// and this is what pulls them back together. Kinematic afterwards, exactly as a box that
    /// settled on its own — the same state SettleAndFreeze leaves behind.
    public void ApplyRemoteRest(Vector3 pos, Quaternion rot)
    {
        if (_grabbed == this) return;   // in these hands, and hands outrank a landing

        StopAuthoritativeMotionProxy();

        // How far this copy had drifted from the answer. Every peer re-simulates the flight rather
        // than streaming it, so a gap here is expected and small; a large one means the two
        // simulations parted company, and a message that never arrives means they stay parted.
        if (TraceThrows)
            Debug.Log($"[BoxThrow] REST TAKEN '{name}'#{GetInstanceID()} id={NetBoxId:X} moved " +
                      $"{Vector3.Distance(transform.position, pos):0.00} m: " +
                      $"was at {transform.position:F2}, answer says {pos:F2}");

        if (_remotelyGrabbed) SetRemoteGrabbed(false, _remoteGrabberId);

        EndFollow();

        transform.SetPositionAndRotation(pos, rot);

        if (_rb != null)
        {
            _rb.position               = pos;
            _rb.rotation               = rot;
            if (!_rb.isKinematic)
            {
                _rb.linearVelocity  = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
            _rb.isKinematic            = !_persistentPhysics;
            _rb.useGravity             = _persistentPhysics;
            _rb.detectCollisions       = true;
            _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _rb.constraints            = RigidbodyConstraints.None;
        }

        // Solid again: a box nobody can walk into is the failure this whole table exists to end.
        foreach (var col in _ownColliders) { if (col != null) { col.enabled = true; col.isTrigger = false; } }
        foreach (var col in _childRbColliders) if (col != null) col.enabled = false;

        FlightBelongsToPhysics(false);
    }

    /// Frees a box this machine has frozen in a carry the server no longer knows about: gives it
    /// its colliders and its weight back and lets it fall where it hangs. Announced only by the
    /// peer that is allowed to speak for it, so several machines noticing at once do not each
    /// broadcast a different landing.
    public void ReleaseStuckRemote(bool announceRest)
    {
        if (!_remotelyGrabbed) return;

        SetRemoteGrabbed(false, _remoteGrabberId);
        Unsupport(announceRest);   // makes it dynamic (colliders included) and settles it
    }

    public void SetRemoteGrabbed(bool grabbed, ulong grabberId = 0)
    {
        if (IsTraced)
            Debug.Log($"[BoxThrow] STATE SetRemoteGrabbed({grabbed}, client {grabberId}) on " +
                      $"'{name}'#{GetInstanceID()} id={NetBoxId:X} at {transform.position:F2}");

        if (grabbed && !_remotelyGrabbed) s_remoteHeld.Add(this);
        if (!grabbed) s_remoteHeld.Remove(this);

        _remotelyGrabbed = grabbed;
        _remoteGrabberId = grabbed ? grabberId : 0;

        if (grabbed)
        {
            LeavePersistentSupportPallet();
            StopAuthoritativeMotionProxy();
            EndFollow();

            // Observers leave the pallet too. Parenting is not replicated for these boxes, so
            // every copy has to be taken out of the stack where it stands, or a forklift driving
            // that pallet away would drag this box across everyone else's screen.
            DetachFromParent();

            if (_rb != null) { _rb.isKinematic = true; _rb.useGravity = false; }
            foreach (var col in _ownColliders) col.enabled = false;
            ApplyRemoteCarryIK(grabberId);
            // Seed the smoothing target at the current pose so the first ApplyRemotePosition
            // packet doesn't lerp in from wherever the box happened to be sitting before.
            _remoteTargetPos  = transform.position;
            _remoteTargetRot  = transform.rotation;
            _remoteSmoothing  = true;
            _remoteCarrier    = FindCarrierRoot(grabberId);
            _haveRemoteLocal  = false;
            _nextCarrierLook  = 0f;
            BoxPhysicsTickDriver.Register(this);

            // Somebody else's hands drive it, through the table. Replication must not also try.
            FlightBelongsToPhysics(true);
            return;
        }
        _remoteSmoothing = false;
        _remoteCarrier   = null;
        _haveRemoteLocal = false;
        BoxPhysicsTickDriver.Unregister(this);
        ClearRemoteCarryIK(grabberId);
    }

    void ApplyRemoteCarryIK(ulong grabberId)
    {
        bool packedOrder = GetComponent<PackedOrderBox>() != null;
        var visual = FindCarrierVisual(grabberId);
        if (visual == null) return;
        var ik = visual.GetComponent<CharacterVisualIK>() ?? visual.gameObject.AddComponent<CharacterVisualIK>();
        if (packedOrder)
        {
            float packedHeight = PackedOrderHeight();
            _bulkyPackedCarry = packedHeight > 1.35f;
            _packedCarryPitch = PackedCarryPitch(packedHeight);
            EnsurePackedGripTargets();
            ik.SetPackedCarryTargets(_packedGripRight, _packedGripLeft);
            ik.SetCarryLean(packedHeight > 1.35f ? CarryLeanForHeight(packedHeight) : 0f);
        }
        else
        {
            var anchors = GetComponent<BoxHandAnchors>();
            if (anchors == null) return;
            ik.SetHandTargets(anchors.RightHandTarget, anchors.LeftHandTarget);
            ik.SetCarryLean(anchors.CarryLean);
        }
    }

    void ClearRemoteCarryIK(ulong grabberId)
    {
        FindCarrierVisual(grabberId)?.GetComponent<CharacterVisualIK>()?.ClearHandTargets();
    }

    /// The carrier's replicated root — the transform his position actually arrives on.
    static Transform FindCarrierRoot(ulong clientId)
    {
        foreach (var np in NetworkPlayer.All)
            if (np != null && np.OwnerClientId == clientId) return np.transform;
        return null;
    }

    static Transform FindCarrierVisual(ulong clientId)
    {
        foreach (var np in NetworkPlayer.All)
            if (np.OwnerClientId == clientId) return np.transform.Find("CharacterVisual");
        return null;
    }

    /// Records the latest position/rotation broadcast by the client carrying this box remotely —
    /// LateUpdate lerps toward it every frame instead of snapping here directly (see _remoteSmoothing).
    public void ApplyRemotePosition(Vector3 pos, Quaternion rot)
    {
        _remoteTargetPos = pos;
        _remoteTargetRot = rot;

        if (_remoteCarrier == null) return;

        // Where the box sits on him, rather than where it sits in the warehouse. Eased into rather
        // than replaced, so a packet that arrives while his own position is a frame stale nudges
        // the box in his hands instead of jerking it.
        Vector3    local    = _remoteCarrier.InverseTransformPoint(pos);
        Quaternion localRot = Quaternion.Inverse(_remoteCarrier.rotation) * rot;

        if (!_haveRemoteLocal)
        {
            _remoteLocalPos  = local;
            _remoteLocalRot  = localRot;
            _haveRemoteLocal = true;
            return;
        }

        _remoteLocalPos = Vector3.Lerp(_remoteLocalPos, local, 0.5f);
        _remoteLocalRot = Quaternion.Slerp(_remoteLocalRot, localRot, 0.5f);
    }

    /// Apply the final release state sent by the grabber client; restore local physics.
    public void ApplyRemoteRelease(Vector3 pos, Quaternion rot, Vector3 vel)
    {
        // Read before the line below clears it: this copy has to know whose throw it is, so the
        // box can be let through that player's puppet the same way it is let through his own body
        // on his own machine.
        ulong thrower = _remoteGrabberId;

        if (IsNetworked && NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
        {
            if (_remotelyGrabbed) SetRemoteGrabbed(false, _remoteGrabberId);
            ApplyAuthoritativeMotionSnapshot(
                pos, rot, vel, Vector3.zero, snapImmediately: true);
            return;
        }

        // Every peer simulates the same throw from the state in this message, so no peer's copy
        // may be steered by replication while it flies — see FlightBelongsToPhysics.
        FlightBelongsToPhysics(true);

        // End the remote carry here rather than waiting to be told a second time.
        //
        // A release reaches an observer as two independent messages -- this ClientRpc, and the
        // grab list losing its entry (SetRemoteGrabbed(false)) -- and nothing orders them, the
        // same trap EndFollow documents from the other direction. When this one landed first the
        // box went fully dynamic with the throw velocity on it while _remoteSmoothing was still
        // set, and EndFollow deliberately leaves a smoothed box on the tick driver, so
        // TickLateUpdate went on lerping the thrown box back toward the last carried pose at 15
        // per second: three or four frames and the throw was eaten, the box dropping at the
        // thrower's feet on this screen.
        //
        // Then the second half of the damage: this copy settled there, and until the line below
        // it announced that as the resting place for everybody -- so the thrower, whose own box
        // had flown properly, watched it teleport back under his feet half a second later. That
        // is the whole of "you cannot throw a box in multiplayer", and both halves are here.
        //
        // Guarded on _remotelyGrabbed so the reverse order stays correct: if the grab list got
        // here first this is already done, and repeating it would clear hand IK on client 0 --
        // _remoteGrabberId having been zeroed -- which is the host, holding his own box.
        if (_remotelyGrabbed) SetRemoteGrabbed(false, _remoteGrabberId);

        EndFollow();

        transform.position = pos;
        transform.rotation = rot;

        bool[] wasTrigger = new bool[_ownColliders.Length];
        for (int i = 0; i < _ownColliders.Length; i++)
        {
            wasTrigger[i]              = _ownColliders[i].isTrigger;
            _ownColliders[i].enabled   = true;
            _ownColliders[i].isTrigger = true;
        }
        foreach (var col in _childRbColliders) col.enabled = false;

        _releaseY = pos.y;

        _rb.position               = pos;
        _rb.rotation               = rot;

        // Wake it before handing it the throw, not after. A kinematic body silently discards a
        // velocity assignment, and this copy is kinematic every time it matters -- that is what
        // SetRemoteGrabbed did to it when the throw started as somebody else picking the box up.
        // So the observer's copy was coming alive at a standstill and simply dropping: measured
        // against a 26 m/s throw, it covered 0.79 m in the first third of a second at a steady
        // three, and hit the floor 0.84 m out while the thrower's copy was 2.71 m away and still
        // going. Same box, two places, and no amount of settling could reconcile that.
        _rb.isKinematic            = false;
        _rb.linearVelocity         = vel;
        _rb.angularVelocity        = Vector3.zero;
        _rb.useGravity             = true;
        _rb.detectCollisions       = true;
        _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        _rb.linearDamping          = _linearDamping;
        _rb.angularDamping         = _angularDamping;
        _rb.constraints            = RigidbodyConstraints.None;

        // This machine is re-simulating a throw it was told about, not one it made. The peer who
        // threw it announces where it came to rest; this copy must not answer with its own, for
        // exactly the reason Unsupport spells out -- several peers each announcing their own
        // landing leaves the box hopping between them, and the one that arrives last wins.
        _skipRestBroadcast = true;

        foreach (var np in NetworkPlayer.All)
            if (np != null && np.OwnerClientId == thrower)
            {
                PassThrough(np.transform);
                _traceThrower = np.transform;
                break;
            }

        TraceThrow("remote", vel);

        StartCoroutine(RestoreColliders(wasTrigger));
        StartCoroutine(SettleAndFreeze());
    }

    /// Position this box on a pallet on a non-owning client without requiring local grab.
    /// Called from PalletStackZone.ApplyRemoteBoxStack.
    public void ApplyRemoteStack(PalletStackZone zone, Transform parent, Vector3 localPos, Quaternion localRot)
    {
        if (_grabbed == this) SilentRelease();
        if (_remotelyGrabbed) SetRemoteGrabbed(false, _remoteGrabberId);

        if (_physicsReleaseRoutine != null)
        {
            StopCoroutine(_physicsReleaseRoutine);
            _physicsReleaseRoutine = null;
        }

        // Parking a transported box invalidates any older settle coroutine. Without this guard a
        // coroutine started while the pallet was moving could wake the just-parked body again.
        _settleGeneration++;
        _persistentPhysics = false;
        _persistentSupportPallet = null;

        // A scene-authored box may still arrive here nested under the pallet. Following and
        // Transform parenting at the same time applies the carrier displacement twice.
        DetachFromParent();
        BeginFollow(parent, localPos, localRot);

        // Unity warns (and captures a full stack trace) whenever velocity is assigned to an
        // already-kinematic body. Reconciliation may legitimately arrive while the box is still
        // dynamic, but an unchanged pallet row is already kinematic and has no velocity to clear.
        if (!_rb.isKinematic)
        {
            _rb.linearVelocity  = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
        _rb.isKinematic     = true;
        _rb.useGravity      = false;

        foreach (var col in _ownColliders) { col.enabled = true; col.isTrigger = false; }
        foreach (var col in _childRbColliders) col.enabled = false;

        StoredOnPallet = zone;
    }

    // ── Per-frame ─────────────────────────────────────────────────────────────────
    // Not real Unity Update/LateUpdate messages — see BoxPhysicsTickDriver. This scene has
    // thousands of these components; only the handful that are grabbed/following/remote-smoothed
    // at any moment (registered via RegisterActive/UnregisterActive below) get ticked per frame.

    public void TickUpdate()
    {
        if (_grabbed != this) return;

        // A panel being open is not "let go of everything": pausing must not cost him the load,
        // and Release() here was not even a gentle one -- it is the throw, camera-aimed, three
        // units of force and a ReleaseBoxServerRpc for everyone else to watch. Carry input is
        // simply ignored while a panel is up, F below included: the keyboard is polled directly
        // here, past the input actions, so it would otherwise fire while a menu has focus.
        //
        // **Input only.** This used to return outright, taking the position broadcast below with
        // it, and the broadcast is the only thing telling anyone else where a carried box is. So
        // for as long as a panel was up -- and the Dispatcher's console is up most of his shift --
        // the carrier walked off while his box hung in the air on every other screen, exactly
        // where he opened the panel. It read as boxes not syncing at all.
        bool panelUp = UIManager.IsAnyPanelOpen;

        // F / pad B = universal drop while carrying (no need to aim at the box)
        var kb = UnityEngine.InputSystem.Keyboard.current;
        var gp = UnityEngine.InputSystem.Gamepad.current;
        if (!panelUp && GameInputBindings.WasPressed(GameBindingAction.Release))
            Release();

        // Multiplayer: report where the box is while this client carries it. Observers now ride the
        // carrier's own transform (see SetRemoteGrabbed), so these packets only keep the offset
        // between his hands and the box honest — a handful a second is plenty, and the bandwidth
        // they used to take is bandwidth the players' own positions were competing with.
        //
        // Was: once per network tick
        // rather than the old 20 Hz -- reporting slower than the tick throws away resolution the
        // connection is already paying for, and a carried box is the one thing a team-mate watches
        // closely enough to see it.
        if (_grabbed == this
            && IsNetworked
            && NetworkPhysBoxManager.Instance != null
            && Time.time - _lastNetUpdate > CarryReportInterval)
        {
            _lastNetUpdate = Time.time;
            NetworkPhysBoxManager.Instance.ReportCarryServerRpc(
                NetBoxId, transform.position, transform.rotation);
        }
    }

    /// Puts the box where the carrier's hands are, from the player's pose as it stands right now.
    ///
    /// Called from two places on purpose. LateUpdate is the normal one -- last write of the frame,
    /// after everything that could have moved the player. The IK pass is the other: it runs before
    /// every LateUpdate, so without a write there the hand goals would be solved against the box's
    /// previous-frame pose while the body had already moved, and the hands would trail the box by
    /// a frame's worth of travel (see PlayerHandIK.SetTargets). Writing twice is cheap and the
    /// second write is idempotent when nothing has moved in between.
    public void SyncCarryPose()
    {
        if (_grabbed != this || _playerTransform == null) return;

        transform.position = _playerTransform.TransformPoint(_carryOffset);
        transform.rotation = _playerTransform.rotation * CarryRotation();
        _rb.position = transform.position;
        _rb.rotation = transform.rotation;
    }

    public void TickLateUpdate()
    {
        if (_grabbed == this && _playerTransform != null)
        {
            SyncCarryPose();
            return;
        }

        // A lifted pallet is already interpolated from the truck/fork every rendered frame.
        // Following it locally is both smoother and newer than independently interpolating the
        // same cargo from server packets. The proxy stays armed underneath and resumes as soon as
        // SetFollowMotionActive(false) is called or the server reports that the box fell away.
        if (_followMotionActive && _followParent != null && _rb.isKinematic)
        {
            SyncFollowPose();
            return;
        }

        if (_authoritativeMotionProxy)
        {
            float age = Mathf.Min(
                Mathf.Max(0f, Time.unscaledTime - _authoritativeSnapshotTime),
                AuthoritativeMotionMaxPrediction);
            Vector3 predictedPosition = _authoritativeTargetPos
                                      + _authoritativeLinearVelocity * age
                                      + Physics.gravity * (0.5f * age * age);
            Quaternion predictedRotation = _authoritativeTargetRot;
            float angularSpeed = _authoritativeAngularVelocity.magnitude;
            if (angularSpeed > 0.0001f)
                predictedRotation = Quaternion.AngleAxis(
                    angularSpeed * age * Mathf.Rad2Deg,
                    _authoritativeAngularVelocity / angularSpeed) * _authoritativeTargetRot;

            float t = 1f - Mathf.Exp(-AuthoritativeMotionSmoothSpeed * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, predictedPosition, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, predictedRotation, t);
            if (_rb != null) { _rb.position = transform.position; _rb.rotation = transform.rotation; }
            return;
        }

        // Smoothly chase the latest position broadcast by whichever client is carrying this box
        // remotely, instead of snapping straight to each 20 Hz packet (see ApplyRemotePosition).
        if (_remoteSmoothing)
        {
            // The carrier's puppet may spawn after the box is already in his hands (a late joiner
            // sees both arrive in some order), so it is looked for again now and then.
            if (_remoteCarrier == null && Time.time >= _nextCarrierLook)
            {
                _nextCarrierLook = Time.time + 0.5f;
                _remoteCarrier   = FindCarrierRoot(_remoteGrabberId);
            }

            if (_remoteCarrier != null && _haveRemoteLocal)
            {
                // Glued to him: no chasing, no drift, and it costs nothing when his own updates
                // are late — the box is simply late with him, which is what a carried box is.
                transform.position = _remoteCarrier.TransformPoint(_remoteLocalPos);
                transform.rotation = _remoteCarrier.rotation * _remoteLocalRot;
            }
            else
            {
                float t = 1f - Mathf.Exp(-RemotePosSmoothSpeed * Time.deltaTime);
                transform.position = Vector3.Lerp(transform.position, _remoteTargetPos, t);
                transform.rotation = Quaternion.Slerp(transform.rotation, _remoteTargetRot, t);
            }

            _rb.position = transform.position;
            _rb.rotation = transform.rotation;
        }
    }

    void TrackServerWorldMotion()
    {
        if (!IsNetworked || NetworkManager.Singleton == null ||
            !NetworkManager.Singleton.IsServer || _rb == null || _rb.isKinematic ||
            _grabbed == this || _remotelyGrabbed)
            return;

        if (_rb.linearVelocity.sqrMagnitude < 0.01f &&
            _rb.angularVelocity.sqrMagnitude < 0.01f)
            return;

        var manager = NetworkPhysBoxManager.Instance;
        if (manager == null || !manager.ServerBeginWorldMotion(
                NetBoxId, transform.position, transform.rotation, _rb.linearVelocity))
            return;

        FlightBelongsToPhysics(true);
        CameraBlind(true);
        StartCoroutine(SettleAndFreeze());
    }

    void OnCollisionStay(Collision _) => TrackServerWorldMotion();

    void OnCollisionEnter(Collision col)
    {
        // Carried boxes can't hurt anyone — the player is holding them, not throwing them.
        if (_grabbed == this) return;

        TrackServerWorldMotion();
        TraceContact(col);

        // Collision audio is presentation-only. Every peer hears its local physics contact, while
        // gameplay consequences below remain server-authoritative. Cue intensity thresholds and
        // cooldown live in the AudioCue asset so this hook needs no hardcoded tuning values.
        AudioManager audio = AudioManager.Instance;
        if (audio != null && Time.unscaledTime >= _nextImpactAudioTime)
        {
            float impactSpeed = col.relativeVelocity.magnitude;
            Vector3 impactPoint = col.contactCount > 0 ? col.GetContact(0).point : transform.position;
            if (audio.PlayAt(GameAudioEvent.BoxImpact, impactPoint, impactSpeed))
                _nextImpactAudioTime = Time.unscaledTime
                                     + audio.GetCooldown(GameAudioEvent.BoxImpact);
        }

        float impulse   = col.impulse.magnitude;
        float threshold = _ragdollImpulseThreshold * GameEventManager.RagdollThresholdMult;
        if (impulse < threshold) return;

        // Impulse alone can't tell "a crate flew into him" from "he walked into a crate": the
        // CharacterController is an unstoppable kinematic shape, so shouldering past a box that is
        // standing still solves into a huge impulse and used to put him on the floor — the
        // "tripping over boxes" in the aisles. relativeVelocity is pre-solve and a controller has
        // no rigidbody velocity, so for that case it is ~0; only a box that was itself moving
        // (thrown, falling off a shelf) is dangerous.
        if (col.relativeVelocity.magnitude < MinKnockdownSpeed) return;

        if (col.gameObject.CompareTag("Player"))
        {
            var ragdoll = col.gameObject.GetComponent<RagdollController>();
            if (ragdoll == null || ragdoll.IsRagdoll) return;

            // Negate: col.impulse is what was applied to the box; player received the opposite.
            ragdoll.TriggerRagdoll(-col.impulse, col.contacts[0].point);
            return;
        }

        // A team-mate. Deliberately GetComponent and not GetComponentInParent: the capsule sits on
        // the NetworkPlayer root, and the only other colliders down that hierarchy are ragdoll
        // bones, which belong to a man who is already on the floor.
        var teammate = col.gameObject.GetComponent<RemotePlayerBody>();
        if (teammate != null) { HitRemotePlayer(teammate.Player, col, impulse); return; }

        HitThief(col, impulse, threshold);
    }

    /// A box thrown hard enough into another player puts him down.
    ///
    /// It used to do nothing whatsoever, and for two compounding reasons. The puppet had no
    /// collider, so the crate flew through him and this method was never reached — and even had it
    /// been, the branch above looks for a RagdollController, which exists only on the local scene
    /// rig. There is no such component on a team-mate's body on your machine, and there must not
    /// be: a player's own machine is the only one allowed to knock his body down.
    ///
    /// So the collision happens here and the consequence happens there. Exactly the shape
    /// ThiefController already uses for a raider's bat: the flinch goes to everyone, the fall and
    /// the injury go to the owner alone, and the server is the one peer that decides — every peer
    /// simulates its own copy of a thrown box, and without that guard a single crate would put him
    /// down once per player in the session.
    void HitRemotePlayer(NetworkPlayer victim, Collision col, float impulse)
    {
        if (victim == null) return;
        if (!IsNetworked || !NetworkManager.Singleton.IsServer) return;
        if (victim.IsRagdolledNet.Value) return;          // already down; let him get up

        Vector3 point = col.contacts[0].point;

        // Negate, as above: col.impulse is what the box received.
        Vector3 blow  = -col.impulse;

        Vector3 flat  = new Vector3(blow.x, 0f, blow.z);
        if (flat.sqrMagnitude < 0.01f)
            flat = victim.transform.position - transform.position;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.01f) flat = victim.transform.forward;

        victim.PlayHitReactionClientRpc(flat.normalized, 1f);

        victim.ServerTakeWorldBlow(
            blow, point, knockdown: true, injuryImpulse: impulse,
            bone: new FixedString32Bytes(BoneAt(point, victim.transform)));
    }

    /// Which part of him the crate caught, from how high up the contact was.
    ///
    /// InjurySystem classifies by bone name, so this is not decoration: without it every box would
    /// be a back injury regardless of whether it hit him in the head or took his legs out. The
    /// heights are read off the capsule RemotePlayerBody puts on him — 1.8 m, standing on the root.
    static string BoneAt(Vector3 point, Transform player)
    {
        float up = point.y - player.position.y;

        if (up > 1.45f) return "Head";
        if (up > 0.85f) return "Spine";
        return "LeftLeg";
    }

    /// A box thrown hard enough into a raider hurts him.
    ///
    /// Without this a box was scenery to a raider: the player could land one square on his head
    /// and watch him keep walking, which quietly taught that throwing is pointless. A warehouse
    /// full of heavy objects and a man to throw them at should not need a bat to be the only
    /// answer.
    ///
    /// GetComponentInParent, because the collision lands on a body collider somewhere down the
    /// skeleton, not on the root that owns the script.
    void HitThief(Collision col, float impulse, float threshold)
    {
        var thief = col.gameObject.GetComponentInParent<ThiefController>();
        if (thief == null || thief.Current == ThiefController.State.Down) return;

        // How hard it landed decides what it does, so a nudge is not the same as a crate thrown
        // across the room. Three times the threshold takes him off his feet outright — the same
        // payoff a fully charged swing gets, for a throw that deserves it.
        float ratio  = impulse / Mathf.Max(threshold, 0.01f);
        bool  heavy  = ratio >= 3f;
        int   damage = ratio >= 2f ? 2 : 1;

        Vector3 dir = col.relativeVelocity.sqrMagnitude > 0.01f
            ? -col.relativeVelocity.normalized
            : (thief.transform.position - transform.position);

        // Server-authoritative, like every other consequence a raider suffers: OnCollisionEnter
        // fires on every peer that simulates the box, and letting each apply its own damage would
        // kill him several times over.
        if (IsNetworked && !NetworkManager.Singleton.IsServer) return;

        thief.TakeHit(damage, dir, heavy);
    }

    /// Knocks the box off a shelf: makes it dynamic (if it wasn't already) and applies a
    /// scatter force. Safe to call on any non-grabbed box — most shelf boxes are actually
    /// dynamic-resting from scene load (never having gone through a drop/throw cycle that
    /// would freeze them kinematic), so this must work on both states.
    public void KnockOff(Vector3 impulse, uint minimumNetVersion = 0)
    {
        if (_grabbed == this) return;    // player is holding it — don't interfere

        // World physics is server-authoritative in multiplayer. Previously the thief ran this
        // only on the server without publishing the flight, while some collapse events ran it on
        // every peer independently; both paths produced a different warehouse on every screen.
        // A Held row also protects a box carried by a remote client, which the server-side static
        // _grabbed reference cannot see.
        NetworkPhysBoxManager net = null;
        if (IsNetworked && NetworkManager.Singleton != null)
        {
            if (!NetworkManager.Singleton.IsServer) return;
            net = NetworkPhysBoxManager.Instance;
            if (net != null && !net.ServerCanMoveBox(NetBoxId)) return;
        }

        // Already-dynamic boxes (the common case for untouched shelf stock) just need the
        // scatter impulse below — no collider/parenting state to unwind.
        if (_rb.isKinematic) MakeDynamic();

        _releaseY        = transform.position.y;
        _noAutoSnapUntil = Time.time + ScatterNoSnapTime;
        FlightBelongsToPhysics(true);
        _rb.AddForce(impulse, ForceMode.Impulse);
        net?.ServerPublishWorldMotion(
            NetBoxId, transform.position, transform.rotation, _rb.linearVelocity,
            minimumNetVersion);
        StartCoroutine(SettleAndFreeze());
    }

    Quaternion CarryRotation()
    {
        Vector3 euler = _carryRotation;
        // Medium cartons can rest diagonally against the torso. A refrigerator-sized package
        // must stay much closer to vertical or its upper half swings far over the robot's head.
        if (GetComponent<PackedOrderBox>() != null) euler.x -= _packedCarryPitch;
        return Quaternion.Euler(euler);
    }

    void EnsurePackedGripTargets()
    {
        if (!TryGetRootLocalVisualBounds(out Bounds local)) return;

        if (_packedGripRight == null) _packedGripRight = transform.Find("PackedBoxGrip_R");
        if (_packedGripLeft == null)  _packedGripLeft  = transform.Find("PackedBoxGrip_L");
        if (_packedGripRight == null)
        {
            _packedGripRight = new GameObject("PackedBoxGrip_R").transform;
            _packedGripRight.SetParent(transform, false);
        }
        if (_packedGripLeft == null)
        {
            _packedGripLeft = new GameObject("PackedBoxGrip_L").transform;
            _packedGripLeft.SetParent(transform, false);
        }

        // Spread the palms across the real carton width, while keeping very wide generated
        // packages inside the robot's comfortable reach. Coordinates remain box-local so these
        // targets follow both local LateUpdate carry and replicated remote carry automatically.
        float scaleX = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.x));
        float scaleZ = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.z));
        float worldWidth = local.size.x * scaleX;
        float halfSpanWorld = Mathf.Min(worldWidth * 0.34f, 0.52f);
        halfSpanWorld = Mathf.Min(halfSpanWorld, Mathf.Max(0f, worldWidth * 0.5f - 0.04f));
        float halfSpanLocal = halfSpanWorld / scaleX;
        // A large wrist offset pushes the target behind the shoulder once the carton is tilted
        // and makes the two-bone solver fold the elbows unnaturally. Keep the original vertical
        // contact and use only a small outward clearance for the palm mesh.
        float gripY = local.min.y + local.size.y * 0.015f;
        float gripZ = local.min.z - 0.025f / scaleZ;

        _packedGripRight.localPosition = new Vector3(local.center.x + halfSpanLocal, gripY, gripZ);
        _packedGripLeft.localPosition  = new Vector3(local.center.x - halfSpanLocal, gripY, gripZ);

        // Preserve the authored palm orientation in character space even though the targets are
        // children of a tilted box: boxRotation * localRotation = authored hand rotation.
        Quaternion inverseCarry = Quaternion.Inverse(CarryRotation());
        _packedGripRight.localRotation = inverseCarry * PlayerHandIK.PackedRightLocalRotation;
        _packedGripLeft.localRotation  = inverseCarry * PlayerHandIK.PackedLeftLocalRotation;
    }

    /// Rebinds the local player's IK to this generated carton's actual lower edge. Called both
    /// on pickup and by PlayerHandIK's recovery path, so script reloads cannot fall back to the
    /// fixed one-size pose and overwrite these targets.
    public void BindPackedCarryIK(PlayerHandIK ik)
    {
        if (ik == null || GetComponent<PackedOrderBox>() == null) return;
        EnsurePackedGripTargets();
        if (_packedGripRight == null || _packedGripLeft == null) return;
        ik.SetPackedCarryTargets(_packedGripRight, _packedGripLeft, refresh: SyncCarryPose);
    }

    static float PackedCarryPitch(float generatedHeightScale)
    {
        // Keep every generated order visibly resting against the robot. Taller refrigerator-sized
        // cartons use a gentler angle so their upper edge does not swing excessively behind him.
        float tall = Mathf.InverseLerp(1.35f, 1.85f, generatedHeightScale);
        return Mathf.Lerp(22f, 14f, tall);
    }

    Vector3 MeasureVisualSize()
    {
        var renderers = GetComponentsInChildren<Renderer>(false);
        if (renderers.Length == 0) return Vector3.zero;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            if (renderers[i] != null) bounds.Encapsulate(renderers[i].bounds);
        return bounds.size;
    }

    bool TryGetRootLocalVisualBounds(out Bounds local)
    {
        local = default;
        bool hasBounds = false;
        foreach (var renderer in GetComponentsInChildren<Renderer>(false))
        {
            if (renderer == null) continue;
            Bounds source = renderer.localBounds;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 corner = new(source.center.x + source.extents.x * x,
                                     source.center.y + source.extents.y * y,
                                     source.center.z + source.extents.z * z);
                Vector3 rootLocal = transform.InverseTransformPoint(
                    renderer.transform.TransformPoint(corner));
                if (!hasBounds)
                {
                    local = new Bounds(rootLocal, Vector3.zero);
                    hasBounds = true;
                }
                else local.Encapsulate(rootLocal);
            }
        }
        return hasBounds;
    }

    float PackedOrderHeight()
    {
        // Packed orders are generated by scaling the box prefab. The design threshold is the
        // generated order's Y scale, not renderer bounds (whose base mesh dimensions differ
        // between box variants and made equally scaled orders behave differently).
        return Mathf.Abs(transform.localScale.y);
    }

    static float CarryLeanForHeight(float height)
    {
        if (height <= 1.35f) return 0f;
        float heightLean = (height - 1.35f) * 14f;
        return Mathf.Clamp(45f + heightLean, 45f, 65f);
    }

    /// Turns a box that had settled and frozen back into one physics is allowed to move.
    ///
    /// Shared by everything that has to un-park a box: a shelf collapsing, and a stack losing the
    /// box that was holding it up.
    void MakeDynamic()
    {
        EndFollow();

        foreach (var col in _ownColliders) col.enabled = true;
        foreach (var col in _childRbColliders) col.enabled = false;

        // A kinematic Rigidbody refuses velocity writes. Wake it into the dynamic simulation
        // first, then initialise motion -- the previous order is the warning recorded in both the
        // host and MPPM client logs and is why unsupported stack boxes could remain suspended.
        _rb.isKinematic     = false;
        _rb.linearVelocity  = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.useGravity      = true;
        _rb.detectCollisions = true;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        _rb.linearDamping   = _linearDamping;
        _rb.angularDamping  = _angularDamping;
        _rb.constraints     = RigidbodyConstraints.None;

        foreach (var rb in _childRbs)
        {
            rb.linearVelocity  = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.detectCollisions = false;
            rb.isKinematic     = true;
            rb.useGravity      = false;
        }
    }

    /// Permanently hands a pallet cargo box to physics. It leaves the pallet hierarchy and stays
    /// dynamic after coming to rest; Unity may still put the Rigidbody to sleep naturally.
    public void EnablePersistentPhysics(Vector3 inheritedVelocity,
        Vector3 inheritedAngularVelocity, bool preservePalletMembership = false)
    {
        bool alreadyPersistent = _persistentPhysics;
        if (!alreadyPersistent) _settleGeneration++;
        _persistentPhysics = true;
        if (!preservePalletMembership)
        {
            if (StoredOnPallet != null)
                _persistentSupportPallet = StoredOnPallet;
            StoredOnPallet = null;
        }

        // These operations are intentionally repeated for duplicate/out-of-order network
        // messages. A persistent body must never remain parented to, or follow, the pallet.
        EndFollow();
        DetachFromParent();
        FlightBelongsToPhysics(true);
        if (_rb.isKinematic) MakeDynamic();

        if (alreadyPersistent)
        {
            _rb.linearVelocity = inheritedVelocity;
            _rb.angularVelocity = inheritedAngularVelocity;
            _rb.WakeUp();
            return;
        }

        // Authored cargo stacks are packed very tightly. Limit contact depenetration so tiny
        // collider overlaps are resolved as a slow settling motion instead of an explosive kick.
        _rb.maxDepenetrationVelocity = 3f;
        _rb.maxAngularVelocity = 3f;
        _rb.sleepThreshold = 0.02f;
        _rb.solverIterations = 20;
        _rb.solverVelocityIterations = 10;
        // Strong world-space damping makes cargo lag behind a moving platform. Keep air drag low;
        // contact friction and the limited carrier-relative grip below provide the heavy feel.
        _rb.linearDamping = 0.15f;
        _rb.angularDamping = 2.5f;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        // Discrete contacts are more stable for slow, tightly stacked cargo. Speculative CCD
        // creates constantly changing ghost contacts here, which makes the stack look gelatinous.
        _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        foreach (var col in _ownColliders)
            if (col != null && col.enabled && !col.isTrigger)
                col.material = CargoPhysicsMaterial;

        _rb.linearVelocity = inheritedVelocity;
        _rb.angularVelocity = inheritedAngularVelocity;
        _releaseY = transform.position.y;
        _rb.WakeUp();
        StartCoroutine(SettleAndFreeze());
    }

    /// Transfers the carried pallet's point velocity to this dynamic box. This is the
    /// moving-platform part of transport: the pallet supplies the motion that its kinematic
    /// transform cannot contribute through PhysX by itself, while contacts still decide whether
    /// the carton slides, tips or is knocked off. Vertical grip is enabled only while the pallet
    /// itself is lifting/lowering, so an unsupported box still falls normally when the lift stops.
    public void StabilizeOnMovingPallet(
        Vector3 carrierPointVelocity, Vector3 carrierAngularVelocity, Vector3 palletUp,
        float stabilizationMultiplier)
    {
        if (!_persistentPhysics || _rb == null || _rb.isKinematic) return;

        Vector3 up = palletUp.sqrMagnitude > 0.0001f ? palletUp.normalized : Vector3.up;
        Vector3 relativeVelocity = _rb.linearVelocity - carrierPointVelocity;
        Vector3 velocityCorrection = -Vector3.ProjectOnPlane(relativeVelocity, up);

        // Without this component the kinematic pallet rises immediately while every dynamic box
        // waits for contacts to push it upward. That one-step separation compounds through a tall
        // stack and is the visible lag/jitter during fork movement. Match vertical carrier speed
        // only while the deck is actually moving vertically; at rest gravity remains untouched.
        float carrierVerticalSpeed = Vector3.Dot(carrierPointVelocity, up);
        if (Mathf.Abs(carrierVerticalSpeed) > 0.015f)
            velocityCorrection -= Vector3.Project(relativeVelocity, up);

        // VelocityChange is mass independent and runs before collision resolution. Obstacles can
        // therefore still redirect or knock a carton outside the pallet footprint, after which
        // PalletBehavior stops applying this grip.
        _rb.AddForce(velocityCorrection, ForceMode.VelocityChange);

        Vector3 angularSlip = _rb.angularVelocity - carrierAngularVelocity;
        Vector3 angularGrip = Vector3.ClampMagnitude(
            -angularSlip,
            1.5f * Mathf.Max(1f, stabilizationMultiplier));
        _rb.AddTorque(angularGrip, ForceMode.VelocityChange);
    }

    /// Strong operator transport: receive the same LateUpdate displacement as the pallet.
    /// Only planar motion is stabilised; gravity and vertical falling remain owned by physics.
    public void CarryWithPalletLateUpdate(
        Vector3 position, Quaternion rotation, Vector3 palletUp)
    {
        if (!_persistentPhysics || _rb == null || _rb.isKinematic) return;

        transform.SetPositionAndRotation(position, rotation);
        _rb.position = position;
        _rb.rotation = rotation;

        // LateUpdate carries the horizontal component, so leaving the old planar velocity would
        // apply the same motion again on the next physics tick. Preserve only falling/lifting.
        _rb.linearVelocity = Vector3.Project(_rb.linearVelocity, palletUp);
        _rb.angularVelocity = Vector3.zero;
    }

    /// What this box was standing on has just been carried away, so let it fall.
    ///
    /// The point is that it has to happen on **every** machine, and until now it happened on
    /// exactly one. On a pallet, a box is parked: settled boxes are frozen kinematic, and an
    /// observer's copy is kinematic and pinned to the pallet by ApplyRemoteStack. Kinematic bodies
    /// do not fall. So a player taking a box out of a stack saw the ones above it drop, and
    /// everybody else saw them keep hanging in the air with nothing underneath.
    ///
    /// Each peer then simulates the fall itself rather than being fed positions — the same bargain
    /// the codebase already makes for a thrown box, and for the same reason: a short fall is cheap
    /// to simulate and expensive to stream. The copies drift a few centimetres on the way down and
    /// are pulled back together at the end, when the peer that started it broadcasts where its own
    /// copy came to rest (see SettleAndFreeze). Only that peer broadcasts: several of them all
    /// announcing their own answer would leave the box hopping between them.
    public void Unsupport(bool broadcastRest)
    {
        if (_grabbed == this)  return;    // in a hand, and hands are not a stack
        if (_remotelyGrabbed)  return;    // in somebody else's hand, same thing
        if (IsNetworked && NetworkManager.Singleton != null &&
            !NetworkManager.Singleton.IsServer)
            return;                       // wait for the host's Flying row/snapshots
        if (_rb == null)
        {
            // Named rather than swallowed: this is the box that aborted somebody's pickup, and the
            // only way to fix the scene is to know which one it is.
            Debug.LogWarning($"[BoxPhysicsInteractable] '{name}' has no Rigidbody, cannot be " +
                             $"unsupported. Path: {SceneNetIds.HierarchyPath(transform)}");
            return;
        }

        _skipRestBroadcast = !broadcastRest;

        if (_rb.isKinematic) MakeDynamic();
        if (IsNetworked) FlightBelongsToPhysics(true);

        _releaseY = transform.position.y;
        _rb.WakeUp();

        // A sleeping server body waking up is a new authoritative flight. Publishing that
        // transition makes remote kinematic proxies follow the fall instead of waiting suspended
        // until the final resting pose arrives.
        if (IsNetworked && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            NetworkPhysBoxManager.Instance?.ServerBeginWorldMotion(
                NetBoxId, transform.position, transform.rotation, _rb.linearVelocity);

        StartCoroutine(SettleAndFreeze());
    }

    /// Vehicle-delivery cartons use loose server physics, but still need to remember which
    /// pallet stack supplied their support graph. This reference is deliberately separate from
    /// StoredOnPallet: the latter routes pickup through the pallet cargo manifest, which delivery
    /// boxes are not part of.
    void LeavePersistentSupportPallet()
    {
        if (_persistentSupportPallet == null) return;

        PalletStackZone supportPallet = _persistentSupportPallet;
        _persistentSupportPallet = null;
        supportPallet.OnBoxPickedUp(this);
    }

    /// Set while a fall was started by news from another machine rather than by this one, so the
    /// resting position is not announced back to the peer that already knows it.
    bool _skipRestBroadcast;

    void OnDisable() { if (_grabbed == this) Release(); }

    // ── Out of the camera's way while it is moving ────────────────────────────
    //
    // Cinemachine's third-person follow avoids obstacles by layer — its filter on this rig is
    // Default plus navMeshObjects, which is the building — and one single tag, which is already
    // spent on the player. A box sat on Default like everything else, so a box tumbling into the
    // player was, as far as the camera was concerned, a wall arriving: it pulled in to the near
    // side of it, and with DampingIntoCollision and DampingFromCollision both at zero it did that
    // in one frame. Being hit by a box is meant to be startling; the camera being slammed into the
    // back of the player's head is not the way to say so.
    //
    // **Only while the box is actually moving.** Carried, thrown, knocked off a shelf: for that
    // window it is on a layer the camera does not know about. The moment it settles it goes back
    // on its own layer and blocks the camera again — which is what stops four and a half thousand
    // boxes of warehouse stock from quietly becoming windows. Furniture blocks the camera; a thing
    // flying at your face does not.
    //
    // The layer swap is the same move LadderController already makes for a carried ladder, and it
    // costs nothing else: the interaction ray takes everything but Outline so a box in flight can
    // still be caught, the collision matrix is all-on so physics is untouched, and the ground check
    // is Default-only — which is right, since a box in mid-air is not a floor.

    const string LooseBoxLayer = "Box";

    int[] _restLayers;
    bool  _cameraBlind;

    /// Takes the box off the camera's radar, or puts it back.
    void CameraBlind(bool blind)
    {
        if (_cameraBlind == blind || _restLayers == null) return;

        int loose = LayerMask.NameToLayer(LooseBoxLayer);
        if (loose < 0) return;      // a project without the layer: boxes simply keep blocking

        for (int i = 0; i < _ownColliders.Length; i++)
        {
            var col = _ownColliders[i];
            if (col == null) continue;

            int now = col.gameObject.layer;

            // Only pieces that are where we left them. Something else may have moved one since —
            // Outline, for the highlight — and whatever put it there owns it.
            if (blind && now != _restLayers[i]) continue;
            if (!blind && now != loose) continue;

            col.gameObject.layer = blind ? loose : _restLayers[i];
        }

        _cameraBlind = blind;
    }
}
