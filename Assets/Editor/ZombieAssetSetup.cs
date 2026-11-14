using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// Tools → Warehouse Demo → Setup Zombie Assets
///
/// Prepares the "Zombie Slaughter" pack for use as the raider body:
///   1. re-imports the six zombie meshes as Humanoid (each gets its own avatar),
///   2. re-imports the 34 animation FBXs as Humanoid clips copied from those avatars —
///      Humanoid is what keeps a walk cycle in place under a NavMeshAgent, because Unity
///      extracts the root motion for us and ThiefController never applies it,
///   3. converts the pack's Built-in Standard (specular) materials to URP/Lit — they render
///      magenta otherwise,
///   4. builds Assets/Animations/Zombie/ZombieThief.controller: the same parameter names
///      ThiefController already drives (Speed / MotionSpeed / Grounded / IsCarryingBox) plus
///      Attack + AttackVariant + Damage for the melee it never had an animation for.
///
/// Idempotent: re-running only rewrites what drifted. Run it before Setup Thief System.
public static class ZombieAssetSetup
{
    const string PackRoot       = "Assets/Zombie Slaughter";
    const string MeshFolder     = PackRoot + "/Meshes";
    const string AnimFolder     = PackRoot + "/Animation";
    const string ControllerDir  = "Assets/Animations/Zombie";
    public const string ControllerPath = ControllerDir + "/ZombieThief.controller";
    public const string MaskPath       = ControllerDir + "/ZombieUpperBody.mask";

    /// The layer ThiefController raises while a raider is carrying something.
    public const string CarryLayerName = "UpperBody";

    /// The mesh whose avatar every "Zombie Guy@…" / "Zombie Girl@…" clip is retargeted from.
    const string GuyAvatarMesh  = MeshFolder + "/Zombie Guy 1 Mesh.FBX";
    const string GirlAvatarMesh = MeshFolder + "/Zombie Girl 1 Mesh.FBX";

    // Locomotion thresholds. The thief agent walks at 3.2 m/s and flees faster, so the
    // shamble covers standing-to-walking and the run carries everything above it.
    // Blend thresholds are the speeds the clips actually depict, not round numbers. The pack's
    // locomotion is in-place — zero root travel — so each was measured off the planted foot's
    // backward slide through the root (see the stride measurement that produced these).
    // Feeding the tree the agent's real velocity against these thresholds is what keeps the
    // feet on the ground instead of skating.
    public const float WalkStride  = 0.32f;
    public const float AggroStride = 0.70f;
    public const float RunStride   = 1.16f;

    /// The player rig's own run — the "robot" one — retargeted onto a raider.
    ///
    /// It depicts a real sprint rather than a shamble: measured at about 4.2 m/s against the
    /// zombie run's 1.16, which is why a raider given this clip has to actually move at something
    /// like it. Play it on a body doing 1.2 m/s and the feet skate the whole way across the floor.
    public const string RobotRunPath =
        "Assets/StarterAssets/ThirdPersonController/Character/Animations/Locomotion--Run_N.anim.fbx";

    public const float RobotRunStride = 4.2f;

    /// Which locomotion a raider is using. 0 shambles, 1 runs — see BuildController.
    public const string GaitParameter = "Gait";

    /// Where the empty-handed tree reaches full Run. Deliberately far below RunStride: a raider
    /// is never meant to be seen walking, so anything above a crawl is already the run clip, and
    /// ThiefController slows playback instead of blending down to a shamble. The walk clips are
    /// still imported — the carry pose is one of them — they just do not drive locomotion.
    public const float RunBlendIn  = 0.15f;

    /// The pack's swing and stagger clips run ~2.5s each, which is slower than the fight
    /// beat ThiefController keeps — play them at this rate so a blow lands on its wind-up.
    const float AttackSpeed = 1.6f;

    [MenuItem("Tools/Warehouse Demo/Setup Zombie Assets")]
    static void Run()
    {
        string report = Execute();
        EditorUtility.DisplayDialog("Zombie assets", report, "OK");
    }

    public static string Execute() => Execute(false);

