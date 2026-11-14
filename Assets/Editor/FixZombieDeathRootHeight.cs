using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// The zombie death clip carries its fall as vertical root motion rather than in the pose.
/// The raider prefab runs with applyRootMotion off (the NavMeshAgent owns the transform), so
/// that motion is thrown away: the body ends the clip in a prone pose still drawn at standing
/// height, and the corpse appears to rise into the air.
///
/// Baking Root Transform Position (Y) into the pose keeps the fall in the animation itself,
/// where it belongs — the body reaches the floor and the transform is never touched.
public static class FixZombieDeathRootHeight
{
    /// Only the clips whose fall is the problem. Leaving the walk/idle/attack clips alone
    /// keeps their (absent) vertical travel out of the diff.
    static readonly string[] Targets =
    {
        "Assets/Zombie Slaughter/Animation/Zombie Guy@Death.FBX",
        "Assets/Zombie Slaughter/Animation/Zombie Girl@Death.FBX",
    };

    [MenuItem("Tools/Warehouse/Fix Zombie Death Root Height")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder();

        foreach (string path in Targets)
        {
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            if (imp == null) { report.AppendLine("• missing: " + path); continue; }

            // clipAnimations is empty until something writes it — the importer is running on
            // defaultClipAnimations. Materialise those first or the assignment below has
            // nothing to carry the rest of the clip's settings.
            var clips = imp.clipAnimations.Length > 0
                ? imp.clipAnimations
                : imp.defaultClipAnimations;

            if (clips.Length == 0) { report.AppendLine("• no clips: " + path); continue; }

            var changed = new List<string>();
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i].lockRootHeightY) continue;

                // "Bake Into Pose" for Root Transform Position (Y). Based Upon stays Original
                // so the clip's own vertical travel is preserved exactly rather than being
                // re-derived from the feet.
                clips[i].lockRootHeightY      = true;
                clips[i].keepOriginalPositionY = true;
                changed.Add(clips[i].name);
            }

            if (changed.Count == 0) { report.AppendLine("• already baked: " + path); continue; }

            // Names are left untouched on purpose: the animator controller references each
            // clip by a fileID derived from its name, so renaming would break the Death state.
            imp.clipAnimations = clips;
            EditorUtility.SetDirty(imp);
            imp.SaveAndReimport();

            report.AppendLine($"• baked Root Y into pose on {path} [{string.Join(", ", changed)}]");
        }

        return report.Length > 0 ? report.ToString() : "nothing to do";
    }
}
