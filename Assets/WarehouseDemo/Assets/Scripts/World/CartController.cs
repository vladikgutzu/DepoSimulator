using UnityEngine;
using UnityEngine.InputSystem;
using System.Reflection;
using Unity.Netcode;

// Attach to Cart root.
// E = grab handle, E = release.
// WASD = push/steer cart. Player is parented to the cart while pushing.
// Hands IK to the two handle targets via PlayerHandIK.
// Works with both FirstPersonController and StarterAssets.ThirdPersonController.
//
// Multiplayer: RequestEnterServerRpc / RequestExitServerRpc via NetworkVehicleSync.
// Single-player: all network code is gated on NetworkManager.Singleton?.IsListening.
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
public class CartController : MonoBehaviour, IInteractable, ICanInteractCheck, INoOutlineHighlight
{
    [Header("IK Targets (children of Handle bone)")]
    [SerializeField] Transform _rightHandTarget;
    [SerializeField] Transform _leftHandTarget;

    [Header("Scene Points (assign or run Setup tool)")]
    [SerializeField] Transform _playerAnchor;  // where player stands while pushing
    [SerializeField] Transform _exitPoint;     // where player goes on release

    [Header("Drive")]
    [SerializeField] float _driveSpeed  = 2f;
    [SerializeField] float _sprintSpeed = 4f;
    [SerializeField] float _turnSpeed   = 55f;

    [Header("Camera (while pushing)")]
    [SerializeField] float _camSensitivity = 1f;
    [SerializeField] float _camPitchMin    = -60f;
    [SerializeField] float _camPitchMax    =  60f;

    Rigidbody             _rb;
    Collider              _bodyCollider;
    bool                  _occupied;
    // Frame Grab() ran on — E's wasPressedThisFrame stays true for the whole Unity frame
    // regardless of script execution order, so without this guard, if this script's own
    // Update() happens to run AFTER PlayerInteraction's in the same frame, it would see
    // _occupied already true and the same E-press as "still pressed", firing Release()
    // immediately on entry.
    int                   _grabbedFrame = -1;
    GameObject            _player;
    Behaviour             _playerCtrl;
    FirstPersonController _fpc;
    CharacterController   _playerCharacterController;
    bool                  _playerCharacterControllerWasEnabled;
    Animator              _playerAnimator;

    Transform _playerCameraRoot;
    float     _camYaw;
    float     _camPitch;

    // Multiplayer: cached vehicle sync component and pending actor for async confirmation.
    NetworkVehicleSync _netVehicle;
    GameObject         _pendingActor;

    // Throttle SetTransform to ~20 Hz (same rate as box/pallet/ladder position sync) instead of
    // every FixedUpdate (50 Hz @ 0.02 fixed timestep) — NetworkVariable writes past the 30 Hz
    // tick rate are just redundant dirty-checks, not extra packets, but still wasted CPU.
    float _lastNetUpdate;

    static readonly BindingFlags Private  = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly int          SpeedHash = Animator.StringToHash("Speed");

    public static bool PlayerOnCart { get; private set; }

    // Exposed for NetworkVehicleSync.ApplyOccupantIK so remote players get hand IK.
    public Transform RightHandTarget => _rightHandTarget;
    public Transform LeftHandTarget  => _leftHandTarget;

    // ── IInteractable ─────────────────────────────────────────────────────────────

    public string InteractPrompt
    {
        get
        {
            if (_occupied) return $"Release cart {InputHints.E}";
            if (_netVehicle != null && _netVehicle.IsOccupied) return "In use";
            return $"{InputHints.EK} Push cart";
        }
    }

    public bool CanInteract(GameObject interactor)
    {
        if (_netVehicle != null && _netVehicle.IsOccupied) return false;
        return !_occupied;
    }

    public void Interact(GameObject actor)
    {
        if (_occupied) return;

        // Multiplayer: request entry — actual Grab() runs after server confirmation.
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
            && _netVehicle != null)
        {
            _pendingActor = actor;
            _netVehicle.RequestEnterServerRpc();
            return;
        }

