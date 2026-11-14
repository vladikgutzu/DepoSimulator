using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Server-authoritative manager for ladder pickup/place across clients.
/// Uses stable integer IDs (LadderController.NetLadderId, derived from scene order — see SceneNetIds).
/// Pattern mirrors NetworkPhysBoxManager (first-come-first-served grab arbitration) combined
/// with NetworkBoxManager's remote-carry visual (parent under carrier's NetworkPlayer + IK via
/// CharacterVisualIK for observers). The ladder itself is a plain scene MonoBehaviour (no
/// NetworkObject), so — unlike BoxPhysicsInteractable — it CAN be safely parented under the
/// carrier's NetworkPlayer transform without Netcode's "no NetworkObject under non-NetworkObject
/// parent" restriction.
///
/// Single-player: all paths are gated on NetworkManager.Singleton?.IsListening at the call site
/// (LadderController), so this manager is simply never invoked outside multiplayer.
public class NetworkLadderManager : NetworkBehaviour
{
    public static NetworkLadderManager Instance { get; private set; }

    NetworkList<LadderCarryState> _carryList;
    NetworkList<LadderClimbState> _climbList;
    NetworkList<LadderPoseState> _poseList;

    readonly Dictionary<int, double> _lastPoseReportAt = new();

    const float InteractionRange = 4.5f;
    const float MaxInitialFallDelta = 8f;
    const float MaxFallStepDelta = 3f;
    const double MinPoseReportInterval = 1d / 30d;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;

