using System.Collections.Generic;
using UnityEngine;

/// Turns a raider's animated skeleton into a physics ragdoll for the rest of his short life.
///
/// Deliberately one-way, unlike the player's RagdollController: a raider only ever ragdolls
/// because he has just been put down, so there is no get-up, no pose blend and no settle
/// watchdog — he flies, he lands, ThiefController despawns him. That is most of why this is a
/// separate component rather than a reuse of the player's.
///
/// The bone rig is built by Tools/Warehouse/Setup Thief Ragdoll. With no rig assigned this
/// reports HasRig false and ThiefController falls back to the death animation, so a raider whose
/// prefab has never been through the tool still dies properly.
[DisallowMultipleComponent]
public class ThiefRagdoll : MonoBehaviour
{
    readonly struct BoneSpec
    {
        public readonly HumanBodyBones Bone;
        public readonly HumanBodyBones Parent;
        public readonly float Mass;

        public BoneSpec(HumanBodyBones bone, HumanBodyBones parent, float mass)
        {
            Bone = bone;
            Parent = parent;
            Mass = mass;
        }
    }

    static readonly BoneSpec[] RuntimeChain =
    {
        new(HumanBodyBones.Hips,          HumanBodyBones.LastBone,      12f),
        new(HumanBodyBones.Spine,         HumanBodyBones.Hips,           8f),
        new(HumanBodyBones.Chest,         HumanBodyBones.Spine,          8f),
        new(HumanBodyBones.Head,          HumanBodyBones.Chest,          4f),
        new(HumanBodyBones.LeftUpperArm,  HumanBodyBones.Chest,        2.5f),
        new(HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftUpperArm, 1.8f),
        new(HumanBodyBones.RightUpperArm, HumanBodyBones.Chest,        2.5f),
        new(HumanBodyBones.RightLowerArm, HumanBodyBones.RightUpperArm,1.8f),
        new(HumanBodyBones.LeftUpperLeg,  HumanBodyBones.Hips,           5f),
        new(HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftUpperLeg, 3.5f),
        new(HumanBodyBones.RightUpperLeg, HumanBodyBones.Hips,           5f),
        new(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightUpperLeg,3.5f),
    };

    static readonly Dictionary<HumanBodyBones, HumanBodyBones> RuntimeTips = new()
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

    [Tooltip("Bone rigidbodies, assigned by Setup Thief Ragdoll. Kinematic until he goes down.")]
    [SerializeField] Rigidbody[] _bones;

    [Tooltip("Root of the bone chain — where an impulse lands if the blow cannot be traced to a " +
             "closer bone.")]
    [SerializeField] Rigidbody _hips;

    [Header("Settling")]
    [Tooltip("Bled off so the body stops tumbling and reads as landed rather than as a top " +
             "slowly winding down. Same idea as RagdollController's bone damping.")]
    [SerializeField] float _linearDamping  = 0.6f;
    [SerializeField] float _angularDamping = 4f;

    [Tooltip("Cap on the impulse a single blow can impart, so a heavy weapon launches him across " +
             "the aisle rather than out of the building.")]
    [SerializeField] float _maxImpulse = 26f;

    public bool IsActive { get; private set; }

    /// False when the prefab has never been through the setup tool, or when Unity retained the
    /// serialized array size but lost its object references. The latter used to look like a valid
    /// rig: Activate disabled the Animator, found no Rigidbody to wake, and left the zombie frozen
    /// upright instead of letting ThiefController play its Death fallback.
    public bool HasRig
    {
        get
        {
            if (_hips == null || _bones == null || _bones.Length == 0) return false;
            foreach (var rb in _bones)
                if (rb != null) return true;
            return false;
        }
    }

    Animator _animator;

    void Awake()
    {
        _animator = GetComponentInChildren<Animator>();

        // Imported prefab references can be lost while the humanoid skeleton itself remains
        // intact. Rebuild locally from the Avatar so every peer gets the same ragdoll without
        // synchronising a dozen bone transforms over the network.
        EnsureRig();

        // Kinematic until he is put down: while animated, the Animator owns these transforms and
        // a live rigidbody on the same bone would fight it every frame.
        SetBonePhysics(false);
    }

    /// Ensures ragdoll is the primary death path even for an instance that survived a script
    /// reload and therefore did not run Awake again. Returns false only when the model cannot
    /// provide a usable humanoid skeleton; ThiefController may then play its Death fallback.
    public bool EnsureRig()
    {
        if (HasRig) return true;
        return TryBuildRuntimeRig();
    }

