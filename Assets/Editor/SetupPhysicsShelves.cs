using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;

/// Tools → Warehouse Demo → Setup Physics Shelves
/// Finds all shelf root objects in the scene and adds PhysicsShelf to each one.
/// Run once after placing shelves in the scene.
public static class SetupPhysicsShelves
{
    static readonly string[] Keywords = { "shelf", "rack", "stell", "regal" };

    [MenuItem("Tools/Warehouse Demo/Setup Physics Shelves")]
    static void Run()
    {
        var roots = FindShelfRoots();
        if (roots.Count == 0)
        {
            Debug.LogWarning("[PhysicsShelves] No shelf objects found. " +
                             "Objects must have 'shelf', 'rack', 'stell', or 'regal' in their name.");
            return;
        }

        int added   = 0;
        int skipped = 0;

        foreach (var go in roots)
        {
            if (go.GetComponent<PhysicsShelf>() != null) { skipped++; continue; }

            Undo.AddComponent<PhysicsShelf>(go);
            added++;
            Debug.Log($"[PhysicsShelves] Added PhysicsShelf → '{go.name}'", go);
        }

        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"[PhysicsShelves] Done. Added={added}  AlreadyHad={skipped}  Total={roots.Count}");
        EditorUtility.DisplayDialog(
            "Physics Shelves",
            $"Done!\n\nAdded PhysicsShelf to {added} object(s).\n{skipped} already had it.",
            "OK");
    }

    static List<GameObject> FindShelfRoots()
    {
        var result = new List<GameObject>();
        var seen   = new HashSet<int>();

        foreach (var go in Object.FindObjectsByType<GameObject>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            // Only root-level or direct children of a non-shelf parent.
            // Walk up to find the topmost object whose name matches a keyword.
            var shelfRoot = FindShelfRoot(go.transform);
            if (shelfRoot == null) continue;
            if (!seen.Add(shelfRoot.GetInstanceID())) continue;
            result.Add(shelfRoot);
        }

        return result;
    }

    static GameObject FindShelfRoot(Transform t)
    {
        // Walk up the hierarchy; return the topmost Transform that has a keyword name.
        GameObject candidate = null;
        var cur = t;
        while (cur != null)
        {
            if (NameMatchesKeyword(cur.name))
                candidate = cur.gameObject;
            cur = cur.parent;
        }
        return candidate;
    }

    static bool NameMatchesKeyword(string name)
    {
        string lower = name.ToLowerInvariant();
        foreach (var kw in Keywords)
            if (lower.Contains(kw)) return true;
        return false;
    }
}