        _carryList = new NetworkList<LadderCarryState>(
            new List<LadderCarryState>(),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        _climbList = new NetworkList<LadderClimbState>(
            new List<LadderClimbState>(),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        _poseList = new NetworkList<LadderPoseState>(
            new List<LadderPoseState>(),
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

        _carryList.OnListChanged += OnCarryListChanged;
        _climbList.OnListChanged += OnClimbListChanged;
        _poseList.OnListChanged += OnPoseListChanged;

        // Late-join clients: apply any carry/climb already in progress.
        if (!IsServer)
        {
            foreach (var state in _poseList)
                ApplyPoseLocally(state);
            foreach (var state in _carryList)
                ApplyCarryLocally(state);
            foreach (var state in _climbList)
                ApplyClimbLocally(state, entering: true);
        }

        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    public override void OnNetworkDespawn()
    {
        _carryList.OnListChanged -= OnCarryListChanged;
        _climbList.OnListChanged -= OnClimbListChanged;
        _poseList.OnListChanged -= OnPoseListChanged;
        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        if (Instance == this) Instance = null;
    }

    // ── Disconnect cleanup ─────────────────────────────────────────────────────────

    /// Server-only: if a disconnecting client was carrying or climbing a ladder, release that
    /// state instead of leaving it stuck. Without this, a client that goes away mid-carry (crash,
    /// closed window, dropped connection) leaves the ladder permanently "taken" — every future
    /// RequestPickupServerRpc silently rejects since IsCarried(ladderId) never clears — and leaves
    /// whoever was watching with that player's hand IK locked onto the ladder's anchors forever,
    /// since (unlike a carried box) the ladder itself is never destroyed to trigger CharacterVisualIK's
    /// own destroyed-reference guard.
    void OnClientDisconnected(ulong clientId)
    {
        for (int i = _carryList.Count - 1; i >= 0; i--)
        {
            if (_carryList[i].CarrierId != clientId) continue;
            int ladderId = _carryList[i].LadderId;
            _carryList.RemoveAt(i);

            var ladder = LadderController.FindByNetId(ladderId);
            Vector3 pos = ladder != null ? ladder.transform.position : Vector3.zero;
            Quaternion rot = ladder != null ? ladder.transform.rotation : Quaternion.identity;
            SetPoseServer(ladderId, pos, rot, false, clientId);
        }

        for (int i = _climbList.Count - 1; i >= 0; i--)
        {
            if (_climbList[i].ClimberId != clientId) continue;
            _climbList.RemoveAt(i);
        }

        for (int i = 0; i < _poseList.Count; i++)
        {
            var pose = _poseList[i];
            if (!pose.IsFalling || pose.AuthorityClientId != clientId) continue;
            pose.IsFalling = false;
            pose.Version++;
            _poseList[i] = pose;
        }
    }

    // ── List changed ─────────────────────────────────────────────────────────────

    void OnCarryListChanged(NetworkListEvent<LadderCarryState> evt)
    {
        if (evt.Type == NetworkListEvent<LadderCarryState>.EventType.Add)
            ApplyCarryLocally(evt.Value);
    }

    void ApplyCarryLocally(LadderCarryState state)
    {
        // Same fix as NetworkPhysBoxManager.ApplyGrabLocally — the host has its own local
        // player and needs to see a ladder carried by OTHER clients too. ApplyRemoteCarryVisual
        // already skips the carrier's own client via its own LocalClientId check.
        ApplyRemoteCarryVisual(state.LadderId, state.CarrierId);
    }

    // ── Pickup ServerRpc ─────────────────────────────────────────────────────────

    /// Client requests exclusive pickup of a ladder. First caller wins.
    [ServerRpc(RequireOwnership = false)]
    public void RequestPickupServerRpc(int ladderId, ServerRpcParams rpcParams = default)
    {
        ulong winner = rpcParams.Receive.SenderClientId;
        var ladder = LadderController.FindByNetId(ladderId);
        if (ladder == null || IsCarried(ladderId) || IsFalling(ladderId) ||
            IsClientCarrying(winner) || IsClientClimbing(winner) ||
            !TryGetPlayer(winner, out var player) ||
            !WithinRange(player.transform.position, ladder.transform.position, InteractionRange))
        {
            RejectPickupClientRpc(ladderId, Target(winner));
            return;
        }

        _carryList.Add(new LadderCarryState { LadderId = ladderId, CarrierId = winner });

        // Everyone (including the winner) gets the remote-visual update; the winner's own
        // client additionally runs its full local Pickup() via the targeted confirm below.
        CarryLadderClientRpc(ladderId, winner);

        ConfirmPickupClientRpc(ladderId, new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new List<ulong> { winner } }
        });
    }

    // Only the winner client receives this — runs the real Pickup() locally (IK, notifications,
    // speed penalty via LadderController.IsCarried).
    [ClientRpc]
    void ConfirmPickupClientRpc(int ladderId, ClientRpcParams _ = default)
    {
        var ladder = LadderController.FindByNetId(ladderId);
        ladder?.ExecuteConfirmedPickup();
    }

    [ClientRpc]
    void RejectPickupClientRpc(int ladderId, ClientRpcParams _ = default)
    {
        LadderController.FindByNetId(ladderId)?.RejectPendingPickup();
    }

    // Broadcast to ALL clients so non-carriers see the ladder attached to the carrier's body.
    [ClientRpc]
    void CarryLadderClientRpc(int ladderId, ulong carrierId)
    {
        ApplyRemoteCarryVisual(ladderId, carrierId);
    }

    void ApplyRemoteCarryVisual(int ladderId, ulong carrierId)
    {
        if (NetworkManager.Singleton == null) return;

        // The carrier's own client already ran (or is about to run) the real Pickup() path.
        if (NetworkManager.Singleton.LocalClientId == carrierId) return;

        var ladder = LadderController.FindByNetId(ladderId);
        if (ladder == null) return;

        Transform carrierRoot = FindPlayerRoot(carrierId);
        if (carrierRoot == null) return;

        ladder.ApplyRemoteCarry(carrierRoot);

        var visual = carrierRoot.Find("CharacterVisual");
        if (visual == null) return;

        var ik = visual.GetComponent<CharacterVisualIK>()
              ?? visual.gameObject.AddComponent<CharacterVisualIK>();
        ik.SetHandTargets(ladder.RightHandTarget, ladder.LeftHandTarget);
    }

    // ── Place ServerRpc ──────────────────────────────────────────────────────────

    /// Carrier reports where they placed the ladder (already computed + applied locally).
    /// Server clears the carry state and relays to everyone else.
    [ServerRpc(RequireOwnership = false)]
    public void RequestPlaceServerRpc(int ladderId, Vector3 basePos, Quaternion baseRot,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!Finite(basePos) || !Finite(baseRot) ||
            (!IsCarrier(ladderId, sender) && !IsFallingAuthority(ladderId, sender)) ||
            !PoseStepAllowed(ladderId, basePos, MaxInitialFallDelta))
            return;

        RemoveCarryState(ladderId, sender);
        SetPoseServer(ladderId, basePos, Normalize(baseRot), false, sender);
        _lastPoseReportAt.Remove(ladderId);
    }

