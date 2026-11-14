using UnityEngine;
using System.Reflection;

public class FixCarryScaleRuntime
{
    public static void Execute()
    {
        var field = typeof(BoxPhysicsInteractable)
            .GetField("_carryMaxSize", BindingFlags.NonPublic | BindingFlags.Instance);

        if (field == null) { Debug.LogError("[FixCarry] _carryMaxSize field not found"); return; }

        int count = 0;
        foreach (var comp in Object.FindObjectsByType<BoxPhysicsInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            field.SetValue(comp, 0.6f);
            count++;
        }
        Debug.Log($"[FixCarry] Set _carryMaxSize=0.55 on {count} runtime instances.");

        // Diagnose: find the held box and print full transform hierarchy with scales
        var grabbed = typeof(BoxPhysicsInteractable)
            .GetField("_grabbed", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as BoxPhysicsInteractable;

        if (grabbed != null)
        {
            Debug.Log($"[FixCarry] Currently grabbed: '{grabbed.name}'");
            var t = grabbed.transform;
            while (t != null)
            {
                Debug.Log($"[FixCarry]   '{t.name}'  localScale={t.localScale}  worldScale={t.lossyScale}");
                t = t.parent;
            }
        }
        else
        {
            Debug.Log("[FixCarry] No box currently grabbed.");
        }
    }
}
