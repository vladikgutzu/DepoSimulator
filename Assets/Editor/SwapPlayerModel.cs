using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Puts a new body on the player without touching anything that makes him the player.
///
/// Everything the project asks of the character goes through the humanoid rig — MeleeSwingArm
/// and MeleeCombat resolve bones by HumanBodyBones, the animator controller retargets through
/// the avatar — so swapping the model is a matter of replacing the skeleton and the skinned mesh
/// under Player and pointing the Animator at the new avatar. The controllers, the
/// CharacterController, the camera root and every tuned value on them stay exactly where they
/// are.
///
/// The old body is deactivated rather than deleted, so a swap can be eyeballed and undone.
public static class SwapPlayerModel
{
    public const string ModelPath = "Assets/Fbx/RobotsModel/xy-bot-costume-rig-t-pose/source/" +
                             "XY Bot Custom RIG - Tpose (with leaf bones)_512/" +
                             "XY Bot Custom RIG - Tpose (with leaf bones)_512.fbx";

    /// Body nodes are named with this prefix so the tool can tell them from the camera root and
    /// the rest of the rig, and so the Hierarchy says which body is which.
    const string NodePrefix = "Body__";

    /// The CharacterController is 1.8 m and the model is 1.90, so it comes down slightly rather
    /// than leaving his head out of his own collider.
    const float TargetHeight = 1.80f;

    [MenuItem("Tools/Warehouse/Swap Player Model")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute() => Execute(ModelPath, ModelPath, "xybot");

    public static string Execute(string bodyPath, string avatarPath) =>
        Execute(bodyPath, avatarPath, System.IO.Path.GetFileNameWithoutExtension(bodyPath));

    /// bodyPath is what gets instantiated -- an FBX, or a prefab built on one, when the prefab
    /// carries material assignments or extra parts the raw FBX does not (the HumanBot's detachable
    /// panels, for one). avatarPath is where the humanoid Avatar lives, which for such a prefab is
    /// still the FBX behind it: a prefab has no Avatar sub-asset of its own.
    /// key names the body's node in the hierarchy, so bodies can be told apart at a glance and
    /// swapped back and forth without piling up anonymous leftovers. One node per body ever:
    /// active is the one being worn, the rest sit deactivated beside it.
    public static string Execute(string bodyPath, string avatarPath, string key)
    {
        var report = new StringBuilder();

        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) return "no GameObject tagged 'Player' in the open scene";

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(bodyPath);
        if (model == null) return "model not found: " + bodyPath;

        var anim = player.GetComponent<Animator>();
        if (anim == null) return "the player has no Animator";

        // ── 1. Materials ─────────────────────────────────────────────────────
        report.AppendLine(ConvertMaterials(model));

        // ── 2. Drop in the new body ──────────────────────────────────────────
        // Retired, never deleted: RagdollController holds bone references into that skeleton,
        // and a body has to be swappable back. Legacy names from earlier one-off swaps are taken
        // in too, so the scene converges on one node per body.
        string nodeName = NodePrefix + key;

        report.AppendLine(RetireOthers(player, nodeName));

        // Already in the scene from an earlier swap: wake it rather than instantiating a second
        // copy. This is what makes switching bodies cheap -- the ragdoll rig and any hand-tuning
        // on that body survive the round trip.
        var revived = player.transform.Find(nodeName);
        if (revived != null)
        {
            Undo.RecordObject(revived.gameObject, "Wear player body");
            revived.gameObject.SetActive(true);

            var back = AssetDatabase.LoadAllAssetsAtPath(avatarPath).OfType<Avatar>().FirstOrDefault();
            if (back != null) anim.avatar = back;

            report.AppendLine($"• {nodeName} revived (already in the scene)");
            report.AppendLine(FitHeight(revived));
            report.AppendLine($"• animator avatar -> {(back != null ? back.name : "unchanged")}");
            report.AppendLine(BindTo(anim, revived));
            report.AppendLine(VerifyBones(anim));

            EditorUtility.SetDirty(player);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(player.scene);
            return report.ToString();
        }

        var body = (GameObject)PrefabUtility.InstantiatePrefab(model);
        PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely,
                                           InteractionMode.AutomatedAction);
        body.name = nodeName;
        Undo.RegisterCreatedObjectUndo(body, "Swap player model");

        body.transform.SetParent(player.transform, false);
        body.transform.localPosition = Vector3.zero;
        body.transform.localRotation = Quaternion.identity;

        // Measured rather than assumed: a model authored in centimetres or with a scaled root
        // would otherwise arrive at the wrong size and nothing downstream would say so.
        report.AppendLine(FitHeight(body.transform));

        // The Animator on Player has to be told which rig it is driving now; the controller and
        // every parameter on it carry over untouched.
        var newAvatar = AssetDatabase.LoadAllAssetsAtPath(avatarPath).OfType<Avatar>().FirstOrDefault();
        if (newAvatar == null || !newAvatar.isHuman)
            return report + "the model has no humanoid avatar - set its rig to Humanoid first";

