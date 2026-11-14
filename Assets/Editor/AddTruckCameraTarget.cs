using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using System.Reflection;

public static class AddTruckCameraTarget
{
    const string TruckName = "Reach_Truck_01a_Rig_Prefab_01";
    static readonly BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    [MenuItem("Tools/Warehouse Demo/Add Truck Camera Target")]
    public static void Execute()
    {
        var root = GameObject.Find(TruckName);
        if (root == null) { Debug.LogError($"[TruckCam] '{TruckName}' not found"); return; }

        var ctrl = root.GetComponent<ReachTruckController>();
        if (ctrl == null) { Debug.LogError("[TruckCam] ReachTruckController missing"); return; }

        // Compute bounds
        var rends = root.GetComponentsInChildren<Renderer>(false);
        Bounds b;
        if (rends.Length > 0) { b = rends[0].bounds; foreach (var r in rends) b.Encapsulate(r.bounds); }
        else b = new Bounds(root.transform.position + Vector3.up * 2f, new Vector3(2f, 4f, 4f));

        // LookAtTarget: truck centre at 1/3 height — camera looks at this point.
        var lookAtWorld = new Vector3(b.center.x, b.min.y + b.size.y * 0.35f, b.center.z);

        // CameraTarget: behind truck (driver side) and above centre height.
        // Truck Y rotation = 180°, so local +Z = world -Z direction (driver's side).
        // Place camera on the driver side (+local Z = world -Z), slightly above centre.
        var camWorld = new Vector3(b.center.x, b.min.y + b.size.y * 0.6f, b.max.z + 1f);

        var lookAtTarget = EnsureChild(root.transform, "LookAtTarget", lookAtWorld);
        var cameraTarget = EnsureChild(root.transform, "CameraTarget",  camWorld);

        // Wire via reflection
        SetField(ctrl, "_lookAtTarget", lookAtTarget);
        SetField(ctrl, "_cameraTarget", cameraTarget);

        EditorUtility.SetDirty(root);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            SceneManager.GetActiveScene());

        Debug.Log($"[TruckCam] LookAtTarget → {lookAtWorld}\n" +
                  $"[TruckCam] CameraTarget → {camWorld}\n" +
                  "Orbit radius=6, pitch=25°. Tweak in Inspector or move objects in Scene view.");
    }

    static Transform EnsureChild(Transform parent, string name, Vector3 worldPos)
    {
        var existing = parent.Find(name);
        if (existing != null) { existing.position = worldPos; return existing; }

        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
        Undo.SetTransformParent(go.transform, parent, $"Parent {name}");
        go.transform.position = worldPos;
        return go.transform;
    }

    static void SetField(Component comp, string fieldName, object value)
    {
        var f = comp.GetType().GetField(fieldName, NonPublic);
        if (f != null) f.SetValue(comp, value);
        else Debug.LogWarning($"[TruckCam] Field '{fieldName}' not found");
    }
}
