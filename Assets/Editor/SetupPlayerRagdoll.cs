using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Sets up ragdoll physics on the Player skeleton and configures RagdollController.
/// Run from: Tools → Warehouse Demo → Setup Player Ragdoll
public static class SetupPlayerRagdoll
{
    // Bones are resolved through the humanoid avatar rather than by walking transform paths.
    // The paths were tied to one rig's names - Player/Skeleton/Hips/Spine/Chest/... - and broke
    // the moment the character model was swapped for one using Mixamo naming. The avatar knows
    // which transform is which whatever the rig calls them, so this now survives any humanoid.
    //
    // The keys are what the joint table below refers to; the bones are how they are found.
    static readonly (HumanBodyBones bone, string key)[] BoneMap =
    {
        (HumanBodyBones.Hips,          "Hips"),
        (HumanBodyBones.Spine,         "Spine"),
        (HumanBodyBones.UpperChest,    "UpperChest"),
        (HumanBodyBones.Head,          "Head"),
        (HumanBodyBones.LeftUpperArm,  "Left_UpperArm"),
        (HumanBodyBones.LeftLowerArm,  "Left_LowerArm"),
        (HumanBodyBones.RightUpperArm, "Right_UpperArm"),
        (HumanBodyBones.RightLowerArm, "Right_LowerArm"),
        (HumanBodyBones.LeftUpperLeg,  "Left_UpperLeg"),
        (HumanBodyBones.LeftLowerLeg,  "Left_LowerLeg"),
        (HumanBodyBones.RightUpperLeg, "Right_UpperLeg"),
        (HumanBodyBones.RightLowerLeg, "Right_LowerLeg"),
    };

    // A humanoid rig is allowed to stop at two spine bones, and plenty do -- the HumanBot has
    // Spine and Spine1 and nothing above them, so UpperChest simply has no transform to be.
    // Refusing such a rig outright cost the whole ragdoll; falling one bone down the spine costs
    // the torso a little articulation and nothing else, because the joint table below refers to
    // KEYS, not to bones: whatever "UpperChest" resolves to is what the head and both arms hang
    // off, and the chain stays connected either way.
    static readonly Dictionary<HumanBodyBones, HumanBodyBones[]> Fallbacks = new()
    {
        { HumanBodyBones.UpperChest, new[] { HumanBodyBones.Chest, HumanBodyBones.Spine } },
    };

    static Transform Resolve(Animator animator, HumanBodyBones bone)
    {
        var t = animator.GetBoneTransform(bone);
        if (t != null) return t;

        if (!Fallbacks.TryGetValue(bone, out var alternates)) return null;
        foreach (var alt in alternates)
        {
            t = animator.GetBoneTransform(alt);
            if (t == null) continue;
            Debug.Log($"[SetupRagdoll] {bone} is not mapped on this rig — using {alt} instead.");
            return t;
        }
        return null;
    }