        anim.avatar = newAvatar;
        report.AppendLine($"• animator avatar -> {newAvatar.name}");

        report.AppendLine(BindTo(anim, body.transform));

        // ── 3. Retire the old body ───────────────────────────────────────────
        foreach (string name in new[] { "Geometry", "Skeleton" })
        {
            var t = player.transform.Find(name);
            if (t == null) continue;

            t.gameObject.SetActive(false);
            t.name = name + "_OLD";
            report.AppendLine($"• {name} deactivated (kept as {t.name} so this is reversible)");
        }

        // ── 4. Check the bones the project actually reaches for ──────────────
        report.AppendLine(VerifyBones(anim));

        EditorUtility.SetDirty(player);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(player.scene);

        return report.ToString();
    }

    /// Unreal's Manny materials come in as Standard and render magenta under URP, the same trap
    /// the Zombie Slaughter pack had.
    static string ConvertMaterials(GameObject model)
    {
        var urp = Shader.Find("Universal Render Pipeline/Lit");
        if (urp == null) return "• URP/Lit shader not found - materials left alone";

        var mats = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(model))
                                .OfType<Material>()
                                .Concat(model.GetComponentsInChildren<Renderer>(true)
                                             .SelectMany(r => r.sharedMaterials))
                                .Where(m => m != null)
                                .Distinct()
                                .ToList();

        int changed = 0;
        foreach (var m in mats)
        {
            if (m.shader == urp) continue;
            // Embedded FBX materials are read-only; only project assets can be retargeted.
            if (!AssetDatabase.IsMainAsset(m) && !AssetDatabase.IsSubAsset(m)) continue;
            if (AssetDatabase.GetAssetPath(m).EndsWith(".fbx")) continue;

            // Read the built-in slots BEFORE the shader changes: assigning the shader drops
            // properties the new one does not declare, and URP renamed the two that matter most.
            // A plain shader swap therefore leaves an untextured white body rather than magenta,
            // which is worse -- it looks deliberate.
            var tex   = m.HasProperty("_MainTex")   ? m.GetTexture("_MainTex")   : null;
            var color = m.HasProperty("_Color")     ? m.GetColor("_Color")       : Color.white;
            float gloss = m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : -1f;

            m.shader = urp;

            if (tex   != null && m.HasProperty("_BaseMap"))   m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_BaseColor"))                  m.SetColor("_BaseColor", color);
            if (gloss >= 0f && m.HasProperty("_Smoothness"))  m.SetFloat("_Smoothness", gloss);

            EditorUtility.SetDirty(m);
            changed++;
        }

        if (changed > 0) AssetDatabase.SaveAssets();

        return $"• materials: {changed}/{mats.Count} switched to URP/Lit" +
               (changed < mats.Count
                   ? " (the rest are embedded in the FBX - extract them if they render magenta)"
                   : "");
    }

    /// Brings a body to the height the CharacterController expects, whatever scale it is at now.
    ///
    /// Reset to 1 before measuring, always. A body being re-worn already carries the scale from
    /// last time, and measuring it as it stands would fold that in again — which turns one bad
    /// number into a compounding one.
    static string FitHeight(Transform body)
    {
        body.localScale = Vector3.one;

        float height = MeasureHeight(body.gameObject);

        // Nothing to go on. Left at 1, which is right far more often than any guess.
        if (height < 0.01f) return "• height could not be measured — scale left at 1";

        // Already a human-sized model. Left alone rather than nudged: the CharacterController is
        // 1.8 m and a body between these is close enough, while a scale applied here would be a
        // permanent multiplier on a skeleton the ragdoll and the IK are both built against.
        if (height > 1.2f && height < 2.6f)
            return $"• model {height:F2} m — already human-sized, scale left at 1";

        float scale = TargetHeight / height;

        // A gross unit mismatch is the only thing worth correcting: a model authored in
        // centimetres, or one whose root carries a baked scale. Anything beyond this band is far
        // likelier to be a bad measurement than a real model, and applying it is how the player
        // became a two-centimetre object on the warehouse floor. Refused out loud instead.
        if (scale < 0.02f || scale > 50f)
            return $"• WARNING: measured {height:F2} m, which would need x{scale:F4} — refused as " +
                   "implausible and left at 1. If the body really is that size, scale it by hand.";

        body.localScale = Vector3.one * scale;
        return $"• model {height:F2} m -> x{scale:F4} to {TargetHeight} m";
    }

    /// How tall the model is, measured off its skeleton.
    ///
    /// Not off geometry, and this cost a while to learn. `Renderer.bounds` is the obvious choice
    /// and it lies on a skinned body: those bounds are whatever was last computed for the renderer,
    /// and on an unposed import they can be nonsense. Mesh bounds are no better here — a skinned
    /// mesh's vertices live in bindpose space, so putting them through the renderer's transform
    /// answers a different question than the one being asked.
    ///
    /// Measured on HumanBot_Skinned_Hands, both of those reported **187.76 m** for a body whose
    /// bones put the hips 0.97 m off the floor. The tool believed them and shrank the robot by a
    /// hundred, which is how the player became a two-centimetre object lying on the warehouse
    /// floor with everything else — avatar, controller, bindings — perfectly correct.
    ///
    /// Bones cannot lie the same way: they are plain transforms, they are what the avatar is built
    /// from, and they are what the animation drives. Their vertical span is a little short of the
    /// full height — the head bone sits below the top of the head — which is exactly why FitHeight
    /// only corrects gross unit mismatches and leaves a human-sized model alone.
    static float MeasureHeight(GameObject go)
    {
        // Two named bones, not every transform in the hierarchy. Taking the whole span was the
        // second wrong answer this measurement gave: a rig carries helper nodes — sockets, IK
        // targets, a reference root — that sit nowhere near the body, and one of them stretched
        // HumanBot_Skinned_Hands to a reported 4.10 m.
        Transform head = null, foot = null;

        foreach (var t in go.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "Head") head = t;

            // Lowest of whatever the feet are called, so a rig with toes and one without both work.
            if (t.name.EndsWith("ToeBase") || t.name.EndsWith("Foot"))
                if (foot == null || t.position.y < foot.position.y) foot = t;
        }

        if (head == null || foot == null) return 0f;

        // Head bone to floor, which is short of the true height by the top of the skull. That is
        // why FitHeight only ever corrects gross unit mismatches and leaves a plausible model be.
        return head.position.y - foot.position.y;
    }

    /// Names every bone the codebase resolves by hand, so a partial mapping is caught here
    /// rather than as an arm that quietly stops swinging.
    /// Deactivates every body except the one being worn.
    ///
    /// Kept, never deleted: RagdollController holds bone references into those skeletons, and a
    /// body has to be swappable back. Legacy names from earlier one-off swaps are taken in too, so
    /// the scene converges on one node per body.
    ///
    /// Deactivating is deliberately *all* this does. Moving them out from under the Player was
    /// tried and dropped: the animator picks its skeleton by sibling order, not by which
    /// hierarchies exist — measured, the binding lands correctly with Skeleton_OLD still sitting
    /// under Player and carrying a Hips of its own. What fixes it is BindTo below.
    static string RetireOthers(GameObject player, string nodeName)
    {
        var lines = new List<string>();

        foreach (Transform child in player.transform.Cast<Transform>().ToList())
        {
            bool isBody = child.name.StartsWith(NodePrefix)
                       || child.name == "Model" || child.name.StartsWith("Model_OLD");

            if (!isBody || child.name == nodeName || !child.gameObject.activeSelf) continue;

            Undo.RecordObject(child.gameObject, "Retire player body");
            child.gameObject.SetActive(false);

            lines.Add($"• {child.name} retired (deactivated, kept)");
        }

        return lines.Count > 0 ? string.Join(System.Environment.NewLine, lines)
                               : "• no other body to retire";
    }

    /// Points the Animator at the body that is actually being worn, and says so.
    ///
    /// An avatar names bones, not a hierarchy. With more than one skeleton under the Animator it
    /// binds to whichever `Hips` it reaches first — and the whole point of this tool is that the
    /// bodies not being worn are kept side by side under Player, so there are always several.
    /// Deactivating them does not take them out of the running.
    ///
    /// That failed silently and expensively: after a switch the animator went on posing the
    /// previous body, invisibly, while the one on screen stood in its bind pose. A T-posed robot
    /// with a valid avatar, a running controller and an empty console.
    ///
    /// Worn body first, then a forced re-bind — and then a check, because a repeat of this should
    /// be a message rather than a mystery.
    static string BindTo(Animator anim, Transform body)
    {
        body.SetAsFirstSibling();
        anim.Rebind();
        anim.Update(0f);

        var hips = anim.GetBoneTransform(HumanBodyBones.Hips);

        return hips != null && hips.IsChildOf(body)
            ? $"• animator bound to {body.name}"
            : $"• WARNING: the animator is still driving " +
              $"{(hips != null ? hips.root.name + "/" + hips.name : "nothing")} — the worn body " +
              $"will stand in a T-pose. Remove the other {NodePrefix}* nodes from Player.";
    }

    static string VerifyBones(Animator anim)
    {
        var needed = new[]
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest,
            HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        };

        var missing = new List<string>();
        foreach (var b in needed)
            if (anim.GetBoneTransform(b) == null) missing.Add(b.ToString());

        return missing.Count == 0
            ? $"• all {needed.Length} bones the project uses resolved"
            : "• MISSING BONES: " + string.Join(", ", missing);
    }
}
