using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// The same body swap as SwapPlayerModel, applied to the co-op player prefab.
///
/// Kept separate because a prefab has to be edited through a contents scope rather than in the
/// scene, and because that scope has bitten this project before: rebuilding Thief.prefab the
/// same way silently emptied a serialized material array. So this reports what the prefab is
/// carrying before and after, rather than assuming a save is lossless.
public static class SwapNetworkPlayerModel
{
    const string PrefabPath = "Assets/WarehouseDemo/Assets/Prefabs/NetworkPlayer.prefab";

    const string ModelPath = "Assets/Fbx/RobotsModel/xy-bot-costume-rig-t-pose/source/" +
                             "XY Bot Custom RIG - Tpose (with leaf bones)_512/" +
                             "XY Bot Custom RIG - Tpose (with leaf bones)_512.fbx";

    const float TargetHeight = 1.80f;

    [MenuItem("Tools/Warehouse/Swap Network Player Model")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute() => Execute(ModelPath, ModelPath);

    /// Same split as SwapPlayerModel: bodyPath is instantiated (an FBX, or a prefab built on one
    /// when the prefab carries material assignments or extra parts), avatarPath is where the
    /// humanoid Avatar actually lives, which is always the FBX.
    public static string Execute(string bodyPath, string avatarPath)
    {
        var report = new StringBuilder();

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(bodyPath);
        if (model == null) return "model not found: " + bodyPath;

        var avatar = AssetDatabase.LoadAllAssetsAtPath(avatarPath).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isHuman)
            return "the model has no humanoid avatar - set its rig to Humanoid first";

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);

        try
        {
            // What it had, so the save can be checked against it rather than trusted.
            var before = Inventory(root);

            // The Animator is not on the root here: the co-op prefab keeps the body on a
            // CharacterVisual child, which NetworkPlayer._characterModel and NetworkAnimator both
            // point at. The swap therefore happens under that child, not under the root, or the
            // bones would end up outside the Animator that is supposed to drive them.
            var anim = root.GetComponentInChildren<Animator>(true);
            if (anim == null) return "NetworkPlayer has no Animator anywhere in the prefab";

            var visual = anim.transform;
            report.AppendLine($"• body lives on '{visual.name}'");

            // A previous run's body.
            var stale = visual.Find("Model");
            if (stale != null) Object.DestroyImmediate(stale.gameObject);

            var body = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely,
                                               InteractionMode.AutomatedAction);
            body.name = "Model";
            body.transform.SetParent(visual, false);
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;

            float height = MeasureHeight(body);
            float scale  = height > 0.01f ? TargetHeight / height : 1f;
            body.transform.localScale = Vector3.one * scale;
            report.AppendLine($"• model {height:F2} m -> x{scale:F3} to {TargetHeight} m");

            anim.avatar = avatar;
            report.AppendLine($"• avatar -> {avatar.name}");

            foreach (string name in new[] { "Geometry", "Skeleton" })
            {
                var t = visual.Find(name);
                if (t == null) continue;

                t.gameObject.SetActive(false);
                t.name = name + "_OLD";
                report.AppendLine($"• {name} deactivated (kept as {t.name})");
            }

            // On the root, where NetworkPlayer is: the skin reads the role off it, and
            // finds the renderer by searching down from wherever it sits.
            if (root.GetComponent<PlayerSkin>() == null)
            {
                root.AddComponent<PlayerSkin>();
                report.AppendLine("• PlayerSkin added");
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);

            // ── Did the save keep everything? ────────────────────────────────
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var after = Inventory(saved);

            var lost = before.Where(c => !after.Contains(c)).ToList();
            report.AppendLine(lost.Count == 0
                ? $"• all {before.Count} component types survived the save"
                : "• LOST IN SAVE: " + string.Join(", ", lost));

            report.AppendLine(VerifyBones(saved.GetComponentInChildren<Animator>(true)));
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        return report.ToString();
    }

    /// Every component type on the prefab, so a save that quietly drops one is caught.
    static HashSet<string> Inventory(GameObject root) =>
        root.GetComponentsInChildren<Component>(true)
            .Where(c => c != null)
            .Select(c => c.GetType().Name)
            .ToHashSet();

    static float MeasureHeight(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return 0f;

        var b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        return b.size.y;
    }

    static string VerifyBones(Animator anim)
    {
        if (anim == null) return "• no Animator to verify";

        var needed = new[]
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
        };

        // On a prefab asset GetBoneTransform needs an instance, so the avatar's own mapping is
        // read instead.
        var map = anim.avatar != null
            ? anim.avatar.humanDescription.human.ToDictionary(h => h.humanName, h => h.boneName)
            : new Dictionary<string, string>();

        var missing = needed.Select(b => b.ToString())
                            .Where(n => !map.ContainsKey(n))
                            .ToList();

        return missing.Count == 0
            ? $"• all {needed.Length} checked bones mapped by the avatar"
            : "• MISSING BONES: " + string.Join(", ", missing);
    }
}
