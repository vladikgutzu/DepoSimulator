using System.Collections.Generic;
using UnityEngine;

/// Where a rigged body's surface actually is, bone by bone.
///
/// "Is any part of him through the floor right now" has to be answered the same way in two places:
/// by the local player's RagdollController, and by the puppet every other client watches through
/// RemoteRagdollVisual. Two copies of this measurement would drift, and a get-up would then be
/// grounded one way on the screen of the player doing it and another way on everybody else's.
public class BodyContactPoints
{
    readonly Transform   _root;
    readonly Rigidbody[] _bones;
    readonly Animator    _animator;

    readonly Dictionary<Transform, Vector3[]> _points = new();
    bool _measured;

    public BodyContactPoints(Transform root, Rigidbody[] bones, Animator animator)
    {
        _root     = root;
        _bones    = bones ?? System.Array.Empty<Rigidbody>();
        _animator = animator;
    }

    /// Collects, per bone, the outermost points of the geometry it carries, in that bone's space.
    ///
    /// This replaced measuring the body through its ragdoll capsules, which are the wrong shape
    /// for the job in both directions. A capsule is a cylinder of the bone's WIDEST radius: at the
    /// pelvis that is the hip flare, so its underside sits well below the actual belly and the
    /// clamp hung the body a hand's width in the air. And the rig has no feet or hands at all —
    /// it stops at the shins and forearms — so the parts that actually take the weight during a
    /// get-up were not covered by anything.
    ///
    /// A box turns with its bone, so one measurement answers for a foot flat on the floor and for
    /// the same foot pointed at the ceiling — which a sphere cannot do, since it reads a toe
    /// reaching forward as a toe reaching down.
    ///
    /// Measured once, off whatever pose the body is in, treating each vertex as rigidly attached
    /// to the bone that owns it. Good enough for a shape that only has to be close.
    void Measure()
    {
        if (_measured) return;
        _measured = true;

        var measured = new HashSet<Transform>();
        foreach (var rb in _bones)
            if (rb != null) measured.Add(rb.transform);

        if (_animator != null && _animator.isHuman)
            foreach (var b in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
                                      HumanBodyBones.LeftHand, HumanBodyBones.RightHand })
            {
                var t = _animator.GetBoneTransform(b);
                if (t != null) measured.Add(t);
            }

        if (measured.Count == 0) return;

        // Geometry belongs to the nearest measured bone at or above it: a toe to the foot, a
        // finger to the hand, a foot to itself rather than to the shin two joints up.
        var ownerOf = new Dictionary<Transform, Transform>();
        Transform Owner(Transform t)
        {
            if (t == null) return null;
            if (ownerOf.TryGetValue(t, out var cached)) return cached;

            Transform found = null;
            for (var p = t; p != null; p = p.parent)
                if (measured.Contains(p)) { found = p; break; }

            ownerOf[t] = found;
            return found;
        }

        // Real surface points, not a box. A box turns with the bone correctly, but its corners are
        // places where the body is not: the pelvis box takes its width from the hip flare, so its
        // lower corner hangs a hand's width below the actual belly, and clamping to that left the
        // body hovering. Keeping the furthest actual VERTEX along each of a handful of directions
        // gives points that exist on the mesh, and the lowest of them is what is touching.
        var extremes = new Dictionary<Transform, Vector3[]>();
        var bestDot  = new Dictionary<Transform, float[]>();

        void Add(Transform owner, Vector3 world)
        {
            Vector3 local = owner.InverseTransformPoint(world);

            if (!extremes.TryGetValue(owner, out var pts))
            {
                pts = new Vector3[SupportDirs.Length];
                var dots = new float[SupportDirs.Length];
                for (int d = 0; d < dots.Length; d++) dots[d] = float.NegativeInfinity;
                extremes[owner] = pts;
                bestDot[owner]  = dots;
            }

            var best = bestDot[owner];
            for (int d = 0; d < SupportDirs.Length; d++)
            {
                float dot = Vector3.Dot(local, SupportDirs[d]);
                if (dot <= best[d]) continue;
                best[d] = dot;
                pts[d]  = local;
            }
        }

        foreach (var smr in _root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            var mesh = smr.sharedMesh;
            if (mesh == null || smr.bones == null) continue;

            var weights   = mesh.boneWeights;
            var vertices  = mesh.vertices;
            var bindposes = mesh.bindposes;
            if (weights.Length == 0) continue;

            for (int v = 0; v < weights.Length && v < vertices.Length; v++)
            {
                int i = weights[v].boneIndex0;
                if (i < 0 || i >= smr.bones.Length || i >= bindposes.Length) continue;

                var bone = smr.bones[i];
                var owner = Owner(bone);
                if (bone == null || owner == null) continue;

                Add(owner, (bone.localToWorldMatrix * bindposes[i]).MultiplyPoint3x4(vertices[v]));
            }
        }

        // Rigid parts bolted onto the skeleton — the HumanBot wears a panel on nearly every bone.
        foreach (var mf in _root.GetComponentsInChildren<MeshFilter>(false))
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null || !mr.enabled || mf.sharedMesh == null) continue;

            var owner = Owner(mf.transform);
            if (owner == null) continue;

            var m = mf.transform.localToWorldMatrix;
            foreach (var v in mf.sharedMesh.vertices) Add(owner, m.MultiplyPoint3x4(v));
        }

        foreach (var pair in extremes)
            _points[pair.Key] = pair.Value;
    }

    /// The directions the surface is sampled along: the six axes and the eight diagonals. Enough
    /// that whichever way a limb is turned, one of the kept points is close to whatever part of it
    /// is pointing at the floor.
    static readonly Vector3[] SupportDirs =
    {
        Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back,
        new Vector3( 1,  1,  1).normalized, new Vector3( 1,  1, -1).normalized,
        new Vector3( 1, -1,  1).normalized, new Vector3( 1, -1, -1).normalized,
        new Vector3(-1,  1,  1).normalized, new Vector3(-1,  1, -1).normalized,
        new Vector3(-1, -1,  1).normalized, new Vector3(-1, -1, -1).normalized,
    };

    /// The lowest point of the body that should be touching the ground, in world space.
    /// float.MaxValue when there is nothing measurable, which callers read as "leave it alone".
    public float LowestY()
    {
        Measure();

        float lowest = float.MaxValue;
        foreach (var pair in _points)
        {
            if (pair.Key == null) continue;
            foreach (var point in pair.Value)
                lowest = Mathf.Min(lowest, pair.Key.TransformPoint(point).y);
        }
        return lowest;
    }


}