        Grab(actor);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────────

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _bodyCollider = GetComponent<Collider>();
        _rb.isKinematic     = true;
        _rb.useGravity      = false;
        _rb.interpolation   = RigidbodyInterpolation.Interpolate;
    }

    void Start()
    {
        _netVehicle = GetComponent<NetworkVehicleSync>();
        if (_netVehicle != null)
            _netVehicle.OnLocalEnterConfirmed += OnEnterConfirmed;
    }

    void OnDestroy()
    {
        if (_netVehicle != null)
            _netVehicle.OnLocalEnterConfirmed -= OnEnterConfirmed;

        // Static, and only Exit() clears it: a cart unloaded with the player still on it would
        // leave every interaction blocked in the next shift. Same as ReachTruckController.
        if (_occupied) PlayerOnCart = false;
    }

    /// Statics outlive a play session in the editor (domain reload is off in this project).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => PlayerOnCart = false;

    // Fired by NetworkVehicleSync when the server grants entry to this client.
    void OnEnterConfirmed(GameObject actor)
    {
        if (_pendingActor == null) return;
        var a = _pendingActor;
        _pendingActor = null;
        Grab(a);
    }

    // ── Grab / Release ────────────────────────────────────────────────────────────

    void Grab(GameObject actor)
    {
        _grabbedFrame = Time.frameCount;
        _player = actor;

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

        // The player becomes a child of this Rigidbody while pushing. Leaving the
        // CharacterController enabled makes its capsule participate in the vehicle's movement:
        // it catches the floor, the cart body and carried cargo, producing the apparent delayed
        // input where the cart only advances after PhysX eventually depenetrates it.
        _playerCharacterController = actor.GetComponent<CharacterController>();
        _playerCharacterControllerWasEnabled = _playerCharacterController != null
                                             && _playerCharacterController.enabled;
        if (_playerCharacterControllerWasEnabled)
            _playerCharacterController.enabled = false;

        if (_playerAnchor != null)
        {
            actor.transform.SetParent(_playerAnchor);
            actor.transform.localPosition = Vector3.zero;
            actor.transform.localRotation = Quaternion.identity;
        }

        // Capture camera root and seed yaw/pitch from current rotation so camera
        // doesn't snap when the player boards the cart.
        _playerCameraRoot = actor.transform.Find("PlayerCameraRoot");
        if (_playerCameraRoot != null)
        {
            var ea  = _playerCameraRoot.eulerAngles;
            _camYaw   = ea.y;
            _camPitch = ea.x > 180f ? ea.x - 360f : ea.x;
        }

        _playerAnimator = actor.GetComponentInChildren<Animator>();

        PlayerHandIK.Instance?.SetTargets(_rightHandTarget, _leftHandTarget);

        _occupied    = true;
        PlayerOnCart = true;
        // Controls are covered by the persistent bottom-of-screen hint (see ShowControlsHint
        // in Update()) — no need to repeat them here.
        HUDController.Instance?.AddNotification(
            "Cart", "Grabbed",
            HUDController.NotifType.Success);
    }

    void Release()
    {
        if (!_occupied || _player == null) return;

        GameInputBindings.SuppressUntilReleased(GameBindingAction.Interact);

        // Multiplayer: return vehicle ownership to server before doing local cleanup.
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
            && _netVehicle != null)
            _netVehicle.RequestExitServerRpc();

        PlayerHandIK.Instance?.ClearTargets();

        _player.transform.SetParent(null);
        if (_exitPoint != null)
        {
            _player.transform.position = _exitPoint.position;
            _player.transform.rotation = _exitPoint.rotation;
        }
        else
        {
            _player.transform.position = transform.position + transform.right * 1f + Vector3.up * 0.05f;
        }

        // Restore the capsule only after the player is detached and placed outside the cart.
        if (_playerCharacterController != null)
            _playerCharacterController.enabled = _playerCharacterControllerWasEnabled;

        // Sync TPC's internal yaw/pitch so camera doesn't snap on dismount.
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
        {
            _playerCtrl.enabled = true;
        }

        _playerAnimator?.SetFloat(SpeedHash, 0f);

        HUDController.Instance?.HideControlsHint();

        _occupied         = false;
        PlayerOnCart      = false;
        _player           = null;
        _playerCtrl       = null;
        _fpc              = null;
        _playerCharacterController = null;
        _playerCharacterControllerWasEnabled = false;
        _playerCameraRoot = null;
        _playerAnimator   = null;
    }

    // ── Input ─────────────────────────────────────────────────────────────────────

    // Gamepad right-stick scale: converts [-1,1] to pixel-equivalent delta per frame.
    const float GpadLookScale = 200f;

    float _fwdInput;
    float _turnInput;
    bool  _sprinting;

    void Update()
    {
        if (!_occupied) return;

        var kb = Keyboard.current;
        var gp = Gamepad.current;
        if (kb == null && gp == null) return;

        // Exit: E / pad interact, matching the E-to-grab entry. PlayerInteraction's E-handling is
        // fully skipped while PlayerOnCart is true, so reusing E here can't double-fire
        // an unrelated interact — except on the grab frame itself: wasPressedThisFrame stays
        // true for the whole frame regardless of script execution order, so the same E-press
        // that just grabbed the cart would otherwise also read as the release press right here.
        bool exitPressed = Time.frameCount != _grabbedFrame
                        && GameInputBindings.WasPressed(GameBindingAction.Interact);
        if (exitPressed) { Release(); return; }

        // Persistent on-screen controls reminder, shown in its own dedicated HUD element
        // (separate from the look-at interact prompt) for as long as we're occupied.
        // Release() hides it again.
        HUDController.Instance?.ShowControlsHint(
            $"W/S — push   A/D — turn   {InputHints.Active(GameBindingAction.Sprint)} — sprint   {InputHints.EK} release");

        // Read movement input — applied in FixedUpdate.
        _turnInput = 0f; _fwdInput = 0f;
        _sprinting = GameInputBindings.IsPressed(GameBindingAction.Sprint);
        if (kb != null)
        {
            _turnInput += (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            _fwdInput  += (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        }
        if (gp != null)
        {
            var stick = gp.leftStick.ReadValue();
            _turnInput = Mathf.Clamp(_turnInput + stick.x, -1f, 1f);
            _fwdInput  = Mathf.Clamp(_fwdInput  + stick.y, -1f, 1f);
        }

        // Drive leg animation: walk blend-tree threshold = 2, sprint = 6.
        float animSpeed = Mathf.Abs(_fwdInput) > 0.01f ? (_sprinting ? 6f : 2f) : 0f;
        _playerAnimator?.SetFloat(SpeedHash, animSpeed, 0.1f, Time.deltaTime);

        // Camera: mouse delta + right stick
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
    }

    void FixedUpdate()
    {
        if (!_occupied) return;

        float currentSpeed = _sprinting ? _sprintSpeed : _driveSpeed;
        Quaternion newRot = _rb.rotation * Quaternion.Euler(0f, _turnInput * _turnSpeed * Time.fixedDeltaTime, 0f);
        Vector3 delta = transform.forward * (_fwdInput * currentSpeed * Time.fixedDeltaTime);

        // A kinematic Rigidbody is not stopped by static geometry when MovePosition is used.
        // Sweep its real collider first so pushing the cart cannot carry the player through walls,
        // shelving or tables. Leave a small skin to avoid ending the frame already interpenetrating.
        const float collisionSkin = 0.025f;
        if (_bodyCollider != null && delta.sqrMagnitude > 0.000001f)
        {
            float allowed = delta.magnitude;
            RaycastHit[] hits = _rb.SweepTestAll(delta.normalized,
                delta.magnitude + collisionSkin, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                // Cargo follows this cart independently in LateUpdate and deliberately keeps
                // solid colliders so it can still be selected. It must not block the sweep of
                // the vehicle carrying it.
                var box = hit.collider != null
                    ? hit.collider.GetComponentInParent<BoxPhysicsInteractable>()
                    : null;
                if (box != null && box.StoredInCart != null
                                && box.StoredInCart.transform == transform)
                    continue;

                allowed = Mathf.Min(allowed, Mathf.Max(0f, hit.distance - collisionSkin));
            }
            delta = delta.normalized * allowed;
        }

        Vector3 newPos = _rb.position + delta;
        _rb.MoveRotation(newRot);
        _rb.MovePosition(newPos);

        // Push transform to all clients so non-owner copies follow the driver — throttled to
        // ~20 Hz, matching box/pallet/ladder position sync (see _lastNetUpdate).
        if (_netVehicle != null && Time.time - _lastNetUpdate > 0.05f)
        {
            _lastNetUpdate = Time.time;
            _netVehicle.SetTransform(newPos, newRot);
        }
    }

    void OnDisable() { if (_occupied) Release(); }
}