    // ── Fall position sync ────────────────────────────────────────────────────────

    /// The placer broadcasts the ladder's live transform at ~20 Hz while PhysicsLeanRoutine's
    /// multi-second fall plays out (see LadderController), so anyone still climbing it — riding
    /// PositionClimber()'s ladder-relative offset — tracks the actual tumble on every client in
    /// real time instead of only snapping once at the very end. Deliberately does NOT touch
    /// climb/carry state; RequestPlaceServerRpc still sends the authoritative final settled pose.
    [ServerRpc(RequireOwnership = false)]
    public void UpdateLadderFallServerRpc(int ladderId, Vector3 pos, Quaternion rot,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!Finite(pos) || !Finite(rot) ||
            (!IsCarrier(ladderId, sender) && !IsFallingAuthority(ladderId, sender)))
            return;

        double now = NetworkManager.ServerTime.Time;
        if (_lastPoseReportAt.TryGetValue(ladderId, out double last) &&
            now - last < MinPoseReportInterval)
            return;

        float maxStep = IsFalling(ladderId) ? MaxFallStepDelta : MaxInitialFallDelta;
        if (!PoseStepAllowed(ladderId, pos, maxStep)) return;

        _lastPoseReportAt[ladderId] = now;
        RemoveCarryState(ladderId, sender);
        SetPoseServer(ladderId, pos, Normalize(rot), true, sender);
    }

    // ── Climb ServerRpc ──────────────────────────────────────────────────────────
    // Exclusive occupancy, same first-come-first-served arbitration as pickup — but unlike carry,
    // the climber's own position already replicates via the normal player-position sync, so no
    // per-frame RPC is needed here, only enter/exit.

    [ServerRpc(RequireOwnership = false)]
    public void RequestEnterClimbServerRpc(int ladderId, ServerRpcParams rpcParams = default)
    {
        ulong winner = rpcParams.Receive.SenderClientId;
        var ladder = LadderController.FindByNetId(ladderId);
        if (ladder == null || IsClimbed(ladderId) || IsCarried(ladderId) || IsFalling(ladderId) ||
            IsClientCarrying(winner) || IsClientClimbing(winner) ||
            !TryGetPlayer(winner, out var player) ||
            !WithinRange(player.transform.position, ladder.transform.position, InteractionRange))
        {
            RejectClimbClientRpc(ladderId, Target(winner));
            return;
        }

        _climbList.Add(new LadderClimbState { LadderId = ladderId, ClimberId = winner });

        ConfirmClimbClientRpc(ladderId, new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new List<ulong> { winner } }
        });
    }

    // Only the winner client receives this — runs the real StartClimb() locally (movement lock,
    // notification, LadderController.IsClimbing).
    [ClientRpc]
    void ConfirmClimbClientRpc(int ladderId, ClientRpcParams _ = default)
    {
        var ladder = LadderController.FindByNetId(ladderId);
        ladder?.ExecuteConfirmedClimb();
    }

    [ClientRpc]
    void RejectClimbClientRpc(int ladderId, ClientRpcParams _ = default)
    {
        LadderController.FindByNetId(ladderId)?.RejectPendingClimb();
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestExitClimbServerRpc(int ladderId, ServerRpcParams rpcParams = default)
    {
        RemoveClimbState(ladderId, rpcParams.Receive.SenderClientId);
    }

    void OnClimbListChanged(NetworkListEvent<LadderClimbState> evt)
    {
        if (evt.Type == NetworkListEvent<LadderClimbState>.EventType.Add)
            ApplyClimbLocally(evt.Value, entering: true);
        else if (evt.Type == NetworkListEvent<LadderClimbState>.EventType.Remove
              || evt.Type == NetworkListEvent<LadderClimbState>.EventType.RemoveAt)
            ApplyClimbLocally(evt.Value, entering: false);
    }

    void ApplyClimbLocally(LadderClimbState state, bool entering)
    {
        // Same fix as NetworkPhysBoxManager.ApplyGrabLocally — the host has its own local
        // player and needs to see OTHER clients climbing a ladder too; the ClimberId check
        // right below already filters out the host's own climb.
        if (NetworkManager.Singleton.LocalClientId == state.ClimberId) return; // climber already applied locally

        var ladder = LadderController.FindByNetId(state.LadderId);
        if (ladder == null) return;

        Transform climberRoot = FindPlayerRoot(state.ClimberId);
        if (entering) ladder.ApplyRemoteClimbEnter(climberRoot);
        else          ladder.ApplyRemoteClimbExit(climberRoot);
    }

    bool IsClimbed(int ladderId)
    {
        foreach (var state in _climbList)
            if (state.LadderId == ladderId) return true;
        return false;
    }

    void RemoveClimbState(int ladderId, ulong expectedClimber)
    {
        for (int i = 0; i < _climbList.Count; i++)
        {
            if (_climbList[i].LadderId == ladderId &&
                _climbList[i].ClimberId == expectedClimber)
            {
                _climbList.RemoveAt(i);
                return;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    static Transform FindPlayerRoot(ulong clientId)
    {
        foreach (var np in NetworkPlayer.All)
            if (np.OwnerClientId == clientId) return np.transform;
        return null;
    }

    static bool TryGetPlayer(ulong clientId, out NetworkPlayer player)
    {
        foreach (var candidate in NetworkPlayer.All)
        {
            if (candidate == null || candidate.OwnerClientId != clientId) continue;
            player = candidate;
            return true;
        }

        player = null;
        return false;
    }

    static bool WithinRange(Vector3 a, Vector3 b, float range) =>
        (a - b).sqrMagnitude <= range * range;

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) &&
        Mathf.Abs(value.x) < 10000f && Mathf.Abs(value.y) < 10000f && Mathf.Abs(value.z) < 10000f;
    static bool Finite(Quaternion value) =>
        Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w) &&
        value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > 0.0001f;

    static Quaternion Normalize(Quaternion value)
    {
        float mag = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
        return new Quaternion(value.x / mag, value.y / mag, value.z / mag, value.w / mag);
    }

    static ClientRpcParams Target(ulong clientId) => new()
    {
        Send = new ClientRpcSendParams { TargetClientIds = new List<ulong> { clientId } }
    };

    void RemoveCarryState(int ladderId, ulong expectedCarrier)
    {
        for (int i = 0; i < _carryList.Count; i++)
        {
            if (_carryList[i].LadderId == ladderId &&
                _carryList[i].CarrierId == expectedCarrier)
            {
                _carryList.RemoveAt(i);
                return;
            }
        }
    }

    bool IsCarried(int ladderId)
    {
        foreach (var state in _carryList)
            if (state.LadderId == ladderId) return true;
        return false;
    }

    bool IsCarrier(int ladderId, ulong clientId)
    {
        foreach (var state in _carryList)
            if (state.LadderId == ladderId && state.CarrierId == clientId) return true;
        return false;
    }

    bool IsClientCarrying(ulong clientId)
    {
        foreach (var state in _carryList)
            if (state.CarrierId == clientId) return true;
        return false;
    }

    bool IsClientClimbing(ulong clientId)
    {
        foreach (var state in _climbList)
            if (state.ClimberId == clientId) return true;
        return false;
    }

    int PoseIndex(int ladderId)
    {
        for (int i = 0; i < _poseList.Count; i++)
            if (_poseList[i].LadderId == ladderId) return i;
        return -1;
    }

    bool IsFalling(int ladderId)
    {
        int index = PoseIndex(ladderId);
        return index >= 0 && _poseList[index].IsFalling;
    }

    bool IsFallingAuthority(int ladderId, ulong clientId)
    {
        int index = PoseIndex(ladderId);
        return index >= 0 && _poseList[index].IsFalling &&
               _poseList[index].AuthorityClientId == clientId;
    }

    bool PoseStepAllowed(int ladderId, Vector3 position, float maxDelta)
    {
        int index = PoseIndex(ladderId);
        Vector3 from;
        if (index >= 0 && _poseList[index].IsFalling)
            from = _poseList[index].Position;
        else
        {
            var ladder = LadderController.FindByNetId(ladderId);
            if (ladder == null) return false;
            from = ladder.transform.position;
        }
        return WithinRange(from, position, maxDelta);
    }

    void SetPoseServer(int ladderId, Vector3 position, Quaternion rotation,
        bool falling, ulong authorityClientId)
    {
        int index = PoseIndex(ladderId);
        uint version = index >= 0 ? _poseList[index].Version + 1u : 1u;
        var state = new LadderPoseState
        {
            LadderId = ladderId,
            Position = position,
            Rotation = rotation,
            IsFalling = falling,
            AuthorityClientId = authorityClientId,
            Version = version
        };

        if (index >= 0) _poseList[index] = state;
        else _poseList.Add(state);
    }

    void OnPoseListChanged(NetworkListEvent<LadderPoseState> evt)
    {
        if (evt.Type == NetworkListEvent<LadderPoseState>.EventType.Add ||
            evt.Type == NetworkListEvent<LadderPoseState>.EventType.Value)
            ApplyPoseLocally(evt.Value);
    }

    void ApplyPoseLocally(LadderPoseState state)
    {
        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.LocalClientId == state.AuthorityClientId)
            return;

        var ladder = LadderController.FindByNetId(state.LadderId);
        if (ladder == null) return;

        Transform carrierRoot = FindPlayerRoot(state.AuthorityClientId);
        var visual = carrierRoot != null ? carrierRoot.Find("CharacterVisual") : null;
        visual?.GetComponent<CharacterVisualIK>()?.ClearHandTargets();

        if (state.IsFalling)
            ladder.ApplyRemoteFallPosition(state.Position, state.Rotation);
        else
            ladder.ApplyRemotePlace(state.Position, state.Rotation);
    }
}