    /// force re-imports every FBX even when its settings already look right — the way to
    /// recover when an avatar came out of the importer without its human rig.
    public static string Execute(bool force)
    {
        var report = new List<string>();

        ConfigureMeshes(report, force);
        AssetDatabase.Refresh();

        var guy  = HumanAvatar(GuyAvatarMesh);
        var girl = HumanAvatar(GirlAvatarMesh);

        report.Add(guy != null ? "• Guy avatar is humanoid." : "• GUY AVATAR NOT HUMANOID — nothing to retarget onto.");
        report.Add(girl != null
            ? "• Girl avatar is humanoid."
            : "• Girl avatars refuse humanoid mapping — the girl bodies and their clips stay " +
              "Generic and unused. The raider uses a Zombie Guy body.");

        if (guy == null) return string.Join("\n", report);

        ConfigureClips(guy, girl, report, force);
        AssetDatabase.Refresh();

        ConvertMaterials(report);
        BuildController(report);

        string body = string.Join("\n", report);
        Debug.Log("[Zombie] Asset setup:\n" + body);
        return body;
    }

    // ── 1. Meshes → Humanoid ──────────────────────────────────────────────────

    static void ConfigureMeshes(List<string> report, bool force)
    {
        // Unity's humanoid auto-mapper only takes on some of the six bodies, even though all
        // six share one Character Studio skeleton. So: import them all, then hand the bone map
        // that did come out right to the ones that came back non-human and import those again.
        var paths = FbxIn(MeshFolder).ToList();

        foreach (var path in paths)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) continue;

            // Already tried and rejected (Generic with no humanoid avatar) — leave it alone
            // unless this is a forced pass, or every run re-logs the same rig error.
            bool settled = importer.animationType == ModelImporterAnimationType.Generic;
            bool ready   = importer.animationType == ModelImporterAnimationType.Human
                        && importer.avatarSetup   == ModelImporterAvatarSetup.CreateFromThisModel
                        && IsHumanoid(path);

