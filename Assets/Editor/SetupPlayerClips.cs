using System.Linq;
using UnityEditor;
using UnityEngine;

/// Re-imports the hand-added clips the player retargets onto the HumanBot.
///
/// Mixamo hands them over as **Generic**, and a generic clip is bound to the skeleton it was
/// authored on: dropped on another rig it produces nothing at all — not a wrong pose, no movement
/// whatsoever. That failure is completely silent, which is what makes it worth a tool. Measured on
/// Standing_from_crouch before this ran: sampled at eleven points across the clip, the hips did not
/// move by a single millimetre and the feet sat 0.74 m below the object root.
///
/// Only the rig is touched. `clipAnimations` is deliberately left alone — writing that list moves
/// the clip's fileID off 7400000 and silently blanks every animator reference pointing at it, and
/// this project has been bitten by that before. Nothing here needs it: loop flags and trims are not
/// wanted on a crouch or a stand-up, both of which are one-shots.
public static class SetupPlayerClips
{
    /// Clips played on the player's own skeleton. The hobble has its own tool because it also needs
    /// a loop flag; these two need nothing but the rig.
    static readonly string[] Paths =
    {
        "Assets/Animations/Injured Standing Jump.fbx",
        "Assets/Animations/Crouch.fbx",
        "Assets/Animations/Standing_from_crouch.fbx",
    };

    const string SetFolder = "Assets/WarehouseDemo/Assets/Resources";
    const string SetPath   = SetFolder + "/" + PlayerClipSet.ResourcePath + ".asset";

    [MenuItem("Tools/Warehouse/Setup Player Clips")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new System.Text.StringBuilder();

        foreach (string path in Paths)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) { report.AppendLine($"• {path}: нет импортёра модели"); continue; }

            bool changed = false;

            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                importer.animationType = ModelImporterAnimationType.Human;

                // Mapped from this model's own skeleton. Mixamo rigs use the naming Unity's
                // automapper expects, so this resolves without hand-assigning a single bone.
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                changed = true;
            }

            if (!importer.importAnimation) { importer.importAnimation = true; changed = true; }

            if (!changed) { report.AppendLine($"• {System.IO.Path.GetFileName(path)}: уже Humanoid"); continue; }

            importer.SaveAndReimport();

            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                                    .FirstOrDefault(c => !c.name.StartsWith("__preview__"));

            report.AppendLine($"• {System.IO.Path.GetFileName(path)}: Generic -> Humanoid, " +
                              $"клип «{clip?.name}» {clip?.length:F2} с, " +
                              $"humanoid={clip?.isHumanMotion}");
        }

        report.Append(BuildSet());

        return "[Setup Player Clips]\n" + report;
    }

    /// Puts the three clips into an asset under Resources.
    ///
    /// Without it they are found by AssetDatabase inside `#if UNITY_EDITOR`, which works in the
    /// editor and hands back null in a build — no hobble, no crouch, and no error to explain it.
    /// The components that need them are added at runtime and so have no serialized field to hold
    /// them, which is why this is an asset rather than an inspector reference.
    static string BuildSet()
    {
        if (!AssetDatabase.IsValidFolder(SetFolder))
            AssetDatabase.CreateFolder("Assets/WarehouseDemo/Assets", "Resources");

        var set = AssetDatabase.LoadAssetAtPath<PlayerClipSet>(SetPath);
        bool made = set == null;

        if (made)
        {
            set = ScriptableObject.CreateInstance<PlayerClipSet>();
            AssetDatabase.CreateAsset(set, SetPath);
        }

        set.Hobble  = Clip(Paths[0]);
        set.Crouch  = Clip(Paths[1]);
        set.StandUp = Clip(Paths[2]);

        EditorUtility.SetDirty(set);
        AssetDatabase.SaveAssets();

        return $"• {(made ? "создан" : "обновлён")} {SetPath}: " +
               $"хромота={Name(set.Hobble)}, присед={Name(set.Crouch)}, " +
               $"вставание={Name(set.StandUp)}\n";
    }

    static string Name(AnimationClip c) => c != null ? $"{c.name} ({c.length:F2}с)" : "НЕТ";

    static AnimationClip Clip(string path) =>
        AssetDatabase.LoadAssetAtPath<AnimationClip>(path)
        ?? AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
               .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
}
