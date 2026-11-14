using UnityEngine;
using UnityEditor;

public static class InspectPalletBoxPack
{
    public static void Execute()
    {
        const string path = "Assets/Prefabs/env/pallet_box_pack_prefab.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            // Print 3 levels deep
            PrintTree(root.transform, 0, 3);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    static void PrintTree(Transform t, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        var comps = t.GetComponents<Component>();
        var ct = "";
        foreach (var c in comps) if (c != null && !(c is Transform)) ct += c.GetType().Name + " ";

        var rends = t.GetComponentsInChildren<Renderer>(true);
        Bounds b = new Bounds();
        bool hasBounds = false;
        foreach (var r in rends) { if (!hasBounds) { b = r.bounds; hasBounds = true; } else b.Encapsulate(r.bounds); }

        var indent = new string(' ', depth * 2);
        var boundsStr = hasBounds ? $" bounds=({b.size.x:F2},{b.size.y:F2},{b.size.z:F2}) center=({b.center.x:F2},{b.center.y:F2},{b.center.z:F2})" : "";
        var compsStr = ct.Trim().Length > 0 ? $" [{ct.Trim()}]" : "";
        Debug.Log($"[Pallet]{indent}{t.name}  children={t.childCount}{compsStr}{boundsStr}");

        for (int i = 0; i < t.childCount; i++)
            PrintTree(t.GetChild(i), depth + 1, maxDepth);
    }
}
