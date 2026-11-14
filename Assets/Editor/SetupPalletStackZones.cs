using UnityEngine;
using UnityEditor;
using System.Reflection;

/// Manages PalletStackZone placement on pallets.
/// Use Context Menu on individual PalletBehavior GameObjects, or the Tools menu entries.
public class SetupPalletStackZones
{
    // ── Remove all (cleanup after accidental mass-add) ─────────────────────────

    public static void Execute() => RemoveAll();

    [MenuItem("Tools/Warehouse Demo/Pallet Zones — Remove All")]
    public static void RemoveAll()
    {
        int removed = 0;
        var zones = Object.FindObjectsByType<PalletStackZone>(FindObjectsSortMode.None);
        foreach (var z in zones)
        {
            Object.DestroyImmediate(z);
            removed++;
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        Debug.Log($"[PalletZones] Removed {removed} zones.");
    }

    // ── Add to selected objects ────────────────────────────────────────────────

    [MenuItem("Tools/Warehouse Demo/Pallet Zones — Add to Selection (Standard 2×2×3)")]
    public static void AddToSelectionStandard()
        => AddToSelection("SetupStandard");

    [MenuItem("Tools/Warehouse Demo/Pallet Zones — Add to Selection (Wide 3×2×3)")]
    public static void AddToSelectionWide()
        => AddToSelection("SetupWide");

    [MenuItem("Tools/Warehouse Demo/Pallet Zones — Add to Selection (Large 3×3×4)")]
    public static void AddToSelectionLarge()
        => AddToSelection("SetupLarge");

    static void AddToSelection(string preset)
    {
        int added = 0;
        foreach (var go in Selection.gameObjects)
        {
            // Accept selection on the pallet root or any child — walk up to PalletBehavior
            var pb = go.GetComponentInParent<PalletBehavior>();
            if (pb == null) { Debug.LogWarning($"[PalletZones] No PalletBehavior on/above '{go.name}'"); continue; }

            var root = pb.gameObject;
            if (root.GetComponent<PalletStackZone>() != null) { Debug.Log($"[PalletZones] Already has zone: {root.name}"); continue; }

            var zone = root.AddComponent<PalletStackZone>();
            InvokePreset(zone, preset);
            EditorUtility.SetDirty(root);
            added++;
            Debug.Log($"[PalletZones] Added {preset} zone to '{root.name}'");
        }

        if (added > 0)
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        Debug.Log($"[PalletZones] Added {added} zones.");
    }

    // ── Setup a specific pallet zone in the TrailerPallets area ───────────────

    [MenuItem("Tools/Warehouse Demo/Pallet Zones — Add to Trailer Pallets")]
    public static void AddToTrailerPallets()
    {
        var trailer = GameObject.Find("TrailerPallets");
        if (trailer == null) { Debug.LogError("[PalletZones] TrailerPallets not found"); return; }

        int added = 0;
        foreach (var pb in trailer.GetComponentsInChildren<PalletBehavior>(true))
        {
            var root = pb.gameObject;
            if (root.GetComponent<PalletStackZone>() != null) continue;
            var zone = root.AddComponent<PalletStackZone>();
            InvokePreset(zone, "SetupStandard");
            EditorUtility.SetDirty(root);
            added++;
        }

        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        Debug.Log($"[PalletZones] Added {added} zones to TrailerPallets.");
    }

    static void InvokePreset(PalletStackZone zone, string methodName)
    {
        var method = typeof(PalletStackZone).GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(zone, null);
    }
}
