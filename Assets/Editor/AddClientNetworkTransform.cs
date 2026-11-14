using UnityEngine;
using UnityEditor;
using Unity.Netcode.Components;

// Tools > Warehouse Demo > Add ClientNetworkTransform to NetworkPlayerTPS
//
// Adds ClientNetworkTransform to the NetworkPlayerTPS prefab root.
// Safe to re-run — skips if the component already exists.
// If a base NetworkTransform (server-authoritative) is found it is removed first.
public static class AddClientNetworkTransform
{
    const string k_PrefabPath = "Assets/Prefabs/Network/NetworkPlayerTPS.prefab";

    [MenuItem("Tools/Warehouse Demo/Add ClientNetworkTransform to NetworkPlayerTPS")]
    public static void Execute()
    {
        var root = PrefabUtility.LoadPrefabContents(k_PrefabPath);
        if (root == null)
        {
            Debug.LogError($"[AddCNT] Prefab not found: {k_PrefabPath}");
            return;
        }

        try
        {
            // Remove plain NetworkTransform if it exists (would block AddComponent due
            // to [DisallowMultipleComponent] on the base class).
            var existing = root.GetComponent<NetworkTransform>();
            if (existing != null && existing.GetType() == typeof(NetworkTransform))
            {
                Object.DestroyImmediate(existing);
                Debug.Log("[AddCNT] Removed server-authoritative NetworkTransform.");
            }

            if (root.GetComponent<ClientNetworkTransform>() != null)
            {
                Debug.Log("[AddCNT] ClientNetworkTransform already present — nothing to do.");
                return;
            }

            root.AddComponent<ClientNetworkTransform>();
            PrefabUtility.SaveAsPrefabAsset(root, k_PrefabPath);
            Debug.Log("[AddCNT] ✓ ClientNetworkTransform added to NetworkPlayerTPS.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "ClientNetworkTransform",
            "Готово!\n\nClientNetworkTransform добавлен на NetworkPlayerTPS.prefab.\n" +
            "Компонент обеспечивает owner-authoritative движение (клиент пишет свою позицию).",
            "OK");
    }
}
