using UnityEngine;
using UnityEngine.InputSystem;

/// Scissor lift the player can board, drive horizontally (WASD),
/// and raise / lower (Q / Z). Press F to exit.
///
/// Expected hierarchy (built by SetupScissorLiftFBX editor tool):
///   ScissorLiftFBX  ← this component + BoxCollider [+ NetworkObject + NetworkVehicleSync]
///   ├── Visual      ← FBX base/stair meshes (static)
///   ├── ScissorArms ← X-beam visual, assigned to _scissorMesh
///   └── Platform    ← moves on Y, assigned to _platform
///       ├── Basket  ← visual basket mesh (reparented from FBX)
///       └── ExitPoint
///
/// Multiplayer: add NetworkObject + NetworkVehicleSync to the prefab so the vehicle
/// syncs position, rotation, and platform height across all clients.
/// _forkT in NetworkVehicleSync is repurposed as the normalized platform height [0,1].
public class ScissorLift : MonoBehaviour, IInteractable, INoOutlineHighlight
{
    [Header("References")]
    [SerializeField] private Transform _platform;
    [SerializeField] private Transform _exitPoint;
    [SerializeField] private Transform _scissorMesh;   // ScissorArms container

    [Header("Platform")]
    [SerializeField] private float _platformBaseHeight = 1.2f;
    [SerializeField] private float _maxHeight  = 5f;
    [SerializeField] private float _liftSpeed  = 2f;

    [Header("Drive")]
    [SerializeField] private float _driveSpeed = 4f;
    [SerializeField] private float _turnSpeed  = 80f;

    [Header("Camera")]
    [SerializeField] private float _cameraDistance = 5f;

    public static bool PlayerOnLift { get; private set; }

    bool  _occupied;
    float _height;

    GameObject            _player;
    FirstPersonController _fpc;
    CharacterController   _cc;

    // Optional: present when a NetworkObject + NetworkVehicleSync are on this prefab.
    // Null in single-player or when the vehicle has no NetworkObject yet.
    NetworkVehicleSync _netSync;

    public string InteractPrompt => _occupied ? "" : $"{InputHints.EK} Operate lift";

    void Awake()
    {
        _height  = _platformBaseHeight;
        _netSync = GetComponent<NetworkVehicleSync>();
        ApplyHeight();
    }

    void Start()
    {
        if (_netSync != null)
            _netSync.OnLocalEnterConfirmed += Enter;
    }

    void OnDestroy()
    {
        if (_netSync != null)
            _netSync.OnLocalEnterConfirmed -= Enter;
    }

    public void Interact(GameObject actor)
    {
        if (_occupied) return;

        bool isMP = _netSync != null && Unity.Netcode.NetworkManager.Singleton?.IsListening == true;
        if (isMP)
            _netSync.RequestEnterServerRpc();
        else
            Enter(actor);
    }

    void Enter(GameObject actor)
    {
        _player = actor;
        _fpc    = actor.GetComponent<FirstPersonController>();
        _cc     = actor.GetComponent<CharacterController>();

        _cc.enabled = false;
        _fpc.SetMovementEnabled(false);

        actor.transform.SetParent(_platform);
        actor.transform.localPosition = new Vector3(0f, 0.1f, 0f);

        _occupied    = true;
        PlayerOnLift = true;
        CameraModeSwitcher.Instance?.EnterVehicle(null, _cameraDistance);
        VehicleHUDController.Instance?.Show("Lift");
    }

    void Exit()
    {
        _player.transform.SetParent(null);
        if (_exitPoint != null)
        {
            _player.transform.position = _exitPoint.position;
            _player.transform.rotation = _exitPoint.rotation;
        }
        else
        {
            _player.transform.position = transform.position + transform.right * 1.5f;
        }

        _cc.enabled = true;
        _fpc.SetMovementEnabled(true);

        _occupied    = false;
        PlayerOnLift = false;
        _player      = null;
        _fpc         = null;
        _cc          = null;
        CameraModeSwitcher.Instance?.ExitVehicle();
        VehicleHUDController.Instance?.Hide();

        bool isMP = _netSync != null && Unity.Netcode.NetworkManager.Singleton?.IsListening == true;
        if (isMP)
            _netSync.RequestExitServerRpc();
    }

    // Normalized platform height [0,1] reused as ForkT in NetworkVehicleSync.
    float NormalizedHeight => _maxHeight > 0f
        ? Mathf.Clamp01((_height - _platformBaseHeight) / _maxHeight)
        : 0f;

    void Update()
    {
        if (!_occupied) return;

        var kb = Keyboard.current;
        var gp = Gamepad.current;
        if (kb == null && gp == null) return;

        if (GameInputBindings.WasPressed(GameBindingAction.Release)) { Exit(); return; }

        // Raise / lower
        bool up   = GameInputBindings.IsPressed(GameBindingAction.LiftUp);
        bool down = GameInputBindings.IsPressed(GameBindingAction.LiftDown);
        float liftDir = (up ? 1f : 0f) - (down ? 1f : 0f);
        _height = Mathf.Clamp(
            _height + liftDir * _liftSpeed * Time.deltaTime,
            _platformBaseHeight,
            _platformBaseHeight + _maxHeight);

        // Drive
        float turn = 0f, fwd = 0f;
        if (kb != null)
        {
            turn += (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            fwd  += (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        }
        if (gp != null)
        {
            var stick = gp.leftStick.ReadValue();
            turn = Mathf.Clamp(turn + stick.x, -1f, 1f);
            fwd  = Mathf.Clamp(fwd  + stick.y, -1f, 1f);
        }
        transform.Rotate(0f, turn * _turnSpeed * Time.deltaTime, 0f);
        transform.position += transform.forward * (fwd * _driveSpeed * Time.deltaTime);

        VehicleHUDController.Instance?.UpdateForkHeight(_height - _platformBaseHeight);
        ApplyHeight();

        // Push current transform + height to non-driver clients every frame.
        _netSync?.SetTransform(transform.position, transform.rotation);
        _netSync?.SetForkT(NormalizedHeight);
    }

    void LateUpdate()
    {
        // Non-driver client: reconstruct platform height from the driver's synced ForkT.
        // Transform (position/rotation) is already applied by NetworkVehicleSync.LateUpdate().
        if (_occupied || _netSync == null || !_netSync.IsSpawned || !_netSync.IsOccupied) return;
        _height = _platformBaseHeight + _netSync.ForkT * _maxHeight;
        ApplyHeight();
    }

    void ApplyHeight()
    {
        if (_platform != null)
            _platform.localPosition = new Vector3(0f, _height, 0f);

        if (_scissorMesh != null && _height > 0.001f)
        {
            _scissorMesh.localPosition = new Vector3(0f, _height * 0.5f, 0f);
            _scissorMesh.localScale    = new Vector3(1f, _height, 1f);
        }
        else if (_scissorMesh != null)
        {
            _scissorMesh.localScale = new Vector3(1f, 0.01f, 1f);
        }
    }
}
