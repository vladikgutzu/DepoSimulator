using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// Where thieves walk in from and where they run out to.
///
/// Entirely optional: drop this on any GameObject and assign door/gate transforms for
/// hand-authored entrances. With no instance in the scene both pickers fall back to sampling
/// the baked navmesh around the warehouse perimeter, so ThiefEvent works in a scene that has
/// never been touched for it.
public class ThiefSpawnPoints : MonoBehaviour
{
    public static ThiefSpawnPoints Instance { get; private set; }

    [Tooltip("Where a thief appears. Leave empty to sample the navmesh perimeter instead.")]
    [SerializeField] Transform[] _entries;

    [Tooltip("Where a thief runs to escape. Falls back to the entries, then to the perimeter.")]
    [SerializeField] Transform[] _exits;

    // Perimeter sampling box — matches the floor extents ToolkitItem.ChooseSpawnPosition uses.
    // The floor extents used to live here as four typed-in numbers covering 32 by 38 metres, in a
    // warehouse measuring 85 by 110. Everything that sampled them was confined to a corner of the
    // building. They now come from the navmesh — see WarehouseBounds.

    /// Two spawns closer together than this count as the same door.
    const float DoorSeparation = 10f;

    /// Large enough to outweigh any distance-from-players difference, so an unused door always
    /// beats a used one — but finite, so a raid bigger than the door count still spawns.
    const float UsedDoorPenalty = 1000f;

    void OnEnable()  { Instance = this; }
    void OnDisable() { if (Instance == this) Instance = null; }

    // ── Public API ────────────────────────────────────────────────────────────

    /// A navmesh position for a thief to appear at, biased away from the players so nobody
    /// watches one materialise in front of them.
    ///
    /// Pass the spawns already picked for this raid in <paramref name="taken"/> and each
    /// raider gets a different door. Both pickers below are "best candidate wins" and so
    /// deterministic within a single raid — without this the whole wave filed in through one
    /// entrance, which reads as a smaller raid than it is.
    public static Vector3 PickSpawnPoint(List<Vector3> taken = null)
    {
        var authored = Instance != null ? Instance._entries : null;
        if (HasAny(authored))
            return FarthestFromPlayers(authored, taken);

        return SamplePerimeter(preferFarFromPlayers: true, Vector3.zero, useReference: false, taken);
    }

    /// A navmesh position for a thief to escape through, preferring one he is already
    /// heading towards rather than one across the whole warehouse.
    public static Vector3 PickExitPoint(Vector3 from)
    {
        var inst = Instance;

        if (inst != null && HasAny(inst._exits)) return NearestTo(inst._exits, from);
        if (inst != null && HasAny(inst._entries)) return NearestTo(inst._entries, from);

        // Then the gates, which are the only real doors this building has.
        //
        // Worth preferring over anything computed: _exits and _entries are both empty in this
        // scene and always have been, so before this every escaping raider was walking to a point
        // on the edge of a sampled rectangle and vanishing in the middle of the floor. A gate is
        // somewhere a person could actually leave through.
        var gate = NearestGate(from);
        if (gate.HasValue) return gate.Value;

        return SamplePerimeter(preferFarFromPlayers: false, from, useReference: true);
    }

    /// The inside of the nearest gate a raid can come through, or null if none is marked.
    static Vector3? NearestGate(Vector3 from)
    {
        Vector3? best = null;
        float bestDist = float.MaxValue;

        foreach (var gate in GateAnimator.Marked())
        {
            if (!gate.TryGetSides(out Vector3 inside, out Vector3 _)) continue;

            float d = Vector3.Distance(from, inside);
            if (d >= bestDist) continue;

            bestDist = d;
            best     = inside;
        }

        return best;
    }

    /// A navmesh position somewhere inside the warehouse for a thief to move through on his
    /// way in. Deliberately not the shortest route to his target: a raider who crosses open
    /// floor first is a raider the team can see coming.
    public static Vector3 PickWanderPoint(Vector3 from)
    {
        Vector3 best      = from;
        float   bestScore = float.MinValue;
        bool    found     = false;

        for (int i = 0; i < 30; i++)
        {
            var candidate = WarehouseBounds.RandomPoint(0.5f);

            if (!NavMesh.SamplePosition(candidate, out var hit, 5f, NavMesh.AllAreas)) continue;

            // Far enough to be an actual walk across the floor, not a shuffle in place.
            float d = Flat(hit.position, from);
            float score = d < 8f ? d - 100f : -Mathf.Abs(d - 16f);

            if (score > bestScore) { bestScore = score; best = hit.position; found = true; }
        }

        return found ? best : from;
    }

