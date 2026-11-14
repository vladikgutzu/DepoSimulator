using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Server-authoritative vehicle occupancy sync for CartController and ReachTruckController.
/// Implements first-come-first-served entry, server-validated transform sync, and optional
/// fork height broadcast for the reach truck. The driver predicts locally, but only the server
/// writes the persistent state observed by the rest of the session.
///
/// Implements: multiplayer vehicle networking design.
/// Single-player: all network code is gated on NetworkManager.Singleton?.IsListening.
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder(-300)]
public class NetworkVehicleSync : NetworkBehaviour
{
    // k_Empty = unoccupied sentinel. Cannot use 0 — that is the host's clientId.
    const ulong k_Empty = ulong.MaxValue;

    NetworkVariable<ulong> _occupantId = new(
        k_Empty,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Normalized fork height [0,1] — only meaningful on ReachTruckController.
    NetworkVariable<float> _forkT = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Vehicle world transform — the server is the only writer. A driver submits predicted
    // snapshots through SubmitDriverStateServerRpc; the server validates and republishes them.
    NetworkVariable<Vector3>    _vehiclePos = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    NetworkVariable<Quaternion> _vehicleRot = new(
        Quaternion.identity,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    const float EnterDistance = 4f;
    const float SnapshotSlackDistance = 0.65f;
    const float SnapshotSlackAngle = 12f;
    const float MinimumSnapshotInterval = 1f / 45f;
    const float DriverPositionSubmitEpsilonSqr = 0.02f * 0.02f;
    const float DriverRotationSubmitEpsilon = 0.25f;
    const float DriverForkSubmitEpsilon = 0.0025f;
    const float StationaryHeartbeatInterval = 0.5f;
    const float ParkedSnapshotInterval = 0.05f;

    float _pendingForkT;
    float _lastAcceptedServerTime;
    uint _nextClientSequence;
    uint _lastAcceptedSequence;
    bool _hasSubmittedDriverState;
    Vector3 _lastSubmittedDriverPosition;
    Quaternion _lastSubmittedDriverRotation;
    float _lastSubmittedDriverForkT;
    float _nextDriverHeartbeatAt;
    float _nextParkedSnapshotAt;

    /// Fired on the local client only when the server grants entry to THIS client.
    /// CartController and ReachTruckController subscribe here to call Grab()/Enter().
    public event System.Action<GameObject> OnLocalEnterConfirmed;

    /// True when any client occupies this vehicle.
    public bool IsOccupied  => _occupantId.Value != k_Empty;

    /// ClientId of the current occupant (k_Empty if unoccupied).
    public ulong OccupantId => _occupantId.Value;

    public bool IsDrivenBy(ulong clientId) => _occupantId.Value == clientId;

    /// Remote normalized fork height [0,1] — read by ReachTruckController on non-owner clients.
    public float ForkT => _forkT.Value;

    // ── Lifecycle ─────────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        var rb = GetComponent<Rigidbody>();
        _baseKinematic = rb != null && rb.isKinematic;
        _pendingForkT = _forkT.Value;
        ConfigureRigidbodyAuthority(_occupantId.Value);

        _occupantId.OnValueChanged += OnOccupantChanged;

        // Seed position NetworkVariables with the actual world position so clients
        // don't interpolate to Vector3.zero before the first SetTransform call.
        if (IsServer)
        {
            _vehiclePos.Value = transform.position;
            _vehicleRot.Value = transform.rotation;
            _pendingForkT = _forkT.Value;
            _lastAcceptedServerTime = Time.unscaledTime;
        }

        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    public override void OnNetworkDespawn()
    {
        _occupantId.OnValueChanged -= OnOccupantChanged;
        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
    }

    /// Server-only: a driver who disconnects mid-drive would otherwise leave _occupantId stuck
    /// forever (RequestEnterServerRpc always rejects afterward) and leave observers with that
    /// player's hand/foot IK — and, for the reach truck, their swapped sitting animator controller —
    /// permanently locked in, since nothing else ever calls OnOccupantChanged(x, k_Empty) again.
    void OnClientDisconnected(ulong clientId)
    {
        if (_occupantId.Value != clientId) return;
        FinishExit(clientId);
    }

    // Cached at spawn — true if the vehicle's Rigidbody starts kinematic (e.g. pallet truck
    // with concave mesh collider). We never flip kinematic→dynamic on such vehicles.
    bool _baseKinematic;

    void ConfigureRigidbodyAuthority(ulong occupantId)
    {
        var rb = GetComponent<Rigidbody>();
        if (rb == null) return;

        // Only the driver simulates an occupied vehicle. When parked, only the server
        // simulates it; client physics must not move separate copies between rides.
        bool localDriver = occupantId != k_Empty &&
                           NetworkManager.Singleton?.LocalClientId == occupantId;
        rb.isKinematic = _baseKinematic || (occupantId == k_Empty ? !IsServer : !localDriver);
    }

    void OnOccupantChanged(ulong oldId, ulong newId)
    {
        ConfigureRigidbodyAuthority(newId);

        // IK: clear the previous occupant's IK, apply for the new one.
        if (oldId != k_Empty) ApplyOccupantIK(oldId, false);
        if (newId != k_Empty) ApplyOccupantIK(newId, true);
        if (newId != k_Empty && NetworkManager.Singleton?.LocalClientId == newId)
        {
            _pendingForkT = _forkT.Value;
            _hasSubmittedDriverState = false;
            _nextDriverHeartbeatAt = 0f;
        }
    }

    // Cached so the sitting controller swap below can be undone on exit — this vehicle only ever
    // has one occupant at a time, so a single field is enough.
    RuntimeAnimatorController _occupantOrigVisualController;
    AnimatorOverrideController _occupantSittingOverrideController;

    void ApplyOccupantIK(ulong clientId, bool enable)
    {
        var np = FindNetworkPlayer(clientId);
        if (np == null) return;
        var visual = np.transform.Find("CharacterVisual");
        if (visual == null) return;

        var ik = visual.GetComponent<CharacterVisualIK>()
              ?? visual.gameObject.AddComponent<CharacterVisualIK>();
        var truck = GetComponent<ReachTruckController>();
        var visualAnim = visual.GetComponent<Animator>();

        if (!enable)
        {
            ik.ClearAllTargets();
            // Undo the sitting-controller swap below, on every client — including the driver's
            // own, where ReachTruckController.Exit() already restores it locally too; reassigning
            // the same reference here is harmless.
            if (truck != null && visualAnim != null && _occupantOrigVisualController != null)
                visualAnim.runtimeAnimatorController = _occupantOrigVisualController;
            _occupantOrigVisualController = null;
            if (_occupantSittingOverrideController != null)
            {
                Destroy(_occupantSittingOverrideController);
                _occupantSittingOverrideController = null;
            }

            if (truck != null && visualAnim != null)
            {
                var visualCarryLayer = visualAnim.GetComponent<PlayerCarryLayer>();
                if (visualCarryLayer != null) visualCarryLayer.enabled = true;
            }
            return;
        }

        var cart = GetComponent<CartController>();
        if (cart != null)
        {
            ik.SetHandTargets(cart.RightHandTarget, cart.LeftHandTarget);
            return;
        }

        if (truck != null)
        {
            ik.SetHandTargets(truck.SteerHandRight, truck.SteerHandLeft);
            if (truck.FootTargetRight != null && truck.FootTargetLeft != null)
                ik.SetFootTargets(truck.FootTargetRight, truck.FootTargetLeft);

            // ReachTruckController.Enter() swaps the DRIVER's own local CharacterVisual to the
            // sitting controller, but that's a per-client Animator reference — it never reaches
            // other clients' copies of this player. Swap it here too, on EVERY client (this
            // callback runs universally off the occupant NetworkVariable), so observers actually
            // see the seated pose instead of the default standing/locomotion one.
            if (visualAnim != null && truck.SittingController != null)
            {
                _occupantOrigVisualController = visualAnim.runtimeAnimatorController;
                _occupantSittingOverrideController =
                    ReachTruckController.BuildCompatibleSittingController(
                        _occupantOrigVisualController, truck.SittingController);
                if (_occupantSittingOverrideController != null)
                    visualAnim.runtimeAnimatorController = _occupantSittingOverrideController;

                // The sitting controller has no "UpperBody" layer -- disable the box-carry layer
                // driver on this same CharacterVisual so it doesn't hold a stale layer index into
                // the swapped controller. Mirrors ReachTruckController.Enter()'s own-client disable,
                // but this callback also covers every observer's copy of the driver.
                var visualCarryLayer = visualAnim.GetComponent<PlayerCarryLayer>();
                if (visualCarryLayer != null) visualCarryLayer.enabled = false;
            }
        }
    }

    static NetworkPlayer FindNetworkPlayer(ulong clientId)
    {
        foreach (var p in NetworkPlayer.All)
            if (p.OwnerClientId == clientId) return p;
        return null;
    }

    void FixedUpdate()
    {
        // An empty vehicle can still be nudged by physics. Publish only the server's
        // parked pose so the next player sees and boards that same vehicle.
        if (!IsSpawned || !IsServer || IsOccupied || Time.unscaledTime < _nextParkedSnapshotAt)
            return;

        _nextParkedSnapshotAt = Time.unscaledTime + ParkedSnapshotInterval;
        var rb = GetComponent<Rigidbody>();
        Vector3 pos = rb != null ? rb.position : transform.position;
        Quaternion rot = rb != null ? rb.rotation : transform.rotation;
        if ((pos - _vehiclePos.Value).sqrMagnitude > DriverPositionSubmitEpsilonSqr)
            _vehiclePos.Value = pos;
        if (Quaternion.Angle(rot, _vehicleRot.Value) > DriverRotationSubmitEpsilon)
            _vehicleRot.Value = rot;
    }

    // ── LateUpdate: apply synced transform on non-owner clients ──────────────────

    void LateUpdate()
    {
        // The local driver predicts immediately. The server follows a remote driver;
        // parked clients follow the server's physics simulation as well.
        if (!IsSpawned ||
            (IsOccupied && NetworkManager.Singleton?.LocalClientId == _occupantId.Value) ||
            (!IsOccupied && IsServer)) return;
        transform.position = Vector3.Lerp(
            transform.position, _vehiclePos.Value, Time.deltaTime * 15f);
        transform.rotation = Quaternion.Slerp(
            transform.rotation, _vehicleRot.Value, Time.deltaTime * 15f);
    }

    // ── Server RPCs ───────────────────────────────────────────────────────────────

    /// Client requests to enter/drive this vehicle. First caller wins; late callers are silently rejected.
    [ServerRpc(RequireOwnership = false)]
    public void RequestEnterServerRpc(ServerRpcParams rpcParams = default)
    {
        if (_occupantId.Value != k_Empty) return; // already occupied

        ulong sender = rpcParams.Receive.SenderClientId;
        var player = FindNetworkPlayer(sender);
        if (player == null || Vector3.SqrMagnitude(
                player.transform.position - transform.position) > EnterDistance * EnterDistance)
            return;

        if (GetComponent<ReachTruckController>() != null)
        {
            if (!SpecializationPenalties.CanOperateForklift(player.RoleType))
                return;
        }

        _vehiclePos.Value = transform.position;
        _vehicleRot.Value = transform.rotation;
        _lastAcceptedServerTime = Time.unscaledTime;
        _lastAcceptedSequence = 0;
        _occupantId.Value = sender;

        // Ownership grants permission to submit driver snapshots; the NetworkVariables remain
        // server-write-only.
        GetComponent<NetworkObject>().ChangeOwnership(sender);

        // Notify the winner only — targeted ClientRpc.
        ConfirmEnterClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new List<ulong> { sender } }
        });
    }

    /// Current driver signals they are leaving. Only the owner (the driver) may call this.
    [ServerRpc(RequireOwnership = true)]
    public void RequestExitServerRpc(ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (_occupantId.Value != sender) return;
        FinishExit(sender);
    }

    /// The reach truck includes its final local pose so the server parks it exactly where
    /// the driver stopped, even if the last periodic snapshot has not arrived yet.
    [ServerRpc(RequireOwnership = true)]
    public void RequestExitWithPoseServerRpc(Vector3 pos, Quaternion rot, float forkT,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (_occupantId.Value != sender) return;

        float elapsed = Mathf.Max(Time.unscaledTime - _lastAcceptedServerTime,
            MinimumSnapshotInterval);
        var controller = GetComponent<ReachTruckController>();
        float maxSpeed = controller != null ? controller.NetworkMaxLinearSpeed : 8f;
        float maxTurn = controller != null ? controller.NetworkMaxAngularSpeed : 180f;
        if (IsFinite(pos) && IsFinite(rot) && IsFinite(forkT) &&
            Vector3.Distance(_vehiclePos.Value, pos) <= maxSpeed * elapsed + SnapshotSlackDistance &&
            Quaternion.Angle(_vehicleRot.Value, rot) <= maxTurn * elapsed + SnapshotSlackAngle)
        {
            _vehiclePos.Value = pos;
            _vehicleRot.Value = Normalize(rot);
            _forkT.Value = Mathf.Clamp01(forkT);
        }

        FinishExit(sender);
    }

    void FinishExit(ulong sender)
    {
        // The server may have been interpolating the remote driver's position. Move its
        // physics body to the last accepted pose before it becomes dynamic again.
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.position = _vehiclePos.Value;
            rb.rotation = _vehicleRot.Value;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        else
            transform.SetPositionAndRotation(_vehiclePos.Value, _vehicleRot.Value);

        NetworkPalletManager.Instance?.ServerReleasePalletsForVehicle(
            NetworkObjectId, sender);
        _occupantId.Value = k_Empty;
        GetComponent<NetworkObject>().RemoveOwnership(); // returns ownership to server
    }

    // ── Client RPCs ───────────────────────────────────────────────────────────────

    // Only the targeted winner client receives this.
    [ClientRpc]
    void ConfirmEnterClientRpc(ClientRpcParams _ = default)
    {
        var actor = NetworkPlayer.Local?.gameObject;
        OnLocalEnterConfirmed?.Invoke(actor);
    }

    // ── Fork height sync (ReachTruckController owner only) ────────────────────────

    /// Cache the predicted normalized fork position. It is submitted together with the next
    /// transform snapshot so position, rotation and fork height share one ordering sequence.
    public void SetForkT(float t)
    {
        if (!IsOwner) return;
        _pendingForkT = Mathf.Clamp01(t);
    }

    /// Submit the driver's predicted transform. Only the server can publish it.
    public void SetTransform(Vector3 pos, Quaternion rot)
    {
        if (!IsOwner || NetworkManager.Singleton == null ||
            _occupantId.Value != NetworkManager.Singleton.LocalClientId) return;

        // ReachTruckController samples at 30 Hz for responsive motion, but a parked occupied
        // vehicle used to send 30 byte-identical ServerRpcs every second. Submit immediately when
        // any authoritative value changes; otherwise keep only a low-rate heartbeat.
        bool changed = !_hasSubmittedDriverState
                    || (pos - _lastSubmittedDriverPosition).sqrMagnitude
                        > DriverPositionSubmitEpsilonSqr
                    || Quaternion.Angle(rot, _lastSubmittedDriverRotation)
                        > DriverRotationSubmitEpsilon
                    || Mathf.Abs(_pendingForkT - _lastSubmittedDriverForkT)
                        > DriverForkSubmitEpsilon;

        float now = Time.unscaledTime;
        if (!changed && now < _nextDriverHeartbeatAt) return;

        _hasSubmittedDriverState = true;
        _lastSubmittedDriverPosition = pos;
        _lastSubmittedDriverRotation = rot;
        _lastSubmittedDriverForkT = _pendingForkT;
        _nextDriverHeartbeatAt = now + StationaryHeartbeatInterval;

        SubmitDriverStateServerRpc(pos, rot, _pendingForkT, ++_nextClientSequence);
    }

    [ServerRpc(RequireOwnership = true)]
    void SubmitDriverStateServerRpc(Vector3 pos, Quaternion rot, float forkT, uint sequence,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (_occupantId.Value != sender || sequence <= _lastAcceptedSequence ||
            !IsFinite(pos) || !IsFinite(rot))
        {
            SendCorrection(sender);
            return;
        }

        float now = Time.unscaledTime;
        float elapsed = Mathf.Max(now - _lastAcceptedServerTime, MinimumSnapshotInterval);
        if (now - _lastAcceptedServerTime < MinimumSnapshotInterval)
            return;

        var controller = GetComponent<ReachTruckController>();
        float maxSpeed = controller != null ? controller.NetworkMaxLinearSpeed : 8f;
        float maxTurn = controller != null ? controller.NetworkMaxAngularSpeed : 180f;
        float maxDistance = maxSpeed * elapsed + SnapshotSlackDistance;
        float maxAngle = maxTurn * elapsed + SnapshotSlackAngle;

        if (Vector3.Distance(_vehiclePos.Value, pos) > maxDistance ||
            Quaternion.Angle(_vehicleRot.Value, rot) > maxAngle)
        {
            SendCorrection(sender);
            return;
        }

        _lastAcceptedSequence = sequence;
        _lastAcceptedServerTime = now;
        _vehiclePos.Value = pos;
        _vehicleRot.Value = Normalize(rot);
        _forkT.Value = Mathf.Clamp01(forkT);
    }

    void SendCorrection(ulong clientId)
    {
        CorrectDriverClientRpc(_vehiclePos.Value, _vehicleRot.Value, _forkT.Value,
            new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            });
    }

    [ClientRpc]
    void CorrectDriverClientRpc(Vector3 pos, Quaternion rot, float forkT,
        ClientRpcParams _ = default)
    {
        transform.SetPositionAndRotation(pos, rot);
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.position = pos;
            rb.rotation = rot;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        GetComponent<ReachTruckController>()?.ApplyAuthoritativeForkT(forkT);
    }

    static bool IsFinite(Vector3 value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    static bool IsFinite(Quaternion value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w) &&
        value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > 0.0001f;

    static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    static Quaternion Normalize(Quaternion value)
    {
        float magnitude = Mathf.Sqrt(value.x * value.x + value.y * value.y +
                                     value.z * value.z + value.w * value.w);
        if (magnitude < 0.0001f) return Quaternion.identity;
        float inverse = 1f / magnitude;
        return new Quaternion(value.x * inverse, value.y * inverse,
                              value.z * inverse, value.w * inverse);
    }
}
