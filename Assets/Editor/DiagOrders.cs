using UnityEngine;
using UnityEditor;
using System.Reflection;

public class DiagOrders
{
    public static void Execute()
    {
        var log = new System.Text.StringBuilder();

        // Check PartDefinition VisualPrefabs
        log.AppendLine("=== PartDefinition VisualPrefab check ===");
        var parts = Resources.LoadAll<PartDefinition>("Catalog/Parts");
        log.AppendLine("Parts loaded: " + parts.Length);
        foreach (var p in parts)
        {
            if (p == null) continue;
            string vpState = "NULL";
            string vpName  = "";
            if (p.VisualPrefab != null)
            {
                vpState = "OK";
                vpName  = p.VisualPrefab.name;
                var goRef = p.VisualPrefab as GameObject;
                vpState += " type=" + p.VisualPrefab.GetType().Name + (goRef != null ? " castOK" : " castFAIL");
            }
            log.AppendLine($"  [{p.PartID}] VisualPrefab={vpState} name='{vpName}'");
        }

        // Check orders
        log.AppendLine("\n=== OrderDefinition check ===");
        var orders = Resources.LoadAll<OrderDefinition>("Catalog/Orders");
        log.AppendLine("Orders loaded: " + orders.Length);
        foreach (var o in orders)
        {
            if (o == null) continue;
            log.AppendLine($"  [{o.name}] parts={o.RequiredParts?.Length ?? 0} SharedGhost={(o.SharedGhostPrefab != null ? o.SharedGhostPrefab.name : "null")}");
            if (o.RequiredParts != null)
                foreach (var req in o.RequiredParts)
                {
                    var vp = req.Part?.VisualPrefab;
                    log.AppendLine($"    part={req.Part?.PartID} vp={(vp != null ? vp.name : "NULL")}");
                }
        }

        // AssemblyReferences prefabs
        log.AppendLine("\n=== Resources/AssemblyReferences ===");
        var refs = Resources.LoadAll<GameObject>("AssemblyReferences");
        log.AppendLine("count=" + refs.Length);
        foreach (var r in refs) log.AppendLine("  " + r.name);

        Debug.Log(log.ToString());
    }
}
