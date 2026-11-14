using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Builds the ragdoll rig on Assets/Prefabs/Thief.prefab.
///
/// Deliberately not a reuse of SetupPlayerRagdoll: that one finds the player by tag and walks
/// hard-coded transform paths under Player/Skeleton, which the Zombie Slaughter rig does not
/// have. Bones here come from the humanoid avatar instead, and every capsule is sized from the
/// distance to the bone's own child, so the rig fits whichever zombie body the prefab was built
/// from rather than a set of numbers measured off one model.
///
/// Idempotent: existing rigidbodies, joints and bone colliders are stripped before rebuilding,
/// so re-running after swapping the body is safe.
public static class SetupThiefRagdoll
{
    const string ThiefPrefabPath = "Assets/Prefabs/Thief.prefab";

    /// Bone, its parent in the joint chain, and its share of body mass.
    static readonly (HumanBodyBones bone, HumanBodyBones parent, float mass)[] Chain =
    {
        (HumanBodyBones.Hips,          HumanBodyBones.LastBone, 12f),   // LastBone = no joint
        (HumanBodyBones.Spine,         HumanBodyBones.Hips,      8f),
        (HumanBodyBones.Chest,         HumanBodyBones.Spine,     8f),
        (HumanBodyBones.Head,          HumanBodyBones.Chest,     4f),

        (HumanBodyBones.LeftUpperArm,  HumanBodyBones.Chest,     2.5f),
        (HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftUpperArm, 1.8f),
        (HumanBodyBones.RightUpperArm, HumanBodyBones.Chest,     2.5f),
        (HumanBodyBones.RightLowerArm, HumanBodyBones.RightUpperArm, 1.8f),

        (HumanBodyBones.LeftUpperLeg,  HumanBodyBones.Hips,      5f),
        (HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftUpperLeg, 3.5f),
        (HumanBodyBones.RightUpperLeg, HumanBodyBones.Hips,      5f),
        (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightUpperLeg, 3.5f),
    };

    /// The bone each capsule is measured against. Ends of the chain have no child bone to
    /// measure to, so they borrow a length from their parent segment.
    static readonly Dictionary<HumanBodyBones, HumanBodyBones> Tip = new()
    {
        { HumanBodyBones.Hips,          HumanBodyBones.Spine },
        { HumanBodyBones.Spine,         HumanBodyBones.Chest },
        { HumanBodyBones.Chest,         HumanBodyBones.Head },
        { HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm },
        { HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand },
        { HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm },
        { HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand },
        { HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg },
        { HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot },
        { HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg },
        { HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot },
    };

    [MenuItem("Tools/Warehouse/Setup Thief Ragdoll")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder();

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ThiefPrefabPath);
        if (prefab == null) return "prefab not found: " + ThiefPrefabPath;

        // Edited through a prefab-contents scope: touching the loaded asset's transforms
        // directly does not reliably persist, and this rewires a dozen of them.
        var root = PrefabUtility.LoadPrefabContents(ThiefPrefabPath);

        try
        {
            var anim = root.GetComponentInChildren<Animator>(true);
            if (anim == null || !anim.isHuman)
                return "the thief body has no humanoid avatar — cannot map bones";

            var bones = new Dictionary<HumanBodyBones, Transform>();
            foreach (var (bone, _, _) in Chain)
            {
                var t = anim.GetBoneTransform(bone);
                if (t == null) return $"bone not mapped by the avatar: {bone}";
                bones[bone] = t;
            }

            StripOld(bones.Values, report);

            var bodies = new List<Rigidbody>();
            foreach (var (bone, parent, mass) in Chain)
                bodies.Add(BuildBone(anim, bones, bone, parent, mass));

            var ragdoll = root.GetComponent<ThiefRagdoll>();
            if (ragdoll == null) ragdoll = root.AddComponent<ThiefRagdoll>();
            ragdoll.EditorAssign(bodies.ToArray(), bones[HumanBodyBones.Hips].GetComponent<Rigidbody>());

            report.AppendLine($"• {bodies.Count} bones rigged, ThiefRagdoll wired.");

            PrefabUtility.SaveAsPrefabAsset(root, ThiefPrefabPath);
            report.AppendLine($"• saved {ThiefPrefabPath}");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        return report.ToString();
    }

