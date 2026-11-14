using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;

// Multiplayer: RequestEnterServerRpc / RequestExitServerRpc via NetworkVehicleSync.
// Fork height is broadcast via NetworkVehicleSync._forkT for remote visual sync.
// Single-player: all network code is gated on NetworkManager.Singleton?.IsListening.
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
[DefaultExecutionOrder(-200)]
public class ReachTruckController : MonoBehaviour, IHoldInteractable, ICanInteractCheck, INoOutlineHighlight
{
    [Header("Fork Bones")]
    [SerializeField] Transform _front1;
    [SerializeField] Transform _front2;

    [Header("Fork Range (local Y — bone coordinates)")]
    [SerializeField] float _front1MinY = 0.0014f;
    [SerializeField] float _front1MaxY = 0.040f;
    [SerializeField] float _front2MinY = 0f;
    [SerializeField] float _front2MaxY = 0.040f;
    [SerializeField] float _liftSpeed  = 0.005f;

    [Header("Fork Height Limits (world Y)")]
    [Tooltip("Minimum fork height in world space — keeps forks above floor level.")]
    [SerializeField] float _forkMinWorldY = 0.05f;
    [Tooltip("Maximum fork height in world space — prevents fork rising past warehouse ceiling.")]
    [SerializeField] float _forkMaxWorldY = 1.8f;
    [Tooltip("World-space height added to the complete visual fork assembly at every lift level.")]
    [SerializeField] float _forkBaseHeightOffset;
    [Tooltip("Ground-only mode: Q/RB grabs pallets but fork never rises (for pallet trucks).")]
    [SerializeField] bool  _groundOnlyFork = false;

    [Header("Fork Mount")]
    [SerializeField] Transform _forkMount;
    [Tooltip("Empty GameObject parented to the actual fork tip bone. " +
             "If assigned, pallet tracks this height instead of _front1. " +
             "Set it when fork visually moves faster/slower than the pallet.")]
    [SerializeField] Transform _forkTip;
    [Tooltip("Multiplier for pallet height relative to fork bone movement. " +
             "Increase (e.g. 2) if the fork visual moves faster than the pallet. " +
             "Decrease (e.g. 0.5) if the pallet moves faster than the fork visual.")]
    [SerializeField] float _forkHeightScale = 1f;

    [Header("Pallet Pickup")]
    // XZ margin added to the pallet board's footprint when checking fork alignment.
    // Tune if grab feels too tight or too loose.
    [SerializeField] Vector3 _forkScanSize = new Vector3(0.3f, 0f, 0.5f);
    [Tooltip("Maximum vertical distance between the fork pickup point and pallet floor.")]
    [SerializeField] float _forkGrabHeightTolerance = 0.45f;

    [Header("Drive")]
    [Tooltip("Player-facing vehicle name used in notifications. Leave empty to infer it from the vehicle type.")]
    [SerializeField] string _displayName;
    [SerializeField] float _driveSpeed = 1.5f;
    [SerializeField] float _turnSpeed  = 25f;

    [Header("Diagnostics")]
    [Tooltip("Logs the collider that blocks the truck while the player is trying to drive.")]
    [SerializeField] bool _debugBlockingCollisions = false;
    [SerializeField, Min(0.1f)] float _collisionDebugInterval = 0.75f;

    [Header("Scene Points")]
    [SerializeField] Transform _driverSeat;
    [SerializeField] Transform _exitPoint;

    [Header("Sitting Animation")]
    [SerializeField] RuntimeAnimatorController _sittingController;

    [Header("Steering Wheel")]
    [SerializeField] Transform _steeringWheelBone;
    [SerializeField] Transform _steerHandRight;
    [SerializeField] Transform _steerHandLeft;
    [SerializeField] float _maxSteerAngle = 45f;
    [SerializeField] float _steerDirection = -1f;

    [Header("Foot IK Targets")]
    [SerializeField] Transform _footTargetRight;
    [SerializeField] Transform _footTargetLeft;

    [Header("Camera")]
    [SerializeField] float _camSensitivity = 1f;
    [SerializeField] float _camPitchMin = -60f;
    [SerializeField] float _camPitchMax =  75f;
    [SerializeField] float _truckEnterPitch = -25f;
    [SerializeField] float _vehicleCameraDistance = 7f;
    [Tooltip("Limits distant warehouse geometry while driving. The elevated forklift camera " +
             "otherwise exposes most racks at once and becomes CPU/draw-call bound.")]
    [SerializeField, Min(20f)] float _vehicleCameraFarClip = 60f;
    [Tooltip("Empty GO child of this truck that the vehicle camera follows. " +
             "Place it behind and above the driver seat. " +
             "Run Tools → Warehouse Demo → Setup ReachTruck to auto-create it.")]
    [SerializeField] Transform _cameraAnchor;

    [Header("Driver Seat")]
    [Tooltip("Local offset from _driverSeat where the player pivot is placed. " +
             "Set to zero if _driverSeat is already positioned at the correct floor level of the cab.")]
    [SerializeField] Vector3 _driverSeatOffset = Vector3.zero;

    // physics
    Rigidbody  _rb;
    Collider[] _truckColliders;
    PhysicsMaterial _drivePhysicsMaterial;
    float      _pendingFwd;
    float      _pendingTurn;
    float      _smoothedFwd;
    float      _smoothedTurn;
    Quaternion _commandedDriveRotation;
    Collider   _lastBlockingCollider;
    float      _nextCollisionDebugTime;

    // fork state
    float          _front1Y;
    float          _front2Y;
    Vector3        _front1BaseLocalPosition;
    Vector3        _front2BaseLocalPosition;
    Vector3        _forkMountBaseLocalPosition;
    PalletBehavior _heldPallet;
    float          _forkBaseWorldY;
    bool           _forkBaseRecorded;

    // boarding state
    bool                      _occupied;
    // Frame Enter() ran on — E's wasPressedThisFrame stays true for the whole Unity frame
    // regardless of script execution order, so without this guard, if this script's own
    // Update() happens to run AFTER PlayerInteraction's in the same frame, it would see
    // _occupied already true and the same E-press as "still pressed", firing Exit()
    // immediately on entry.
    int                       _boardedFrame = -1;
    GameObject                _player;
    Behaviour                 _playerCtrl;
    FirstPersonController     _fpc;
    CharacterController       _playerCharacterController;
    bool                      _playerCharacterControllerWasEnabled;
    Animator                  _playerAnimator;
    RuntimeAnimatorController _origAnimController;
    AnimatorOverrideController _sittingOverrideController;

    // steering wheel state
    Quaternion _steerNeutral;

    // camera state
    Transform  _playerCameraRoot;
    Transform  _camRootOrigParent;
    Vector3    _camRootOrigLocalPos;
    Quaternion _camRootOrigLocalRot;
    float      _camYaw;
    float      _camPitch;
    Camera     _drivingCamera;
    float      _originalCameraFarClip;
    bool       _cameraFarClipOverridden;

    // specialization penalty state
    bool  _drivingAsNovice;
    float _effectiveDriveSpeed;
    float _effectiveTurnSpeed;

