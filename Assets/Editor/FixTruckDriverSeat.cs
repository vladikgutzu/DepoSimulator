using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

public static class FixTruckDriverSeat
{
    public static void Execute()
    {
        var root = GameObject.Find("Reach_Truck_01a_Rig_Prefab_01");
        if (root == null) { Debug.LogError("[TruckSeat] Root not found"); return; }

        // Actual cab positions from rig bones (world space):
        //   Pedal_1:       Y=1.187, Z=-0.527
        //   Steering_Wheel:Y=1.766, Z=-0.663
        // Driver stands on the cab platform, looking toward the forks (world -Z).
        // Cab floor ≈ pedal Y - a small step = 1.0 world.

        var rends = root.GetComponentsInChildren<Renderer>(false);
        Bounds b;
        if (rends.Length > 0) { b = rends[0].bounds; foreach (var r in rends) b.Encapsulate(r.bounds); }
        else b = new Bounds(root.transform.position + Vector3.up * 2f, new Vector3(2f, 4f, 4f));

        // Seat: inside cab at floor level, centred on truck X, near the controls.
        var seatWorld = new Vector3(b.center.x, 1.0f, -0.62f);

        // ExitPoint: right side of truck at ground level (outside geometry).
        var exitWorld = new Vector3(b.max.x + 0.6f, b.min.y + 0.05f, -0.5f);

        var seat = root.transform.Find("DriverSeat");
        var exit = root.transform.Find("ExitPoint");

        if (seat != null)
        {
            seat.position = seatWorld;
            // Face world -Z = toward forks (driver looks at what they're picking up)
            seat.rotation = Quaternion.Euler(0f, 180f, 0f);
            Debug.Log($"[TruckSeat] DriverSeat → {seatWorld} (inside cab at floor level)");
        }
        else Debug.LogWarning("[TruckSeat] DriverSeat child not found");

        if (exit != null)
        {
            exit.position = exitWorld;
            exit.rotation = Quaternion.Euler(0f, 90f, 0f);
        }

        EditorUtility.SetDirty(root);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            SceneManager.GetActiveScene());
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(
            SceneManager.GetActiveScene());

        Debug.Log("[TruckSeat] Done. If seat feels too high/low, tweak DriverSeat Y in Inspector.");
    }
}
