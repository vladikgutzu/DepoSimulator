using UnityEngine;
using UnityEditor;

public class DiagPallets
{
    public static void Execute()
    {
        var log = new System.Text.StringBuilder();

        // Find first pallet_boxes_prefab or pallet_prefab and measure it
        var palletGrp = GameObject.Find("Pallet_GRP_01");
        if (palletGrp == null) { Debug.LogError("Pallet_GRP_01 not found"); return; }

        log.AppendLine($"Pallet_GRP_01 pos={palletGrp.transform.position}");
        log.AppendLine($"Children: {palletGrp.transform.childCount}");

        for (int i = 0; i < palletGrp.transform.childCount; i++)
        {
            var child = palletGrp.transform.GetChild(i);
            var rends = child.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) { log.AppendLine($"  [{i}] {child.name} — no renderers"); continue; }

            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);

            log.AppendLine($"  [{i}] {child.name}");
            log.AppendLine($"       worldPos={child.position:F3}");
            log.AppendLine($"       bounds min={b.min:F3} max={b.max:F3}");
            log.AppendLine($"       size={b.size:F3}");
            log.AppendLine($"       surfaceY={b.min.y:F3}");
            log.AppendLine($"       components: {string.Join(", ", System.Array.ConvertAll(child.GetComponents<Component>(), c => c.GetType().Name))}");
        }

        // Also check a sample box on pallet
        log.AppendLine("\n--- Sample boxes ---");
        int found = 0;
        foreach (Transform t in palletGrp.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.ToLower().Contains("box")) continue;
            var rends = t.GetComponents<Renderer>();
            if (rends.Length == 0) continue;
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            log.AppendLine($"  {t.name} size={b.size:F3}");
            if (++found >= 5) break;
        }

        Debug.Log("[DiagPallets]\n" + log);
    }
}
