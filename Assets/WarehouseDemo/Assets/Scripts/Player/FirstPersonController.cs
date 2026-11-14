using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float _walkSpeed              = 5f;
    [SerializeField] private float _sprintSpeed            = 9f;
    [SerializeField] private float _crouchSpeed            = 3f;
    [SerializeField] private float _heavyCarryMultiplier   = 0.55f;
    [SerializeField] private float _ladderCarryMultiplier  = 0.8f;
    [SerializeField] private float _mouseSensitivity       = 0.15f;
    [SerializeField] private float _gravity                = -18f;

    [Header("Crouch")]
    [SerializeField] private float _standHeight    = 2.0f;
    [SerializeField] private float _crouchHeight   = 1.0f;
    [SerializeField] private float _crouchLerpSpeed = 10f;

    // Movement state — read by HeadBob, FOVController, FootstepSystem
    public bool  IsMoving    { get; private set; }
    public bool  IsSprinting { get; private set; }
    public bool  IsCrouching { get; private set; }
    public float CurrentSpeed { get; private set; }
    // Vertical camera angle — read by TPSCameraController
    public float Pitch => _pitch;

    private CharacterController _cc;
    private Camera   _cam;
    private Animator _anim;
    private float  _pitch;
    private float  _camYaw;
    private float  _yVelocity;
    private bool   _movementEnabled  = true;
    private bool   _rotationLocked   = false;

    // StarterAssetsThirdPerson blend-tree thresholds: 0=idle, 2=walk, 6=run.
    const float AnimWalkSpeed   = 2f;
    const float AnimSprintSpeed = 6f;

    static readonly int _animSpeed       = Animator.StringToHash("Speed");
    static readonly int _animMotionSpeed = Animator.StringToHash("MotionSpeed");
    static readonly int _animGrounded    = Animator.StringToHash("Grounded");

    private void Awake()
    {
        _cc   = GetComponent<CharacterController>();
        _cam  = GetComponentInChildren<Camera>();
        _anim = GetComponentInChildren<Animator>();
    }

    private void Start()
    {
        _cc.height = _standHeight;
        _cc.center = new Vector3(0f, _standHeight * 0.5f, 0f);
        if (!UIManager.IsAnyPanelOpen) SetCursor(true);
    }

    private void Update()
    {
        HandleEscape();
        HandleLook();
        HandleCrouch();
        HandleMove();
    }

    // ── Escape / cursor ──────────────────────────────────────────────────────

    private void HandleEscape()
    {
        if (!GameInputBindings.WasPressed(GameBindingAction.Menu)) return;
        if (!UIManager.IsAnyPanelOpen)
            SetCursor(Cursor.lockState != CursorLockMode.Locked);
    }

    // Locks the player body yaw so only the camera orbits horizontally.
    // Used when the player controls a vehicle/cart — body faces a fixed direction.
    public void SetRotationLocked(bool locked)
    {
        _rotationLocked = locked;
        _camYaw = 0f;   // reset so camera snaps back to player forward on lock change
    }

    // ── Look ─────────────────────────────────────────────────────────────────

    private void HandleLook()
    {
        if (UIManager.IsAnyPanelOpen || Cursor.lockState != CursorLockMode.Locked) return;

        var mouse = Mouse.current;
        if (mouse == null) return;

        var delta = mouse.delta.ReadValue();
        _pitch -= GameSettings.VerticalLook(delta.y) * _mouseSensitivity;
        _pitch  = Mathf.Clamp(_pitch, -89f, 89f);

        if (_rotationLocked)
        {
            // Rotation locked: accumulate yaw on the camera only, body stays fixed.
            _camYaw += delta.x * _mouseSensitivity * GameSettings.LookSensitivity;
            _camYaw  = Mathf.Clamp(_camYaw, -160f, 160f);
            _cam.transform.localRotation = Quaternion.Euler(_pitch, _camYaw, 0f);
        }
        else
        {
            transform.Rotate(0f, delta.x * _mouseSensitivity * GameSettings.LookSensitivity, 0f);
            _cam.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }
    }

    // ── Crouch ───────────────────────────────────────────────────────────────

    private void HandleCrouch()
    {
        var kb = Keyboard.current;
        if (kb == null || UIManager.IsAnyPanelOpen) return;

        bool wantCrouch = kb.leftCtrlKey.isPressed || kb.cKey.isPressed;

        // Force stay crouched if there's no headroom to stand
        if (IsCrouching && !wantCrouch && !HasHeadroom())
            wantCrouch = true;

        IsCrouching = wantCrouch;

        float targetHeight = IsCrouching ? _crouchHeight : _standHeight;
        _cc.height = Mathf.Lerp(_cc.height, targetHeight, Time.deltaTime * _crouchLerpSpeed);
        _cc.center = new Vector3(0f, _cc.height * 0.5f, 0f);
    }

    private bool HasHeadroom()
    {
        float checkDist = (_standHeight - _crouchHeight) + 0.1f;
        return !Physics.Raycast(transform.position + Vector3.up * _crouchHeight, Vector3.up, checkDist);
    }

    // ── Move ─────────────────────────────────────────────────────────────────

    public void SetMovementEnabled(bool enabled) => _movementEnabled = enabled;

    private void HandleMove()
    {
        var kb = Keyboard.current;
        if (kb == null) { IsMoving = false; IsSprinting = false; DriveAnimator(0f); return; }

        float h = (kb.dKey.isPressed || kb.rightArrowKey.isPressed ? 1f : 0f)
                - (kb.aKey.isPressed || kb.leftArrowKey.isPressed  ? 1f : 0f);
        float v = (kb.wKey.isPressed || kb.upArrowKey.isPressed    ? 1f : 0f)
                - (kb.sKey.isPressed || kb.downArrowKey.isPressed  ? 1f : 0f);

        var move = transform.right * h + transform.forward * v;
        bool carryingBulkyOrder = BoxPhysicsInteractable.GrabbedBox?.HasBulkyCarryDebuff == true;
        IsMoving    = move.sqrMagnitude > 0.01f;
        IsSprinting = IsMoving && !IsCrouching
                   && !carryingBulkyOrder
                   && GameInputBindings.IsPressed(GameBindingAction.Sprint);

        // Drive animator before early-return so cart/vehicle motion plays leg animation.
        float animTarget = IsMoving ? (IsSprinting ? AnimSprintSpeed : AnimWalkSpeed) : 0f;
        DriveAnimator(animTarget);

        // Skip physics when movement is blocked (cart, vehicle, UI) but keep anim updated.
        if (!_movementEnabled || UIManager.IsAnyPanelOpen)
            return;

        if (move.sqrMagnitude > 1f) move.Normalize();

        float baseSpeed  = IsCrouching  ? _crouchSpeed
                         : IsSprinting  ? _sprintSpeed
                                        : _walkSpeed;

        // The carry slowdown is a PackedOrderBox size mechanic, not a mass mechanic. Ordinary
        // warehouse boxes keep normal movement regardless of Rigidbody.mass.
        float heavyMult  = carryingBulkyOrder ? _heavyCarryMultiplier : 1f;
        // Scoped to THIS player, not the global IsCarried flag — that goes true the instant ANY
        // player anywhere picks up a ladder, slowing down everyone else too.
        float ladderMult = LadderController.IsActorCarryingLadder(gameObject) ? _ladderCarryMultiplier : 1f;
        float roleMult = (!SpecializationPenalties.SoloMode && RoleController.Instance != null)
            ? RoleController.Instance.GetSpeedMultiplier() : 1f;
        CurrentSpeed = baseSpeed * heavyMult * ladderMult * roleMult;

        _yVelocity = _cc.isGrounded ? -1f : _yVelocity + _gravity * Time.deltaTime;
        _cc.Move((move * CurrentSpeed + Vector3.up * _yVelocity) * Time.deltaTime);
    }

    private void DriveAnimator(float targetSpeed)
    {
        if (_anim == null) return;
        // Smooth speed transitions (damp 0.1 s) to avoid animation pops.
        _anim.SetFloat(_animSpeed,       targetSpeed, 0.1f, Time.deltaTime);
        _anim.SetFloat(_animMotionSpeed, 1f);
        _anim.SetBool (_animGrounded,    _cc.isGrounded);
    }

    // ── Cursor ───────────────────────────────────────────────────────────────

    private static void SetCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible   = !locked;
    }

    // Re-lock cursor when the game window regains focus (e.g. after Alt-Tab)
    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && !UIManager.IsAnyPanelOpen)
            SetCursor(true);
    }
}
