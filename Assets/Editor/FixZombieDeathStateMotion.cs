using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// Re-points animator states at clips whose fileID moved.
///
/// A clip inside an FBX with no authored clipAnimations gets the placeholder fileID 7400000.
/// The moment anything writes clipAnimations — including only flipping Bake Into Pose — the
/// importer assigns the clip a real internalID instead, and every reference held by fileID
/// 7400000 silently resolves to null. The animator keeps the state, loses the motion, and the
/// only symptom is a state that plays nothing.
///
/// Re-assigning through the loaded AnimationClip object writes whatever fileID the importer
/// settled on, so this is safe to re-run.
public static class FixZombieDeathStateMotion
{
    const string ControllerPath = "Assets/Animations/Zombie/ZombieThief.controller";
    const string DeathClipPath  = "Assets/Zombie Slaughter/Animation/Zombie Guy@Death.FBX";

    [MenuItem("Tools/Warehouse/Fix Zombie Death State Motion")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var sb = new StringBuilder();

        var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (ac == null) return "controller not found: " + ControllerPath;

        var clip = AssetDatabase.LoadAllAssetsAtPath(DeathClipPath)
                                .OfType<AnimationClip>()
                                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
        if (clip == null) return "death clip not found in " + DeathClipPath;

        foreach (var layer in ac.layers)
        {
            foreach (var st in layer.stateMachine.states)
            {
                if (st.state.name != "Death") continue;

                st.state.motion = clip;
                sb.AppendLine($"• Death state re-pointed at '{clip.name}' ({DeathClipPath})");
            }
        }

        // Anything else the reimport may have orphaned — reported, not guessed at.
        foreach (var layer in ac.layers)
            foreach (var st in layer.stateMachine.states)
                if (st.state.motion == null)
                    sb.AppendLine($"• STILL NULL: state '{st.state.name}' on layer '{layer.name}'");

        EditorUtility.SetDirty(ac);
        AssetDatabase.SaveAssets();

        return sb.Length > 0 ? sb.ToString() : "no Death state found";
    }
}
