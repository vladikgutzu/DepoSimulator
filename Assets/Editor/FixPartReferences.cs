using UnityEngine;
using UnityEditor;

public class FixPartReferences
{
    [MenuItem("Tools/Fix Part References")]
    public static void Execute()
    {
        // Fix cooler_fridge VisualPrefab — point to the PrefabVariant instead of the FBX
        FixVisualPrefab(
            "Assets/Resources/Catalog/Parts/part_cooler_fridge.asset",
            "Assets/Prefabs/Orders/Cooler Fridge.prefab"
        );

        // Fix cooler_fridge order SharedGhostPrefab
        FixSharedGhost(
            "Assets/Resources/Catalog/Orders/_disabled/order_cooler_fridge.asset",
            "Assets/Prefabs/Orders/Cooler Fridge.prefab"
        );
    }

    static void FixVisualPrefab(string partPath, string prefabPath)
    {
        var part = AssetDatabase.LoadAssetAtPath<PartDefinition>(partPath);
        if (part == null) { Debug.LogError("FixPartReferences: part not found at " + partPath); return; }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) { Debug.LogError("FixPartReferences: prefab not found at " + prefabPath); return; }

        Debug.Log($"FixPartReferences: {part.PartID} VisualPrefab was: {(part.VisualPrefab ? part.VisualPrefab.name : "null")} → {prefab.name}");
        part.VisualPrefab = prefab;
        EditorUtility.SetDirty(part);
        AssetDatabase.SaveAssets();
        Debug.Log($"FixPartReferences: saved {partPath}");
    }

    static void FixSharedGhost(string orderPath, string prefabPath)
    {
        var order = AssetDatabase.LoadAssetAtPath<OrderDefinition>(orderPath);
        if (order == null) { Debug.LogError("FixPartReferences: order not found at " + orderPath); return; }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) { Debug.LogError("FixPartReferences: prefab not found at " + prefabPath); return; }

        Debug.Log($"FixPartReferences: {order.name} SharedGhostPrefab → {prefab.name}");
        order.SharedGhostPrefab = prefab;
        EditorUtility.SetDirty(order);
        AssetDatabase.SaveAssets();
        Debug.Log($"FixPartReferences: saved {orderPath}");
    }
}
