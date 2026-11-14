using UnityEngine;
using UnityEngine.AI;

/// How big the warehouse floor actually is.
///
/// It replaces a rectangle that was typed in when the demo was small and never revisited:
/// `X -20..12, Z -30..8`, which is 32 by 38 metres. Measured, this warehouse is **85 by 110**
/// (`X -48..37, Z -55..55`). Everything that used those numbers to pick a spot — where a toolkit
/// lands, where a weapon is scattered, which way a raider leaves — was confined to a corner of a
/// building nearly nine times the size, and the failure was invisible: things still spawned, just
/// always in the same quarter.
///
/// Taken from the navmesh rather than from renderers, because the question every caller is really
/// asking is "somewhere a person can stand", and that is exactly what the navmesh is. A floor that
/// is rebaked wider or narrower moves these numbers with it, and nothing has to be edited.
public static class WarehouseBounds
{
    /// The old rectangle, kept only as the answer when there is no navmesh at all — an unbaked
    /// scene, or a unit test. Wrong, but wrong in a familiar way rather than returning nothing.
    static readonly Bounds Fallback = new(new Vector3(-4f, 0.5f, -11f), new Vector3(32f, 4f, 38f));

    static Bounds _floor;
    static bool   _known;

    /// The walkable floor, in world space.
    public static Bounds Floor
    {
        get
        {
            if (_known) return _floor;

            _floor = Measure();
            _known = true;
            return _floor;
        }
    }

    /// Forces a re-measure. Worth calling after the navmesh is rebuilt at runtime; the value is
    /// otherwise computed once, since triangulating the whole mesh is not free.
    public static void Invalidate() => _known = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ResetOnLoad() => Invalidate();

    static Bounds Measure()
    {
        var mesh = NavMesh.CalculateTriangulation();
        if (mesh.vertices == null || mesh.vertices.Length == 0) return Fallback;

        var bounds = new Bounds(mesh.vertices[0], Vector3.zero);
        for (int i = 1; i < mesh.vertices.Length; i++) bounds.Encapsulate(mesh.vertices[i]);

        return bounds;
    }

    /// A random spot over the floor, at the given height. Not guaranteed walkable on its own —
    /// callers that need that sample the navmesh or raycast down, which they were doing anyway.
    public static Vector3 RandomPoint(float y)
    {
        var b = Floor;
        return new Vector3(Random.Range(b.min.x, b.max.x), y, Random.Range(b.min.z, b.max.z));
    }

    /// A random spot on the edge of the floor, inset so it does not land in a wall.
    public static Vector3 RandomEdgePoint(float y, float inset = 1f)
    {
        var b = Floor;

        return Random.Range(0, 4) switch
        {
            0 => new Vector3(b.min.x + inset,                    y, Random.Range(b.min.z, b.max.z)),
            1 => new Vector3(b.max.x - inset,                    y, Random.Range(b.min.z, b.max.z)),
            2 => new Vector3(Random.Range(b.min.x, b.max.x),     y, b.min.z + inset),
            _ => new Vector3(Random.Range(b.min.x, b.max.x),     y, b.max.z - inset),
        };
    }
}
