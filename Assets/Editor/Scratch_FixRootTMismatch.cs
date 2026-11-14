using UnityEditor;
using UnityEngine;
using System.Text;

public static class Scratch_FixRootTMismatch
{
    static readonly string[] Paths =
    {
        "Assets/Animations/Tripping_InPlace.anim",
        "Assets/Animations/StandingUp_InPlace.anim",
    };

    public static string Execute()
    {
        var sb = new StringBuilder();
        foreach (var path in Paths)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null) { sb.AppendLine($"{path}: NOT FOUND"); continue; }

            // Make bake settings consistent across both clips.
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopBlendPositionXZ = false;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                if (b.propertyName == "RootT.x" || b.propertyName == "RootT.z")
                {
                    // Force BOTH clips to the exact same constant (0) so there's no
                    // reference-frame discontinuity between Tripping's end pose and
                    // StandingUp's start pose.
                    var flat = new AnimationCurve(
                        new Keyframe(0f, 0f, 0f, 0f),
                        new Keyframe(clip.length, 0f, 0f, 0f));
                    AnimationUtility.SetEditorCurve(clip, b, flat);
                }
            }

            EditorUtility.SetDirty(clip);
            sb.AppendLine($"Fixed {path}");
        }
        AssetDatabase.SaveAssets();
        return sb.ToString();
    }
}
