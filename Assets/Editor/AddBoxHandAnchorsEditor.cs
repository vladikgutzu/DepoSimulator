using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

// Tools > Warehouse Demo > Add Box Hand Anchors
// For every BoxPhysicsInteractable in the active scene:
//   • Adds BoxHandAnchors component if absent
//   • Creates child GameObjects HandTarget_R / HandTarget_L
//   • Positions them at the left/right sides of the box at grip height
// Run once, then tweak HandTarget positions per box in the Inspector.
public static class AddBoxHandAnchorsEditor
{
    // Fraction of box height from the bottom where hands grip
    const float GripHeightFactor = 0.35f;

    [MenuItem("Tools/Warehouse Demo/Add Box Hand Anchors to Scene Boxes")]
    static void Execute()
    {
        int added   = 0;
        int skipped = 0;

        var allBoxes = Object.FindObjectsByType<BoxPhysicsInteractable>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (var box in allBoxes)
        {
            var go = box.gameObject;

            var rends = go.GetComponentsInChildren<Renderer>(false);
            if (rends.Length == 0) { skipped++; continue; }

            Bounds b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);

            if (go.GetComponent<BoxHandAnchors>() == null)
                Undo.AddComponent<BoxHandAnchors>(go);

            var anchors = go.GetComponent<BoxHandAnchors>();
            var so      = new SerializedObject(anchors);

            float gripY = b.min.y + b.size.y * GripHeightFactor;

            // Right hand: +X side of box at grip height, palm facing inward (-X)
            var rightTarget = EnsureChild(go, "HandTarget_R",
                new Vector3(b.max.x, gripY, b.center.z),
                Quaternion.LookRotation(-go.transform.right, go.transform.up));

            // Left hand: -X side of box at grip height, palm facing inward (+X)
            var leftTarget = EnsureChild(go, "HandTarget_L",
                new Vector3(b.min.x, gripY, b.center.z),
                Quaternion.LookRotation(go.transform.right, go.transform.up));

            SetField(so, "_rightHandTarget", rightTarget);
            SetField(so, "_leftHandTarget",  leftTarget);
            so.ApplyModifiedProperties();

            EditorUtility.SetDirty(go);
            added++;
        }

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            SceneManager.GetActiveScene());

        Debug.Log($"[BoxHandAnchors] ✓ Коробок обработано: {added}, пропущено (без Renderer): {skipped}");
        EditorUtility.DisplayDialog("Box Hand Anchors",
            $"Якоря рук добавлены на {added} коробок.\n" +
            $"Пропущено (нет Renderer): {skipped}.\n\n" +
            "Выберите коробку → HandTarget_R / HandTarget_L видны как цветные сферы Gizmo.\n" +
            "Подстройте их позицию и поворот под свою модель.",
            "OK");
    }

    static Transform EnsureChild(GameObject parent, string name,
        Vector3 worldPos, Quaternion worldRot)
    {
        var existing = parent.transform.Find(name);
        if (existing != null) return existing;

        var child = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(child, $"Create {name}");
        Undo.SetTransformParent(child.transform, parent.transform, $"Parent {name}");
        child.transform.position = worldPos;
        child.transform.rotation = worldRot;
        return child.transform;
    }

    static void SetField(SerializedObject so, string fieldName, Transform value)
    {
        var prop = so.FindProperty(fieldName);
        if (prop != null) prop.objectReferenceValue = value;
    }
}
