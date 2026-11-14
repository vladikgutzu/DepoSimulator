using UnityEngine;
using UnityEditor;
using System.Reflection;

// Tools > Warehouse Demo > Diag Vehicle Camera
public static class DiagVehicleCamera
{
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [MenuItem("Tools/Warehouse Demo/Diag Vehicle Camera")]
    public static void Execute()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Vehicle Camera Diagnostic ===\n");

        // ── CameraModeSwitcher ────────────────────────────────────────────────
        var switcher = Object.FindFirstObjectByType<CameraModeSwitcher>();
        if (switcher == null)
            sb.AppendLine("CameraModeSwitcher: NOT FOUND in scene");
        else
            sb.AppendLine($"CameraModeSwitcher: found on '{switcher.gameObject.name}'");

        // ── TPSCameraController ───────────────────────────────────────────────
        var tps = Object.FindFirstObjectByType<TPSCameraController>();
        if (tps != null)
        {
            var vTarget = (Transform)typeof(TPSCameraController)
                .GetField("_vehicleTarget", Private)?.GetValue(tps);
            var vDist = (float)typeof(TPSCameraController)
                .GetField("_vehicleDistance", Private)?.GetValue(tps);
            sb.AppendLine($"\nTPSCameraController on '{tps.gameObject.name}'");
            sb.AppendLine($"  enabled        : {tps.enabled}");
            sb.AppendLine($"  _vehicleTarget : {(vTarget ? vTarget.name + " (" + GetPath(vTarget) + ")" : "null — player mode")}");
            sb.AppendLine($"  _vehicleDistance: {vDist}");
        }
        else
        {
            sb.AppendLine("\nTPSCameraController: NOT FOUND in scene");
        }

        sb.AppendLine();

        // ── ReachTruckController ──────────────────────────────────────────────
        var trucks = Object.FindObjectsByType<ReachTruckController>(FindObjectsSortMode.None);
        sb.AppendLine($"ReachTruckController instances: {trucks.Length}");
        foreach (var truck in trucks)
        {
            sb.AppendLine($"\n  Truck: '{truck.gameObject.name}'");
            var anchor  = (Transform)typeof(ReachTruckController).GetField("_cameraAnchor",         Private)?.GetValue(truck);
            var seat    = (Transform)typeof(ReachTruckController).GetField("_driverSeat",            Private)?.GetValue(truck);
            var camDist = (float)    typeof(ReachTruckController).GetField("_vehicleCameraDistance", Private)?.GetValue(truck);

            sb.AppendLine($"    _cameraAnchor         : {(anchor ? anchor.name + " pos=" + anchor.position : "NULL ← fallback is truck root")}");
            sb.AppendLine($"    _driverSeat           : {(seat   ? seat.name   + " pos=" + seat.position   : "NULL")}");
            sb.AppendLine($"    _vehicleCameraDistance: {camDist}");

            if (anchor != null)
            {
                var local = truck.transform.InverseTransformPoint(anchor.position);
                sb.AppendLine($"    _cameraAnchor local   : {local} (should be behind+above driver)");
            }
        }

        Debug.Log(sb.ToString());
        EditorUtility.DisplayDialog("Vehicle Camera Diagnostic", "Результат в Console. Нажми OK.", "OK");
    }

    static string GetPath(Transform t)
    {
        if (t == null) return "null";
        var path = t.name;
        var p = t.parent;
        while (p != null) { path = p.name + "/" + path; p = p.parent; }
        return path;
    }
}
