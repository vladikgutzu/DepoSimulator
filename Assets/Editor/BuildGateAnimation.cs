using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// Builds the roller gate's two animations and the controller that plays them, and puts an
/// Animator on gate_01_GRP inside the wall gate prefab.
///
/// The gate is a roller shutter: a 3.02 m leaf on rollers inside a 5.18 m frame. Measured rather
/// than assumed — gate_01_GRP has no rotation and no scale of its own, and world up lands exactly
/// on its local +Y, which is what lets the open clip be a single position curve.
///
/// Two clips, and they are built differently on purpose:
///
/// • Gate_Open drives local position outright. It is the gate's actual state, so it must own it.
///   One clip, not two: closing is the same motion played backwards, which is what the controller's
///   negative speed on the closing state does. Two mirrored clips would be two things to keep in
///   step every time the travel changes.
///
/// • Gate_Shake is authored around zero and played on an ADDITIVE layer, so it is a delta rather
///   than a pose. That is what lets the gate rattle while half open — a normal override layer would
///   snap it back to the shake clip's own height and undo the opening.
public static class BuildGateAnimation
{
    const string PrefabPath = "Assets/Prefabs/env/wall_gate_GRP_prefab.prefab";
    const string OutputDir  = "Assets/Animations/Gate";
    const string GateName   = "gate_01_GRP";

    const string OpenClipPath  = OutputDir + "/Gate_Open.anim";
    const string ShakeClipPath = OutputDir + "/Gate_Shake.anim";
    const string ControllerPath = OutputDir + "/Gate.controller";

    /// How far the leaf rises to clear the opening. The leaf is 3.02 m tall; a few centimetres
    /// over that puts its bottom edge just past the lintel instead of exactly level with it.
    const float OpenHeight = 3.05f;

    /// Long enough to read as a heavy motorised shutter rather than a lid flipping open.
    const float OpenDuration = 2.2f;

    [MenuItem("Tools/Warehouse/Build Gate Animation")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        Directory.CreateDirectory(OutputDir);

        var open  = BuildOpenClip();
        var shake = BuildShakeClip();
        var ac    = BuildController(open, shake);

        string wired = AttachAnimator(ac);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return $"• {OpenClipPath}: {OpenHeight} m over {OpenDuration}s\n" +
               $"• {ShakeClipPath}: looping rattle, additive\n" +
               $"• {ControllerPath}: Progress (float, 0 shut to 1 open) + a Shake layer at weight 0\n" +
               $"• {wired}";
    }

    // ── Clips ─────────────────────────────────────────────────────────────────

    static AnimationClip BuildOpenClip()
    {
        var clip = LoadOrCreate(OpenClipPath);
        clip.ClearCurves();

        // Eased at both ends: a shutter this size does not start or stop instantly, and the ease
        // is most of what sells the weight.
        var y = AnimationCurve.EaseInOut(0f, 0f, OpenDuration, OpenHeight);
        SetCurve(clip, "m_LocalPosition.y", y);

        // The other two are held flat so the clip fully owns the position and cannot leave a
        // stale X or Z behind from whatever played before it.
        SetCurve(clip, "m_LocalPosition.x", Flat(0f, OpenDuration));
        SetCurve(clip, "m_LocalPosition.z", Flat(0f, OpenDuration));

        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = false;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        EditorUtility.SetDirty(clip);
        return clip;
    }

    static AnimationClip BuildShakeClip()
    {
        var clip = LoadOrCreate(ShakeClipPath);
        clip.ClearCurves();

        const float length = 0.6f;
        const int   keys   = 15;

        // A fixed seed, so re-running the tool does not silently produce a different rattle.
        var rng = new System.Random(20260828);

        var yc = new AnimationCurve();
        var zc = new AnimationCurve();
        var xc = new AnimationCurve();

        for (int i = 0; i <= keys; i++)
        {
            float t = length * i / keys;

            // Ends pinned to zero so the loop closes and the additive delta starts at nothing.
            bool edge = i == 0 || i == keys;

            // Vertical is the loudest axis on a shutter — it is what the rollers rattle against.
            yc.AddKey(t, edge ? 0f : (float)(rng.NextDouble() * 2 - 1) * 0.014f);
            zc.AddKey(t, edge ? 0f : (float)(rng.NextDouble() * 2 - 1) * 0.008f);
            xc.AddKey(t, edge ? 0f : (float)(rng.NextDouble() * 2 - 1) * 0.004f);
        }

        SetCurve(clip, "m_LocalPosition.y", yc);
        SetCurve(clip, "m_LocalPosition.z", zc);
        SetCurve(clip, "m_LocalPosition.x", xc);

        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        EditorUtility.SetDirty(clip);
        return clip;
    }

