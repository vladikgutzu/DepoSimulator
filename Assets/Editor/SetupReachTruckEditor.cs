using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

// Tools > Warehouse Demo > Setup Reach Truck
//
// Finds Reach_Truck_01a_Rig_Prefab_01 in the active scene and:
//   1. Adds ReachTruckController + BoxCollider to the root
//   2. Finds Front_01 and Front_02 bones and assigns them
//   3. Creates DriverSeat, CameraAnchor, ExitPoint child GameObjects
//   4. Creates IK targets: SteerHandRight/Left (near steering wheel), FootRight/Left (near pedals)
//   5. Wires all SerializedField references
//
// Safe to re-run: skips steps that are already done.
public static class SetupReachTruckEditor
{
    const string TruckName  = "Reach_Truck_01a_Rig_Prefab_01";
    const string Front1Path = "Reach_Truck_01a_Rig/Root/Body/Front_01";
    const string Front2Path = "Reach_Truck_01a_Rig/Root/Body/Front_01/Front_02";

    [MenuItem("Tools/Warehouse Demo/Setup Reach Truck")]
    public static void Execute()
    {
        var root = GameObject.Find(TruckName);
        if (root == null)
        {
            EditorUtility.DisplayDialog("Ошибка", $"GameObject '{TruckName}' не найден в сцене.", "OK");
            return;
        }

        Undo.RecordObject(root, "Setup Reach Truck");

        // ── 1. ReachTruckController ───────────────────────────────────────────
        var ctrl = root.GetComponent<ReachTruckController>();
        if (ctrl == null)
            ctrl = Undo.AddComponent<ReachTruckController>(root);

        // ── 2. BoxCollider ────────────────────────────────────────────────────
        var col = root.GetComponent<Collider>();
        if (col == null)
        {
            var rends = root.GetComponentsInChildren<Renderer>(false);
            var bc = Undo.AddComponent<BoxCollider>(root);
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                bc.center = root.transform.InverseTransformPoint(b.center);
                bc.size   = new Vector3(
                    b.size.x / root.transform.lossyScale.x,
                    b.size.y / root.transform.lossyScale.y,
                    b.size.z / root.transform.lossyScale.z);
            }
            else
            {
                bc.size = new Vector3(1.8f, 3.5f, 4f);
            }
        }

        // ── 3. Bones ──────────────────────────────────────────────────────────
        var front1 = root.transform.Find(Front1Path);
        var front2 = root.transform.Find(Front2Path);
        if (front1 == null) Debug.LogWarning($"[ReachTruck] Bone not found: {Front1Path}");
        if (front2 == null) Debug.LogWarning($"[ReachTruck] Bone not found: {Front2Path}");

        // ── 4. Compute bounds ─────────────────────────────────────────────────
        var allRends = root.GetComponentsInChildren<Renderer>(false);
        Bounds bounds;
        if (allRends.Length > 0)
        {
            bounds = allRends[0].bounds;
            foreach (var r in allRends) bounds.Encapsulate(r.bounds);
        }
        else
        {
            bounds = new Bounds(root.transform.position, Vector3.one * 2f);
        }

        // Truck faces -Z (world rotation Y≈180). Driver cab is on the +Z side of bounds.
        // seatWorld: floor of driver cab — adjust Y if player still floats.
        var seatWorld = new Vector3(bounds.center.x,
                                    bounds.min.y + 0.5f,
                                    bounds.max.z - 0.5f);

        // camAnchorWorld: behind and above driver for 3rd-person vehicle cam.
        var camAnchorWorld = new Vector3(bounds.center.x,
                                         bounds.min.y + 2.8f,
                                         bounds.max.z + 0.8f);  // behind the cab (+Z = behind)

        // exitWorld: right side at ground level
        var exitWorld = new Vector3(bounds.max.x + 0.7f,
                                    0f,
                                    bounds.max.z - 0.5f);

        // Steering wheel hand IK targets — seated arm height, left/right of center wheel
        var steerHeightY = seatWorld.y + 0.8f;   // ~0.8 m above seat
        var steerFwdZ    = seatWorld.z - 0.5f;   // slightly toward forks
        var handRWorld   = new Vector3(seatWorld.x + 0.2f, steerHeightY, steerFwdZ);
        var handLWorld   = new Vector3(seatWorld.x - 0.2f, steerHeightY, steerFwdZ);

