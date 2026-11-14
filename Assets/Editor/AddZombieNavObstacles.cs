using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

/// Stops the raiders walking through the warehouse's furniture.
///
/// The raiders are NavMeshAgents and go wherever the baked mesh says there is floor. The bake only
/// reads the navMeshObjects layer, so everything on any other layer — the corner fences, the low
/// forklift shelving, the assembly benches, the shipping zone, the order board — was baked as open
/// floor and walked straight through. So were the vehicles, the cart and the ladder, which could
/// not be baked in any case because they move.
///
/// Rather than moving props between layers (the camera filter and other code read those layers)
/// and rebaking, each such object gets a carving NavMeshObstacle sized to its own collider:
///
///   • Found, not listed: any solid, body-less collider standing on the floor whose footprint is
///     still walkable mesh. That is precisely the "walks through it" condition, so a prop added
///     later is caught by running this again, and nothing that already blocks is touched.
///   • Equipment that moves — forklifts, pallet truck, cart, ladder — found by component. Those
///     carve only while standing still (carveOnlyStationary), and are steered around by the
///     agents' own avoidance while they are driven.
///
/// Loose boxes are handled at runtime (FloorBoxNavObstacles) because they move all the time.
/// Marks the open scene modified; saving is left to whoever runs it. Re-runnable: an object that
/// already has an obstacle is left alone.
public static class AddZombieNavObstacles
{
    static readonly int NavMeshLayer = LayerMask.NameToLayer("navMeshObjects");

    [MenuItem("Tools/Warehouse/Add Zombie Nav Obstacles")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        if (Application.isPlaying) return "edit mode only — this writes to the scene";

        var report  = new StringBuilder("[ZombieNav]\n");
        var counts  = new Dictionary<string, int>();
        var done    = new HashSet<GameObject>();

        // 1. Moving equipment, by component.
        var movers = new List<Component>();
        movers.AddRange(Object.FindObjectsByType<ReachTruckController>(FindObjectsSortMode.None));
        movers.AddRange(Object.FindObjectsByType<CartController>(FindObjectsSortMode.None));
        movers.AddRange(Object.FindObjectsByType<LadderController>(FindObjectsSortMode.None));

        foreach (var m in movers)
            if (AddObstacle(m.gameObject, done)) Count(counts, m.GetType().Name);

        // 2. Static props the mesh runs through.
        foreach (var col in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
        {
            if (col.isTrigger || !col.enabled || col.attachedRigidbody != null) continue;
            // Already baked. If its middle is still walkable, that is by design: a wall whose
            // bounds straddle a doorway, a rack with an aisle through it. A box over those bounds
            // would brick the doorway up — including the gates the raiders come in by.
            if (col.gameObject.layer == NavMeshLayer) continue;
            if (col.GetComponentInParent<ThiefController>() != null) continue;
            if (col.GetComponentInParent<CharacterController>() != null) continue;
            if (movers.Any(m => col.transform.IsChildOf(m.transform))) continue;

            var b = col.bounds;
            if (b.min.y > 0.4f || b.max.y < 0.3f) continue;         // not standing on the floor
            if (b.size.x < 0.3f && b.size.z < 0.3f) continue;        // thinner than an agent
            if (b.size.x > 15f || b.size.z > 15f) continue;          // floor slabs and outer walls

            var foot = new Vector3(b.center.x, 0f, b.center.z);
            if (!NavMesh.SamplePosition(foot, out _, 0.15f, NavMesh.AllAreas)) continue;   // blocks already

            if (AddObstacle(col.gameObject, done)) Count(counts, Label(col.transform));
        }

        foreach (var kv in counts.OrderByDescending(k => k.Value))
            report.AppendLine($"• {kv.Key} ×{kv.Value}");
        report.AppendLine($"total {counts.Values.Sum()} obstacles");

        // Marked, not saved: the scene may be carrying someone's unsaved work, and saving it from a
        // tool would commit that too without anyone having looked at it.
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        report.AppendLine("scene marked modified — save it (Ctrl+S) to keep the obstacles");
        return report.ToString();
    }

    /// One carving obstacle per object, boxed around that object's colliders in its own space.
    static bool AddObstacle(GameObject go, HashSet<GameObject> done)
    {
        if (!done.Add(go)) return false;
        if (go.GetComponent<NavMeshObstacle>() != null) return false;

        var cols = go.GetComponentsInChildren<Collider>(true)
                     .Where(c => !c.isTrigger && c.GetComponentInParent<ThiefController>() == null)
                     .ToArray();
        if (cols.Length == 0) return false;

        // Collider bounds are world-aligned; bring their corners into this object's space.
        var t = go.transform;
        bool any = false;
        var local = new Bounds();
        foreach (var c in cols)
        {
            var wb = c.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = wb.center + Vector3.Scale(wb.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = t.InverseTransformPoint(corner);
                if (!any) { local = new Bounds(p, Vector3.zero); any = true; }
                else local.Encapsulate(p);
            }
        }

        var obstacle = Undo.AddComponent<NavMeshObstacle>(go);
        obstacle.shape               = NavMeshObstacleShape.Box;
        obstacle.center              = local.center;
        obstacle.size                = local.size;
        obstacle.carving             = true;
        // Static props never move, so this changes nothing for them; for the vehicles it means
        // carve when parked and be avoided, not re-carved every frame, while driven.
        obstacle.carveOnlyStationary = true;
        return true;
    }

    static string Label(Transform t)
    {
        while (t.parent != null && t.parent.parent != null) t = t.parent;
        return System.Text.RegularExpressions.Regex.Replace(t.name, @"\s*\(\d+\)$", "");
    }

    static void Count(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
}
