using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Text;

public class DiagPalletContents
{
    public static void Execute()
    {
        var log = new StringBuilder();

        // Find one of each pallet type
        var allPallets = Object.FindObjectsByType<PalletBehavior>(FindObjectsSortMode.None);

        // Group by prefab name (root name without index suffix)
        var byType = new Dictionary<string, List<GameObject>>();
        foreach (var pb in allPallets)
        {
            string n = pb.name;
            // Strip trailing " (N)" suffix to get type name
            int paren = n.IndexOf(" (");
            string key = paren >= 0 ? n.Substring(0, paren) : n;
            if (!byType.ContainsKey(key)) byType[key] = new();
            byType[key].Add(pb.gameObject);
        }

        foreach (var kv in byType)
        {
            string typeName = kv.Key;
            var instances = kv.Value;
            log.AppendLine($"\n=== {typeName}  ({instances.Count} instances) ===");

            // Inspect first instance
            var root = instances[0];
            log.AppendLine($"  Sample: '{root.name}'  parent='{root.transform.parent?.name}'  pos={root.transform.position:F2}");

            // Walk ALL children, log meshes and colliders
            var allChildren = root.GetComponentsInChildren<Transform>(true);
            var meshes   = new List<string>();
            var colliders = new List<string>();

            foreach (var t in allChildren)
            {
                if (t == root.transform) continue;

                bool hasMesh    = t.GetComponent<MeshRenderer>()    != null || t.GetComponent<SkinnedMeshRenderer>() != null;
                bool hasCollider = t.GetComponent<Collider>()       != null;
                bool hasRB       = t.GetComponent<Rigidbody>()      != null;
                bool hasBPI      = t.GetComponent<BoxPhysicsInteractable>() != null;

                string flags = "";
                if (hasMesh)     flags += " [Mesh]";
                if (hasCollider) flags += " [Collider]";
                if (hasRB)       flags += " [RB]";
                if (hasBPI)      flags += " [BoxPhysics]";

                if (flags.Length > 0)
                    log.AppendLine($"    child: '{t.name}'{flags}  localPos={t.localPosition:F3}  localScale={t.localScale:F2}");
            }

            // Count direct children by name
            var childNames = new Dictionary<string, int>();
            foreach (Transform ch in root.transform)
            {
                string cn = ch.name;
                int p = cn.IndexOf(" ("); if (p >= 0) cn = cn.Substring(0, p);
                childNames.TryGetValue(cn, out int cnt);
                childNames[cn] = cnt + 1;
            }

            log.AppendLine($"  Direct children ({root.transform.childCount} total):");
            foreach (var ck in childNames)
                log.AppendLine($"    '{ck.Key}' × {ck.Value}");

            // Measure bounding box of whole pallet
            var rends = root.GetComponentsInChildren<Renderer>(true);
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                log.AppendLine($"  World bounds: size={b.size:F3}  surfaceY≈{b.min.y:F3}  topY≈{b.max.y:F3}");
            }
        }

        Debug.Log("[DiagPalletContents]\n" + log);
    }
}
