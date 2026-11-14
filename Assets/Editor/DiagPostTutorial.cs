using UnityEngine;
using System.Reflection;

/// Forces a post-tutorial order and checks whether ghost + box assignment works.
public class DiagPostTutorial
{
    public static void Execute()
    {
        var log = new System.Text.StringBuilder();

        var gm = Object.FindAnyObjectByType<GameManager>();
        if (gm == null) { Debug.LogError("[DiagPostTutorial] GameManager not found!"); return; }

        // Force CompletedOrders = 1 to simulate post-tutorial state
        var completedField = typeof(GameManager).GetField("CompletedOrders",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        // CompletedOrders is a property — find the backing field or setter
        var prop = typeof(GameManager).GetProperty("CompletedOrders");
        // It's auto-property: set via backing field
        var backingField = typeof(GameManager).GetField("<CompletedOrders>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (backingField == null)
        {
            // Try to find field named differently
            foreach (var f in typeof(GameManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                log.AppendLine("  field: " + f.Name);
            Debug.Log("[DiagPostTutorial] Fields:\n" + log);
            return;
        }
        backingField.SetValue(gm, 1);
        log.AppendLine("CompletedOrders set to 1. Actual: " + gm.CompletedOrders);

        // Also null out ActiveOrder so AcceptOrder works
        var activeOrderProp = typeof(GameManager).GetProperty("ActiveOrder");
        var activeOrderBacking = typeof(GameManager).GetField("<ActiveOrder>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        activeOrderBacking?.SetValue(gm, null);

        // Also set GameStarted = true
        var gameStartedBacking = typeof(GameManager).GetField("<GameStarted>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        gameStartedBacking?.SetValue(gm, true);

        // Get available orders (post-tutorial should show all 3)
        var orders = gm.GetAvailableOrders();
        log.AppendLine("Available orders: " + orders.Length);
        foreach (var o in orders)
            log.AppendLine("  - " + (o != null ? o.name + " reward=" + o.Reward : "null"));

        // Find cooler_fridge order
        OrderDefinition targetOrder = null;
        foreach (var o in orders)
        {
            if (o != null && o.name.Contains("cooler"))
            { targetOrder = o; break; }
        }
        if (targetOrder == null && orders.Length > 0) targetOrder = orders[0];

        if (targetOrder == null)
        {
            log.AppendLine("No target order found!");
            Debug.Log(log.ToString());
            return;
        }

        log.AppendLine("\nAccepting order: " + targetOrder.name);
        log.AppendLine("RequiredParts count: " + targetOrder.RequiredParts.Length);
        foreach (var req in targetOrder.RequiredParts)
        {
            log.AppendLine("  Part: " + (req.Part != null ? req.Part.PartID + " visual=" + (req.Part.VisualPrefab != null ? req.Part.VisualPrefab.name : "NULL") : "NULL PART"));
        }

        // Also check SharedGhostPrefab
        var sharedGhostProp = typeof(OrderDefinition).GetField("SharedGhostPrefab",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (sharedGhostProp == null)
        {
            // Try properties
            var p = typeof(OrderDefinition).GetProperty("SharedGhostPrefab");
            if (p != null) log.AppendLine("SharedGhostPrefab (prop): " + (p.GetValue(targetOrder) != null ? "SET" : "null"));
            else log.AppendLine("SharedGhostPrefab: field/property not found in OrderDefinition");
        }
        else
        {
            var sgp = sharedGhostProp.GetValue(targetOrder);
            log.AppendLine("SharedGhostPrefab: " + (sgp != null ? sgp.ToString() : "null"));
        }

        // Accept the order
        bool accepted = gm.AcceptOrder(targetOrder);
        log.AppendLine("AcceptOrder result: " + accepted);

        // Check bench ghost
        var bench = Object.FindAnyObjectByType<AssemblyBench>();
        if (bench != null)
        {
            var ghostField = typeof(AssemblyBench).GetField("_ghostInstance",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var ghost = ghostField?.GetValue(bench) as GameObject;
            log.AppendLine("\n=== After AcceptOrder ===");
            log.AppendLine("_ghostInstance: " + (ghost != null ? ghost.name + " pos=" + ghost.transform.position + " active=" + ghost.activeSelf + " children=" + ghost.transform.childCount : "NULL"));

            var partGroupsField = typeof(AssemblyBench).GetField("_partGroups",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var partGroups = partGroupsField?.GetValue(bench) as System.Collections.Generic.Dictionary<string, object>;
            log.AppendLine("_partGroups: " + (partGroupsField != null ? "found" : "not found"));
        }

        // Check BoxWithPart.All after spawning
        log.AppendLine("\nBoxWithPart.All.Count=" + BoxWithPart.All.Count);
        int req2 = 0;
        foreach (var bwp in BoxWithPart.All)
            if (bwp != null && bwp.IsRequired) req2++;
        log.AppendLine("BoxWithPart required=" + req2);

        Debug.Log("[DiagPostTutorial]\n" + log);
    }
}
