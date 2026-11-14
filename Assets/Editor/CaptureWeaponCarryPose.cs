using System.Linq;
using UnityEditor;
using UnityEngine;

/// Writes the pose the player is standing in right now into the WeaponArms layer's clip.
///
/// Run it in play mode with a weapon in hand and the arms where you want them — including anything
/// MeleeSwingArm's springs are doing, since this reads the bones as they are drawn, not as the
/// animator left them.
///
/// The clip is one frame of humanoid MUSCLE values, not bone rotations: muscles are what a
/// humanoid clip is made of, so the pose retargets onto any body the project ends up wearing
/// rather than being tied to the HumanBot's skeleton.
public static class CaptureWeaponCarryPose
{
    const string ClipPath = "Assets/WarehouseDemo/Assets/Animations/WeaponCarry.anim";

    [MenuItem("Tools/Warehouse/Capture Weapon Carry Pose")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        if (!Application.isPlaying) return "play mode only — the pose has to exist to be read";

        var player = GameObject.FindGameObjectWithTag("Player");
        var anim   = player != null ? player.GetComponent<Animator>() : null;
        if (anim == null || !anim.isHuman) return "no humanoid animator on the player";

        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        if (clip == null) return "no clip at " + ClipPath + " — run Build Weapon Arms Layer first";

        var pose    = new HumanPose();
        var handler = new HumanPoseHandler(anim.avatar, anim.transform);
        handler.GetHumanPose(ref pose);
        handler.Dispose();

        clip.ClearCurves();

        // One frame, held: a layer plays this as a pose, not as motion.
        for (int i = 0; i < HumanTrait.MuscleCount; i++)
        {
            var curve = AnimationCurve.Constant(0f, 1f / 60f, pose.muscles[i]);
            clip.SetCurve("", typeof(Animator), HumanTrait.MuscleName[i], curve);
        }

        EditorUtility.SetDirty(clip);
        AssetDatabase.SaveAssets();

        // Named so the report says something a person can check against the screen.
        var combat = Object.FindAnyObjectByType<MeleeCombat>();
        string held = combat != null ? combat.CurrentWeapon.ToString() : "unknown";

        var loudest = Enumerable.Range(0, HumanTrait.MuscleCount)
                                .Where(i => HumanTrait.MuscleName[i].Contains("Arm"))
                                .OrderByDescending(i => Mathf.Abs(pose.muscles[i]))
                                .Take(4)
                                .Select(i => $"{HumanTrait.MuscleName[i]}={pose.muscles[i]:F2}");

        return $"captured the carry pose while holding {held} → {ClipPath}\n" +
               $"  arm muscles doing most of the work: {string.Join(", ", loudest)}\n" +
               "  the layer plays this whenever a weapon is in hand";
    }
}
