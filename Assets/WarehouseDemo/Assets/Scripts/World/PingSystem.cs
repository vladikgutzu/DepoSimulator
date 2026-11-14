using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

// Alt + Left Click — place a world-space ping marker for team coordination.
// Add to the Player GameObject.
public class PingSystem : MonoBehaviour
{
    [SerializeField] private float _pingRange    = 50f;
    [SerializeField] private float _pingCooldown = 1f;

    private Camera _cam;
    private float  _lastPingTime = -999f;

    private void Awake() => _cam = GetComponentInChildren<Camera>();

    private void Update()
    {
        if (UIManager.IsAnyPanelOpen) return;
        if (Cursor.lockState != CursorLockMode.Locked) return;

        var kb    = Keyboard.current;
        var mouse = Mouse.current;
        var gp    = Gamepad.current;

        // Alt + left click, or RB on foot — in a vehicle RB raises the forks instead.
        bool keyboardPing = kb != null && mouse != null
                         && kb.leftAltKey.isPressed && mouse.leftButton.wasPressedThisFrame;
        bool padPing = gp != null && PadControls.Ping(gp).wasPressedThisFrame
                    && !ReachTruckController.PlayerOnTruck && !CartController.PlayerOnCart;
        if (!keyboardPing && !padPing) return;
        if (Time.time - _lastPingTime < _pingCooldown) return;

        PlacePing();
    }

    private void PlacePing()
    {
        var ray = new Ray(_cam.transform.position, _cam.transform.forward);
        if (!Physics.Raycast(ray, out var hit, _pingRange)) return;

        _lastPingTime = Time.time;

        bool networked = NetworkManager.Singleton != null
                      && NetworkManager.Singleton.IsListening;

        if (networked && NetworkPlayer.Local != null)
        {
            // Fan the marker out through the owning NetworkPlayer. Previously it existed only on
            // this machine, so the Dispatcher's advertised coordination tool helped nobody else.
            NetworkPlayer.Local.PlacePingServerRpc(hit.point);
            return;
        }

        PingMarker.Spawn(hit.point, new Color(1f, 0.55f, 0f));
        AudioManager.Instance?.PlayPing();
    }
}