        // Foot pedal IK targets — near floor of cab, slightly forward
        var pedalY   = seatWorld.y - 0.35f;
        var pedalZ   = seatWorld.z - 0.45f;
        var footRWorld = new Vector3(seatWorld.x + 0.15f, pedalY, pedalZ);
        var footLWorld = new Vector3(seatWorld.x - 0.15f, pedalY, pedalZ);

        // ── 5. Create / ensure child GOs ──────────────────────────────────────
        var driverSeat   = EnsureChild(root.transform, "DriverSeat",      seatWorld,
                               Quaternion.Euler(0f, 180f, 0f));
        var cameraAnchor = EnsureChild(root.transform, "CameraAnchor",    camAnchorWorld,
                               Quaternion.Euler(10f, 180f, 0f)); // slight downward tilt
        var exitPoint    = EnsureChild(root.transform, "ExitPoint",       exitWorld,
                               Quaternion.Euler(0f, 90f, 0f));

        // IK targets parented to DriverSeat so they move with cab orientation
        var steerR = EnsureChild(driverSeat, "SteerHandRight", handRWorld, Quaternion.identity);
        var steerL = EnsureChild(driverSeat, "SteerHandLeft",  handLWorld, Quaternion.identity);
        var footR  = EnsureChild(driverSeat, "FootTargetRight", footRWorld, Quaternion.identity);
        var footL  = EnsureChild(driverSeat, "FootTargetLeft",  footLWorld, Quaternion.identity);

        // ── 6. Wire SerializedFields ──────────────────────────────────────────
        var so = new SerializedObject(ctrl);
        SetRef(so, "_front1",          front1);
        SetRef(so, "_front2",          front2);
        SetRef(so, "_driverSeat",      driverSeat);
        SetRef(so, "_cameraAnchor",    cameraAnchor);
        SetRef(so, "_exitPoint",       exitPoint);
        SetRef(so, "_steerHandRight",  steerR);
        SetRef(so, "_steerHandLeft",   steerL);
        SetRef(so, "_footTargetRight", footR);
        SetRef(so, "_footTargetLeft",  footL);
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(root);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            SceneManager.GetActiveScene());

        Debug.Log("[ReachTruck] ✓ Настройка завершена. " +
                  "Подстройте DriverSeat / CameraAnchor / IK-targets в сцене по вкусу.");

        EditorUtility.DisplayDialog("Reach Truck настроен",
            "Готово!\n\n" +
            "• ReachTruckController добавлен\n" +
            "• Front_01 / Front_02 назначены\n" +
            "• DriverSeat / CameraAnchor / ExitPoint созданы\n" +
            "• IK targets: SteerHandRight/Left, FootTargetRight/Left — дочерние к DriverSeat\n\n" +
            "Важные шаги:\n" +
            "1. Если игрок висит в воздухе — переместите DriverSeat вниз до пола кабины\n" +
            "2. Передвиньте CameraAnchor за спину водителя на нужное расстояние\n" +
            "3. IK targets работают только когда IK Pass включён в аниматоре —\n" +
            "   запустите Tools > Warehouse Demo > Enable IK Pass + Add PlayerHandIK\n\n" +
            "Управление: E — сесть  W/S — ехать  A/D — поворот  Q/Z — вилка  F — выйти",
            "OK");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static Transform EnsureChild(Transform parent, string name,
        Vector3 worldPos, Quaternion worldRot)
    {
        var existing = parent.Find(name);
        if (existing != null) return existing;

        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
        Undo.SetTransformParent(go.transform, parent, $"Parent {name}");
        go.transform.position = worldPos;
        go.transform.rotation = worldRot;
        return go.transform;
    }

    static void SetRef(SerializedObject so, string field, Object value)
    {
        if (value == null) return;
        var prop = so.FindProperty(field);
        if (prop != null) prop.objectReferenceValue = value;
        else Debug.LogWarning($"[ReachTruck] Field '{field}' not found on ReachTruckController");
    }
}