    // ── Authored-transform pickers ────────────────────────────────────────────

    static bool HasAny(Transform[] points)
    {
        if (points == null) return false;
        foreach (var t in points)
            if (t != null) return true;
        return false;
    }

    static Vector3 FarthestFromPlayers(Transform[] points, List<Vector3> taken)
    {
        var players = PlayerPositions();

        Vector3 best      = Vector3.zero;
        float   bestScore = float.MinValue;

        foreach (var t in points)
        {
            if (t == null) continue;

            float nearest = 999f;
            foreach (var p in players)
                nearest = Mathf.Min(nearest, Flat(t.position, p));

            float score = nearest - (IsTaken(t.position, taken) ? UsedDoorPenalty : 0f);

            if (score > bestScore) { bestScore = score; best = t.position; }
        }

        return Snap(best);
    }

    /// True when this raid already sent someone in through (near enough to) that spot.
    static bool IsTaken(Vector3 pos, List<Vector3> taken)
    {
        if (taken == null) return false;

        foreach (var t in taken)
            if (Flat(pos, t) < DoorSeparation) return true;

        return false;
    }

    static Vector3 NearestTo(Transform[] points, Vector3 from)
    {
        Vector3 best = from;
        float bestDist = float.MaxValue;

        foreach (var t in points)
        {
            if (t == null) continue;
            float d = Flat(t.position, from);
            if (d < bestDist) { bestDist = d; best = t.position; }
        }

        return Snap(best);
    }

    // ── Navmesh perimeter fallback ────────────────────────────────────────────

    // Samples random points around the edge of the floor box and keeps the best one: the
    // farthest from the players when spawning, the closest to the thief when escaping.
    static Vector3 SamplePerimeter(bool preferFarFromPlayers, Vector3 reference, bool useReference,
                                   List<Vector3> taken = null)
    {
        var players = PlayerPositions();

        Vector3 best      = Vector3.zero;
        float   bestScore = float.MinValue;
        bool    found     = false;

        for (int i = 0; i < 40; i++)
        {
            Vector3 candidate = RandomEdgePoint();
            if (!NavMesh.SamplePosition(candidate, out var hit, 6f, NavMesh.AllAreas)) continue;

            float score;
            if (useReference)
            {
                score = -Flat(hit.position, reference);
            }
            else
            {
                float nearest = 999f;
                foreach (var p in players)
                    nearest = Mathf.Min(nearest, Flat(hit.position, p));
                score = preferFarFromPlayers ? nearest : -nearest;
            }

            if (IsTaken(hit.position, taken)) score -= UsedDoorPenalty;

            if (score > bestScore) { bestScore = score; best = hit.position; found = true; }
        }

        if (found) return best;

        // Nothing sampled — fall back to any navmesh position near the warehouse centre so a
        // spawned thief is at least on the mesh and able to path.
        Vector3 centre = WarehouseBounds.Floor.center;
        return NavMesh.SamplePosition(centre, out var mid, 25f, NavMesh.AllAreas)
            ? mid.position
            : centre;
    }

    static Vector3 RandomEdgePoint() => WarehouseBounds.RandomEdgePoint(0.5f);

    static Vector3 Snap(Vector3 pos) =>
        NavMesh.SamplePosition(pos, out var hit, 5f, NavMesh.AllAreas) ? hit.position : pos;

    static List<Vector3> PlayerPositions()
    {
        var result = new List<Vector3>(4);

        var local = PlayerFinder.Find();
        if (local != null) result.Add(local.position);

        foreach (var np in NetworkPlayer.All)
            if (np != null) result.Add(np.transform.position);

        return result;
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        a.y = b.y;
        return Vector3.Distance(a, b);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.9f, 0.3f, 0.9f);
        if (_entries != null)
            foreach (var t in _entries)
                if (t != null) Gizmos.DrawWireSphere(t.position, 0.6f);

        Gizmos.color = new Color(0.95f, 0.35f, 0.2f, 0.9f);
        if (_exits != null)
            foreach (var t in _exits)
                if (t != null) Gizmos.DrawWireSphere(t.position, 0.6f);
    }
#endif
}
