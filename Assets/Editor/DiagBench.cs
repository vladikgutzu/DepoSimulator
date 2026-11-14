using UnityEngine;
using System.Text;

public static class DiagBench
{
    public static string Execute()
    {
        var sb = new StringBuilder();

        // Find bench via Instance and via FindObjects
        var byInstance    = AssemblyBench.Instance;
        var byFindActive  = Object.FindFirstObjectByType<AssemblyBench>(FindObjectsInactive.Exclude);
        var byFindAll     = Object.FindFirstObjectByType<AssemblyBench>(FindObjectsInactive.Include);

        sb.AppendLine($"AssemblyBench.Instance:      {byInstance != null}  path={GetPath(byInstance)}");
        sb.AppendLine($"FindObjects (active only):   {byFindActive != null}  path={GetPath(byFindActive)}  active={byFindActive?.gameObject.activeInHierarchy}");
        sb.AppendLine($"FindObjects (incl inactive): {byFindAll != null}    path={GetPath(byFindAll)}  active={byFindAll?.gameObject.activeInHierarchy}");

        // Check both bench GOs in hierarchy
        var gprBench = GameObject.Find("GameplayRoot/AssemblyBench");
        var wrhBench = GameObject.Find("Warehouse/AssemblyBench");
        sb.AppendLine($"\nGameplayRoot/AssemblyBench: {gprBench != null}  active={gprBench?.activeInHierarchy}");
        sb.AppendLine($"Warehouse/AssemblyBench:    {wrhBench != null}  active={wrhBench?.activeInHierarchy}");

        // HUD
        var hud = HUDController.Instance;
        sb.AppendLine($"\nHUDController.Instance:     {hud != null}");

        // Active GO
        if (gprBench != null)
        {
            var ab = gprBench.GetComponent<AssemblyBench>();
            sb.AppendLine($"\nGameplayRoot bench component: {ab != null}");
            sb.AppendLine($"  IsOrderComplete={ab?.IsOrderComplete()}");
        }

        return sb.ToString();
    }

    static string GetPath(MonoBehaviour mb)
    {
        if (mb == null) return "null";
        var parts = new System.Collections.Generic.List<string>();
        var t = mb.transform;
        int depth = 0;
        while (t != null && depth++ < 6) { parts.Insert(0, t.name); t = t.parent; }
        return string.Join("/", parts);
    }
}
