using System.Linq;
using UnityEditor;
using UnityEngine;

/// Writes the hobble clip onto the player's InjuredLocomotion in the open scene.
///
/// The component can find the clip by asset path on its own, but only in the editor — a build has
/// no AssetDatabase, and a clip nothing references is not in the build to be found anyway. A
/// serialized reference is what makes it exist in the player, so this is not a convenience.
///
/// Re-runnable. Run it again after swapping the player's body.
public static class AssignInjuredClip
{
    [MenuItem("Tools/Warehouse/Assign Injured Clip")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var clip = AssetDatabase.LoadAllAssetsAtPath(SetupInjuredClip.ClipPath)
                                .OfType<AnimationClip>()
                                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));

        if (clip == null)
            return $"no clip in {SetupInjuredClip.ClipPath} — run Setup Injured Clip first";

        if (!clip.isLooping)
            return $"'{clip.name}' is not set to loop — run Setup Injured Clip first, or the " +
                   "hobble plays once and then holds a pose";

        var targets = Object.FindObjectsByType<InjuredLocomotion>(FindObjectsInactive.Include,
                                                                   FindObjectsSortMode.None);
        if (targets.Length == 0)
            return "no InjuredLocomotion in the open scene. It is added at runtime by InjurySystem, " +
                   "so add one to the player by hand for the reference to be saved with the scene.";

        int written = 0;

        foreach (var target in targets)
        {
            var so   = new SerializedObject(target);
            var prop = so.FindProperty("_injuredClip");

            if (prop.objectReferenceValue == clip) continue;

            prop.objectReferenceValue = clip;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
            written++;
        }

        if (written > 0) UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();

        return written == 0
            ? $"'{clip.name}' was already assigned on {targets.Length} object(s)."
            : $"assigned '{clip.name}' ({clip.length:F2}s, looping) to {written} of " +
              $"{targets.Length} InjuredLocomotion component(s). Save the scene to keep it.";
    }
}
