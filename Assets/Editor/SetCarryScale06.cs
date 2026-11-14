using UnityEngine;
using System.Reflection;

public class SetCarryScale06
{
    public static void Execute()
    {
        var field = typeof(BoxPhysicsInteractable)
            .GetField("_carryMaxSize", BindingFlags.NonPublic | BindingFlags.Instance);

        if (field == null) { Debug.LogError("[SetCarryScale06] _carryMaxSize not found"); return; }

        int count = 0;
        foreach (var comp in Object.FindObjectsByType<BoxPhysicsInteractable>(FindObjectsInactive.Include))
        {
            field.SetValue(comp, 0.6f);
            count++;
        }
        Debug.Log($"[SetCarryScale06] Done. _carryMaxSize=0.6 on {count} instances.");
    }
}