    bool TryBuildRuntimeRig()
    {
        if (_animator == null || !_animator.isHuman) return false;

        var transforms = new Dictionary<HumanBodyBones, Transform>(RuntimeChain.Length);
        foreach (var spec in RuntimeChain)
        {
            Transform bone = MappedBone(spec.Bone);
            if (bone == null) return false;
            transforms[spec.Bone] = bone;
        }

        var bodies = new List<Rigidbody>(RuntimeChain.Length);
        foreach (var spec in RuntimeChain)
        {
            Transform bone = transforms[spec.Bone];
            Rigidbody body = bone.GetComponent<Rigidbody>();
            if (body == null) body = bone.gameObject.AddComponent<Rigidbody>();
            body.mass = spec.Mass;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.isKinematic = true;
            body.detectCollisions = false;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            FitCapsule(bone, spec.Bone);

            if (spec.Parent != HumanBodyBones.LastBone)
            {
                CharacterJoint joint = bone.GetComponent<CharacterJoint>();
                if (joint == null) joint = bone.gameObject.AddComponent<CharacterJoint>();
                joint.connectedBody = transforms[spec.Parent].GetComponent<Rigidbody>();
                joint.axis = Vector3.right;
                joint.swingAxis = Vector3.forward;
                joint.lowTwistLimit  = new SoftJointLimit { limit = -35f };
                joint.highTwistLimit = new SoftJointLimit { limit =  35f };
                joint.swing1Limit    = new SoftJointLimit { limit =  45f };
                joint.swing2Limit    = new SoftJointLimit { limit =  30f };
                joint.enablePreprocessing = false;
            }

            bodies.Add(body);
        }

        _bones = bodies.ToArray();
        _hips = transforms[HumanBodyBones.Hips].GetComponent<Rigidbody>();
        return HasRig;
    }

    void FitCapsule(Transform bone, HumanBodyBones boneId)
    {
        float length = 0.2f;
        Transform tip = null;
        if (RuntimeTips.TryGetValue(boneId, out HumanBodyBones tipId))
            tip = MappedBone(tipId);
        if (tip != null) length = Vector3.Distance(bone.position, tip.position);
        length = Mathf.Max(length, 0.08f);

        CapsuleCollider capsule = bone.GetComponent<CapsuleCollider>();
        if (capsule == null) capsule = bone.gameObject.AddComponent<CapsuleCollider>();
        capsule.height = length;
        capsule.radius = Mathf.Clamp(length * 0.3f, 0.04f, 0.13f);

        Vector3 toTip = tip != null
            ? bone.InverseTransformPoint(tip.position)
            : Vector3.up * length;
        Vector3 abs = new(Mathf.Abs(toTip.x), Mathf.Abs(toTip.y), Mathf.Abs(toTip.z));
        capsule.direction = abs.x >= abs.y && abs.x >= abs.z ? 0
                          : abs.y >= abs.z                   ? 1
                          : 2;
        capsule.center = toTip * 0.5f;
        capsule.enabled = true;
    }

    Transform MappedBone(HumanBodyBones bone)
    {
        Transform mapped = _animator.GetBoneTransform(bone);
        // Chest is optional in Unity's humanoid mapping. Some imported zombies expose the same
        // torso segment as UpperChest only; accepting it keeps the whole ragdoll path available.
        if (mapped == null && bone == HumanBodyBones.Chest)
            mapped = _animator.GetBoneTransform(HumanBodyBones.UpperChest);
        return mapped;
    }

    /// Hands the body to physics and throws it. impulse is already in world space; hitPoint is
    /// where the blow landed, used only to pick which bone takes it.
    public void Activate(Vector3 impulse, Vector3 hitPoint)
    {
        if (IsActive || !HasRig) return;

        // Resolve the receiving body before animation is disabled. A partially broken serialized
        // rig must fail harmlessly and leave the Death animation available as a fallback.
        var target = NearestBone(hitPoint);
        if (target == null) target = _hips;
        if (target == null) return;

        IsActive = true;

        // The Animator has to stop first. Left running it would keep writing bone transforms on
        // top of the simulation, which teleports the rigidbodies and destroys the physics state —
        // the same collision between animation and physics that keeps MeleeSwingArm off a real
        // ragdoll arm.
        if (_animator != null) _animator.enabled = false;

        SetBonePhysics(true);

        target.AddForceAtPosition(Vector3.ClampMagnitude(impulse, _maxImpulse),
                                  hitPoint, ForceMode.Impulse);
    }

    void SetBonePhysics(bool live)
    {
        if (_bones == null) return;

        foreach (var rb in _bones)
        {
            if (rb == null) continue;

            rb.isKinematic      = !live;
            rb.detectCollisions = live;

            if (!live) continue;

            rb.linearDamping  = _linearDamping;
            rb.angularDamping = _angularDamping;
            rb.WakeUp();
        }
    }

    Rigidbody NearestBone(Vector3 point)
    {
        Rigidbody best = null;
        float bestSqr = float.MaxValue;

        foreach (var rb in _bones)
        {
            if (rb == null) continue;

            float sqr = (rb.worldCenterOfMass - point).sqrMagnitude;
            if (sqr >= bestSqr) continue;

            bestSqr = sqr;
            best    = rb;
        }

        return best;
    }

#if UNITY_EDITOR
    /// Called by the setup tool. Editor-only: nothing at runtime should be rewiring the rig.
    public void EditorAssign(Rigidbody[] bones, Rigidbody hips)
    {
        _bones = bones;
        _hips  = hips;
    }
#endif
}