    static AnimationCurve Flat(float value, float length)
    {
        var c = new AnimationCurve();
        c.AddKey(0f, value);
        c.AddKey(length, value);
        return c;
    }

    /// Empty path: the Animator sits on gate_01_GRP itself, so the curves address that transform.
    static void SetCurve(AnimationClip clip, string property, AnimationCurve curve) =>
        AnimationUtility.SetEditorCurve(
            clip, EditorCurveBinding.FloatCurve("", typeof(Transform), property), curve);

    static AnimationClip LoadOrCreate(string path)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (clip != null) return clip;

        clip = new AnimationClip();
        AssetDatabase.CreateAsset(clip, path);
        return clip;
    }

    // ── Controller ────────────────────────────────────────────────────────────

    static AnimatorController BuildController(AnimationClip open, AnimationClip shake)
    {
        var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (ac == null) ac = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        else            Clear(ac);

        ac.AddParameter("Progress", AnimatorControllerParameterType.Float);

        var root = ac.layers[0].stateMachine;

        // One state, scrubbed by a parameter, rather than a Closed/Opening/Opened/Closing ring.
        //
        // The ring was the obvious build and it does not hold: a state that is meant to sit on the
        // clip's last frame has to be told to, and cycleOffset does not do it for a non-looping
        // clip at speed zero — measured, the gate dropped back to the floor the moment it arrived.
        // Reversing halfway is worse again, because a Closing state entered from Opening starts at
        // the beginning of its own clip and snaps the gate shut instead of lowering it.
        //
        // Driving normalised time directly answers all of it: the gate is wherever Progress says,
        // 0 shut and 1 open, it holds either end for free, and reversing from any point is just
        // moving the number the other way.
        var state = root.AddState("Gate", new Vector3(300f, 60f, 0f));
        state.motion             = open;
        state.timeParameterActive = true;
        state.timeParameter       = "Progress";

        root.defaultState = state;

        BuildShakeLayer(ac, shake);

        EditorUtility.SetDirty(ac);
        return ac;
    }

    /// The rattle, on its own additive layer at weight zero.
    ///
    /// Additive because the shake has to work at any gate height: on an override layer the clip's
    /// own position would replace the opening one and drop a half-open gate back to the floor.
    /// Weight zero because the layer is switched on from code — the same way the raider's carry
    /// layer and the player's weapon layer are driven, rather than through another parameter.
    static void BuildShakeLayer(AnimatorController ac, AnimationClip shake)
    {
        var sm = new AnimatorStateMachine
        {
            name      = "ShakeStates",
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(sm, ac);

        var state = sm.AddState("Shake", new Vector3(300f, 60f, 0f));
        state.motion = shake;
        sm.defaultState = state;

        ac.AddLayer(new AnimatorControllerLayer
        {
            name          = "Shake",
            stateMachine  = sm,
            defaultWeight = 0f,
            blendingMode  = AnimatorLayerBlendingMode.Additive,
        });
    }

    static void Clear(AnimatorController ac)
    {
        foreach (var p in ac.parameters.ToArray()) ac.RemoveParameter(p);

        var sm = ac.layers[0].stateMachine;
        foreach (var t in sm.anyStateTransitions.ToArray()) sm.RemoveAnyStateTransition(t);
        foreach (var s in sm.states.ToArray()) sm.RemoveState(s.state);

        while (ac.layers.Length > 1) ac.RemoveLayer(ac.layers.Length - 1);

        var baseSm = ac.layers[0].stateMachine;
        foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(ac)))
            if (sub is AnimatorStateMachine other && other != baseSm)
                Object.DestroyImmediate(sub, true);
    }

    // ── Prefab ────────────────────────────────────────────────────────────────

    static string AttachAnimator(AnimatorController ac)
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);

        try
        {
            var gate = root.GetComponentsInChildren<Transform>(true)
                           .FirstOrDefault(t => t.name == GateName);
            if (gate == null) return $"{GateName} not found in {PrefabPath} — clips written, nothing wired";

            var animator = gate.GetComponent<Animator>();
            if (animator == null) animator = gate.gameObject.AddComponent<Animator>();

            animator.runtimeAnimatorController = ac;

            // The thing that actually opens it. Without a driver the clips are inert and there is
            // no way to see them, in the editor or in play.
            if (gate.GetComponent<GateAnimator>() == null) gate.gameObject.AddComponent<GateAnimator>();

            // The leaf is nowhere near the camera most of the time, and a culled animator would
            // leave a half-open gate frozen at whatever height it was last drawn at.
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.applyRootMotion = false;

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            return $"Animator on {GateName} in {PrefabPath} (all 14 gates in the scene use it)";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
