using UnityEditor;
using UnityEngine;

/// Re-imports the injured-movement clip so it can actually be played on the player.
///
/// It arrives from Mixamo as a **Generic** rig, which is the one thing that stops it working here:
/// a generic clip is bound to the skeleton it was authored on and cannot be retargeted, so dropping
/// it on the HumanBot produces nothing at all — not a broken pose, simply no movement. Measured
/// before this ran: sampling it on the player's body moved the hips 0.000 m.
///
/// Only the rig and the loop flag are touched. The clip's own entry in clipAnimations is left
/// alone on purpose: writing that list changes the clip's fileID away from 7400000 and silently
/// empties every animator reference pointing at it, which this project has been bitten by before.
public static class SetupInjuredClip
{
    public const string ClipPath = "Assets/Animations/Injured Standing Jump.fbx";

    [MenuItem("Tools/Warehouse/Setup Injured Clip")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var importer = AssetImporter.GetAtPath(ClipPath) as ModelImporter;
        if (importer == null) return $"no model importer at {ClipPath}";

        var report = new System.Collections.Generic.List<string>();

        if (importer.animationType != ModelImporterAnimationType.Human)
        {
            importer.animationType = ModelImporterAnimationType.Human;

            // Mapped from this model's own skeleton. Mixamo rigs use the naming Unity's automapper
            // expects, so this resolves without hand-assigning a single bone.
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

            report.Add("rig: Generic -> Humanoid");
        }

        if (!importer.importAnimation) { importer.importAnimation = true; report.Add("animation import: on"); }

        // Looped, because measured it already is one: sampled on the player's skeleton, the last
        // frame differs from the first by 0.0 degrees summed over all 51 humanoid bones. (Walk_N,
        // a known-good cycle, measures 0.2; Run_N measures 77, because it is a half-cycle.) So it
        // needs no blending or trimming to run continuously — only the flag.
        //
        // Writing clipAnimations is the one genuinely dangerous edit here: it moves the clip's
        // fileID off 7400000 and silently blanks every animator reference to it. It happens now,
        // before anything points at the clip, and must not be repeated once something does.
        var clips = importer.defaultClipAnimations;
        bool loopFixed = false;

        for (int i = 0; i < clips.Length; i++)
        {
            if (clips[i].loopTime) continue;

            clips[i].loopTime = true;
            loopFixed = true;
        }

        if (loopFixed)
        {
            importer.clipAnimations = clips;
            report.Add("loop: off -> on");
        }

        if (report.Count == 0) return $"{ClipPath} already set up (Humanoid, looping).";

        importer.SaveAndReimport();

        return $"{ClipPath}:\n• " + string.Join("\n• ", report) +
               "\n\nRe-run the clip probe to see what it now measures.";
    }
}
