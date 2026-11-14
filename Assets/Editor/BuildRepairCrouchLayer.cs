using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// Gives the repair crouch a layer of its own on the player's controller.
///
/// It used to run by overriding the Idle clip inside the locomotion blend tree, which was cheap and
/// wrong in a way that only showed up on screen. Swapping an override changes what the state's
/// *current* time samples, instantly: the state sits at some normalized time, and the moment the
/// slot holds a different clip that same number reads a different pose. Anything that then tries to
/// blend — a crossfade of the state into itself — blends **from** that wrong pose. Releasing the
/// button sampled the stand-up clip at its end, which is a man standing, and blended back down to
/// its start, which is a man crouched: crouch, snap upright, sink back, then finally stand up.
///
/// Cutting instead of blending is no answer either. Measured across every humanoid bone with the
/// feet pinned to the floor, the three joins are 17 cm, 13 cm and 29 cm apart — the arms and the
/// planted foot are simply in different places — so a hard cut is visible at all three.
///
/// A layer fixes the class of problem rather than an instance of it. Its state machine has its own
/// clock, so the two clips can never alias onto each other's time, the transition between them is a
/// real one, and fading the layer weight in and out blends the whole thing against whatever the
/// legs happen to be doing underneath. Same pattern, and same reasoning, as the weapon arms layer.
public static class BuildRepairCrouchLayer
{
    // Named by the component that drives them, not here: runtime code cannot see this assembly,
    // so the names have to live on that side and be read from this one.
    const string LayerName = RepairCrouch.LayerName;
    const string IdleState = RepairCrouch.IdleState;
    const string DownState = RepairCrouch.DownState;
    const string UpState   = RepairCrouch.UpState;

    const string ControllerPath =
        "Assets/StarterAssets/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";

    const string CrouchPath = "Assets/Animations/Crouch.fbx";
    const string StandPath  = "Assets/Animations/Standing_from_crouch.fbx";

    [MenuItem("Tools/Warehouse/Build Repair Crouch Layer")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (ac == null) return $"no controller at {ControllerPath}";

        var crouch = LoadClip(CrouchPath);
        var stand  = LoadClip(StandPath);

        if (crouch == null || stand == null)
            return "clips missing — run Tools/Warehouse/Setup Player Clips first";

        var report = new System.Text.StringBuilder("[Build Repair Crouch Layer]\n");

        // ── The parameter the puppet rides on ────────────────────────────────
        //
        // Nothing in this controller reads it. It exists so PlayerAnimatorBridge has something to
        // copy and NetworkAnimator has something to replicate: a layer weight cannot travel that
        // road, so the far side derives the weight from this bool instead.
        if (!ac.parameters.Any(prm => prm.name == RepairCrouch.CrouchParam))
        {
            ac.AddParameter(RepairCrouch.CrouchParam, AnimatorControllerParameterType.Bool);
            report.AppendLine($"• parameter '{RepairCrouch.CrouchParam}' (bool) added");
        }
        else report.AppendLine($"• parameter '{RepairCrouch.CrouchParam}' already there");

        // ── The layer ────────────────────────────────────────────────────────
        if (!ac.layers.Any(l => l.name == LayerName))
        {
            // Added last so it sits above the weapon arms: a man kneeling to bolt his own leg back
            // on is not swinging a bat, and if the two ever overlapped the repair should win.
            ac.AddLayer(LayerName);

            var layers = ac.layers;
            var added  = layers[^1];

            added.defaultWeight = 0f;                                // RepairCrouch raises it
            added.blendingMode  = AnimatorLayerBlendingMode.Override;
            added.avatarMask    = null;                              // whole body: he crouches
            added.iKPass        = false;

            layers[^1] = added;
            ac.layers  = layers;

            report.AppendLine($"• layer '{LayerName}' added (override, whole body, weight 0)");
        }
        else report.AppendLine($"• layer '{LayerName}' already there");

        var layer = ac.layers.First(l => l.name == LayerName);
        var sm    = layer.stateMachine;

        // ── States ───────────────────────────────────────────────────────────
        //
        // Write-defaults off throughout. At weight zero this layer must contribute exactly nothing,
        // and a state that writes defaults would push a rest pose over the locomotion underneath
        // even while it is supposed to be silent.
        var idle = Ensure(sm, IdleState, null, ref report);
        var down = Ensure(sm, DownState, crouch, ref report);
        var up   = Ensure(sm, UpState,   stand,  ref report);

        sm.defaultState = idle;

        // No authored transitions. RepairCrouch drives the layer with CrossFade, which needs none,
        // and every transition it does not author is one that cannot fire at a moment nobody
        // expected — this controller has a history of exit-time transitions doing exactly that.
        foreach (var st in new[] { idle, down, up })
            while (st.transitions.Length > 0)
                st.RemoveTransition(st.transitions[0]);

        EditorUtility.SetDirty(ac);
        AssetDatabase.SaveAssets();

        report.AppendLine($"• clips: {DownState} = {crouch.name} ({crouch.length:F2} с), " +
                          $"{UpState} = {stand.name} ({stand.length:F2} с)");

        return report.ToString();
    }

    static AnimatorState Ensure(AnimatorStateMachine sm, string name, AnimationClip clip,
                                ref System.Text.StringBuilder report)
    {
        var existing = sm.states.FirstOrDefault(s => s.state.name == name).state;

        if (existing == null)
        {
            existing = sm.AddState(name);
            report.AppendLine($"• state '{name}' added");
        }

        existing.motion             = clip;
        existing.writeDefaultValues = false;
        existing.speedParameterActive = false;

        return existing;
    }

    static AnimationClip LoadClip(string path) =>
        AssetDatabase.LoadAssetAtPath<AnimationClip>(path)
        ?? AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
               .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
}