/// Identifies a ladder currently held by a specific client.
public struct LadderCarryState : INetworkSerializable, System.IEquatable<LadderCarryState>
{
    public int   LadderId;
    public ulong CarrierId;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref LadderId);
        serializer.SerializeValue(ref CarrierId);
    }

    public bool Equals(LadderCarryState other) =>
        LadderId == other.LadderId && CarrierId == other.CarrierId;

    public override bool Equals(object obj) =>
        obj is LadderCarryState other && Equals(other);

    public override int GetHashCode() =>
        LadderId.GetHashCode() * 397 ^ CarrierId.GetHashCode();
}

/// Identifies a ladder currently being climbed by a specific client.
public struct LadderClimbState : INetworkSerializable, System.IEquatable<LadderClimbState>
{
    public int   LadderId;
    public ulong ClimberId;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref LadderId);
        serializer.SerializeValue(ref ClimberId);
    }

    public bool Equals(LadderClimbState other) =>
        LadderId == other.LadderId && ClimberId == other.ClimberId;

    public override bool Equals(object obj) =>
        obj is LadderClimbState other && Equals(other);

    public override int GetHashCode() =>
        LadderId.GetHashCode() * 397 ^ ClimberId.GetHashCode();
}

/// Persistent accepted world pose. Unlike a transient ClientRpc this is included in the
/// NetworkList snapshot, so a player joining during or after placement sees the same ladder.
public struct LadderPoseState : INetworkSerializable, System.IEquatable<LadderPoseState>
{
    public int LadderId;
    public Vector3 Position;
    public Quaternion Rotation;
    public bool IsFalling;
    public ulong AuthorityClientId;
    public uint Version;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref LadderId);
        serializer.SerializeValue(ref Position);
        serializer.SerializeValue(ref Rotation);
        serializer.SerializeValue(ref IsFalling);
        serializer.SerializeValue(ref AuthorityClientId);
        serializer.SerializeValue(ref Version);
    }

    public bool Equals(LadderPoseState other) =>
        LadderId == other.LadderId && Position == other.Position && Rotation == other.Rotation &&
        IsFalling == other.IsFalling && AuthorityClientId == other.AuthorityClientId &&
        Version == other.Version;

    public override bool Equals(object obj) => obj is LadderPoseState other && Equals(other);

    public override int GetHashCode() => LadderId.GetHashCode() * 397 ^ Version.GetHashCode();
}