    // repair state — per-vehicle, so the reach truck and the pallet truck (rohlya)
    // break and get repaired independently of each other.
    bool  _forkliftBroken;
    float _damageMult = 1f;
    float _extraDrift = 0f;
    bool  _forkJammed;
    float _lastForkWarnTime;
    float _lastLimitNotifyTime;

    // The specific event instances currently targeting THIS vehicle — Interact() tells
    // them (not a global static) to clean up when the hold-repair completes.
    public BrokenForkliftEvent OwnerForkliftEvent { get; set; }
    public BrokenForkEvent     OwnerForkEvent     { get; set; }

    // Multiplayer: cached vehicle sync component and pending actor for async confirmation.
    NetworkVehicleSync _netVehicle;
    GameObject         _pendingActor;

    // Throttle predicted snapshots to the project's 30 Hz network tick.
    float _lastNetUpdate;

    const float RepairDurationOperator = 10f;
    const float RepairDurationDefault  = 25f;
    const float DriveStickDeadzone      = 0.18f;
    const float RescueForkThreshold     = 0.4f;
    const float RescueRange             = 5f;
    const RigidbodyConstraints DrivingConstraints = RigidbodyConstraints.FreezeRotationX
                                                    | RigidbodyConstraints.FreezeRotationZ
                                                    | RigidbodyConstraints.FreezePositionY;
    const RigidbodyConstraints ParkedConstraints = DrivingConstraints
                                                   | RigidbodyConstraints.FreezeRotationY
                                                   | RigidbodyConstraints.FreezePositionX
                                                   | RigidbodyConstraints.FreezePositionZ;

    static readonly BindingFlags Private   = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly int          SpeedHash = Animator.StringToHash("Speed");

    public static bool PlayerOnTruck { get; private set; }

    // Registry of every reach/pallet truck in the scene — lets BrokenForkliftEvent /
    // BrokenForkEvent target one specific vehicle instead of every vehicle sharing this script.
    public static readonly List<ReachTruckController> All = new();
    void OnEnable()  => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// Server-side proof for a forklift rescue. A client can ask, but the authoritative vehicle
    /// must show that the sender is its current driver, the raised fork crossed the rescue height,
    /// and the truck is actually beside the crushed player.
    public static bool TryGetServerRescuePosition(ulong driverClientId, Vector3 victimPosition,
        out Vector3 respawnPosition)
    {
        respawnPosition = default;
        foreach (var truck in All)
        {
            if (truck == null || truck._groundOnlyFork || truck._netVehicle == null) continue;
            if (!truck._netVehicle.IsServer || !truck._netVehicle.IsDrivenBy(driverClientId)) continue;
            if (truck._netVehicle.ForkT < RescueForkThreshold) continue;
            if ((truck.transform.position - victimPosition).sqrMagnitude > RescueRange * RescueRange)
                continue;

            respawnPosition = truck.transform.position + truck.transform.right * 1.5f;
            return true;
        }
        return false;
    }

    public void ApplyForkliftDamage(float damageMult, float extraDrift)
    {
        _forkliftBroken = true;
        _damageMult     = damageMult;
        _extraDrift     = extraDrift;
    }

    public void ClearForkliftDamage()
    {
        _forkliftBroken = false;
        _damageMult     = 1f;
        _extraDrift     = 0f;
    }

    public void SetForkJammed(bool jammed) => _forkJammed = jammed;

    /// Applied directly in solo. In multiplayer, applied on every client via CoopGameManager's
    /// ClientRpc echo — never called straight from Interact() there.
    public void ForceRepairFaults()
    {
        OwnerForkliftEvent?.Remove();
        OwnerForkEvent?.Remove();
    }

    // Exposed for NetworkVehicleSync.ApplyOccupantIK so remote players get hand/foot IK.
    public Transform SteerHandRight  => _steerHandRight;
    public Transform SteerHandLeft   => _steerHandLeft;
    public Transform FootTargetRight => _footTargetRight;
    public Transform FootTargetLeft  => _footTargetLeft;
    // Exposed for NetworkVehicleSync so OBSERVER clients can swap the occupant's remote
    // CharacterVisual to the same sitting pose the driver's own client already swaps to locally.
    public RuntimeAnimatorController SittingController => _sittingController;
    public Transform ForkMount => _forkMount;
    public bool IsLocallyOccupied => _occupied;
    public bool IsGroundOnlyVehicle => _groundOnlyFork;
    public float DriveCommand => Mathf.Abs(_smoothedFwd) > Mathf.Abs(_pendingFwd)
        ? _smoothedFwd
        : _pendingFwd;
    public float ForkNormalized
    {
        get
        {
            float span = _front1MaxY - _front1MinY;
            return span > Mathf.Epsilon
                ? Mathf.Clamp01((_front1Y - _front1MinY) / span)
                : 0f;
        }
    }

    /// Builds a seated controller without changing the Animator's parameter or layer layout.
    /// NetworkAnimator and PlayerAnimatorBridge cache that layout at startup, so assigning the
    /// standalone one-layer SittingController directly makes both systems query missing hashes
    /// every frame. An override keeps the original controller graph intact and replaces only its
    /// animation clips with the authored sitting clip.
    public static AnimatorOverrideController BuildCompatibleSittingController(
        RuntimeAnimatorController baseController,
        RuntimeAnimatorController sittingController)
    {
        if (baseController == null || sittingController == null) return null;

        AnimationClip[] sittingClips = sittingController.animationClips;
        if (sittingClips == null || sittingClips.Length == 0 || sittingClips[0] == null)
            return null;

        var compatible = new AnimatorOverrideController(baseController)
        {
            name = $"{baseController.name}_SeatedRuntime"
        };
        var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>(
            compatible.overridesCount);
        compatible.GetOverrides(overrides);
        for (int i = 0; i < overrides.Count; i++)
            overrides[i] = new KeyValuePair<AnimationClip, AnimationClip>(
                overrides[i].Key, sittingClips[0]);
        compatible.ApplyOverrides(overrides);
        return compatible;
    }

    // Server-side snapshot validation budgets. Slack is added by NetworkVehicleSync for jitter;
    // these values describe the fastest legitimate motion this controller can produce.
    public float NetworkMaxLinearSpeed => Mathf.Max(3f, _driveSpeed * 2.5f);
    public float NetworkMaxAngularSpeed => Mathf.Max(60f, _turnSpeed * 2.5f);

    public void ApplyAuthoritativeForkT(float t)
    {
        t = Mathf.Clamp01(t);
        float span = _front1MaxY - _front1MinY;
        _front1Y = _front1MinY + t * span;
        float normalized = span > 0f ? (_front1Y - _front1MinY) / span : 0f;
        _front2Y = Mathf.Lerp(_front2MinY, _front2MaxY, normalized);
        CaptureDriveRotation();
    }

    /// Human-readable name for HUD messages. The scene object's technical name remains unchanged
    /// because editor setup tools and serialized references may still depend on it.
    public string DisplayName => !string.IsNullOrWhiteSpace(_displayName)
        ? _displayName.Trim()
        : (_groundOnlyFork ? "Pallet Jack" : "Reach Truck");

    bool AnyBrokenEvent => _forkliftBroken || _forkJammed;
    public bool HasRepairFault => AnyBrokenEvent;

