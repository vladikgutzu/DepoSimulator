using UnityEditor;
using UnityEngine;

public class InspectAssemblyPrefabs
{
    public static void Execute()
    {
        string[] prefabs = {
            "Assets/Resources/AssemblyReferences/order_moto_drive.prefab",
            "Assets/Resources/AssemblyReferences/order_moto_frame.prefab",
            "Assets/Resources/AssemblyReferences/order_moto_arms.prefab",
        };

        foreach (var path in prefabs)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) { Debug.Log($"NOT FOUND: {path}"); continue; }

            Debug.Log($"=== {go.name} ===");
            PrintChildren(go.transform, 0);
        }
    }

    static void PrintChildren(Transform t, int depth)
    {
        string indent = new string(' ', depth * 2);
        var renderers = t.GetComponents<Renderer>();
        string rInfo = renderers.Length > 0 ? $" [Renderer x{renderers.Length}]" : "";
        Debug.Log($"{indent}{t.name}{rInfo}");
        foreach (Transform child in t)
            PrintChildren(child, depth + 1);
    }
}