    [MenuItem("Tools/Warehouse Demo/Setup Player Ragdoll")]
    public static void Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null)
        {
            Debug.LogError("[SetupRagdoll] No GameObject tagged 'Player' found in the active scene.");
            return;
        }

        var animator = player.GetComponent<Animator>();
        if (animator == null || !animator.isHuman || animator.avatar == null)
        {
            Debug.LogError("[SetupRagdoll] The player needs a humanoid Animator - the rig is " +
                           "read through its avatar, not through transform names.");
            return;
        }

        var bones = BuildRigOn(player, animator);
        if (bones == null) return;

        // ── 4. Add InjurySystem + PlayerSpeedOverride ────────────────────────
        if (player.GetComponent<InjurySystem>() == null)
            Undo.AddComponent<InjurySystem>(player);

        if (player.GetComponent<PlayerSpeedOverride>() == null)
            Undo.AddComponent<PlayerSpeedOverride>(player);

        // ── 5. Add / configure RagdollController ─────────────────────────────
        var rc = player.GetComponent<RagdollController>();
        if (rc == null) rc = Undo.AddComponent<RagdollController>(player);

        var so = new SerializedObject(rc);
        so.FindProperty("_hipBone").objectReferenceValue = bones["Hips"];
        so.FindProperty("_animator").objectReferenceValue = player.GetComponent<Animator>();

        // Collect movement scripts to disable during ragdoll.
        // We search by type name string so this compiles without StarterAssets references.
        // Movement and input are NOT in this list any more: RagdollController takes those away
        // with ThirdPersonController.MovementLocked instead, because disabling that component
        // also stops its LateUpdate — and CameraRotation lives there, so a downed player could
        // not look around. See RagdollController.SetControlLocked. Left here is only what should
        // genuinely stop working while face-down.
        var toDisable = new List<Object>();
        foreach (var typeName in new[] { "PlayerInteraction", "HeadBob" })
        {
            var c = player.GetComponent(typeName);
            if (c != null) toDisable.Add(c);
        }

        var scriptsProp = so.FindProperty("_scriptsToDisable");
        scriptsProp.arraySize = toDisable.Count;
        for (int i = 0; i < toDisable.Count; i++)
            scriptsProp.GetArrayElementAtIndex(i).objectReferenceValue = toDisable[i];

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(player);

        // ── 5. Done ───────────────────────────────────────────────────────────
        EditorSceneManager.MarkSceneDirty(player.scene);

        Debug.Log($"[SetupRagdoll] ✓ Ragdoll ready! " +
                  $"{BoneMap.Length} bones configured, {toDisable.Count} scripts registered.\n" +
                  "Enter Play Mode and throw a heavy box at the player to test. " +
                  "Tune '_ragdollImpulseThreshold' on boxes and '_ragdollDuration' on RagdollController.");
    }

    // ── Measuring the body that is actually on ────────────────────────────────

    /// Half-thickness of the visible body around one bone, across the capsule's axis.
    ///
    /// Two kinds of geometry answer for a bone here, and this body has both. A skinned mesh gives
    /// its vertices through bone weights — the shell of the HumanBot's damaged variation. Rigid
    /// parts parented straight onto the skeleton give theirs as their own vertices, which is what
    /// the variation-B panels bolted to each bone are. Measuring only one of the two would size
    /// the capsule to whichever half happened to be authored that way.
    ///
    /// reference is the figure this rig was tuned with on the Starter Assets humanoid. The result
    /// may depart from it by a set factor either way and no further: one stray vertex — an
    /// antenna, a loose panel — must not turn a forearm into a barrel, and nothing should come
    /// out thinner than the tuned figure, since too thin is the failure this exists to fix.
    static bool MeasureBoneGirth(Dictionary<string, Transform> rig, Transform bone, int direction,
                                 float reference, out float radius)
    {
        radius = reference;
        if (bone == null) return false;

        Vector3 axis = direction == 0 ? bone.right : direction == 2 ? bone.forward : bone.up;

        float max = 0f;
        int   n   = 0;

        foreach (var smr in bone.root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            var mesh = smr.sharedMesh;
            if (mesh == null || smr.bones == null) continue;

            int index = System.Array.IndexOf(smr.bones, bone);
            if (index < 0) continue;

            var weights   = mesh.boneWeights;
            var vertices  = mesh.vertices;
            var bindposes = mesh.bindposes;
            if (weights.Length == 0 || index >= bindposes.Length) continue;

            // Mesh space -> bone space -> world, for the bone as it stands right now.
            Matrix4x4 toWorld = bone.localToWorldMatrix * bindposes[index];

            for (int v = 0; v < weights.Length && v < vertices.Length; v++)
            {
                // Dominant weight only: a vertex half-owned by the next bone belongs to the joint
                // between them and would stretch both capsules toward each other.
                var w = weights[v];
                if (w.boneIndex0 != index || w.weight0 < 0.5f) continue;

                max = Mathf.Max(max, DistanceFromAxis(toWorld.MultiplyPoint3x4(vertices[v]), bone.position, axis));
                n++;
            }
        }

        // Rigid parts hanging off this bone — their own vertices, not their bounds. A bounding box
        // is axis-aligned in world space, so a panel sitting at an angle reports a box far wider
        // than the panel is: that is how the first pass produced a 30 cm shoulder.
        foreach (var mr in bone.GetComponentsInChildren<MeshRenderer>(false))
        {
            if (NearestRiggedBone(rig, mr.transform) != bone) continue;

            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            var m = mr.transform.localToWorldMatrix;
            foreach (var v in mf.sharedMesh.vertices)
            {
                max = Mathf.Max(max, DistanceFromAxis(m.MultiplyPoint3x4(v), bone.position, axis));
                n++;
            }
        }

        if (n == 0) return false;

        radius = Mathf.Clamp(max, reference * MinGirthFactor, reference * MaxGirthFactor);
        return true;
    }

    /// Which rigged bone a piece of geometry belongs to: the first one found walking up from it.
    /// Without this a hand panel counts toward the upper arm as well as the forearm, because both
    /// of them are its ancestors.
    static Transform NearestRiggedBone(Dictionary<string, Transform> rig, Transform t)
    {
        for (var p = t; p != null; p = p.parent)
            foreach (var b in rig.Values)
                if (p == b) return b;
        return null;
    }

    /// How far the measurement may move the tuned figure.
    const float MinGirthFactor = 1.0f;
    const float MaxGirthFactor = 2.5f;

    static float DistanceFromAxis(Vector3 point, Vector3 origin, Vector3 axis)
    {
        Vector3 d = point - origin;
        return Vector3.ProjectOnPlane(d, axis).magnitude;
    }


    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Collects the humanoid bones, strips any rig already on them and builds the joint chain.
    ///
    /// Public because the co-op player prefab needs exactly this rig on exactly these bones. The
    /// masses, capsule sizes and joint limits below are tuned for a ~1.8 m humanoid and are the
    /// kind of number that must not exist in two places - a second copy would drift, and the
    /// local player and his networked twin would fall over differently.
    public static Dictionary<string, Transform> BuildRigOn(GameObject player, Animator animator)
    {
        // ── 1. Collect bone transforms ────────────────────────────────────────
        var bones = new Dictionary<string, Transform>();
        foreach (var (bone, key) in BoneMap)
        {
            var t = Resolve(animator, bone);
            if (t == null)
            {
                Debug.LogError($"[SetupRagdoll] The avatar does not map {bone}, and no bone this " +
                               "chain can stand in with is mapped either.");
                return null;
            }

            // Two keys landing on one transform would have AddBone build a joint from a bone to
            // itself. Only possible on a rig so short that the fallback collides with Spine.
            if (bones.ContainsValue(t))
            {
                Debug.LogError($"[SetupRagdoll] {bone} resolved to '{t.name}', which another bone " +
                               "already uses — this rig is too short for the joint chain.");
                return null;
            }

            bones[key] = t;
        }

        // ── 2. Clear old ragdoll components from all skeleton bones ───────────
        foreach (var (_, key) in BoneMap)
            StripRagdoll(bones[key]);

        // Retired bodies too. A model swap leaves the previous skeleton in the hierarchy,
        // deactivated, and RagdollController collects bones with "include inactive" - leaving its
        // rigidbodies in place would hand the controller two skeletons and it would throw the
        // invisible one around. Named _OLD by the swap tools, so that is what is looked for.
        foreach (var t in player.GetComponentsInChildren<Transform>(true))
        {
            // Named _OLD by the older one-off swaps, Body__<key> by the body switcher. The
            // switcher keeps every body in the scene, so "deactivated" is what marks a retired
            // one -- checking the name alone here would strip the rig off the body being worn.
            bool retired = t.name.EndsWith("_OLD")
                        || (t.name.StartsWith("Body__") && !t.gameObject.activeSelf);
            if (!retired) continue;

            foreach (var b in t.GetComponentsInChildren<Transform>(true)) StripRagdoll(b);
        }

        // ── 3. Build ragdoll ──────────────────────────────────────────────────
        // Values calibrated for the Starter Assets Mixamo humanoid (~1.8 m).
        // Collider direction: 0=X(Horizontal), 1=Y(Vertical), 2=Z(Depth)

        // Hips — root bone, no joint
        AddBone(bones, "Hips",          connectedKey: null,
            mass: 15f, dir: 1, radius: 0.10f, height: 0.30f, center: new Vector3(0, 0.05f, 0),
            axis: Vector3.right,  swing: Vector3.forward,
            lowT: 0,    highT: 0,  sw1: 0,    sw2: 0);

        AddBone(bones, "Spine",         connectedKey: "Hips",
            mass: 10f, dir: 1, radius: 0.10f, height: 0.28f, center: new Vector3(0, 0.12f, 0),
            axis: Vector3.right,  swing: Vector3.forward,
            lowT: -30f, highT: 30f, sw1: 30f, sw2: 20f);

        AddBone(bones, "UpperChest",    connectedKey: "Spine",
            mass: 10f, dir: 1, radius: 0.12f, height: 0.25f, center: new Vector3(0, 0.10f, 0),
            axis: Vector3.right,  swing: Vector3.forward,
            lowT: -20f, highT: 20f, sw1: 25f, sw2: 15f);

        AddBone(bones, "Head",          connectedKey: "UpperChest",
            mass: 5f,  dir: 1, radius: 0.09f, height: 0.22f, center: new Vector3(0, 0.10f, 0),
            axis: Vector3.right,  swing: Vector3.forward,
            lowT: -20f, highT: 20f, sw1: 40f, sw2: 20f);

        // Left arm — horizontal capsule, X-axis toward hand
        AddBone(bones, "Left_UpperArm", connectedKey: "UpperChest",
            mass: 3f,  dir: 0, radius: 0.05f, height: 0.28f, center: new Vector3(-0.14f, 0, 0),
            axis: Vector3.right,  swing: Vector3.forward,
            lowT: -70f, highT: 10f, sw1: 70f, sw2: 30f);

        AddBone(bones, "Left_LowerArm", connectedKey: "Left_UpperArm",
            mass: 2f,  dir: 0, radius: 0.04f, height: 0.25f, center: new Vector3(-0.12f, 0, 0),
            axis: Vector3.up,     swing: Vector3.forward,
            lowT: -90f, highT: 0f,  sw1: 30f, sw2: 0f);

        // Right arm — X toward hand (positive)
        AddBone(bones, "Right_UpperArm", connectedKey: "UpperChest",
            mass: 3f,  dir: 0, radius: 0.05f, height: 0.28f, center: new Vector3(0.14f, 0, 0),
            axis: Vector3.right,  swing: Vector3.forward,
            lowT: -10f, highT: 70f, sw1: 70f, sw2: 30f);

        AddBone(bones, "Right_LowerArm", connectedKey: "Right_UpperArm",
            mass: 2f,  dir: 0, radius: 0.04f, height: 0.25f, center: new Vector3(0.12f, 0, 0),
            axis: Vector3.up,     swing: Vector3.forward,
            lowT: 0f,   highT: 90f, sw1: 30f, sw2: 0f);

        // Legs — vertical capsule, Y-axis down toward knee/foot
        AddBone(bones, "Left_UpperLeg", connectedKey: "Hips",
            mass: 8f,  dir: 1, radius: 0.08f, height: 0.40f, center: new Vector3(0, -0.20f, 0),
            axis: Vector3.right,  swing: Vector3.forward,
            lowT: -20f, highT: 70f, sw1: 30f, sw2: 10f);

        AddBone(bones, "Left_LowerLeg", connectedKey: "Left_UpperLeg",
            mass: 5f,  dir: 1, radius: 0.06f, height: 0.35f, center: new Vector3(0, -0.175f, 0),
            axis: Vector3.right,  swing: Vector3.forward,
            lowT: -80f, highT: 0f,  sw1: 0f,  sw2: 0f);

        AddBone(bones, "Right_UpperLeg", connectedKey: "Hips",
            mass: 8f,  dir: 1, radius: 0.08f, height: 0.40f, center: new Vector3(0, -0.20f, 0),
            axis: Vector3.right,  swing: Vector3.forward,
            lowT: -20f, highT: 70f, sw1: 30f, sw2: 10f);

        AddBone(bones, "Right_LowerLeg", connectedKey: "Right_UpperLeg",
            mass: 5f,  dir: 1, radius: 0.06f, height: 0.35f, center: new Vector3(0, -0.175f, 0),
            axis: Vector3.right,  swing: Vector3.forward,
            lowT: -80f, highT: 0f,  sw1: 0f,  sw2: 0f);

        return bones;
    }

    /// Ragdoll parts only. Colliders are filtered because CharacterController derives from
    /// Collider, and taking that off the player would leave him unable to stand on the floor.
    static void StripRagdoll(Transform bone)
    {
        foreach (var c in bone.GetComponents<CharacterJoint>()) Object.DestroyImmediate(c);
        foreach (var c in bone.GetComponents<Rigidbody>())      Object.DestroyImmediate(c);

        foreach (var c in bone.GetComponents<Collider>())
            if (!(c is CharacterController)) Object.DestroyImmediate(c);
    }

    static void AddBone(Dictionary<string, Transform> bones, string key, string connectedKey,
        float mass, int dir, float radius, float height, Vector3 center,
        Vector3 axis, Vector3 swing,
        float lowT, float highT, float sw1, float sw2)
    {
        var bone = bones[key];

        var rb = bone.gameObject.AddComponent<Rigidbody>();
        rb.mass = mass;
        // RagdollController/RemoteRagdollVisual override these to 1.5/5 the moment ragdoll
        // physics activates (SetBonePhysics) — set here too so the bone doesn't sit at
        // near-zero damping in the brief window before that runs, or if inspected at rest.
        rb.linearDamping = 1.5f;
        rb.angularDamping = 5f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        rb.isKinematic = true;  // animation-driven by default

        var col = bone.gameObject.AddComponent<CapsuleCollider>();
        col.direction = dir;
        col.center    = center;

        // The numbers passed in describe the Starter Assets humanoid this rig was first tuned on.
        // They are kept as the fallback and as proportions, but the capsule is sized from the body
        // ACTUALLY worn: measured against the HumanBot, a 10 cm torso capsule left the shell
        // hanging 15-29 cm through the floor while the bones themselves rested on top of it, so a
        // body lying down read as sunk into the concrete and the get-up played from inside it.
        if (MeasureBoneGirth(bones, bone, dir, radius, out float measured))
        {
            col.radius = measured;
            col.height = Mathf.Max(height, measured * 2f + 0.02f);
        }
        else
        {
            col.radius = radius;
            col.height = height;
        }

        if (connectedKey == null) return;

        var connected = bones[connectedKey];
        var j = bone.gameObject.AddComponent<CharacterJoint>();
        j.connectedBody = connected.GetComponent<Rigidbody>();
        j.axis = axis;
        j.swingAxis = swing;
        j.enableProjection = true;
        j.lowTwistLimit  = new SoftJointLimit { limit = lowT };
        j.highTwistLimit = new SoftJointLimit { limit = highT };
        j.swing1Limit    = new SoftJointLimit { limit = sw1 };
        j.swing2Limit    = new SoftJointLimit { limit = sw2 };
    }
}
