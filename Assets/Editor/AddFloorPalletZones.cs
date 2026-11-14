using UnityEngine;
using UnityEditor;
using System.Reflection;

/// Adds PalletStackZone to every pallet_boxes_prefab and pallet_box_pack_prefab in the scene.
public class AddFloorPalletZones
{
    public static void Execute()
    {
        // Remove all existing zones first
        int removed = 0;
        foreach (var z in Object.FindObjectsByType<PalletStackZone>(FindObjectsSortMode.None))
        { Object.DestroyImmediate(z); removed++; }
        if (removed > 0) Debug.Log($"[PalletZones] Removed {removed} old zones.");

        int added = 0;
        foreach (var pb in Object.FindObjectsByType<PalletBehavior>(FindObjectsSortMode.None))
        {
            var root = pb.gameObject;
            string n = root.name.ToLower();

            bool isTarget = n.StartsWith("pallet_boxes_prefab") || n.StartsWith("pallet_box_pack_prefab");
            if (!isTarget) continue;
            if (root.GetComponent<PalletStackZone>() != null) continue;

            var zone = root.AddComponent<PalletStackZone>();

            // Large preset for pallet_box_pack, Standard for pallet_boxes
            string preset = n.StartsWith("pallet_box_pack") ? "SetupLarge" : "SetupStandard";
            var method = typeof(PalletStackZone).GetMethod(preset,
                BindingFlags.Instance | BindingFlags.NonPublic);
            method?.Invoke(zone, null);

            EditorUtility.SetDirty(root);
            added++;
        }

        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        Debug.Log($"[PalletZones] Added {added} zones.");
    }
}
