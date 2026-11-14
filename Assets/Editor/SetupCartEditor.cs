using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

// Tools > Warehouse Demo > Setup Cart
//
// Finds Cart_01a_Rig_Prefab_01 in the active scene and:
//   1. Adds CartController + BoxCollider to the root
//   2. Finds the Handle bone and creates HandTarget_R / HandTarget_L on it
//      (targets sit at ~78% of cart height, ±half-grip-width from center)
//   3. Creates PlayerAnchor (behind handle, ground level) and ExitPoint (right side)
//   4. Wires all SerializedField references
//
// Safe to re-run: existing children are reused, not duplicated.
public static class SetupCartEditor
{
    const string CartName    = "Cart_01a_Rig_Prefab_01";
    const string HandlePath  = "Cart_01a_Rig/Root/Body/Handle";
    const float  GripHalfWidth  = 0.18f;  // distance from center to each hand (world units)
    const float  HeightFraction = 0.78f;  // hand targets at this fraction of cart height

    [MenuItem("Tools/Warehouse Demo/Setup Cart")]
    static void Execute()
    {
        var root = GameObject.Find(CartName);
        if (root == null)
        {
            EditorUtility.DisplayDialog("Ошибка", $"GameObject '{CartName}' не найден в сцене.", "OK");
            return;
        }

        // ── 1. CartController ─────────────────────────────────────────────────
        if (root.GetComponent<CartController>() == null)
            Undo.AddComponent<CartController>(root);
        var ctrl = root.GetComponent<CartController>();

        // ── 2. BoxCollider for interaction ────────────────────────────────────
        if (root.GetComponent<Collider>() == null)
        {
            var rends = root.GetComponentsInChildren<Renderer>(false);
            var bc = Undo.AddComponent<BoxCollider>(root);
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                var ls  = root.transform.lossyScale;
                bc.center = root.transform.InverseTransformPoint(b.center);
                bc.size   = new Vector3(b.size.x / ls.x, b.size.y / ls.y, b.size.z / ls.z);
            }
            else
            {
                bc.size = new Vector3(0.6f, 1.3f, 1.0f);
            }
        }

        // ── 3. Handle bone ────────────────────────────────────────────────────
        var handle = root.transform.Find(HandlePath);
        if (handle == null)
        {
            Debug.LogWarning($"[SetupCart] Handle bone not found at: {HandlePath}");
            EditorUtility.DisplayDialog("Предупреждение",
                $"Кость Handle не найдена по пути:\n{HandlePath}\n\n" +
                "Назначьте HandTarget_R и HandTarget_L вручную в Inspector.",
                "OK");
        }

        // ── 4. Compute IK target world positions ──────────────────────────────
        // Use cart bounds to find handle grip height (78% of cart height).
        var allRends = root.GetComponentsInChildren<Renderer>(false);
        Bounds bounds;
        if (allRends.Length > 0)
        {
            bounds = allRends[0].bounds;
            foreach (var r in allRends) bounds.Encapsulate(r.bounds);
        }
        else
        {
            bounds = new Bounds(root.transform.position + Vector3.up * 0.6f, new Vector3(0.6f, 1.3f, 1f));
        }

        float gripY = bounds.min.y + bounds.size.y * HeightFraction;

        // Hand targets are placed on the Handle bone (or cart root if Handle not found).
        // X offset = ± half-grip-width from cart center, Z = handle bone back face.
        var targetParent = handle != null ? handle : root.transform;
        float handleZ    = handle != null ? handle.position.z : bounds.min.z;
        float centerX    = bounds.center.x;

        var rightWorldPos = new Vector3(centerX + GripHalfWidth, gripY, handleZ);
        var leftWorldPos  = new Vector3(centerX - GripHalfWidth, gripY, handleZ);

        var rightTarget = EnsureChild(targetParent, "HandTarget_R", rightWorldPos,
                              targetParent.rotation);
        var leftTarget  = EnsureChild(targetParent, "HandTarget_L", leftWorldPos,
                              targetParent.rotation);

        // ── 5. PlayerAnchor: behind the handle at ground level ─────────────────
        // Cart forward = +Z (no Y rotation). Player stands at the handle side (-Z edge)
        // a bit further back, facing the cart (+Z direction).
        float anchorZ = bounds.min.z - 0.35f;
        var anchorWorld = new Vector3(bounds.center.x, bounds.min.y + 0.02f, anchorZ);
        var playerAnchor = EnsureChild(root.transform, "PlayerAnchor", anchorWorld,
                               Quaternion.Euler(0f, 0f, 0f));

        // ── 6. ExitPoint: to the right, at handle Z, ground level ─────────────
        var exitWorld = new Vector3(bounds.max.x + 0.55f, bounds.min.y + 0.02f, bounds.min.z);
        var exitPoint = EnsureChild(root.transform, "ExitPoint", exitWorld,
                            Quaternion.Euler(0f, 90f, 0f));

        // ── 7. Wire references ────────────────────────────────────────────────
        var so = new SerializedObject(ctrl);
        SetRef(so, "_rightHandTarget", rightTarget);
        SetRef(so, "_leftHandTarget",  leftTarget);
        SetRef(so, "_playerAnchor",    playerAnchor);
        SetRef(so, "_exitPoint",       exitPoint);
        so.ApplyModifiedProperties();

        // ── 8. CartStorageInteractable + StackBase ────────────────────────────
        var storage = root.GetComponent<CartStorageInteractable>();
        if (storage == null)
            storage = Undo.AddComponent<CartStorageInteractable>(root);

        var stackBaseGO = root.transform.Find("StackBase");
        if (stackBaseGO == null)
        {
            var sb = new GameObject("StackBase");
            Undo.RegisterCreatedObjectUndo(sb, "Create StackBase");
            Undo.SetTransformParent(sb.transform, root.transform, "Parent StackBase");
            sb.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            sb.transform.localRotation = Quaternion.identity;
            stackBaseGO = sb.transform;
        }
        var soStorage = new SerializedObject(storage);
        SetRef(soStorage, "_stackBase", stackBaseGO);
        soStorage.ApplyModifiedProperties();

        EditorUtility.SetDirty(root);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            SceneManager.GetActiveScene());

        Debug.Log("[SetupCart] ✓ Тележка настроена. " +
                  "Подстройте HandTarget_R / HandTarget_L на кости Handle по вкусу.");

        EditorUtility.DisplayDialog("Cart настроен",
            "Готово!\n\n" +
            "• CartController добавлен\n" +
            "• CartStorageInteractable добавлен (складывай коробки на тележку)\n" +
            "• HandTarget_R / HandTarget_L созданы на кости Handle\n" +
            "• PlayerAnchor, ExitPoint и StackBase созданы\n\n" +
            "Переместите HandTarget_R и HandTarget_L туда, куда должны ложиться руки.\n" +
            "Переместите StackBase на поверхность платформы тележки (Y).\n\n" +
            "Управление: E — взяться/положить коробку, W/S — толкать, A/D — поворот, F — отпустить.",
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
        var prop = so.FindProperty(field);
        if (prop != null) prop.objectReferenceValue = value;
        else Debug.LogWarning($"[SetupCart] Field '{field}' not found on CartController");
    }
}
