using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class CleanChildRigidbodies
{
    [MenuItem("Tools/Warehouse Demo/Clean Child Rigidbodies")]
    public static void Execute()
    {
        // Find every Rigidbody whose parent chain contains a "BoxInteract"-tagged object.
        // Those are child Rigidbodies of box roots — they should be removed.
        var allRbs = Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include);
        int removed = 0, total = allRbs.Length;

        foreach (var rb in allRbs)
        {
            if (rb == null) continue;
            var go = rb.gameObject;

            // If this object IS the box root, keep it.
            if (go.CompareTag("BoxInteract")) continue;

            // Walk up — if any ancestor is tagged BoxInteract, this is a child Rb.
            bool isChildOfBoxRoot = false;
            var t = go.transform.parent;
            while (t != null)
            {
                if (t.CompareTag("BoxInteract")) { isChildOfBoxRoot = true; break; }
                t = t.parent;
            }

            if (!isChildOfBoxRoot) continue;

            foreach (var bc in go.GetComponents<BoxCollider>())   Object.DestroyImmediate(bc);
            foreach (var mc in go.GetComponents<MeshCollider>())  Object.DestroyImmediate(mc);
            Object.DestroyImmediate(rb);
            EditorUtility.SetDirty(go);
            removed++;
        }

        // Also fix prefab assets directly
        int prefabRemoved = FixPrefabs();

        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log($"[CleanChildRb] Total Rigidbodies in scene: {total}. " +
                  $"Removed from scene: {removed}, from prefabs: {prefabRemoved}.");
    }

    static readonly string[] PrefabPaths =
    {
        "Assets/Prefabs/env/box_med_prefab.prefab",
        "Assets/Prefabs/env/box_large_prefab.prefab",
        "Assets/Prefabs/env/box_long_flat_prefab.prefab",
        "Assets/Prefabs/env/box_wide_flat_prefab.prefab",
    };

    static int FixPrefabs()
    {
        int count = 0;
        foreach (var path in PrefabPaths)
        {
            using var scope = new PrefabUtility.EditPrefabContentsScope(path);
            var root = scope.prefabContentsRoot;
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child == root.transform) continue;
                var rb = child.GetComponent<Rigidbody>();
                if (rb == null) continue;
                Object.DestroyImmediate(rb);
                foreach (var bc in child.GetComponents<BoxCollider>())  Object.DestroyImmediate(bc);
                foreach (var mc in child.GetComponents<MeshCollider>()) Object.DestroyImmediate(mc);
                count++;
            }
        }
        AssetDatabase.SaveAssets();
        return count;
    }
}
