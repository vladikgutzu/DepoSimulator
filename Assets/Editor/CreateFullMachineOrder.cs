using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// Tools → Warehouse Demo → Create Full Machine Order
///
/// Creates:
///   Assets/Resources/AssemblyReferences/order_moto_full.prefab
///     — combined ghost prefab from all 3 sub-order ghost prefabs
///   Assets/Resources/MotoOrders/order_moto_full.asset
///     — single OrderDefinition with all 14 parts across 3 stages
///
/// Run once; re-running overwrites existing assets.
public static class CreateFullMachineOrder
{
    const string GhostOutPath = "Assets/Resources/AssemblyReferences/order_moto_full.prefab";
    const string OrderOutPath = "Assets/Resources/MotoOrders/order_moto_full.asset";

    // All 14 parts — single stage, no ordering
    static readonly (string partID, int stage)[] PartStages = {
        ("motor",           0),
        ("cables",          0),
        ("cable_housing",   0),
        ("body_main",       0),
        ("body_front",      0),
        ("frame_cover",     0),
        ("bridge",          0),
        ("arm_chassis",     0),
        ("chassis_support", 0),
        ("claw",            0),
        ("inner_shaft",     0),
        ("outer_shaft",     0),
        ("gliders_left",    0),
        ("gliders_right",   0),
    };

    [MenuItem("Tools/Warehouse Demo/Create Full Machine Order")]
    public static void Run()
    {
        CreateGhostPrefab();
        CreateOrderAsset();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[CreateFullMachineOrder] Done. Run 'Build Demo Scene' to reload the scene.");
    }

    // ── Ghost prefab ─────────────────────────────────────────────────────────

    static void CreateGhostPrefab()
    {
        const string srcPath = "Assets/Prefabs/moto_bot/moto_bot_forassembly.prefab";

        if (AssetDatabase.LoadAssetAtPath<GameObject>(srcPath) == null)
        {
            Debug.LogError($"[CreateFullMachineOrder] Source prefab not found: {srcPath}");
            return;
        }

        // Remove stale asset if it exists, then copy fresh
        if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(GhostOutPath)))
            AssetDatabase.DeleteAsset(GhostOutPath);

        AssetDatabase.CopyAsset(srcPath, GhostOutPath);
        Debug.Log($"[CreateFullMachineOrder] Ghost prefab → {GhostOutPath}  (copied from moto_bot_forassembly)");
    }

    // ── Order asset ──────────────────────────────────────────────────────────

    static void CreateOrderAsset()
    {
        // Build a PartID → PartDefinition lookup from all assets in MotoParts
        var partLookup = new Dictionary<string, PartDefinition>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var guid in AssetDatabase.FindAssets("t:PartDefinition",
                                                       new[] { "Assets/Resources/MotoParts" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var pd   = AssetDatabase.LoadAssetAtPath<PartDefinition>(path);
            if (pd != null && !string.IsNullOrEmpty(pd.PartID))
                partLookup[pd.PartID] = pd;
        }

        // Build requirements list
        var reqs = new List<OrderDefinition.PartRequirement>();
        foreach (var (partID, stage) in PartStages)
        {
            if (!partLookup.TryGetValue(partID, out var pd))
            {
                Debug.LogWarning($"[CreateFullMachineOrder] Part not found: '{partID}'");
                continue;
            }
            reqs.Add(new OrderDefinition.PartRequirement
            {
                Part          = pd,
                Count         = 1,
                AssemblyStage = stage,
            });
        }

        // Create or overwrite the asset
        var existing = AssetDatabase.LoadAssetAtPath<OrderDefinition>(OrderOutPath);
        if (existing == null)
        {
            existing = ScriptableObject.CreateInstance<OrderDefinition>();
            AssetDatabase.CreateAsset(existing, OrderOutPath);
        }

        existing.OrderName        = "Complete AGV Machine";
        existing.ClientName       = "Prime Contractor";
        existing.Description      = "Assemble a complete AGV machine: drive unit, chassis, and manipulator.";
        existing.Reward           = 800;
        existing.TimeLimitSeconds = 0f; // no time limit
        existing.RequiredParts    = reqs.ToArray();

        EditorUtility.SetDirty(existing);
        Debug.Log($"[CreateFullMachineOrder] Order asset saved → {OrderOutPath}  ({reqs.Count} parts)");
    }
}
