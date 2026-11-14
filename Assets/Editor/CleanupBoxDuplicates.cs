using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

// Removes duplicate BoxPhysicsInteractable / BoxHandAnchors components
// that appear when scene overrides conflict with newly-patched prefab assets.
// Run once after SetupBoxPrefabAnchors.
public static class CleanupBoxDuplicates
{
    public static void Execute()
    {
        int fixedObjects = 0;

        // Find every root of each box prefab type in the active scene
        string[] prefabNames = {
            "box_large_prefab", "box_long_flat_prefab",
            "box_med_prefab",   "box_wide_flat_prefab"
        };

        var scene = SceneManager.GetActiveScene();
        var roots  = scene.GetRootGameObjects();

        int totalBPI = 0, totalBHA = 0;

        foreach (var root in roots)
            FixRecursive(root.transform, prefabNames, ref fixedObjects, ref totalBPI, ref totalBHA);

        if (fixedObjects > 0)
        {
            EditorUtility.SetDirty(SceneManager.GetActiveScene().GetRootGameObjects()[0]);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        }

        Debug.Log($"[BoxCleanup] Done. Fixed objects: {fixedObjects}. " +
                  $"Removed {totalBPI} duplicate BoxPhysicsInteractable, " +
                  $"{totalBHA} duplicate BoxHandAnchors.");
    }

    static void FixRecursive(Transform t, string[] names, ref int fixedObjects,
                              ref int dupBPI, ref int dupBHA)
    {
        // Check if this object is one of the 4 box prefab roots
        bool isBox = false;
        foreach (var n in names)
            if (t.name == n || t.name.StartsWith(n + " (")) { isBox = true; break; }

        if (isBox)
        {
            var go   = t.gameObject;
            bool changed = false;

            // Remove extra BoxPhysicsInteractable (keep first / prefab-linked one)
            var bpiAll = go.GetComponents<BoxPhysicsInteractable>();
            for (int i = 1; i < bpiAll.Length; i++)
            {
                Object.DestroyImmediate(bpiAll[i], true);
                dupBPI++;
                changed = true;
            }

            // Remove extra BoxHandAnchors (keep first)
            var bhaAll = go.GetComponents<BoxHandAnchors>();
            for (int i = 1; i < bhaAll.Length; i++)
            {
                Object.DestroyImmediate(bhaAll[i], true);
                dupBHA++;
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(go);
                fixedObjects++;
            }
        }

        // Recurse into children
        for (int i = 0; i < t.childCount; i++)
            FixRecursive(t.GetChild(i), names, ref fixedObjects, ref dupBPI, ref dupBHA);
    }
}
