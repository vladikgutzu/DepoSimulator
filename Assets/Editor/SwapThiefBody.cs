using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// Puts a different zombie body under the existing raider.
///
/// The raider was built on Zombie Guy 2, who is the heavy one in the pack, while every locomotion
/// clip is retargeted from Zombie Guy 1's avatar. A humanoid clip authored on one set of
/// proportions and replayed on a much wider one comes out as a waddle no matter how good the clip
/// is: the legs have to swing around a belly that was not there when the stride was authored, and
/// a run reads as a lumber. That is not something tuning playback speed can fix.
///
/// This swaps the body only. Everything that makes a raider a raider — ThiefController and its
/// tuned numbers, the agent, the voice, the networking — lives on the prefab root and is left
/// exactly as it is, which is the whole reason not to simply re-run Setup Thief System: that
/// rebuilds the prefab from scratch and takes every tuned value with it.
public static class SwapThiefBody
{
    const string ThiefPrefabPath  = "Assets/Prefabs/Thief.prefab";
    const string MaterialFolder   = "Assets/Zombie Slaughter/Materials";
    const string PrefabFolder     = "Assets/Zombie Slaughter/Prefabs";
    const string MeshFolder       = "Assets/Zombie Slaughter/Meshes";

    /// Guy 1 rather than Guy 3, though both are lean: Guy 1 is the avatar the clips were
    /// retargeted from, so his run plays with no retargeting error at all.
    const string BodyFamily = "Zombie Guy 1";

    [MenuItem("Tools/Warehouse Demo/Swap Thief Body")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        string sourcePath = $"{PrefabFolder}/{BodyFamily}A/{BodyFamily}A.prefab";
        string meshPath   = $"{MeshFolder}/{BodyFamily} Mesh.FBX";

        var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
        if (source == null) return $"no body prefab at {sourcePath}";

        var avatar = AssetDatabase.LoadAllAssetsAtPath(meshPath).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isHuman)
            return $"{meshPath} has no humanoid avatar — run Setup Zombie Assets first";

        var root = PrefabUtility.LoadPrefabContents(ThiefPrefabPath);
        var report = new List<string>();

        try
        {
            // Out with the old skeleton and skins. Children only: every component that matters
            // sits on the root.
            var children = new List<GameObject>();
            foreach (Transform c in root.transform) children.Add(c.gameObject);
            foreach (var c in children) Object.DestroyImmediate(c);
            report.Add($"• removed {children.Count} old body object(s)");

            // In with the new, moved out from under its own root so the raider keeps his.
            var body = (GameObject)PrefabUtility.InstantiatePrefab(source);
            PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely,
                                               InteractionMode.AutomatedAction);

            var moved = new List<Transform>();
            foreach (Transform c in body.transform) moved.Add(c);
            foreach (var c in moved) c.SetParent(root.transform, false);
            Object.DestroyImmediate(body);
            report.Add($"• {BodyFamily} body attached ({moved.Count} object(s))");

            var animator = root.GetComponent<Animator>();
            if (animator != null)
            {
                animator.avatar = avatar;
                report.Add("• avatar switched to " + avatar.name);
            }

            report.Add(RepointSkins(root));

            PrefabUtility.SaveAsPrefabAsset(root, ThiefPrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return string.Join("\n", report);
    }

    /// The skin variants are per-body materials, so they have to move with the body — left
    /// pointing at the old family every raider would come out wearing another man's textures.
    static string RepointSkins(GameObject root)
    {
        var thief = root.GetComponent<ThiefController>();
        if (thief == null) return "• no ThiefController — skins left alone";

        var mats = new List<Material>();
        foreach (char v in "ABCDE")
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/{BodyFamily}{v}.mat");
            if (mat != null) mats.Add(mat);
        }

        if (mats.Count == 0) return $"• no {BodyFamily}x materials — every raider will look the same";

        var so  = new SerializedObject(thief);
        var arr = so.FindProperty("_bodyVariants");
        arr.arraySize = mats.Count;
        for (int i = 0; i < mats.Count; i++)
            arr.GetArrayElementAtIndex(i).objectReferenceValue = mats[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        return $"• {mats.Count} skin variant(s) re-pointed at {BodyFamily}";
    }
}
