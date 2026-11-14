using UnityEngine;
using UnityEditor;
using Unity.Netcode;
using Unity.Netcode.Components;

// Tools > Warehouse Demo > Add NetworkTransform to Physics Boxes
//
// Finds every prefab that has BoxPhysicsInteractable and adds:
//   1. NetworkObject  (required by all Netcode components)
//   2. NetworkTransform (server-authoritative — server owns box physics)
//
// Safe to re-run: skips prefabs that already have the components.
public static class AddNetworkTransformToBoxes
{
    static readonly string[] k_PrefabPaths =
    {
        "Assets/Prefabs/env/box_large_prefab.prefab",
        "Assets/Prefabs/env/box_med_prefab.prefab",
        "Assets/Prefabs/env/box_wide_flat_prefab.prefab",
        "Assets/Prefabs/env/box_long_flat_prefab.prefab",
        "Assets/Prefabs/env/pallet_box_pack_prefab.prefab",
    };

    [MenuItem("Tools/Warehouse Demo/Add NetworkTransform to Physics Boxes")]
    public static void Execute()
    {
        int patched = 0;
        int skipped = 0;

        foreach (var path in k_PrefabPaths)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
            {
                Debug.LogWarning($"[AddNT] Prefab not found: {path}");
                continue;
            }

            try
            {
                bool changed = false;

                if (root.GetComponent<NetworkObject>() == null)
                {
                    root.AddComponent<NetworkObject>();
                    changed = true;
                    Debug.Log($"[AddNT] Added NetworkObject → {path}");
                }

                // ClientNetworkTransform derives from NetworkTransform, so
                // GetComponent<NetworkTransform>() catches both. Only add the
                // server-authoritative NetworkTransform if neither is present.
                if (root.GetComponent<NetworkTransform>() == null)
                {
                    root.AddComponent<NetworkTransform>();
                    changed = true;
                    Debug.Log($"[AddNT] Added NetworkTransform → {path}");
                }

                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    patched++;
                }
                else
                {
                    skipped++;
                    Debug.Log($"[AddNT] Already up-to-date: {path}");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[AddNT] Done. Patched: {patched}  Skipped: {skipped}");
        EditorUtility.DisplayDialog(
            "NetworkTransform на боксы",
            $"Готово!\n\nОбновлено префабов: {patched}\nУже были готовы: {skipped}\n\n" +
            "Каждый бокс теперь имеет NetworkObject + NetworkTransform (server-auth).\n" +
            "Сохраните сцену (Ctrl+S).",
            "OK");
    }
}