    // IHoldInteractable — true only while there's something broken and the player is
    // carrying the toolkit; PlayerInteraction then requires a hold instead of a single
    // press. Otherwise this object behaves as a normal single-press "enter vehicle".
    public bool RequiresHold => AnyBrokenEvent && ToolkitItem.IsHeld;
    public float HoldDuration => (SpecializationPenalties.SoloMode || SpecializationPenalties.IsOperator(SpecializationPenalties.GetLocalRole()))
        ? RepairDurationOperator : RepairDurationDefault;

    public string InteractPrompt
    {
        get
        {
            if (RequiresHold)
                return $"{InputHints.EK} Hold to repair forklift ({HoldDuration:0} sec)";
            if (_netVehicle != null && _netVehicle.IsOccupied && !_occupied) return "In use";
            if (!_occupied && !CanLocalPlayerDrive()) return "Forklift operator required";
            return _occupied ? "In use" : $"{InputHints.EK} Operate forklift";
        }
    }

    public bool CanInteract(GameObject interactor)
    {
        bool netOccupied = _netVehicle != null && _netVehicle.IsOccupied;
        return RequiresHold || (!_occupied && !netOccupied && CanLocalPlayerDrive());
    }

    bool CanLocalPlayerDrive() => SpecializationPenalties.CanOperateForklift(
        SpecializationPenalties.GetLocalRole());

    // Persistent on-screen reminder while occupied (see Update()'s ShowControlsHint call).
    string ControlsHintText
    {
        get
        {
            if (_forkJammed)
                return $"W/S — move   A/D — turn   Fork jammed   {InputHints.EK} exit";
            return _groundOnlyFork
                ? $"W/S — move   A/D — turn   {InputHints.Active(GameBindingAction.LiftUp)} — grab pallet   {InputHints.Active(GameBindingAction.LiftDown)} — drop pallet   {InputHints.EK} exit"
                : $"W/S — move   A/D — turn   {InputHints.Active(GameBindingAction.LiftUp)}/{InputHints.Active(GameBindingAction.LiftDown)} — fork   {InputHints.EK} exit";
        }
    }

    void Awake()
    {
        // Audio is presentation-only and observes this controller's state. Adding it here also
        // upgrades older scene/prefab instances without requiring a risky serialized-scene edit.
        if (!TryGetComponent<ReachTruckAudio>(out _))
            gameObject.AddComponent<ReachTruckAudio>();

        if (_front1 != null) _front1BaseLocalPosition = _front1.localPosition;
        if (_front2 != null) _front2BaseLocalPosition = _front2.localPosition;
        if (_forkMount != null) _forkMountBaseLocalPosition = _forkMount.localPosition;
        _front1Y = _front1MinY;
        _front2Y = _front2MinY;
        if (_steeringWheelBone != null)
            _steerNeutral = _steeringWheelBone.localRotation;

        _rb = GetComponent<Rigidbody>();
        CaptureDriveRotation();
        // The vehicle prefabs contain decorative non-convex MeshColliders below the root.
        // A child collider belongs to this dynamic Rigidbody, so those meshes are not a valid
        // PhysX shape and produce unstable wall contacts while turning. The authored root
        // BoxCollider is the deliberate driving hull; pallet pickup is proximity-based.
        foreach (var mc in GetComponentsInChildren<MeshCollider>(true))
            mc.enabled = false;

        _truckColliders = GetComponentsInChildren<Collider>(true);
        _drivePhysicsMaterial = new PhysicsMaterial($"{name}_Drive")
        {
            hideFlags       = HideFlags.HideAndDontSave,
            dynamicFriction = 0f,
            staticFriction  = 0f,
            bounciness      = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounceCombine   = PhysicsMaterialCombine.Minimum,
        };
        foreach (var collider in _truckColliders)
            if (collider != null && collider.enabled && !collider.isTrigger)
                collider.material = _drivePhysicsMaterial;

        _rb.isKinematic    = false;
        _rb.useGravity     = false;
        // Drive/braking are fully controlled below. Rigidbody damping ran after the requested
        // velocity and silently reduced top speed on every physics tick.
        _rb.linearDamping  = 0f;
        _rb.angularDamping = 0f;
        _rb.interpolation  = RigidbodyInterpolation.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        _rb.constraints    = ParkedConstraints;
    }

    void Start()
    {
        _netVehicle = GetComponent<NetworkVehicleSync>();
        if (_netVehicle != null)
            _netVehicle.OnLocalEnterConfirmed += OnEnterConfirmed;

        // Pallet truck: grab is proximity-based, never physics-based.
        // Pre-ignore all scene pallets so the dynamic Rigidbody never launches them on contact.
        if (_groundOnlyFork)
        {
            foreach (var p in FindObjectsByType<PalletBehavior>(FindObjectsSortMode.None))
                IgnorePalletTruckCollision(p, true);
        }

    }

    void OnDestroy()
    {
        RestoreVehicleCameraBudget();
        if (_sittingOverrideController != null)
            Destroy(_sittingOverrideController);
        if (_drivePhysicsMaterial != null)
            Destroy(_drivePhysicsMaterial);
        if (_netVehicle != null)
            _netVehicle.OnLocalEnterConfirmed -= OnEnterConfirmed;

        // The flag is static and only Exit() clears it. A truck unloaded with the player still in
        // it — back to the menu mid-drive, a new shift — would otherwise leave it set, and
        // PlayerInteraction refuses every interaction while it is: the next shift starts with E
        // doing nothing at all.
        if (_occupied) PlayerOnTruck = false;
    }

    /// Statics outlive a play session in the editor (domain reload is off in this project).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => PlayerOnTruck = false;

    // Fired by NetworkVehicleSync when the server grants entry to this client.
    void OnEnterConfirmed(GameObject actor)
    {
        if (_pendingActor == null) return;
        var a = _pendingActor;
        _pendingActor = null;
        Enter(a);
    }