            if ((ready || settled) && !force) continue;

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup   = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.SaveAndReimport();
        }

        var donorPath = paths.FirstOrDefault(IsHumanoid);
        if (donorPath == null)
        {
            report.Add("• Meshes: NO humanoid avatar came out of the importer at all.");
            return;
        }

        var donor = (ModelImporter)AssetImporter.GetAtPath(donorPath);
        int repaired = 0, human = 0;

        foreach (var path in paths)
        {
            if (!IsHumanoid(path))
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);

                // Generic here means an earlier run already tried the donor map and it failed.
                if (importer.animationType == ModelImporterAnimationType.Generic && !force)
                {
                    report.Add($"• {Path.GetFileName(path)}: no humanoid rig — left Generic.");
                    continue;
                }

                importer.humanDescription = donor.humanDescription;
                importer.SaveAndReimport();

                if (IsHumanoid(path)) repaired++;
                else
                {
                    // The girl skeletons parent their toes differently, so the donor map is
                    // rejected outright. Put them back to Generic: left as Human they would
                    // log a rig error on every import from here on.
                    importer.animationType = ModelImporterAnimationType.Generic;
                    importer.avatarSetup   = ModelImporterAvatarSetup.CreateFromThisModel;
                    importer.SaveAndReimport();
                }
            }

            if (IsHumanoid(path)) human++;
        }

        report.Add($"• Meshes: {human}/{paths.Count} humanoid" +
                   (repaired > 0 ? $" ({repaired} repaired from {Path.GetFileName(donorPath)})." : "."));
    }

    // ── 2. Clips → Humanoid, retargeted onto the pack's own avatars ───────────

    static void ConfigureClips(Avatar guy, Avatar girl, List<string> report, bool force)
    {
        int changed = 0, total = 0;

        foreach (var path in FbxIn(AnimFolder))
        {
            total++;
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) continue;

            bool isGirl = Path.GetFileName(path).StartsWith("Zombie Girl", System.StringComparison.OrdinalIgnoreCase);
            var  avatar = isGirl ? girl : guy;

            // No humanoid girl avatar means her clips cannot be retargeted; leave them Generic
            // rather than re-importing a broken rig on every run.
            if (avatar == null)
            {
                if (importer.animationType != ModelImporterAnimationType.Generic)
                {
                    importer.animationType = ModelImporterAnimationType.Generic;
                    importer.avatarSetup   = ModelImporterAvatarSetup.CreateFromThisModel;
                    importer.SaveAndReimport();
                }
                continue;
            }

            bool ready = importer.animationType == ModelImporterAnimationType.Human
                      && importer.avatarSetup   == ModelImporterAvatarSetup.CopyFromOther
                      && importer.sourceAvatar  == avatar
                      && HasClip(path);

            if (ready && !force) continue;

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup   = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar  = avatar;
            importer.SaveAndReimport();
            changed++;
        }

        report.Add($"• Clips: {changed}/{total} re-imported as Humanoid.");
    }

    // ── 3. Materials → URP ───────────────────────────────────────────────────

    /// The pack ships Standard (Specular setup) materials from 2018. Under URP those draw as
    /// the magenta error shader, so move every property across to URP/Lit in specular mode.
    static void ConvertMaterials(List<string> report)
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            report.Add("• Materials SKIPPED — URP/Lit shader not found.");
            return;
        }

        int converted = 0, total = 0;

        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { PackRoot }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mat  = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;

            total++;
            if (mat.shader == lit) continue;

            // Read everything off the old shader before swapping it — the properties go away
            // with it.
            var albedo    = mat.HasProperty("_MainTex")      ? mat.GetTexture("_MainTex")      : null;
            var bump      = mat.HasProperty("_BumpMap")      ? mat.GetTexture("_BumpMap")      : null;
            var specGloss = mat.HasProperty("_SpecGlossMap") ? mat.GetTexture("_SpecGlossMap") : null;
            var occlusion = mat.HasProperty("_OcclusionMap") ? mat.GetTexture("_OcclusionMap") : null;
            var color     = mat.HasProperty("_Color")        ? mat.GetColor("_Color")          : Color.white;
            var specColor = mat.HasProperty("_SpecColor")    ? mat.GetColor("_SpecColor")      : Color.gray;
            float gloss   = mat.HasProperty("_Glossiness")   ? mat.GetFloat("_Glossiness")     : 0.5f;
            float cutoff  = mat.HasProperty("_Cutoff")       ? mat.GetFloat("_Cutoff")         : 0.5f;
            bool  cutout  = mat.IsKeywordEnabled("_ALPHATEST_ON");

            mat.shader = lit;

            mat.SetFloat("_WorkflowMode", 0f);            // 0 = specular
            mat.SetTexture("_BaseMap", albedo);
            mat.SetColor("_BaseColor", color);
            mat.SetTexture("_SpecGlossMap", specGloss);
            mat.SetColor("_SpecColor", specColor);
            mat.SetFloat("_Smoothness", gloss);
            mat.SetTexture("_BumpMap", bump);
            mat.SetTexture("_OcclusionMap", occlusion);

            mat.SetFloat("_AlphaClip", cutout ? 1f : 0f);
            mat.SetFloat("_Cutoff", cutoff);

            mat.SetShaderPassEnabled("ShadowCaster", true);

            mat.DisableKeyword("_SPECGLOSSMAP");
            mat.EnableKeyword("_SPECULAR_SETUP");
            if (specGloss != null) mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            if (bump      != null) mat.EnableKeyword("_NORMALMAP");
            if (occlusion != null) mat.EnableKeyword("_OCCLUSIONMAP");

            if (cutout)
            {
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                mat.SetOverrideTag("RenderType", "TransparentCutout");
            }

            EditorUtility.SetDirty(mat);
            converted++;
        }

        if (converted > 0) AssetDatabase.SaveAssets();
        report.Add($"• Materials: {converted}/{total} converted to URP/Lit.");
    }

    // ── 4. The controller ─────────────────────────────────────────────────────

    static void BuildController(List<string> report)
    {
        var idle      = FindClip("Zombie Guy@Idle");
        var walk      = FindClip("Zombie Guy@Walk");
        var run       = FindClip("Zombie Guy@Run");
        var walkAggro = FindClip("Zombie Guy@Walk Aggro");
        var slapR     = FindClip("Zombie Guy@Slap Right");
        var slapL     = FindClip("Zombie Guy@Slap Left");
        var headbutt  = FindClip("Zombie Guy@Headbutt");
        var damage    = FindClip("Zombie Guy@Damage");
        var death     = FindClip("Zombie Guy@Death");

        if (idle == null || walk == null || run == null || slapR == null)
        {
            report.Add("• Controller SKIPPED — core clips not found after import.");
            return;
        }

        Directory.CreateDirectory(ControllerDir);

        // Rebuilt in place rather than deleted and re-created: a new asset means a new GUID,
        // and every prefab pointing at the controller would quietly lose it.
        var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (ac == null) ac = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        else            ClearController(ac);

        ac.AddParameter("Speed",         AnimatorControllerParameterType.Float);
        ac.AddParameter(GaitParameter,   AnimatorControllerParameterType.Float);
        ac.AddParameter("MotionSpeed",   AnimatorControllerParameterType.Float);
        ac.AddParameter("Grounded",      AnimatorControllerParameterType.Bool);
        ac.AddParameter("IsCarryingBox", AnimatorControllerParameterType.Bool);
        ac.AddParameter("Attack",        AnimatorControllerParameterType.Trigger);
        ac.AddParameter("AttackVariant", AnimatorControllerParameterType.Int);
        ac.AddParameter("Damage",        AnimatorControllerParameterType.Trigger);
        ac.AddParameter("Down",          AnimatorControllerParameterType.Trigger);

        var root = ac.layers[0].stateMachine;

        // Two ways of moving, chosen by the Gait parameter rather than by speed.
        //
        // A raider on his way to wreck a pallet runs — properly, on the player rig's own run clip,
        // because a thing sprinting at your stock is a problem you have to deal with now. A raider
        // who has turned on a player shambles, which is the older, slower threat and reads as one.
        // Speed alone cannot express that: both happen at whatever pace the agent is doing, and
        // which one it is depends on what he has decided to do, not on how fast he is going.
        var move = AddGaitTree(ac, root, "Locomotion", idle,
                               shamble: walkAggro ?? walk,
                               run: RobotRun() ?? run,
                               position: new Vector3(280f, 120f, 0f));

        // There is no second locomotion state for carrying any more. There used to be, holding
        // the aggro walk so a raider with a box looked like he was holding one — but that made
        // carrying cost a walk, and the whole body walked. Now the legs keep running here and the
        // arms come from the masked UpperBody layer below, which is what lets him do both.
        root.defaultState = move;

        // Three swings so a fight does not loop one clip. ThiefController picks the variant.
        // They play faster than authored: the pack's swings open with a long slow rear-back,
        // and the blow has to land on ThiefController's half-second wind-up.
        AddOneShot(ac, root, "Attack_SlapRight", slapR,    "Attack", 0, move, new Vector3(600f, 40f, 0f), AttackSpeed);
        AddOneShot(ac, root, "Attack_SlapLeft",  slapL ?? slapR, "Attack", 1, move, new Vector3(600f, 120f, 0f), AttackSpeed);
        AddOneShot(ac, root, "Attack_Headbutt",  headbutt ?? slapR, "Attack", 2, move, new Vector3(600f, 200f, 0f), AttackSpeed);

        if (damage != null)
            AddOneShot(ac, root, "Damage", damage, "Damage", -1, move, new Vector3(600f, 280f, 0f), AttackSpeed);

        // Death is the one state with no way out: he holds the last frame until he despawns.
        if (death != null)
            AddOneShot(ac, root, "Death", death, "Down", -1, null, new Vector3(600f, 360f, 0f));
        else
            report.Add("• No Death clip — the raider falls over procedurally instead.");

        BuildCarryLayer(ac, walkAggro, report);

        EditorUtility.SetDirty(ac);
        AssetDatabase.SaveAssets();

        report.Add($"• Controller written to {ControllerPath}.");
    }

    /// The arms, on their own layer, so a raider can run and carry at the same time.
    ///
    /// The base layer runs him: legs, hips, the whole stride. This one is masked to the arms and
    /// hands alone and holds the aggro walk's arms-forward pose over the top, which is the shape a
    /// man carrying a box in front of him makes. Splitting it this way is what lets the carry stop
    /// costing a walk — before, carrying meant playing a walk clip on the whole body.
    ///
    /// IK Pass is the other half. The pose puts the arms roughly right; CarryIK then pins the
    /// wrists to two points on the box itself, so the hands land on the thing rather than near it,
    /// whatever size the box is. Without iKPass Unity never calls OnAnimatorIK and the pose is all
    /// there would be.
    static void BuildCarryLayer(AnimatorController ac, AnimationClip armsPose, List<string> report)
    {
        if (armsPose == null)
        {
            report.Add("• No Walk Aggro clip — carrying has no arm pose, IK alone will have to do.");
        }

        var mask = BuildUpperBodyMask();

        var sm = new AnimatorStateMachine
        {
            name       = "UpperBodyStates",
            hideFlags  = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(sm, ac);

        var state = sm.AddState("CarryArms", new Vector3(300f, 100f, 0f));
        state.motion = armsPose;
        sm.defaultState = state;

        ac.AddLayer(new AnimatorControllerLayer
        {
            name              = CarryLayerName,
            stateMachine      = sm,
            defaultWeight     = 0f,   // raised by ThiefController only while he is carrying
            blendingMode      = AnimatorLayerBlendingMode.Override,
            avatarMask        = mask,
            iKPass            = true,
        });

        report.Add($"• {CarryLayerName} layer added (mask: arms, fingers, hand IK; IK pass on).");
    }

    /// Arms, fingers and the hand IK goals; nothing else.
    ///
    /// Deliberately not the whole upper half. The torso is left to the base layer so it keeps the
    /// run's cadence — the pose on this layer comes from a walk clip, and letting a walking sway
    /// ride over running legs is a mismatch you can see. Only the arms need replacing.
    static AvatarMask BuildUpperBodyMask()
    {
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
        if (mask == null)
        {
            mask = new AvatarMask();
            AssetDatabase.CreateAsset(mask, MaskPath);
        }

        foreach (AvatarMaskBodyPart part in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
        {
            if (part == AvatarMaskBodyPart.LastBodyPart) continue;

            bool on = part == AvatarMaskBodyPart.LeftArm
                   || part == AvatarMaskBodyPart.RightArm
                   || part == AvatarMaskBodyPart.LeftFingers
                   || part == AvatarMaskBodyPart.RightFingers
                   || part == AvatarMaskBodyPart.LeftHandIK
                   || part == AvatarMaskBodyPart.RightHandIK;

            mask.SetHumanoidBodyPartActive(part, on);
        }

        EditorUtility.SetDirty(mask);
        return mask;
    }

    /// Strips a controller back to an empty base layer, sub-assets included, so it can be
    /// rebuilt without accumulating orphaned blend trees.
    static void ClearController(AnimatorController ac)
    {
        foreach (var p in ac.parameters.ToArray())
            ac.RemoveParameter(p);

        var sm = ac.layers[0].stateMachine;

        foreach (var t in sm.anyStateTransitions.ToArray())
            sm.RemoveAnyStateTransition(t);

        foreach (var s in sm.states.ToArray())
            sm.RemoveState(s.state);

        // Everything above the base layer goes too. Without this a re-run stacks a second
        // UpperBody layer on the first and the arms are driven twice.
        while (ac.layers.Length > 1)
            ac.RemoveLayer(ac.layers.Length - 1);

        var baseSm = ac.layers[0].stateMachine;

        foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(ac)))
        {
            if (sub is BlendTree) Object.DestroyImmediate(sub, true);

            // A removed layer leaves its state machine behind as an orphan sub-asset.
            if (sub is AnimatorStateMachine sm2 && sm2 != baseSm) Object.DestroyImmediate(sub, true);
        }
    }

    /// The player rig's run, or null if the Starter Assets pack is not there.
    static AnimationClip RobotRun() =>
        AssetDatabase.LoadAllAssetsAtPath(RobotRunPath)
                     .OfType<AnimationClip>()
                     .FirstOrDefault(c => !c.name.StartsWith("__preview__"));

    /// Idle into a move, where the move is itself a choice between shambling and running.
    ///
    /// Nested rather than one flat tree because the two axes mean different things: Speed is how
    /// fast the body is actually crossing the floor, and Gait is which animation that ought to look
    /// like. Flattening them would make "walking slowly" and "shambling" the same thing, and they
    /// are not — a raider creeping round a corner on his way to wreck something is still running,
    /// just not very fast yet.
    static AnimatorState AddGaitTree(AnimatorController ac, AnimatorStateMachine sm, string name,
                                     AnimationClip idle, AnimationClip shamble, AnimationClip run,
                                     Vector3 position)
    {
        var state = sm.AddState(name, position);

        // Playback is driven through MotionSpeed rather than Animator.speed so it never touches
        // the attack, damage and death clips. ThiefController sets it against whichever stride the
        // current gait depicts — the two clips are nowhere near each other, 0.70 against 4.2.
        state.speedParameterActive = true;
        state.speedParameter       = "MotionSpeed";

        var gait = new BlendTree
        {
            name                   = name + "Gait",
            blendType              = BlendTreeType.Simple1D,
            blendParameter         = GaitParameter,
            useAutomaticThresholds = false,
        };
        AssetDatabase.AddObjectToAsset(gait, ac);

        gait.AddChild(shamble, 0f);
        gait.AddChild(run,     1f);

        var tree = new BlendTree
        {
            name                   = name + "Tree",
            blendType              = BlendTreeType.Simple1D,
            blendParameter         = "Speed",
            useAutomaticThresholds = false,
        };
        AssetDatabase.AddObjectToAsset(tree, ac);

        tree.AddChild(idle, 0f);
        tree.AddChild(gait, RunBlendIn);

        state.motion = tree;
        return state;
    }

    static AnimatorState AddBlendTree(AnimatorController ac, AnimatorStateMachine sm, string name,
                                      AnimationClip idle, AnimationClip walk, float walkThreshold,
                                      AnimationClip run, Vector3 position)
    {
        var state = sm.AddState(name, position);

        // Above RunStride there is no faster clip to blend to, so ThiefController scales
        // playback through MotionSpeed instead. Driving it as the state's speed multiplier
        // rather than Animator.speed keeps it off the attack, damage and death clips.
        state.speedParameterActive = true;
        state.speedParameter       = "MotionSpeed";

        var tree = new BlendTree
        {
            name            = name + "Tree",
            blendType       = BlendTreeType.Simple1D,
            blendParameter  = "Speed",
            useAutomaticThresholds = false,
        };

        AssetDatabase.AddObjectToAsset(tree, ac);

        tree.AddChild(idle, 0f);

        // A middle clip is optional. Without one the tree is idle straight into the run, and the
        // run takes over almost immediately — see RunBlendIn.
        if (walk != null)
        {
            tree.AddChild(walk, walkThreshold);
            tree.AddChild(run,  RunStride);
        }
        else
        {
            tree.AddChild(run, RunBlendIn);
        }

        state.motion = tree;
        return state;
    }

    /// A one-shot state entered from anywhere by trigger and left when the clip ends.
    /// variant < 0 means the trigger alone drives it; returnTo == null makes it terminal.
    static void AddOneShot(AnimatorController ac, AnimatorStateMachine sm, string name,
                           AnimationClip clip, string trigger, int variant,
                           AnimatorState returnTo, Vector3 position, float speed = 1f)
    {
        if (clip == null) return;

        var state = sm.AddState(name, position);
        state.motion = clip;
        state.speed  = speed;

        var enter = sm.AddAnyStateTransition(state);
        enter.AddCondition(AnimatorConditionMode.If, 0f, trigger);
        if (variant >= 0) enter.AddCondition(AnimatorConditionMode.Equals, variant, "AttackVariant");
        enter.hasExitTime           = false;
        enter.duration              = 0.08f;
        enter.canTransitionToSelf   = false;

        // returnTo == null: a terminal state, held on its last frame.
        if (returnTo == null) return;

        var exit = state.AddTransition(returnTo);
        exit.hasExitTime = true;
        exit.exitTime    = 0.85f;
        exit.duration    = 0.15f;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static IEnumerable<string> FbxIn(string folder) =>
        Directory.GetFiles(folder, "*.FBX", SearchOption.TopDirectoryOnly)
                 .Select(p => p.Replace('\\', '/'))
                 .OrderBy(p => p);

    /// The avatar baked into this model, but only if it actually carries a human rig.
    static Avatar HumanAvatar(string modelPath)
    {
        var avatar = LoadAvatar(modelPath);
        return avatar != null && avatar.isValid && avatar.isHuman ? avatar : null;
    }

    static bool IsHumanoid(string modelPath) => HumanAvatar(modelPath) != null;

    static bool HasClip(string path) =>
        AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                     .Any(c => !c.name.StartsWith("__preview__"));

    static Avatar LoadAvatar(string modelPath) =>
        AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();

    /// Clips live inside their FBX, so load the file and pull the clip out of it.
    public static AnimationClip FindClip(string fbxName)
    {
        string path = $"{AnimFolder}/{fbxName}.FBX";
        return AssetDatabase.LoadAllAssetsAtPath(path)
                            .OfType<AnimationClip>()
                            .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
    }
}
