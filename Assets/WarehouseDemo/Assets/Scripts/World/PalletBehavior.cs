using UnityEngine;
using System.Collections.Generic;
using Unity.Netcode;

// Component on pallet_boxes_prefab / pallet_box_pack_prefab root.
// Enables fork pickup: ReachTruckController checks proximity via PalletBehavior.All.
//
// Multiplayer: stable NetPalletId assigned at Awake (scene-load order).
// Lift/release are broadcast via NetworkPalletManager.
// Single-player: all network code is gated on NetworkManager.Singleton?.IsListening.
[DefaultExecutionOrder(-100)]
public class PalletBehavior : MonoBehaviour
{
    // Auto-found in Awake: first child named "pallet_prefab" or "pallet"
    [SerializeField] Collider _grabCollider;

    public bool     IsLifted     { get; private set; }
    public Collider GrabCollider => _grabCollider;
    // World Y of the pallet floor — used by ScanForPallet instead of collider
    // bounds.min/max which are unreliable when the FBX pivot is offset.
    public float    GrabY        => transform.position.y;

    // Static registry — no FindObjectsOfType in hot path
    public static readonly HashSet<PalletBehavior> All = new();

    Rigidbody   _rb;
    Rigidbody[] _childRbs;
    BoxPhysicsInteractable[] _cargoBoxes;
    Collider _cargoEnvelopeCollider;
    bool _cargoPhysicsActivated;
    Coroutine _cargoActivationRoutine;
    Vector3    _attachOffset;
    Quaternion _attachRotOffset;
    Vector3    _physicsTargetPosition;
    Quaternion _physicsTargetRotation;
    bool       _hasPhysicsTarget;
    Vector3    _carrierVelocity;
    Vector3    _carrierAngularVelocity;
    float      _cargoStabilizationMultiplier = 1f;

    // ── Multiplayer: stable per-pallet integer ID ─────────────────────────────────

    public int NetPalletId { get; private set; }
    bool _runtimeNetworkId;

    static readonly Dictionary<int, PalletBehavior> s_byNetId = new();
    public static int SceneIdCount { get; private set; }
    public static ulong SceneIdHash { get; private set; }

    // Numbered from scene order, not Awake order — see SceneNetIds. Only pallet_box_pack_prefab
    // carries a NetworkObject; pallet_boxes_prefab (the majority of the scene's pallets) does
    // not, so the whole set is numbered this way to keep one id space rather than two.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void InstallNetworkIdPass() => SceneNetIds.RunEveryLoad(AssignNetworkIds);