    public void Interact(GameObject actor)
    {
        if (RequiresHold)
        {
            // PlayerInteraction only calls Interact() here once the hold completes.
            // Only THIS vehicle's owning events get cleared — repairing the pallet
            // truck doesn't fix the reach truck and vice versa.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            {
                // Don't clear the fault locally — CoopGameManager echoes to every client
                // (including this one) so all copies of this vehicle repair together,
                // regardless of which player performed the hold-repair.
                CoopGameManager.Instance?.RepairVehicleFaultServerRpc(All.IndexOf(this));
                return;
            }

            ForceRepairFaults();
            return;
        }
        if (!_occupied)
        {
            if (!CanLocalPlayerDrive())
            {
                HUDController.Instance?.AddNotification(
                    "Forklift operator required",
                    "This vehicle is assigned to the Forklift role",
                    HUDController.NotifType.Warning);
                return;
            }

            // Multiplayer: request entry — actual Enter() runs after server confirmation.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
                && _netVehicle != null)
            {
                _pendingActor = actor;
                _netVehicle.RequestEnterServerRpc();
                return;
            }
            Enter(actor);
        }
    }

    // ── Board / Exit ──────────────────────────────────────────────────────────────

    void Enter(GameObject actor)
    {
        _boardedFrame = Time.frameCount;
        _player = actor;
        StopBodyMotion();
        CaptureDriveRotation();

        _fpc = actor.GetComponent<FirstPersonController>();
        if (_fpc != null)
        {
            _playerCtrl = _fpc;
            _fpc.SetMovementEnabled(false);
            _fpc.SetRotationLocked(true);
        }
        else
        {
            _playerCtrl = actor.GetComponent<StarterAssets.ThirdPersonController>();
            if (_playerCtrl != null) _playerCtrl.enabled = false;
        }

        // The driver is parented beneath this dynamic Rigidbody. An enabled character capsule
        // then catches the floor/cab and fights the vehicle solver, which looks like movement
        // input being ignored until the penetration happens to clear.
        _playerCharacterController = actor.GetComponent<CharacterController>();
        _playerCharacterControllerWasEnabled = _playerCharacterController != null
                                             && _playerCharacterController.enabled;
        if (_playerCharacterControllerWasEnabled)
            _playerCharacterController.enabled = false;

        if (_groundOnlyFork)
        {
            // Cart-style: stand and push. Parent directly to the truck root, no seat.
            actor.transform.SetParent(transform);
            actor.transform.localPosition = _driverSeatOffset;
            actor.transform.localRotation = Quaternion.identity;
        }
        else if (_driverSeat != null)
        {
            actor.transform.SetParent(_driverSeat);
            actor.transform.localPosition = _driverSeatOffset;

            if (_steeringWheelBone != null)
            {
                Vector3 toWheel = _steeringWheelBone.position - actor.transform.position;
                toWheel.y = 0f;
                if (toWheel.sqrMagnitude > 0.001f)
                    actor.transform.rotation = Quaternion.LookRotation(toWheel.normalized);
            }
            else
            {
                actor.transform.localRotation = Quaternion.identity;
            }
        }

        var fader = actor.GetComponent<CameraOcclusionFader>();
        if (fader != null) fader.enabled = false;

        var carryLayer = actor.GetComponent<PlayerCarryLayer>();
        if (carryLayer != null) carryLayer.enabled = false;

        _playerAnimator = actor.GetComponent<Animator>();
        if (_playerAnimator != null && _sittingController != null)
        {
            _origAnimController = _playerAnimator.runtimeAnimatorController;
            _sittingOverrideController = BuildCompatibleSittingController(
                _origAnimController, _sittingController);
            if (_sittingOverrideController != null)
                _playerAnimator.runtimeAnimatorController = _sittingOverrideController;
        }

        // Multiplayer: the CharacterVisual animator (sitting-pose swap + PlayerCarryLayer disable,
        // for both the driver's own copy and every observer's) is handled entirely by
        // NetworkVehicleSync.ApplyOccupantIK, driven off the server-authoritative _occupantId
        // NetworkVariable. Swapping it here too raced with that callback — whichever ran second
        // would cache the other's already-swapped controller as "original", so Exit() restored
        // the sitting controller instead of the real one and the animation never came back.

        if (_steerHandRight != null && _steerHandLeft != null)
            PlayerHandIK.Instance?.SetTargets(_steerHandRight, _steerHandLeft);

        if (_footTargetRight != null && _footTargetLeft != null)
            PlayerHandIK.Instance?.SetFootTargets(_footTargetRight, _footTargetLeft);

        _playerCameraRoot = _player.transform.Find("PlayerCameraRoot");
        if (_playerCameraRoot != null)
        {
            if (_groundOnlyFork)
            {
                // Cart-style: camera stays under player (who is parented to truck), just seed direction.
                var ea = _playerCameraRoot.eulerAngles;
                _camYaw   = ea.y;
                _camPitch = ea.x > 180f ? ea.x - 360f : ea.x;
            }
            else
            {
                // Reparent PlayerCameraRoot to CameraAnchor so TPS follow-camera orbits behind the cab.
                _camRootOrigParent   = _playerCameraRoot.parent;
                _camRootOrigLocalPos = _playerCameraRoot.localPosition;
                _camRootOrigLocalRot = _playerCameraRoot.localRotation;
                var anchor = _cameraAnchor != null ? _cameraAnchor : transform;
                _playerCameraRoot.SetParent(anchor, worldPositionStays: false);
                _playerCameraRoot.localPosition = Vector3.zero;
                _camYaw   = transform.eulerAngles.y;
                _camPitch = _truckEnterPitch;
                _playerCameraRoot.rotation = Quaternion.Euler(_camPitch, _camYaw, 0f);
            }
        }

        ApplyVehicleCameraBudget();

        _occupied     = true;
        PlayerOnTruck = true;

        // Penalties only in multiplayer: solo player drives at full competence.
        var role = SpecializationPenalties.GetLocalRole();
        _drivingAsNovice     = !SpecializationPenalties.SoloMode
                               && !SpecializationPenalties.IsOperator(role);
        _effectiveDriveSpeed = _driveSpeed * (_drivingAsNovice ? SpecializationPenalties.Forklift.DriveSpeedMult : 1f);
        _effectiveTurnSpeed  = _turnSpeed  * (_drivingAsNovice ? SpecializationPenalties.Forklift.TurnSpeedMult  : 1f);

        VehicleHUDController.Instance?.Show("Forklift");

        if (_drivingAsNovice)
            HUDController.Instance?.AddNotification(
                "Not your specialty",
                "Controls are unusual — expect drift and inertia",
                HUDController.NotifType.Warning);
        else
            HUDController.Instance?.AddNotification(
                "Forklift", "Boarded",
                HUDController.NotifType.Success);
        // Controls are covered by the persistent bottom-of-screen hint (see ControlsHintText) —
        // no need to repeat them here.
    }

    void Exit()
    {
        GameInputBindings.SuppressUntilReleased(GameBindingAction.Interact);
        StopBodyMotion();

        // Multiplayer: send the final position before returning ownership to the server.
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
            && _netVehicle != null)
        {
            float span = _front1MaxY - _front1MinY;
            float forkT = span > 0f ? (_front1Y - _front1MinY) / span : 0f;
            _netVehicle.RequestExitWithPoseServerRpc(
                transform.position, transform.rotation, Mathf.Clamp01(forkT));
        }

        _player.transform.SetParent(null);
        if (_exitPoint != null)
        {
            _player.transform.position = _exitPoint.position;
            _player.transform.rotation = _exitPoint.rotation;
        }
        else
        {
            _player.transform.position = transform.position + transform.right * 1.5f + Vector3.up * 0.1f;
        }

        // Re-enable collision only after the player is outside the vehicle hierarchy.
        if (_playerCharacterController != null)
            _playerCharacterController.enabled = _playerCharacterControllerWasEnabled;

        // Restore PlayerCameraRoot back under the player before re-enabling controller,
        // so ThirdPersonController's yaw/pitch sync writes to the right object.
        if (_playerCameraRoot != null && _camRootOrigParent != null)
        {
            _playerCameraRoot.SetParent(_camRootOrigParent, worldPositionStays: false);
            _playerCameraRoot.localPosition = _camRootOrigLocalPos;
            _playerCameraRoot.localRotation = _camRootOrigLocalRot;
        }
        RestoreVehicleCameraBudget();

        if (_playerCtrl != null && _playerCameraRoot != null)
        {
            var t = _playerCtrl.GetType();
            t.GetField("_cinemachineTargetYaw",   Private)?.SetValue(_playerCtrl, _camYaw);
            t.GetField("_cinemachineTargetPitch", Private)?.SetValue(_playerCtrl, _camPitch);
        }

        if (_fpc != null)
        {
            _fpc.SetMovementEnabled(true);
            _fpc.SetRotationLocked(false);
        }
        else if (_playerCtrl != null)
            _playerCtrl.enabled = true;

        if (_playerAnimator != null && _origAnimController != null)
            _playerAnimator.runtimeAnimatorController = _origAnimController;
        if (_sittingOverrideController != null)
        {
            Destroy(_sittingOverrideController);
            _sittingOverrideController = null;
        }
        if (_groundOnlyFork && _playerAnimator != null)
            _playerAnimator.SetFloat(SpeedHash, 0f);
        _playerAnimator     = null;
        _origAnimController = null;

        // CharacterVisual restoration (controller + PlayerCarryLayer) happens in
        // NetworkVehicleSync.ApplyOccupantIK(clientId, false) — see the comment in Enter().

        PlayerHandIK.Instance?.ClearAllTargets();

        _playerCameraRoot    = null;
        _camRootOrigParent   = null;
        _camRootOrigLocalPos = Vector3.zero;
        _camRootOrigLocalRot = Quaternion.identity;

        if (_player != null)
        {
            var carryLayer = _player.GetComponent<PlayerCarryLayer>();
            if (carryLayer != null) carryLayer.enabled = true;
            var fader = _player.GetComponent<CameraOcclusionFader>();
            if (fader != null) fader.enabled = true;
        }

        if (_heldPallet != null)
            ReleasePallet();

        HUDController.Instance?.HideControlsHint();

        _occupied            = false;
        PlayerOnTruck        = false;
        _drivingAsNovice     = false;
        _effectiveDriveSpeed = _driveSpeed;
        _effectiveTurnSpeed  = _turnSpeed;
        _player              = null;
        _playerCtrl          = null;
        _fpc                 = null;
        _playerCharacterController = null;
        _playerCharacterControllerWasEnabled = false;
        VehicleHUDController.Instance?.Hide();
    }

    void ApplyVehicleCameraBudget()
    {
        if (_groundOnlyFork || _cameraFarClipOverridden || _vehicleCameraFarClip <= 0f) return;

        _drivingCamera = Camera.main;
        if (_drivingCamera == null) return;

        _originalCameraFarClip = _drivingCamera.farClipPlane;
        _drivingCamera.farClipPlane = Mathf.Min(_originalCameraFarClip, _vehicleCameraFarClip);
        _cameraFarClipOverridden = true;
    }

    void RestoreVehicleCameraBudget()
    {
        if (!_cameraFarClipOverridden) return;

        if (_drivingCamera != null)
            _drivingCamera.farClipPlane = _originalCameraFarClip;
        _drivingCamera = null;
        _cameraFarClipOverridden = false;
    }

    void StopBodyMotion()
    {
        _pendingFwd = 0f;
        _pendingTurn = 0f;
        _smoothedFwd = 0f;
        _smoothedTurn = 0f;
        if (_rb == null || _rb.isKinematic) return;
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.constraints = ParkedConstraints;
    }

    void CaptureDriveRotation()
    {
        if (_rb == null) return;
        _commandedDriveRotation = Quaternion.Euler(0f, _rb.rotation.eulerAngles.y, 0f);
    }

    static Vector2 ReadDriveStick(Gamepad gamepad)
    {
        if (gamepad == null) return Vector2.zero;

        Vector2 value = gamepad.leftStick.ReadValue();
        float magnitude = value.magnitude;
        if (magnitude <= DriveStickDeadzone) return Vector2.zero;

        // Apply our own radial dead zone even when the Input System device layout has no
        // stickDeadzone processor. Rescaling avoids a jump at the edge of the dead zone.
        float scaledMagnitude = Mathf.InverseLerp(DriveStickDeadzone, 1f, Mathf.Min(magnitude, 1f));
        return value.normalized * scaledMagnitude;
    }

    // ── Input ─────────────────────────────────────────────────────────────────────

    // Gamepad right-stick scale: converts [-1,1] to pixel-equivalent delta per frame.
    const float GpadLookScale = 200f;

    void Update()
    {
        if (!_occupied) return;

        var kb = Keyboard.current;
        var gp = Gamepad.current;
        if (kb == null && gp == null) return;

        // Exit: E / pad interact, matching the E-to-grab entry. PlayerInteraction's E-handling is
        // fully skipped while PlayerOnTruck is true, so reusing E here can't double-fire
        // an unrelated interact — except on the boarding frame itself: wasPressedThisFrame
        // stays true for the whole frame regardless of script execution order, so the same
        // E-press that just boarded us would otherwise also read as the exit press right here.
        bool exitPressed = Time.frameCount != _boardedFrame
                        && GameInputBindings.WasPressed(GameBindingAction.Interact);
        if (exitPressed) { Exit(); return; }

        // Persistent on-screen controls reminder, shown in its own dedicated HUD element
        // (separate from the look-at interact prompt) for as long as we're occupied.
        // Exit() hides it again.
        HUDController.Instance?.ShowControlsHint(ControlsHintText);

        // Movement: WASD + left stick
        float turn = 0f, fwd = 0f;
        if (kb != null)
        {
            turn += (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            fwd  += (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        }
        if (gp != null)
        {
            Vector2 stick = ReadDriveStick(gp);
            turn = Mathf.Clamp(turn + stick.x, -1f, 1f);
            fwd  = Mathf.Clamp(fwd  + stick.y, -1f, 1f);
        }
        _pendingTurn = turn;
        _pendingFwd  = fwd;

        // Walk animation while pushing in ground-only (pallet truck) mode.
        // TPC is disabled but animator is still active; drive Speed manually like CartController does.
        if (_groundOnlyFork && _playerAnimator != null)
            _playerAnimator.SetFloat(SpeedHash, Mathf.Abs(fwd) > 0.01f ? 2f : 0f, 0.1f, Time.deltaTime);

        // Fork controls: Q/Z keys + RB (lift up) / LB (lower down)
        bool liftUp   = GameInputBindings.IsPressed(GameBindingAction.LiftUp);
        bool liftDown = GameInputBindings.IsPressed(GameBindingAction.LiftDown);

        if (_forkJammed)
        {
            // Both lift controls are locked — fork is fully stuck.
            if ((liftUp || liftDown) && Time.time > _lastForkWarnTime + 3f)
            {
                _lastForkWarnTime = Time.time;
                HUDController.Instance?.AddNotification(
                    "Fork is jammed",
                    "Hydraulics broken — find the yellow repair kit",
                    HUDController.NotifType.Danger);
            }
        }
        else
        {
            if (liftUp)
            {
                if (!_groundOnlyFork)
                {
                    LiftStep(_liftSpeed * Time.deltaTime);
                    SyncForkT(); // Multiplayer: broadcast new fork height
                }
                // Pallet grab always works regardless of groundOnlyFork mode.
                if (_heldPallet == null)
                {
                    var found = ScanForPallet();
                    if (found != null)
                    {
                        _heldPallet = found;
                        // Solo and the forklift specialist get stronger cargo control. Other
                        // multiplayer roles retain the current, less forgiving stabilization.
                        _heldPallet.SetCargoStabilization(
                            SpecializationPenalties.SoloMode || !_drivingAsNovice ? 2f : 1f);
                        _heldPallet.Attach(_forkMount);
                        IgnorePalletTruckCollision(_heldPallet, true);
                    }
                }
            }
            else if (liftDown)
            {
                if (!_groundOnlyFork)
                {
                    // Attached pallets are moved kinematically and therefore cannot be stopped
                    // by a rack collider. Release as soon as a horizontal shelf/floor surface is
                    // directly below the pallet, before the next lowering step passes through it.
                    if (_heldPallet != null
                        && _heldPallet.TryGetSupportBelow(0.16f, transform, out float supportTopY))
                    {
                        _heldPallet.AlignBottomToSupport(supportTopY);
                        ReleasePallet();
                    }
                    else
                    {
                        LiftStep(-_liftSpeed * Time.deltaTime);
                        SyncForkT(); // Multiplayer: broadcast new fork height
                    }
                }
                else if (_heldPallet != null)
                {
                    ReleasePallet(); // Z/LB drops the pallet on the pallet truck
                }
            }
        }

        // Fallback release at the absolute bottom only while the player is actively lowering.
        // The old !liftUp condition released a newly grabbed pallet as soon as Q was released:
        // one short lift step is smaller than the 0.001 tolerance, so the pickup appeared not to
        // register. Normal floor/shelf placement is handled by TryGetSupportBelow above.
        if (_heldPallet != null && liftDown && !_groundOnlyFork
            && _front1Y <= _front1MinY + 0.0001f)
            ReleasePallet();

        // Camera look: rotate PlayerCameraRoot (which is parented to CameraAnchor while driving).
        // Cinemachine3rdPersonFollow on PlayerFollowCamera orbits around it automatically.
        if (_playerCameraRoot != null && Cursor.lockState == CursorLockMode.Locked)
        {
            float dx = 0f, dy = 0f;
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var d = mouse.delta.ReadValue();
                dx += d.x; dy += d.y;
            }
            if (gp != null)
            {
                var look = gp.rightStick.ReadValue();
                dx += look.x * GpadLookScale * Time.deltaTime;
                dy += look.y * GpadLookScale * Time.deltaTime;
            }
            _camYaw   += dx * _camSensitivity * GameSettings.LookSensitivity;
            _camPitch -= GameSettings.VerticalLook(dy) * _camSensitivity;
            _camPitch  = Mathf.Clamp(_camPitch, _camPitchMin, _camPitchMax);
            _playerCameraRoot.rotation = Quaternion.Euler(_camPitch, _camYaw, 0f);
        }

        float span = _front1MaxY - _front1MinY;
        float norm = span > 0f ? (_front1Y - _front1MinY) / span : 0f;
        VehicleHUDController.Instance?.UpdateForkHeight(norm * 1.2f);
    }

    void FixedUpdate()
    {
        if (!_occupied || _rb == null)
        {
            _pendingTurn = 0f;
            _pendingFwd = 0f;
            _smoothedTurn = 0f;
            _smoothedFwd = 0f;
            return;
        }

        // Recompute speeds each physics frame so BrokenForkliftEvent takes effect immediately.
        float activeDrive = _effectiveDriveSpeed * _damageMult;
        float activeTurn  = _effectiveTurnSpeed  * _damageMult;

        // A very short input ramp removes the single-frame velocity/rotation impact without
        // bringing back the long, noticeably progressive acceleration the truck had before.
        const float InputResponse = 6f;
        _smoothedFwd = Mathf.MoveTowards(
            _smoothedFwd, _pendingFwd, InputResponse * Time.fixedDeltaTime);
        _smoothedTurn = Mathf.MoveTowards(
            _smoothedTurn, _pendingTurn, InputResponse * Time.fixedDeltaTime);

        // Finish the short input ramp exactly at zero. Leaving a tiny remainder keeps the body
        // awake and lets contact impulses from the cargo slowly move the parked truck.
        if (Mathf.Abs(_pendingFwd) < 0.001f && Mathf.Abs(_smoothedFwd) < 0.02f)
            _smoothedFwd = 0f;
        if (Mathf.Abs(_pendingTurn) < 0.001f && Mathf.Abs(_smoothedTurn) < 0.02f)
            _smoothedTurn = 0f;

        bool hasDriveCommand = Mathf.Abs(_smoothedFwd) >= 0.001f
                            || Mathf.Abs(_smoothedTurn) >= 0.001f;
        if (!_rb.isKinematic)
            _rb.constraints = hasDriveCommand ? DrivingConstraints : ParkedConstraints;

        // Steering is player-controlled, not torque-driven. MoveRotation on a dynamic body can
        // leave angular velocity behind after a long turn (especially while touching shelving or
        // the floor). That residual rotation makes the next forward velocity fight the contact
        // solver and the truck appears to get progressively slower. Clear it every physics step.
        if (!_rb.isKinematic)
            _rb.angularVelocity = Vector3.zero;

        if (Mathf.Abs(_smoothedTurn) >= 0.001f)
        {
            _commandedDriveRotation *= Quaternion.Euler(
                0f, _smoothedTurn * activeTurn * Time.fixedDeltaTime, 0f);
        }

        // Only steering input is allowed to change the commanded heading. If a contact impulse
        // rotates the dynamic body slightly, using _rb.rotation here would adopt that error as
        // the new forward direction and the truck would keep veering with no A/D input.
        _commandedDriveRotation = Quaternion.Euler(
            0f, _commandedDriveRotation.eulerAngles.y, 0f);
        _rb.MoveRotation(_commandedDriveRotation);

        if (Mathf.Abs(_smoothedFwd) >= 0.001f)
        {
            // Use the rotation selected for THIS physics frame. transform.forward still contains
            // the previous frame until MoveRotation is resolved, which used to create sideways
            // velocity whenever steering and driving overlapped.
            Vector3 driveForward = _commandedDriveRotation * Vector3.forward;
            Vector3 targetVelocity = driveForward * (_smoothedFwd * activeDrive);
            if (_rb.isKinematic)
                _rb.MovePosition(_rb.position + targetVelocity * Time.fixedDeltaTime);
            else
            {
                _rb.WakeUp();
                _rb.linearVelocity = targetVelocity;
            }
        }
        else if (!_rb.isKinematic)
        {
            _rb.linearVelocity = Vector3.zero;
            if (!hasDriveCommand)
                _rb.Sleep();
        }

        // Drift: novice always drifts; BrokenForkliftEvent adds drift for everyone.
        // AddForce is a no-op on kinematic rigidbodies — skip for pallet truck.
        float driftScale = (_drivingAsNovice ? 1f : 0f) + _extraDrift;
        if (driftScale > 0f && _smoothedFwd != 0f && !_rb.isKinematic)
        {
            float t     = Time.time * SpecializationPenalties.Forklift.DriftFrequency;
            float drift = (Mathf.PerlinNoise(t, 0f) * 2f - 1f)
                        * SpecializationPenalties.Forklift.DriftForce * driftScale;
            _rb.AddForce(transform.right * drift, ForceMode.Force);
        }

        // Submit the locally predicted result. NetworkVehicleSync validates it on the server and
        // only then republishes it to observers.
        if (_netVehicle != null && Time.time - _lastNetUpdate > 1f / 30f)
        {
            _lastNetUpdate = Time.time;
            _netVehicle.SetTransform(transform.position, transform.rotation);
        }
    }

    void OnCollisionEnter(Collision collision) => LogBlockingCollision(collision);

    void LogBlockingCollision(Collision collision)
    {
        if (!_debugBlockingCollisions || !_occupied || collision == null
            || collision.contactCount == 0)
            return;

        float forwardInput = Mathf.Abs(_smoothedFwd) >= 0.001f
            ? _smoothedFwd
            : _pendingFwd;
        bool tryingToMove = Mathf.Abs(forwardInput) >= 0.1f
                         || Mathf.Abs(_smoothedTurn) >= 0.1f
                         || Mathf.Abs(_pendingTurn) >= 0.1f;
        if (!tryingToMove) return;

        Vector3 desiredDirection = _commandedDriveRotation * Vector3.forward
                                 * (forwardInput < 0f ? -1f : 1f);
        ContactPoint bestContact = collision.GetContact(0);
        float bestOpposition = float.NegativeInfinity;
        bool foundNonFloorContact = false;

        for (int i = 0; i < collision.contactCount; i++)
        {
            ContactPoint contact = collision.GetContact(i);
            // Floor contacts are expected and are not useful for finding an invisible wall.
            if (contact.normal.y > 0.65f) continue;

            foundNonFloorContact = true;
            float opposition = Mathf.Abs(forwardInput) >= 0.1f
                ? Vector3.Dot(desiredDirection, -contact.normal)
                : 0f;
            if (opposition > bestOpposition)
            {
                bestOpposition = opposition;
                bestContact = contact;
            }
        }

        if (!foundNonFloorContact) return;

        Collider obstacle = bestContact.otherCollider;
        Collider truckCollider = bestContact.thisCollider;
        // Be defensive about ContactPoint ownership on compound Rigidbody hierarchies.
        if (obstacle != null && obstacle.transform.IsChildOf(transform))
        {
            (obstacle, truckCollider) = (truckCollider, obstacle);
        }
        if (obstacle == null) obstacle = collision.collider;
        if (obstacle == null || obstacle.transform.IsChildOf(transform)) return;

        if (obstacle == _lastBlockingCollider && Time.unscaledTime < _nextCollisionDebugTime)
            return;

        _lastBlockingCollider = obstacle;
        _nextCollisionDebugTime = Time.unscaledTime + _collisionDebugInterval;

        Renderer obstacleRenderer = obstacle.GetComponentInParent<Renderer>();
        if (obstacleRenderer == null)
            obstacleRenderer = obstacle.GetComponentInChildren<Renderer>(true);

        Bounds bounds = obstacle.bounds;
        string layerName = LayerMask.LayerToName(obstacle.gameObject.layer);
        string path = HierarchyPath(obstacle.transform);
        string rendererState = obstacleRenderer == null
            ? "none (invisible)"
            : $"{obstacleRenderer.GetType().Name}, enabled={obstacleRenderer.enabled}";

        Debug.LogWarning(
            $"[Forklift Collision] BLOCKED BY '{path}' | "
          + $"layer={obstacle.gameObject.layer} ({layerName}), tag={obstacle.tag} | "
          + $"obstacle={obstacle.GetType().Name}, enabled={obstacle.enabled}, "
          + $"trigger={obstacle.isTrigger}, center={bounds.center:F2}, size={bounds.size:F2} | "
          + $"truckCollider={(truckCollider != null ? truckCollider.name : "unknown")} | "
          + $"contact={bestContact.point:F2}, normal={bestContact.normal:F2}, "
          + $"opposition={bestOpposition:F2} | renderer={rendererState}",
            obstacle);
    }

    static string HierarchyPath(Transform target)
    {
        if (target == null) return "<missing>";
        string path = target.name;
        for (Transform parent = target.parent; parent != null; parent = parent.parent)
            path = parent.name + "/" + path;
        return path;
    }

    void LateUpdate()
    {
        // Multiplayer: apply remote fork position for non-driving clients so the fork
        // bone animation plays correctly when another player is using the truck.
        if (!_occupied && _netVehicle != null && _netVehicle.IsOccupied
            && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            float t    = _netVehicle.ForkT;
            float span = _front1MaxY - _front1MinY;
            _front1Y   = _front1MinY + t * span;
            float normT = span > 0f ? (_front1Y - _front1MinY) / span : 0f;
            _front2Y   = Mathf.Lerp(_front2MinY, _front2MaxY, normT);
        }

        // Keep the complete lift assembly in the truck hierarchy. The old implementation wrote
        // Front_01.position in world space every LateUpdate while the truck root was being moved
        // by Rigidbody in FixedUpdate. Under interpolation that let the visual mast lag behind the
        // body and appear to detach. Convert the metre-based correction to the bone parent's local
        // space instead, so every fork transform remains rigidly attached to the vehicle root.
        if (_front1 != null)
        {
            Vector3 p = _front1BaseLocalPosition;
            p.y = _front1Y;
            if (!Mathf.Approximately(_forkBaseHeightOffset, 0f) && _front1.parent != null)
                p += _front1.parent.InverseTransformVector(Vector3.up * _forkBaseHeightOffset);
            _front1.localPosition = p;
        }

        if (_front2 != null)
        {
            Vector3 p = _front2BaseLocalPosition;
            p.y = _front2Y;
            _front2.localPosition = p;
        }

        // Keep forkMount world-Y in sync with the fork tines so the pickup zone and
        // SyncToFork both operate at the correct height as the fork lifts/lowers.
        // Prefer _forkTip (a dedicated empty at the actual fork tip) over _front1.
        // _forkHeightScale compensates for rig amplification — increase it if the fork
        // visual moves faster than the pallet, decrease if the pallet moves too fast.
        if (_forkMount != null && _front1 != null)
        {
            // Front_02 is a child of Front_01 — it carries the actual fork prongs and
            // accumulates both stages of the scissor lift, giving the correct world height.
            var trackBone = (_forkTip != null ? _forkTip : (_front2 != null ? _front2 : _front1));

            // Record the world Y of the tracking bone at minimum fork position (first frame).
            if (!_forkBaseRecorded)
            {
                _forkBaseWorldY   = trackBone.position.y;
                _forkBaseRecorded = true;
            }

            float desiredWorldY = _forkBaseWorldY
                                + (trackBone.position.y - _forkBaseWorldY) * _forkHeightScale;

            // Rebuild from the authored local pose on every frame. Only the vertical tracking
            // delta is converted from world metres; X/Z stay local to the mast and therefore
            // cannot remain behind when the Rigidbody moves or turns the truck.
            Vector3 local = _forkMountBaseLocalPosition;
            if (_forkMount.parent != null)
            {
                float baseWorldY = _forkMount.parent.TransformPoint(local).y;
                local += _forkMount.parent.InverseTransformVector(
                    Vector3.up * (desiredWorldY - baseWorldY));
                _forkMount.localPosition = local;
            }
        }

        if (_heldPallet != null && !_heldPallet.IsLifted)
        {
            IgnorePalletTruckCollision(_heldPallet, false);
            _heldPallet = null;
        }
        _heldPallet?.SyncToFork(_forkMount);

        if (_steeringWheelBone != null)
        {
            float steer = 0f;
            if (_occupied)
            {
                var kb = Keyboard.current;
                var gp = Gamepad.current;
                if (kb != null)
                    steer += (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
                if (gp != null)
                    steer = Mathf.Clamp(steer + ReadDriveStick(gp).x, -1f, 1f);
            }
            float angle = steer * _maxSteerAngle * _steerDirection;
            _steeringWheelBone.localRotation = _steerNeutral * Quaternion.AngleAxis(angle, Vector3.up);
        }
    }


    // ── Pallet helpers ────────────────────────────────────────────────────────────

    // Release the held pallet and schedule collision restoration after a delay
    // so depenetration forces don't launch the pallet while the truck is still nearby.
    void ReleasePallet()
    {
        if (_heldPallet == null) return;
        var pallet = _heldPallet;
        _heldPallet = null;
        pallet.Release();
        StartCoroutine(RestoreCollisionAfterDelay(pallet, 1.5f));
    }

    void IgnorePalletTruckCollision(PalletBehavior pallet, bool ignore)
    {
        // Pallet truck never needs physical contact with pallets — keep ignoring even after drop.
        if (_groundOnlyFork && !ignore) return;
        var palletCols = pallet.GetComponentsInChildren<Collider>(true);
        foreach (var tc in _truckColliders)
            foreach (var pc in palletCols)
            {
                // Cargo becomes independent physics shortly after pickup. Ignoring it here used
                // to persist after detachment, letting the complete truck pass through boxes.
                if (pc != null && pc.GetComponentInParent<BoxPhysicsInteractable>() != null)
                    continue;
                Physics.IgnoreCollision(tc, pc, ignore);
            }
    }

    System.Collections.IEnumerator RestoreCollisionAfterDelay(PalletBehavior pallet, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (pallet != null) IgnorePalletTruckCollision(pallet, false);
    }

    // ── Fork ──────────────────────────────────────────────────────────────────────

    void LiftStep(float delta)
    {
        // World-space height guard — checked against previous frame's forkMount position.
        // One frame of latency is acceptable; the bone clamp below is the hard stop.
        if (_forkMount != null)
        {
            float mountY = _forkMount.position.y;
            if (delta > 0f && mountY >= _forkMaxWorldY)
            {
                NotifyForkLimit(atTop: true);
                return;
            }
            if (delta < 0f && mountY <= _forkMinWorldY)
            {
                NotifyForkLimit(atTop: false);
                return;
            }
        }

        float prevY = _front1Y;
        _front1Y = Mathf.Clamp(_front1Y + delta, _front1MinY, _front1MaxY);
        float t  = (_front1MaxY > _front1MinY)
            ? (_front1Y - _front1MinY) / (_front1MaxY - _front1MinY)
            : 0f;
        _front2Y = Mathf.Lerp(_front2MinY, _front2MaxY, t);

        // Forklift rescue: when lifting (delta > 0) and fork crosses 40% of travel,
        // check if there's a crushed player within reach of the truck.
        if (delta > 0f && prevY < _front1MinY + (_front1MaxY - _front1MinY) * RescueForkThreshold
                       && _front1Y >= _front1MinY + (_front1MaxY - _front1MinY) * RescueForkThreshold)
        {
            CrushedPlayerMarker nearestMarker = null;
            float nearestDist = RescueRange;
            foreach (var marker in CrushedPlayerMarker.AllMarkers)
            {
                float dist = Vector3.Distance(transform.position, marker.transform.position);
                if (dist <= nearestDist) { nearestDist = dist; nearestMarker = marker; }
            }
            if (nearestMarker != null)
            {
                if (nearestMarker.OwnerClientId.HasValue)
                    NetworkPlayer.Local?.RequestRescueServerRpc(nearestMarker.OwnerClientId.Value, true);
                else
                    PlayerDeathManager.Instance?.RescueByForklift(transform.position);
            }
        }
    }

    void NotifyForkLimit(bool atTop)
    {
        if (Time.time < _lastLimitNotifyTime + 3f) return;
        _lastLimitNotifyTime = Time.time;
        string msg = atTop ? "Fork raised to maximum" : "Fork lowered to minimum";
        HUDController.Instance?.AddNotification("Fork", msg, HUDController.NotifType.Warning);
    }

    // Broadcast current normalized fork height to remote clients via NetworkVehicleSync.
    void SyncForkT()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening) return;
        if (_netVehicle == null) return;
        float span = _front1MaxY - _front1MinY;
        float t    = span > 0f ? (_front1Y - _front1MinY) / span : 0f;
        _netVehicle.SetForkT(t);
    }

    // ── Pallet Detection ──────────────────────────────────────────────────────────

    // Proximity-based scan in all three axes. The Y check is essential when pallets are
    // stacked on several rack tiers at the same XZ position.
    PalletBehavior ScanForPallet()
    {
        if (_forkMount == null) return null;

        var col = _forkMount.GetComponent<BoxCollider>();
        Vector3 forkCenter = col != null
            ? _forkMount.TransformPoint(col.center)
            : _forkMount.position;

        PalletBehavior best     = null;
        float          bestDist = float.MaxValue;

        foreach (var pb in PalletBehavior.All)
        {
            if (pb.IsLifted || pb.GrabCollider == null) continue;

            Bounds b = pb.GrabCollider.bounds;

            // Fork must be within the pallet footprint and close to its floor height.
            float dx = Mathf.Abs(forkCenter.x - b.center.x);
            float dy = Mathf.Abs(forkCenter.y - pb.GrabY);
            float dz = Mathf.Abs(forkCenter.z - b.center.z);
            if (dx > b.extents.x + _forkScanSize.x * 0.5f) continue;
            if (dy > _forkGrabHeightTolerance) continue;
            if (dz > b.extents.z + _forkScanSize.z * 0.5f) continue;

            float dist = dx * dx + dy * dy + dz * dz;
            if (dist < bestDist) { bestDist = dist; best = pb; }
        }
        return best;
    }

    // ── Gizmo ─────────────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        if (_forkMount == null) return;

        // Fork collider bounds
        var col = _forkMount.GetComponent<BoxCollider>();
        if (col != null)
        {
            var b = col.bounds;
            Gizmos.color  = new Color(0f, 1f, 1f, 0.3f);
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.DrawWireCube(b.center, b.size);
        }

        // Fork tine center (what ScanForPallet uses for XZ/Y check)
        Vector3 forkCenter = col != null
            ? _forkMount.TransformPoint(col.center)
            : _forkMount.position;
        Gizmos.color = _heldPallet != null ? Color.green : Color.yellow;
        Gizmos.DrawSphere(forkCenter, 0.06f);
    }

}
