using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

public static class AddBoxIdleToController
{
    const string ControllerPath = "Assets/StarterAssets/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";
    const string FbxPath        = "Assets/Animations/Box Idle.fbx";
    const string ParamName      = "IsCarryingBox";

    [MenuItem("Tools/Warehouse Demo/Add Box Idle Animation")]
    public static void Execute()
    {
        // ── 1. Включаем loop на клипе через ModelImporter ───────────────────
        var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
        if (importer != null)
        {
            var clips = importer.clipAnimations;
            if (clips.Length == 0)
            {
                // Нет явных клипов — создаём из default
                var defaultClips = importer.defaultClipAnimations;
                clips = defaultClips;
            }
            if (clips.Length > 0)
            {
                clips[0].loopTime = true;
                importer.clipAnimations = clips;
                importer.SaveAndReimport();
                Debug.Log("[BoxIdle] Loop включён на клипе.");
            }
        }

        // ── 2. Загружаем AnimationClip из FBX ───────────────────────────────
        AnimationClip clip = null;
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(FbxPath))
        {
            if (asset is AnimationClip c && !c.name.StartsWith("__preview__"))
            {
                clip = c;
                break;
            }
        }
        if (clip == null) { Debug.LogError("[BoxIdle] AnimationClip не найден в FBX!"); return; }
        Debug.Log($"[BoxIdle] Клип: {clip.name} ({clip.length:F1}с)");

        // ── 3. Загружаем контроллер ──────────────────────────────────────────
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null) { Debug.LogError("[BoxIdle] AnimatorController не найден!"); return; }

        // ── 4. Добавляем параметр IsCarryingBox (если нет) ──────────────────
        bool paramExists = false;
        foreach (var p in controller.parameters)
            if (p.name == ParamName) { paramExists = true; break; }

        if (!paramExists)
        {
            controller.AddParameter(ParamName, AnimatorControllerParameterType.Bool);
            Debug.Log("[BoxIdle] Параметр IsCarryingBox добавлен.");
        }

        // ── 5. Находим существующие состояния ────────────────────────────────
        var sm = controller.layers[0].stateMachine;

        AnimatorState idleWalkRun = null, jumpLand = null, jumpStart = null, boxIdle = null;
        foreach (var cs in sm.states)
        {
            switch (cs.state.name)
            {
                case "Idle Walk Run Blend": idleWalkRun = cs.state; break;
                case "JumpLand":           jumpLand    = cs.state; break;
                case "JumpStart":          jumpStart   = cs.state; break;
                case "BoxIdle":            boxIdle     = cs.state; break;
            }
        }

        // ── 6. Создаём состояние BoxIdle (если нет) ──────────────────────────
        if (boxIdle == null)
        {
            boxIdle        = sm.AddState("BoxIdle", new Vector3(500f, 0f, 0f));
            boxIdle.speed  = 1f;
            Debug.Log("[BoxIdle] Состояние BoxIdle создано.");
        }
        boxIdle.motion = clip;

        // ── 7. Переходы ──────────────────────────────────────────────────────

        // Idle Walk Run → BoxIdle (берём коробку)
        if (idleWalkRun != null && !HasTransitionTo(idleWalkRun, boxIdle))
        {
            var t = idleWalkRun.AddTransition(boxIdle);
            t.hasExitTime = false;
            t.duration    = 0.25f;
            t.AddCondition(AnimatorConditionMode.If, 0, ParamName);
        }

        // BoxIdle → Idle Walk Run (кладём коробку)
        if (idleWalkRun != null && !HasTransitionTo(boxIdle, idleWalkRun))
        {
            var t = boxIdle.AddTransition(idleWalkRun);
            t.hasExitTime = false;
            t.duration    = 0.25f;
            t.AddCondition(AnimatorConditionMode.IfNot, 0, ParamName);
        }

        // BoxIdle → JumpStart (прыжок с коробкой)
        if (jumpStart != null && !HasTransitionTo(boxIdle, jumpStart))
        {
            var t = boxIdle.AddTransition(jumpStart);
            t.hasExitTime = false;
            t.duration    = 0.07f;
            t.AddCondition(AnimatorConditionMode.If, 0, "Jump");
        }

        // JumpLand → BoxIdle (приземлился с коробкой)
        if (jumpLand != null && !HasTransitionTo(jumpLand, boxIdle))
        {
            var t = jumpLand.AddTransition(boxIdle);
            t.hasExitTime = false;
            t.duration    = 0.25f;
            t.AddCondition(AnimatorConditionMode.If, 0, ParamName);
        }

        // ── 8. Сохраняем ────────────────────────────────────────────────────
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log("[BoxIdle] ✓ Готово! Проверь контроллер в Animator window.");
    }

    static bool HasTransitionTo(AnimatorState from, AnimatorState to)
    {
        foreach (var t in from.transitions)
            if (t.destinationState == to) return true;
        return false;
    }
}