    // Idempotent by construction -- s_byNetId is cleared first -- because SceneNetIds.RunEveryLoad
    // calls this again after every scene load, including the one NGO performs on a joining client.
    static void AssignNetworkIds()
    {
        s_byNetId.Clear();
        var all = new List<PalletBehavior>(
            FindObjectsByType<PalletBehavior>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        // Runtime deliveries already own deterministic IDs from their delivery records. Including
        // them in a later scene pass would renumber the host and a joining client differently.
        all.RemoveAll(pallet => pallet != null && pallet._runtimeNetworkId);
        SceneNetIds.SortDeterministically(all);
        for (int i = 0; i < all.Count; i++)
        {
            all[i].NetPalletId = i;
            s_byNetId[i]       = all[i];
        }
        SceneIdCount = all.Count;
        ulong hash = 14695981039346656037UL;
        foreach (var pallet in all)
        {
            string path = SceneNetIds.HierarchyPath(pallet.transform);
            for (int c = 0; c < path.Length; c++) { hash ^= path[c]; hash *= 1099511628211UL; }
        }
        SceneIdHash = hash;
        NetworkPalletManager.ReportSceneIds();
    }

    /// Find a pallet by its stable network ID. Returns null if not found.
    public static PalletBehavior FindByNetId(int id)
    {
        s_byNetId.TryGetValue(id, out var pb);
        return pb;
    }

    // Pallets created at runtime (VehicleDeliveryManager's delivery pallets) miss the
    // scene-load numbering pass, so without this they would all sit at the default 0 and
    // shadow the first scene pallet. Their caller-supplied key is already identical on every
    // machine — SpawnDeliveryLocal runs from the same ClientRpc with the same deliveryId — so
    // offsetting it into a reserved range keeps the ids both shared and collision-free.
    public const int RuntimeIdBase = 1_000_000;

    public void AssignRuntimeNetId(int key)
    {
        int runtimeId = RuntimeIdBase + key;
        if (s_byNetId.TryGetValue(runtimeId, out var collision) && collision != null &&
            collision != this)
        {
            Debug.LogError($"[PalletBehavior] Runtime pallet ID collision {runtimeId}: " +
                           $"'{collision.name}' and '{name}'. The new pallet was not registered.");
            return;
        }

        if (s_byNetId.TryGetValue(NetPalletId, out var old) && old == this)
            s_byNetId.Remove(NetPalletId);

        _runtimeNetworkId = true;
        NetPalletId = runtimeId;
        s_byNetId[runtimeId] = this;
    }

    // True when the LOCAL client's truck is currently carrying this pallet.
    public bool IsLiftedByLocalTruck { get; private set; }
    bool _liftRequestPending;
    uint _liftVersion;
    Coroutine _liftRequestTimeoutRoutine;
    Vector3 _liftRequestStartPosition;
    Quaternion _liftRequestStartRotation;

    // Observers follow the already-smoothed remote fork every LateUpdate. The replicated world
    // pose remains a fallback for spawn/ordering gaps and the authoritative release pose.
    const float RemotePosSmoothSpeed = 15f;
    Vector3    _remoteTargetPos;
    Quaternion _remoteTargetRot;
    bool       _remoteSmoothing;
    Transform  _remoteForkMount;
    Vector3    _remoteForkLocalPosition;
    Quaternion _remoteForkLocalRotation;

    static readonly string[] s_grabNames = { "pallet_prefab", "pallet" };

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _cargoBoxes = GetComponentsInChildren<BoxPhysicsInteractable>(true);
        _cargoEnvelopeCollider = GetComponent<Collider>();
        if (_grabCollider == null) AutoFindGrabCollider();

        var all = GetComponentsInChildren<Rigidbody>(true);
        var list = new System.Collections.Generic.List<Rigidbody>(all.Length);
        foreach (var rb in all)
            if (rb != _rb) list.Add(rb);
        _childRbs = list.ToArray();

        // A parked pallet is an immovable support. It becomes dynamic only after Release (or an
        // explicit impact event) so it can fall/tip naturally, then returns to kinematic once a
        // real support is detected and motion has settled.
        if (_rb != null)
        {
            _rb.isKinematic = true;
            _rb.useGravity  = false;
            _rb.constraints = RigidbodyConstraints.None;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        // The root collider is only a parked-stack envelope, not the wooden pallet geometry.
        // Leaving it active creates a large invisible wall that pushes loose boxes before the
        // visible pallet reaches them. The actual pallet/grab collider remains physical.
        if (_cargoEnvelopeCollider != null && _cargoEnvelopeCollider != _grabCollider)
            _cargoEnvelopeCollider.enabled = false;
    }

    void OnEnable()  => All.Add(this);
    void OnDisable() => All.Remove(this);

    void OnDestroy()
    {
        if (s_byNetId.TryGetValue(NetPalletId, out var registered) && registered == this)
            s_byNetId.Remove(NetPalletId);
    }

    void LateUpdate()
    {
        if (!_remoteSmoothing) return;

        if (_remoteForkMount != null)
        {
            MovePalletInLateUpdate(
                _remoteForkMount.TransformPoint(_remoteForkLocalPosition),
                _remoteForkMount.rotation * _remoteForkLocalRotation);
            return;
        }

        float t = 1f - Mathf.Exp(-RemotePosSmoothSpeed * Time.deltaTime);
        MovePalletInLateUpdate(
            Vector3.Lerp(transform.position, _remoteTargetPos, t),
            Quaternion.Slerp(transform.rotation, _remoteTargetRot, t));
    }

    void FixedUpdate()
    {
        if (!IsLifted || !_hasPhysicsTarget || _rb == null || !_rb.isKinematic) return;

        Bounds cargoBounds = _grabCollider != null
            ? _grabCollider.bounds
            : new Bounds(_rb.position, Vector3.one * 4f);
        foreach (var box in _cargoBoxes)
        {
            if (box == null) continue;
            Vector3 boxPosition = box.transform.position;
            // Keep grip through the small solver displacement produced by a tall stack. A real
            // impact still breaks transport once the carton is clearly outside this envelope.
            const float horizontalMargin = 0.4f;
            bool abovePallet = boxPosition.x >= cargoBounds.min.x - horizontalMargin
                            && boxPosition.x <= cargoBounds.max.x + horizontalMargin
                            && boxPosition.z >= cargoBounds.min.z - horizontalMargin
                            && boxPosition.z <= cargoBounds.max.z + horizontalMargin
                            && boxPosition.y >= cargoBounds.min.y - 0.25f;
            if (!abovePallet) continue;

            Vector3 pointVelocity = _carrierVelocity + Vector3.Cross(
                _carrierAngularVelocity, boxPosition - _rb.position);
            box.StabilizeOnMovingPallet(
                pointVelocity, _carrierAngularVelocity, transform.up,
                _cargoStabilizationMultiplier);
        }
    }

    void MovePalletInLateUpdate(Vector3 position, Quaternion rotation)
    {
        float deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
        Vector3 previousPosition = transform.position;
        Quaternion previousRotation = transform.rotation;
        Vector3 frameDelta = position - previousPosition;

        // Rigidbody interpolation and contact solving can change the fork pose by fractions of a
        // millimetre while the truck is parked. Do not propagate that noise through the pallet to
        // every dynamic box in the stack.
        const float PositionDeadzone = 0.0005f;
        const float RotationDeadzone = 0.02f;
        bool hasMeaningfulMotion = frameDelta.sqrMagnitude
                                 >= PositionDeadzone * PositionDeadzone
                                || Quaternion.Angle(previousRotation, rotation)
                                 >= RotationDeadzone;

        _physicsTargetPosition = position;
        _physicsTargetRotation = rotation;
        _hasPhysicsTarget = true;
        if (!hasMeaningfulMotion)
        {
            _carrierVelocity = Vector3.zero;
            _carrierAngularVelocity = Vector3.zero;
            return;
        }

        _carrierVelocity = frameDelta / deltaTime;
        _carrierAngularVelocity = AngularVelocityTo(previousRotation, rotation, deltaTime);

        transform.SetPositionAndRotation(position, rotation);
        if (_rb != null && _rb.isKinematic)
        {
            _rb.position = position;
            _rb.rotation = rotation;
        }

        // Authoritative cargo remains a dynamic Rigidbody and is driven by carrier-relative
        // velocity in FixedUpdate, so its colliders still resolve walls and can be knocked off.
        // Non-authoritative peers render the same cargo from its pallet-local follow pose; this
        // removes packet-rate lag without creating a second physics simulation.
    }

    static Vector3 AngularVelocityTo(Quaternion from, Quaternion to, float deltaTime)
    {
        Quaternion delta = to * Quaternion.Inverse(from);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;
        if (axis.sqrMagnitude < 0.000001f || Mathf.Abs(angle) < 0.001f)
            return Vector3.zero;
        return axis.normalized * (angle * Mathf.Deg2Rad / Mathf.Max(deltaTime, 0.0001f));
    }

    public void SetCargoStabilization(float multiplier) =>
        _cargoStabilizationMultiplier = Mathf.Clamp(multiplier, 1f, 2f);

    void AutoFindGrabCollider()
    {
        foreach (var n in s_grabNames)
        {
            var child = FindDeep(transform, n);
            if (child == null) continue;
            // Any collider on the named pallet child (trigger is fine —
            // used only for bounds, not physics contact).
            var cols = child.GetComponents<Collider>();
            if (cols.Length > 0) { _grabCollider = cols[0]; return; }
        }
        // Fallback: non-trigger collider on the root itself
        foreach (var col in GetComponents<Collider>())
        {
            if (!col.isTrigger) { _grabCollider = col; return; }
        }
    }

    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var result = FindDeep(t.GetChild(i), name);
            if (result != null) return result;
        }
        return null;
    }

    // Called by ReachTruckController when the fork enters the grab collider.
    public void Attach(Transform forkMount)
    {
        if (IsLifted) return;
        _remoteForkMount = null;
        _liftRequestStartPosition = transform.position;
        _liftRequestStartRotation = transform.rotation;
        IsLifted = true;
        RefreshCargoBoxesFromZone();

        if (_rb != null)
        {
            _rb.constraints     = RigidbodyConstraints.None;
            _rb.isKinematic     = true;
            _rb.linearVelocity  = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        // Record pallet position/rotation in fork-local space so it follows naturally.
        _attachOffset    = forkMount.InverseTransformPoint(transform.position);
        _attachRotOffset = Quaternion.Inverse(forkMount.rotation) * transform.rotation;
        _physicsTargetPosition = transform.position;
        _physicsTargetRotation = transform.rotation;
        _hasPhysicsTarget = true;

        // Multiplayer: notify server that the local truck lifted this pallet.
        bool networking = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        IsLiftedByLocalTruck = !networking;
        _liftRequestPending = networking;
        if (!networking)
        {
            ActivateCargoPhysics();
            PalletLiftSfx.Play(transform.position);
        }
        if (networking)
        {
            var vehicle = forkMount != null
                ? forkMount.GetComponentInParent<NetworkVehicleSync>()
                : null;
            if (NetworkPalletManager.Instance != null && vehicle != null && vehicle.IsSpawned)
            {
                NetworkPalletManager.Instance.RequestLiftServerRpc(
                    NetPalletId, vehicle.NetworkObjectId);
                if (_liftRequestTimeoutRoutine != null)
                    StopCoroutine(_liftRequestTimeoutRoutine);
                _liftRequestTimeoutRoutine = StartCoroutine(LiftRequestTimeout());
            }
            else
                CancelLocalLift(transform.position, transform.rotation);
        }
    }

    public void ApplyLiftResult(bool approved, Vector3 authoritativePos,
        Quaternion authoritativeRot, uint version)
    {
        if (_liftRequestTimeoutRoutine != null)
        {
            StopCoroutine(_liftRequestTimeoutRoutine);
            _liftRequestTimeoutRoutine = null;
        }
        if (!approved)
        {
            _liftRequestPending = false;
            IsLiftedByLocalTruck = false;
            CancelLocalLift(authoritativePos, authoritativeRot);
            return;
        }

        _liftVersion = version;
        if (!_liftRequestPending || !IsLifted)
        {
            NetworkPalletManager.Instance?.ReleasePalletServerRpc(
                NetPalletId, _liftVersion);
            return;
        }

        _liftRequestPending = false;
        IsLiftedByLocalTruck = true;

        // This pallet may have been observed while another client was driving it. In that case
        // the instance still carries the remote smoothing mode and the previous role's weaker
        // cargo multiplier. The local fork moves immediately, so leaving either state behind
        // makes dynamic cartons visibly trail the pallet until the server catches up.
        _remoteSmoothing = false;
        _remoteForkMount = null;
        _carrierVelocity = Vector3.zero;
        _carrierAngularVelocity = Vector3.zero;
        RefreshCargoBoxesFromZone();
        SetCargoStabilization(2f);

        PalletLiftSfx.Play(transform.position);
    }

    System.Collections.IEnumerator LiftRequestTimeout()
    {
        yield return new WaitForSecondsRealtime(2.5f);
        _liftRequestTimeoutRoutine = null;
        if (!_liftRequestPending) yield break;

        _liftRequestPending = false;
        IsLiftedByLocalTruck = false;
        CancelLocalLift(_liftRequestStartPosition, _liftRequestStartRotation);
        HUDController.Instance?.AddNotification(
            "Pallet", "Lift request timed out", HUDController.NotifType.Warning);
    }

    void CancelLocalLift(Vector3 pos, Quaternion rot)
    {
        IsLifted = false;
        IsLiftedByLocalTruck = false;
        _liftRequestPending = false;
        _remoteSmoothing = false;
        _remoteForkMount = null;
        _hasPhysicsTarget = false;
        SetCargoFollowMotion(false);
        transform.SetPositionAndRotation(pos, rot);
        if (_rb != null)
        {
            _rb.position = pos;
            _rb.rotation = rot;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.constraints = RigidbodyConstraints.None;
        }
        if (!_cargoPhysicsActivated)
        {
            foreach (var rb in _childRbs)
            {
                if (rb == null) continue;
                rb.isKinematic = true;
                rb.detectCollisions = true;
            }
        }
    }

    // Called every LateUpdate by ReachTruckController while pallet is lifted.
    public void SyncToFork(Transform forkMount)
    {
        if (!IsLifted) return;
        Vector3 targetPosition = forkMount.TransformPoint(_attachOffset);
        Quaternion targetRotation = forkMount.rotation * _attachRotOffset;
        _physicsTargetPosition = targetPosition;
        _physicsTargetRotation = targetRotation;
        MovePalletInLateUpdate(targetPosition, targetRotation);

        // In multiplayer the server independently derives the same pose from the vehicle's
        // accepted transform and fork height. This local movement is visual prediction only.
    }

    void ActivateCargoPhysics()
    {
        if (_cargoPhysicsActivated) return;
        _cargoPhysicsActivated = true;

        _cargoActivationRoutine = StartCoroutine(ActivateCargoPhysicsAfterContactSettles());
    }

    /// Server-authoritative transport mode. Cargo keeps its pallet membership for interaction and
    /// persistence, but each box leaves kinematic follow and becomes a real dynamic body. Boxes
    /// are activated in small batches so a full pallet does not enter PhysX in one frame.
    public void BeginAuthoritativeCargoTransport()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer || !IsLifted) return;

        RefreshCargoBoxesFromZone();
        if (_cargoActivationRoutine != null)
            StopCoroutine(_cargoActivationRoutine);

        _cargoPhysicsActivated = true;
        _cargoActivationRoutine = StartCoroutine(ActivateAuthoritativeCargoBatched());
    }

    System.Collections.IEnumerator ActivateAuthoritativeCargoBatched()
    {
        const int BoxesPerPhysicsStep = 4;
        int activated = 0;

        foreach (var box in _cargoBoxes)
        {
            if (!IsLifted) break;
            if (box == null) continue;

            // Until its turn the box still follows the pallet, so batching cannot leave the rear
            // half of a large load hanging at the pickup point while the truck starts moving.
            box.EnablePersistentPhysics(
                _carrierVelocity, _carrierAngularVelocity,
                preservePalletMembership: true);
            NetworkPhysBoxManager.Instance?.ServerPublishWorldMotion(
                box.NetBoxId, box.transform.position, box.transform.rotation,
                _carrierVelocity);

            activated++;
            if (activated % BoxesPerPhysicsStep == 0)
                yield return new WaitForFixedUpdate();
        }

        _cargoActivationRoutine = null;
    }

    void StopCargoActivation()
    {
        if (_cargoActivationRoutine != null)
        {
            StopCoroutine(_cargoActivationRoutine);
            _cargoActivationRoutine = null;
        }
        _cargoPhysicsActivated = false;
    }

    /// Cargo membership is logical, not Transform-based. Scene-authored boxes are detached from
    /// the pallet hierarchy at runtime and boxes placed by players were never parented to it.
    void RefreshCargoBoxesFromZone()
    {
        var zone = GetComponent<PalletStackZone>() ?? GetComponentInChildren<PalletStackZone>();
        if (zone == null) return;

        var tracked = zone.BoxesOnPallet;
        _cargoBoxes = new BoxPhysicsInteractable[tracked.Count];
        for (int i = 0; i < tracked.Count; i++)
            _cargoBoxes[i] = tracked[i];

        SetCargoFollowMotion(IsLifted);
    }

    void SetCargoFollowMotion(bool active)
    {
        foreach (var box in _cargoBoxes)
            box?.SetFollowMotionActive(active);
    }

    /// Multiplayer release is decided once by the server. Cargo remains kinematic and attached
    /// during transport, then the server starts every box from one shared pose/velocity and
    /// publishes that motion through NetworkPhysBoxManager for all clients to reproduce.
    public void ReleaseCargoAuthoritatively()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer || _cargoPhysicsActivated) return;

        _cargoPhysicsActivated = true;
        if (_cargoActivationRoutine != null)
        {
            StopCoroutine(_cargoActivationRoutine);
            _cargoActivationRoutine = null;
        }
        if (_cargoEnvelopeCollider != null && _cargoEnvelopeCollider != _grabCollider)
            _cargoEnvelopeCollider.enabled = false;

        Vector3 inheritedVelocity = _carrierVelocity;
        Vector3 inheritedAngularVelocity = _carrierAngularVelocity;
        RefreshCargoBoxesFromZone();
        foreach (var box in _cargoBoxes)
        {
            if (box == null) continue;
            box.EnablePersistentPhysics(inheritedVelocity, inheritedAngularVelocity);
            NetworkPhysBoxManager.Instance?.ServerPublishWorldMotion(
                box.NetBoxId, box.transform.position, box.transform.rotation, inheritedVelocity);
        }
    }

    System.Collections.IEnumerator ActivateCargoPhysicsAfterContactSettles()
    {
        // Give the forks a few solver steps to establish contact before releasing the tightly
        // packed stack. Immediate activation resolves every tiny overlap in the same frame.
        for (int i = 0; i < 3; i++)
            yield return new WaitForFixedUpdate();

        if (!IsLifted)
        {
            _cargoPhysicsActivated = false;
            _cargoActivationRoutine = null;
            yield break;
        }

        // This root collider is only a parked-stack envelope. Keeping it after activation
        // traps the independent boxes inside the pallet and ejects them on the next physics step.
        if (_cargoEnvelopeCollider != null && _cargoEnvelopeCollider != _grabCollider)
            _cargoEnvelopeCollider.enabled = false;

        // Cardboard on wood should resist small accelerations. Apply the same material to the
        // pallet base; box colliders receive it when they become persistent physics bodies.
        foreach (var col in GetComponentsInChildren<Collider>(true))
        {
            if (col == null || col.isTrigger) continue;
            if (col.GetComponentInParent<BoxPhysicsInteractable>() != null) continue;
            col.material = BoxPhysicsInteractable.CargoPhysicsMaterial;
        }

        Vector3 inheritedVelocity = _rb != null ? _rb.linearVelocity : Vector3.zero;
        Vector3 inheritedAngularVelocity = _rb != null ? _rb.angularVelocity : Vector3.zero;
        foreach (var box in _cargoBoxes)
            if (box != null)
                box.EnablePersistentPhysics(inheritedVelocity, inheritedAngularVelocity);

        _cargoActivationRoutine = null;
    }

    /// Finds a horizontal shelf/floor surface immediately below the carried pallet.
    /// The pallet is moved kinematically while attached, so contacts cannot stop it by
    /// themselves; ReachTruckController uses this sweep to release before tunnelling.
    public bool TryGetSupportBelow(float maxDistance, Transform ignoredVehicleRoot,
                                   out float supportTopY)
    {
        supportTopY = 0f;
        if (_grabCollider == null || maxDistance <= 0f) return false;

        Bounds bounds = _grabCollider.bounds;
        Vector3 halfExtents = new(
            Mathf.Max(0.05f, bounds.extents.x * 0.82f),
            0.025f,
            Mathf.Max(0.05f, bounds.extents.z * 0.82f));
        Vector3 origin = new(bounds.center.x, bounds.min.y + 0.04f, bounds.center.z);

        RaycastHit[] hits = Physics.BoxCastAll(
            origin, halfExtents, Vector3.down, Quaternion.identity,
            maxDistance + 0.04f, ~0, QueryTriggerInteraction.Ignore);

        bool found = false;
        float highest = float.NegativeInfinity;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null || hit.normal.y < 0.55f) continue;
            Transform hitTransform = hit.collider.transform;
            if (hitTransform.IsChildOf(transform)) continue;
            if (ignoredVehicleRoot != null && hitTransform.IsChildOf(ignoredVehicleRoot)) continue;
            if (hit.point.y <= highest) continue;

            highest = hit.point.y;
            found = true;
        }

        supportTopY = highest;
        return found;
    }

    /// Places the pallet base precisely on a detected support before Release() enables gravity.
    public void AlignBottomToSupport(float supportTopY)
    {
        if (_grabCollider == null) return;

        float deltaY = supportTopY - _grabCollider.bounds.min.y + 0.005f;
        if (Mathf.Abs(deltaY) > 0.35f) return;

        Vector3 position = transform.position;
        position.y += deltaY;
        transform.position = position;
        if (_rb != null) _rb.position = position;
    }

    // Called when the fork is lowered to minimum height or the player exits the truck.
    public void Release()
    {
        if (!IsLifted) return;

        if (_liftRequestTimeoutRoutine != null)
        {
            StopCoroutine(_liftRequestTimeoutRoutine);
            _liftRequestTimeoutRoutine = null;
        }

        // Multiplayer: notify server of release before clearing state.
        if (IsLiftedByLocalTruck
            && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkPalletManager.Instance?.ReleasePalletServerRpc(
                NetPalletId, _liftVersion);

        IsLifted             = false;
        IsLiftedByLocalTruck = false;
        _liftRequestPending  = false;
        _remoteForkMount     = null;
        _hasPhysicsTarget    = false;
        StopCargoActivation();
        SetCargoFollowMotion(false);

        if (_rb != null)
        {
            // The pallet root is stable authoritative support, not a separately simulated body.
            // Letting every peer simulate a short landing produced a different resting pose on
            // each machine. The server publishes the accepted pose; released cargo owns the
            // dynamic physics instead.
            _rb.position        = transform.position;
            _rb.rotation        = transform.rotation;
            SnapAboveGround();
            _rb.linearVelocity  = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.useGravity      = false;
            _rb.isKinematic     = true;
            _rb.constraints     = RigidbodyConstraints.None;
        }

    }

    // ── Multiplayer: remote pallet state ─────────────────────────────────────────

    /// Mark this pallet as lifted by another client's truck.
    /// IsLifted is set so ScanForPallet skips it; physics is frozen.
    /// The release state is restored by ApplyRemoteRelease when the other client lowers it.
    public void SetRemoteLifted(bool lifted, Transform forkMount,
        Vector3 forkLocalPosition, Quaternion forkLocalRotation, bool playSound = true)
    {
        if (lifted)
        {
            bool wasLifted = IsLifted;
            IsLifted = true;
            _remoteForkMount = forkMount;
            _remoteForkLocalPosition = forkLocalPosition;
            _remoteForkLocalRotation = forkLocalRotation;
            if (!wasLifted) RefreshCargoBoxesFromZone();
            else SetCargoFollowMotion(true);
            if (_rb != null) { _rb.isKinematic = true; _rb.useGravity = false; }
            // Seed the smoothing target at the current pose so the first ApplyRemotePosition
            // packet doesn't lerp in from wherever the pallet happened to be sitting before.
            _remoteTargetPos = transform.position;
            _remoteTargetRot = transform.rotation;
            _physicsTargetPosition = transform.position;
            _physicsTargetRotation = transform.rotation;
            _hasPhysicsTarget = true;
            _remoteSmoothing = true;
            if (playSound && !wasLifted)
                PalletLiftSfx.Play(transform.position);
        }
        // For lifted=false: ApplyRemoteRelease handles full physics restore.
    }

    /// Records the latest position/rotation broadcast by the client lifting this pallet remotely —
    /// LateUpdate lerps toward it every frame instead of snapping here directly (see _remoteSmoothing).
    public void ApplyRemotePosition(Vector3 pos, Quaternion rot)
    {
        _remoteTargetPos = pos;
        _remoteTargetRot = rot;
    }

    /// Server-only exact pose used immediately before publishing a release. Unlike the remote
    /// smoothing path, this guarantees cargo starts physics from the accepted fork position.
    public void ApplyAuthoritativeLiftPose(Vector3 pos, Quaternion rot)
    {
        MovePalletInLateUpdate(pos, rot);
        if (_rb == null) return;

        _rb.position = pos;
        _rb.rotation = rot;
        SnapAboveGround();
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.useGravity = false;
        _rb.isKinematic = true;
    }

    /// Restore physics when another client releases the pallet.
    public void ApplyRemoteRelease(Vector3 pos, Quaternion rot)
    {
        if (_liftRequestTimeoutRoutine != null)
        {
            StopCoroutine(_liftRequestTimeoutRoutine);
            _liftRequestTimeoutRoutine = null;
        }
        IsLifted = false;
        IsLiftedByLocalTruck = false;
        _liftRequestPending = false;
        _remoteSmoothing = false;
        _remoteForkMount = null;
        _hasPhysicsTarget = false;
        StopCargoActivation();
        SetCargoFollowMotion(false);

        transform.SetPositionAndRotation(pos, rot);

        if (_rb != null)
        {
            _rb.position        = pos;
            _rb.rotation        = rot;
            _rb.isKinematic     = true;
            _rb.useGravity      = false;
            _rb.linearVelocity  = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.constraints     = RigidbodyConstraints.None;
        }

    }

    // ── Explosion ────────────────────────────────────────────────────────────────

    const int ExplodeBatchSize = 4; // child boxes launched per frame — see LaunchChildrenBatched

    /// Explodes the pallet — child boxes fly outward, root gets pushed.
    /// Used by PalletDropEvent.
    public void Explode(float force, float radius)
    {
        Vector3 center = transform.position;

        var manager = NetworkManager.Singleton;
        if (manager != null && manager.IsListening)
        {
            // Every peer used to explode its own copy with PhysX and immediately produced a
            // different pile. In host/client play the host removes the authoritative manifest
            // rows and launches each carton; clients consume the resulting box states/snapshots.
            if (!manager.IsServer) return;
            NetworkPalletManager.Instance?.ServerScatterCargo(
                NetPalletId, center, force, radius);
            return;
        }

        // Root pallet slides/spins from the blast too — single rigidbody, cheap, immediate.
        if (_rb != null && !IsLifted)
        {
            _rb.constraints = RigidbodyConstraints.None;
            _rb.isKinematic = false;
            _rb.useGravity  = true;
            _rb.AddExplosionForce(force * 0.4f, center - Vector3.up * 0.3f,
                                  radius, 0.3f, ForceMode.Impulse);
            StartCoroutine(FreezePalletYAfterLanding());
        }

        // Spread the child boxes' kinematic->dynamic transition across a few frames so the
        // physics solver isn't resolving dozens of newly-dynamic, tightly-packed rigidbodies
        // waking up in the same FixedUpdate — that simultaneous wake-up is what causes the
        // frame-time spike, not the force math itself.
        StartCoroutine(LaunchChildrenBatched(center, force, radius));
    }

    // Matches BoxPhysicsInteractable.Release's damping — without it, debris keeps its full
    // explosion-impulse velocity indefinitely (Rigidbody defaults to zero drag), so a single
    // pallet blast can skid boxes clear across the aisle into neighboring pallets/shelves and
    // chain-react their own resting (already-dynamic) stock — reads as the whole warehouse
    // getting wrecked instead of one contained incident.
    const float DebrisLinearDamping  = 2.5f;
    const float DebrisAngularDamping = 4.0f;

    System.Collections.IEnumerator LaunchChildrenBatched(Vector3 center, float force, float radius)
    {
        int count = 0;
        foreach (var rb in _childRbs)
        {
            if (rb == null) continue;
            rb.isKinematic      = false;
            rb.useGravity       = true;
            rb.detectCollisions = true;
            rb.linearDamping    = DebrisLinearDamping;
            rb.angularDamping   = DebrisAngularDamping;
            rb.AddExplosionForce(force, center, radius, 1f, ForceMode.Impulse);

            count++;
            if (count % ExplodeBatchSize == 0)
                yield return null;
        }

        StartCoroutine(FreezeChildrenAfterDelay(4f));
    }

    System.Collections.IEnumerator FreezeChildrenAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        foreach (var rb in _childRbs)
        {
            if (rb == null || rb.isKinematic) continue;
            rb.linearVelocity  = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic     = true;
        }
    }

    // Prevents the pallet from tunnelling through the floor after kinematic transport.
    // Temporarily disables all colliders under this pallet — including cargo boxes
    // parented on after Awake — so the downward raycast finds the actual floor
    // instead of a box sitting on the pallet. Fixes a bug where mixed-box loads
    // (whose boxes aren't covered by the pre-cached _ownStructuralColliders list)
    // could have a box directly under the pallet pivot, making the raycast report
    // the box's top as "ground" and snap the pallet up onto it on release.
    void SnapAboveGround()
    {
        if (_rb == null) return;

        var allColliders = GetComponentsInChildren<Collider>(true);
        foreach (var c in allColliders)
            if (c != null) c.enabled = false;

        // Search only below the pallet. Casting from metres above could hit the next rack tier
        // first and incorrectly snap the pallet upward onto that shelf.
        Bounds grabBounds = _grabCollider != null
            ? _grabCollider.bounds
            : new Bounds(transform.position, Vector3.one * 0.24f);
        float pivotToBottom = transform.position.y - grabBounds.min.y;
        const float castOriginAboveBottom = 0.08f;
        const float castDistance          = 0.8f;
        Vector3 origin = new(
            grabBounds.center.x,
            grabBounds.min.y + castOriginAboveBottom,
            grabBounds.center.z);

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, castDistance,
                            ~0, QueryTriggerInteraction.Ignore))
        {
            float needed = hit.point.y + pivotToBottom;
            if (transform.position.y < needed)
            {
                var p = transform.position;
                p.y           = needed;
                transform.position = p;
                _rb.position  = p;
            }
        }

        foreach (var c in allColliders)
            if (c != null) c.enabled = true;
    }

    System.Collections.IEnumerator FreezePalletYAfterLanding()
    {
        // Wait for physics to take ownership after the kinematic transport. A single
        // FixedUpdate is not enough to assume gravity has already changed linearVelocity.
        yield return new WaitForFixedUpdate();

        const float settleGrace = 0.2f;
        float timer = 0f;
        bool landed = false;
        while (timer < 3f)
        {
            if (_rb == null || _rb.isKinematic) yield break; // picked up again

            // Low velocity immediately after Release does not mean the pallet landed: it may
            // simply be the frame before gravity integration. Freeze only when a real support
            // is directly below it and the body has had time to start moving.
            bool supported = TryGetSupportBelow(0.08f, null, out _);
            if (timer >= settleGrace && supported
                && _rb.linearVelocity.sqrMagnitude < 0.0025f
                && _rb.angularVelocity.sqrMagnitude < 0.0025f)
            {
                landed = true;
                break;
            }

            timer += Time.deltaTime;
            yield return null;
        }

        // A timeout is not proof of landing. Keep gravity active when no support was found.
        // Once a real support is confirmed, return the pallet to its stable parked state.
        if (landed && _rb != null && !_rb.isKinematic)
        {
            _rb.linearVelocity  = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.useGravity      = false;
            _rb.isKinematic     = true;
            // Restore child box collisions now that pallet is solid again.
            foreach (var rb in _childRbs)
                if (rb != null) rb.detectCollisions = true;
        }
    }
}
