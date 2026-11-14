using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// Tools → Warehouse Demo → Check Shelf Mesh Colliders
/// Prints every sub-mesh on shelf/rack prefabs that has a MeshFilter but no MeshCollider,
/// and vice-versa. Run while the main scene is open.
public static class CheckShelfMeshColliders
{
    [MenuItem("Tools/Warehouse Demo/Check Shelf Mesh Colliders")]
    static void Run()
    {
        // Keywords that identify shelf/rack roots
        var keywords = new[] { "shelf", "rack", "stell", "regal" };

        var roots = new List<GameObject>();
        foreach (var go in Object.FindObjectsByType<GameObject>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (go.transform.parent != null) continue; // top-level only
            string n = go.name.ToLowerInvariant();
            foreach (var kw in keywords)
                if (n.Contains(kw)) { roots.Add(go); break; }
        }

        // Also include any GameObject whose prefab asset name contains a keyword
        foreach (var go in Object.FindObjectsByType<GameObject>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (roots.Contains(go)) continue;
            var src = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (src == null) continue;
            string n = src.name.ToLowerInvariant();
            foreach (var kw in keywords)
                if (n.Contains(kw)) { roots.Add(go); break; }
        }

        if (roots.Count == 0)
        {
            Debug.LogWarning("[ShelfCheck] No shelf/rack GameObjects found in the scene.");
            return;
        }

        Debug.Log($"[ShelfCheck] Scanning {roots.Count} shelf root(s)...");

        int missingCollider = 0;
        int missingRenderer = 0;
        int ok              = 0;

        foreach (var root in roots)
        {
            var allChildren = root.GetComponentsInChildren<Transform>(includeInactive: true);
            foreach (var t in allChildren)
            {
                var go  = t.gameObject;
                var mf  = go.GetComponent<MeshFilter>();
                var mr  = go.GetComponent<MeshRenderer>();
                var mc  = go.GetComponent<MeshCollider>();

                bool hasMesh     = mf != null && mf.sharedMesh != null;
                bool hasRenderer = mr != null;
                bool hasCollider = mc != null;

                if (!hasMesh) continue; // no geometry at all — skip

                if (hasRenderer && !hasCollider)
                {
                    Debug.LogWarning(
                        $"[ShelfCheck] MISSING COLLIDER  '{FullPath(go)}'  " +
                        $"(has MeshRenderer, no MeshCollider)  " +
                        $"mesh='{mf.sharedMesh.name}'",
                        go);
                    missingCollider++;
                }
                else if (!hasRenderer && hasCollider)
                {
                    // Invisible collider — intentional or leftover?
                    Debug.Log(
                        $"[ShelfCheck] INVISIBLE COLLIDER  '{FullPath(go)}'  " +
                        $"(MeshCollider present, no MeshRenderer)  " +
                        $"mesh='{mf.sharedMesh.name}'",
                        go);
                }
                else if (!hasRenderer && !hasCollider)
                {
                    Debug.LogWarning(
                        $"[ShelfCheck] GHOST MESH (no renderer, no collider)  '{FullPath(go)}'  " +
                        $"mesh='{mf.sharedMesh.name}'",
                        go);
                    missingRenderer++;
                }
                else
                {
                    ok++;
                }
            }
        }

        Debug.Log(
            $"[ShelfCheck] Done. " +
            $"OK={ok}  " +
            $"MissingCollider={missingCollider}  " +
            $"GhostMesh(no renderer+no collider)={missingRenderer}");

        if (missingCollider == 0 && missingRenderer == 0)
            Debug.Log("[ShelfCheck] All shelf meshes have both MeshRenderer and MeshCollider.");
    }

    static string FullPath(GameObject go)
    {
        var parts = new System.Text.StringBuilder(go.name);
        var t = go.transform.parent;
        while (t != null) { parts.Insert(0, t.name + "/"); t = t.parent; }
        return parts.ToString();
    }
}
