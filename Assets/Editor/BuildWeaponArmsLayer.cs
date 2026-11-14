using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// Builds the masked animation layer that holds the arms on a weapon, and the pose it plays.
///
/// Two steps, and they have to happen in this order because the pose can only be read off a live
/// character:
///
///   1. Build Weapon Arms Layer — creates the mask, the layer and an empty pose clip, and puts
///      PlayerWeaponLayer on the player. Safe to re-run; existing pieces are left alone.
///   2. Capture Weapon Carry Pose — in play mode, weapon in hand, arms where you want them:
///      writes the current humanoid pose into that clip.
///
/// Why a layer rather than code holding the bones: this is what masks are for. The legs keep
/// running, the torso keeps leaning, and the arms play their own thing — and the pose in the clip
/// can be swapped for a proper carry animation later without touching a line of code. The same
/// pattern already carries a box (UpperBody + UpperBodyMask + PlayerCarryLayer).
public static class BuildWeaponArmsLayer
{
    const string ControllerPath = "Assets/StarterAssets/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";
    const string AnimFolder     = "Assets/WarehouseDemo/Assets/Animations";
    const string MaskPath       = AnimFolder + "/WeaponArmsMask.mask";
    const string AttackPath     = "Assets/Animations/Standing Melee Attack Downward.fbx";

    public const string AttackState = "WeaponAttack";
    public const string SpeedParam  = "AttackSpeed";

    [MenuItem("Tools/Warehouse/Build Weapon Arms Layer")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder();

        var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (ac == null) return "controller not found: " + ControllerPath;

        Directory.CreateDirectory(AnimFolder);

        // ── Mask ─────────────────────────────────────────────────────────────
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
        if (mask == null)
        {
            mask = new AvatarMask();

            // Arms and hands only. The box layer's mask also takes Body and Head, because a
            // carried box turns the whole upper body; a weapon does not — leaving the torso to
            // locomotion is what keeps him breathing and leaning while he runs with a bat.
            foreach (AvatarMaskBodyPart part in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
            {
                if (part == AvatarMaskBodyPart.LastBodyPart) continue;
                // Right arm only. The attack clip animates both, but the second hand is not on
                // the weapon and has nothing to do in the blow — letting the clip through to it
                // means the free arm stops running and starts miming a two-handed swing that is
                // not happening. Left out of the mask, it keeps whatever locomotion is doing.
                bool on = part is AvatarMaskBodyPart.RightArm
                               or AvatarMaskBodyPart.RightFingers
                               or AvatarMaskBodyPart.RightHandIK;
                mask.SetHumanoidBodyPartActive(part, on);
            }

            AssetDatabase.CreateAsset(mask, MaskPath);
            report.AppendLine("• mask created: " + MaskPath);
        }
        else report.AppendLine("• mask already there: " + MaskPath);


        // ── Layer ────────────────────────────────────────────────────────────
        if (ac.layers.Any(l => l.name == PlayerWeaponLayer.LayerName))
            report.AppendLine($"• layer '{PlayerWeaponLayer.LayerName}' already on the controller");
        else
        {
            // Added last so it sits above UpperBody: a weapon and a box are never held at once,
            // but if they ever were, the thing in his hands should win.
            ac.AddLayer(PlayerWeaponLayer.LayerName);

            var layers = ac.layers;
            var layer  = layers[^1];
            layer.avatarMask    = mask;
            layer.defaultWeight = 0f;          // PlayerWeaponLayer raises it when armed
            layer.blendingMode  = AnimatorLayerBlendingMode.Override;
            layer.iKPass        = true;        // the off hand is still placed by IK
            layers[^1] = layer;
            ac.layers  = layers;

            // Deliberately EMPTY, and write-defaults off with it. A masked override layer playing
            // no clip contributes nothing, which is exactly right for carrying: the arms keep
            // their locomotion and the right hand is placed on the grip by IK — measured at 13°
            // for the free arm and 55° for the one on the weapon, which is what a man carrying a
            // bat looks like. Filling it with a captured pose put both arms out at 90°, because a
            // pose captured off this rig comes back as a T-pose (see CaptureWeaponCarryPose).
            //
            // Drop a real "carry" clip in here when there is one; nothing else has to change.
            var state = layer.stateMachine.AddState("WeaponCarry");
            state.motion             = null;
            state.writeDefaultValues = false;
            layer.stateMachine.defaultState = state;

            EditorUtility.SetDirty(ac);
            report.AppendLine($"• layer '{PlayerWeaponLayer.LayerName}' added (mask {mask.name}, weight 0)");
        }

        // ── The attack state ─────────────────────────────────────────────────
        var attack = AssetDatabase.LoadAllAssetsAtPath(AttackPath)
                                  .OfType<AnimationClip>()
                                  .FirstOrDefault(c => !c.name.StartsWith("__preview__"));

        if (attack == null) report.AppendLine("• no attack clip at " + AttackPath);
        else
        {
            var armsLayer = ac.layers.First(l => l.name == PlayerWeaponLayer.LayerName);

            if (armsLayer.stateMachine.states.Any(st => st.state.name == AttackState))
                report.AppendLine($"• state '{AttackState}' already there");
            else
            {
                // The clip runs 2.3 s and a bat swings in under one, so its speed is driven by a
                // parameter rather than baked: every weapon squeezes the same animation into its
                // own cooldown, and MeleeCombat sets the number.
                if (!ac.parameters.Any(prm => prm.name == SpeedParam))
                    ac.AddParameter(SpeedParam, AnimatorControllerParameterType.Float);

                var st = armsLayer.stateMachine.AddState(AttackState);
                st.motion               = attack;
                st.writeDefaultValues   = true;
                st.speedParameter       = SpeedParam;
                st.speedParameterActive = true;

                EditorUtility.SetDirty(ac);
                report.AppendLine($"• state '{AttackState}' added, driven by float {SpeedParam}");
            }
        }

        // ── The component that raises the weight ─────────────────────────────
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) report.AppendLine("• no Player in the open scene — add PlayerWeaponLayer by hand");
        else if (player.GetComponent<PlayerWeaponLayer>() != null)
            report.AppendLine("• PlayerWeaponLayer already on the player");
        else
        {
            Undo.AddComponent<PlayerWeaponLayer>(player);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(player.scene);
            report.AppendLine("• PlayerWeaponLayer added to the player");
        }

        AssetDatabase.SaveAssets();

        report.AppendLine("\nNext: enter play, pick a weapon up, put the arms where you want them, "
                        + "then Tools/Warehouse/Capture Weapon Carry Pose.");
        return report.ToString();
    }
}