    static void StripOld(IEnumerable<Transform> bones, StringBuilder report)
    {
        int removed = 0;

        foreach (var bone in bones)
        {
            foreach (var c in bone.GetComponents<CharacterJoint>()) { Object.DestroyImmediate(c); removed++; }
            foreach (var c in bone.GetComponents<Collider>())       { Object.DestroyImmediate(c); removed++; }
            foreach (var c in bone.GetComponents<Rigidbody>())      { Object.DestroyImmediate(c); removed++; }
        }

        if (removed > 0) report.AppendLine($"• stripped {removed} components from a previous rig.");
    }

    static Rigidbody BuildBone(Animator anim, Dictionary<HumanBodyBones, Transform> bones,
                               HumanBodyBones bone, HumanBodyBones parent, float mass)
    {
        var t = bones[bone];

        var rb = t.gameObject.AddComponent<Rigidbody>();
        rb.mass          = mass;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.isKinematic   = true;          // ThiefRagdoll wakes them when he goes down
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        AddCapsule(anim, t, bone);

        if (parent == HumanBodyBones.LastBone) return rb;

        var joint = t.gameObject.AddComponent<CharacterJoint>();
        joint.connectedBody = bones[parent].GetComponent<Rigidbody>();

        // Generous limits: this is a corpse being thrown, not a wrestler. Tight limits on a rig
        // whose bone axes are not known ahead of time is how you get a body that locks up in
        // mid-air instead of flopping.
        joint.axis       = Vector3.right;
        joint.swingAxis  = Vector3.forward;
        joint.lowTwistLimit  = new SoftJointLimit { limit = -35f };
        joint.highTwistLimit = new SoftJointLimit { limit =  35f };
        joint.swing1Limit    = new SoftJointLimit { limit =  45f };
        joint.swing2Limit    = new SoftJointLimit { limit =  30f };
        joint.enablePreprocessing = false;   // preprocessing makes long chains snap straight

        return rb;
    }

    /// Capsule laid along the bone, sized from the distance to whatever comes next down the
    /// chain. Falls back to a fraction of the character's height for a bone with no measurable
    /// child, so nothing ends up with a zero-length collider.
    static void AddCapsule(Animator anim, Transform t, HumanBodyBones bone)
    {
        float length = 0.2f;

        if (Tip.TryGetValue(bone, out var tipBone))
        {
            var tip = anim.GetBoneTransform(tipBone);
            if (tip != null) length = Vector3.Distance(t.position, tip.position);
        }

        length = Mathf.Max(length, 0.08f);

        var col = t.gameObject.AddComponent<CapsuleCollider>();
        col.height = length;
        col.radius = Mathf.Clamp(length * 0.3f, 0.04f, 0.13f);

        // Which local axis actually points down the bone varies per rig, so it is measured
        // rather than assumed: whichever local axis the child sits furthest along wins.
        Vector3 toTip = Tip.TryGetValue(bone, out var tb) && anim.GetBoneTransform(tb) != null
            ? t.InverseTransformPoint(anim.GetBoneTransform(tb).position)
            : Vector3.up * length;

        var abs = new Vector3(Mathf.Abs(toTip.x), Mathf.Abs(toTip.y), Mathf.Abs(toTip.z));
        col.direction = abs.x >= abs.y && abs.x >= abs.z ? 0
                      : abs.y >= abs.z                   ? 1
                      : 2;

        // Centred halfway down the bone rather than on the joint, or every capsule would stick
        // out of the back of the body it belongs to.
        col.center = toTip * 0.5f;
    }
}
