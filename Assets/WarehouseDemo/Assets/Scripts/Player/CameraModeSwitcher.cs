using UnityEngine;

// Singleton. Attach to Player.
// Vehicles call EnterVehicle / ExitVehicle for camera mode transitions.
//
// StarterAssets TPC setup: ReachTruckController reparents PlayerCameraRoot
// to the truck's CameraAnchor on entry — no camera switching needed here.
// TPSCameraController setup (if present): SetVehicleTarget redirects the spring-arm.
public class CameraModeSwitcher : MonoBehaviour
{
    public static CameraModeSwitcher Instance { get; private set; }

    TPSCameraController _tpsCam;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        _tpsCam = GetComponent<TPSCameraController>();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void EnterVehicle(Transform followTarget = null, float cameraDistance = 4f)
    {
        // TPSCameraController path: redirect spring-arm pivot to vehicle anchor.
        _tpsCam?.SetVehicleTarget(followTarget, cameraDistance);
        // StarterAssets TPC path: handled by ReachTruckController (PlayerCameraRoot reparent).
    }

    public void ExitVehicle()
    {
        _tpsCam?.SetVehicleTarget(null);
        if (_tpsCam != null) _tpsCam.enabled = true;
    }
}
