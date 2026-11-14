using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

public static class SetupUpperBodyLayer
{
    const string ControllerPath = "Assets/StarterAssets/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";
    const string FbxPath        = "Assets/Animations/Box Idle.fbx";
    const string MaskPath       = "Assets/Animations/UpperBodyMask.mask";

    [MenuItem("Tools/Warehouse Demo/Setup Upper Body Animation Layer")]
    public static void Execute()
    {
        // ── 1. Avatar Mask — только верхняя часть тела ──────────────────────
        var mask = new AvatarMask();
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root,         false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body,         true);  // позвоночник
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Head,         true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg,      false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg,     false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm,      true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm,     true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers,  true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFingers, true);
        AssetDatabase.CreateAsset(mask, MaskPath);
        Debug.Log("[UpperBody] Avatar Mask создан.");

        // ── 2. Загружаем клип ────────────────────────────────────────────────
        AnimationClip clip = null;
        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(FbxPath))
            if (a is AnimationClip c && !c.name.StartsWith("__preview__")) { clip = c; break; }
        if (clip == null) { Debug.LogError("[UpperBody] Клип не найден!"); return; }

        // ── 3. Загружаем контроллер ──────────────────────────────────────────
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null) { Debug.LogError("[UpperBody] Контроллер не найден!"); return; }

        // ── 4. Убираем BoxIdle из Base Layer ─────────────────────────────────
        var baseSM = controller.layers[0].stateMachine;
        AnimatorState boxIdleBase = null;
        foreach (var cs in baseSM.states)
            if (cs.state.name == "BoxIdle") { boxIdleBase = cs.state; break; }

        if (boxIdleBase != null)
        {
            // Убираем переходы К BoxIdle из других состояний
            foreach (var cs in baseSM.states)
            {
                if (cs.state == boxIdleBase) continue;
                var toRemove = new List<AnimatorStateTransition>();
                foreach (var t in cs.state.transitions)
                    if (t.destinationState == boxIdleBase) toRemove.Add(t);
                foreach (var t in toRemove) cs.state.RemoveTransition(t);
            }
            baseSM.RemoveState(boxIdleBase);
            Debug.Log("[UpperBody] BoxIdle удалён из Base Layer.");
        }

        // ── 5. Добавляем UpperBody Layer ─────────────────────────────────────
        var layers = controller.layers;
        int upperIdx = -1;
        for (int i = 0; i < layers.Length; i++)
            if (layers[i].name == "UpperBody") { upperIdx = i; break; }

        if (upperIdx < 0)
        {
            controller.AddLayer("UpperBody");
            layers  = controller.layers;
            upperIdx = layers.Length - 1;
        }

        layers[upperIdx].defaultWeight  = 0f;   // вес контролируется кодом
        layers[upperIdx].blendingMode   = AnimatorLayerBlendingMode.Override;
        layers[upperIdx].avatarMask     = mask;
        layers[upperIdx].iKPass        = false;
        controller.layers = layers;

        // ── 6. Добавляем BoxIdle состояние в UpperBody Layer ─────────────────
        var upperSM = controller.layers[upperIdx].stateMachine;

        AnimatorState upperBoxIdle = null;
        foreach (var cs in upperSM.states)
            if (cs.state.name == "BoxIdle") { upperBoxIdle = cs.state; break; }
        if (upperBoxIdle == null)
            upperBoxIdle = upperSM.AddState("BoxIdle", Vector3.zero);

        upperBoxIdle.motion = clip;
        upperBoxIdle.speed  = 1f;
        upperSM.defaultState = upperBoxIdle;

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[UpperBody] ✓ Готово! UpperBody Layer index = {upperIdx}. " +
                  $"Используй animator.SetLayerWeight({upperIdx}, 1f) при подборе.");
    }
}
