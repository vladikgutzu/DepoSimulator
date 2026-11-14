using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class AddShelfColliders
{
    // Shelf level Y positions (local, from shelf base).
    // Measured: box bottoms cluster every ~0.34 m; grills cover above 5.5 m.
    static readonly float[] LevelYs =
    {
        0.00f, 0.34f, 0.68f, 1.02f, 1.36f, 1.70f,
        2.04f, 2.38f, 2.72f, 3.06f, 3.40f, 3.74f,
        4.08f, 4.42f, 4.76f, 5.10f,
    };

    // BoxCollider dimensions — slightly smaller than shelf (W=12.02, D=3.58)
    const float ColW     = 11.6f;
    const float ColD     = 3.3f;
    const float ColThick = 0.05f;
    const string MarkerName = "ShelfSurfaceColliders";

    public static void Execute()
    {
        var shelves = new List<Transform>();
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            if (t.name == "shelfLarge") shelves.Add(t);

        if (shelves.Count == 0) { Debug.LogWarning("[ShelfCol] No shelfLarge found."); return; }

        int added = 0, skipped = 0;
        foreach (var shelf in shelves)
        {
            // Skip if already added
            bool exists = false;
            foreach (Transform c in shelf)
                if (c.name == MarkerName) { exists = true; break; }

            if (exists) { skipped++; continue; }

            var root = new GameObject(MarkerName);
            root.transform.SetParent(shelf, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale    = Vector3.one;

            foreach (float levelY in LevelYs)
            {
                var col = root.AddComponent<BoxCollider>();
                col.center = new Vector3(0f, levelY + ColThick * 0.5f, 0f);
                col.size   = new Vector3(ColW, ColThick, ColD);
            }

            EditorUtility.SetDirty(shelf.gameObject);
            added++;
        }

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"[ShelfCol] Done — added to {added} shelves ({LevelYs.Length} levels each), skipped {skipped} already done.");
    }

    // Run this to remove all added colliders (for re-run or cleanup)
    public static void RemoveAll()
    {
        int removed = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (t.name != MarkerName) continue;
            Undo.DestroyObjectImmediate(t.gameObject);
            removed++;
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log($"[ShelfCol] Removed {removed} ShelfSurfaceColliders.");
    }
}
