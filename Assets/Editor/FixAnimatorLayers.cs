using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class FixAnimatorLayers
{
    const string ControllerPath = "Assets/StarterAssets/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";
    const string MaskPath       = "Assets/WarehouseDemo/Assets/Animations/UpperBodyMask.mask";

    public static void Run()
    {
        // ── 1. Create upper-body Avatar Mask ────────────────────────────────
        var mask = new AvatarMask();
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root,          false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body,          true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Head,          true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg,       false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg,      false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm,       true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm,      true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers,   true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFingers,  true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFootIK,    false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFootIK,   false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftHandIK,    true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightHandIK,   true);

        System.IO.Directory.CreateDirectory("Assets/WarehouseDemo/Assets/Animations");
        AssetDatabase.CreateAsset(mask, MaskPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[FixAnimator] Avatar mask saved → {MaskPath}");

        // ── 2. Patch AnimatorController ─────────────────────────────────────
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (ctrl == null) { Debug.LogError("[FixAnimator] Controller not found"); return; }

        var layers = ctrl.layers;   // value copy — we must write back

        for (int i = 0; i < layers.Length; i++)
        {
            if (layers[i].name == "Base Layer")
            {
                layers[i].iKPass = true;          // enable IK pass so OnAnimatorIK fires
                Debug.Log("[FixAnimator] Base Layer: IK Pass ON");
            }
            else if (layers[i].name == "UpperBody")
            {
                layers[i].avatarMask = mask;      // upper-body only
                layers[i].blendingMode = AnimatorLayerBlendingMode.Override;
                layers[i].defaultWeight = 1f;     // was 0 — enable the layer
                layers[i].iKPass = true;
                Debug.Log("[FixAnimator] UpperBody Layer: mask set, weight=1, IK Pass ON");
            }
        }

        ctrl.layers = layers;   // write back (Unity requirement for struct array)
        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();

        Debug.Log("[FixAnimator] ✓ Done. Run game to verify legs-only locomotion.");
    }
}
