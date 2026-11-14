using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEditor;
using Unity.EditorCoroutines.Editor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

// Editor-only, temporary staging. Never saves a scene or changes source assets.
[InitializeOnLoad]
public static class SteamShotSession
{
    const string Folder = "Screenshots/SteamSession";
    static GameObject stage;
    static Camera camera;
    static Camera previousMainCamera;
    static bool previousMainCameraEnabled;
    static readonly List<TextMeshPro> coopNameTags = new();
    static string coopFolder;
    static float previousTime;
    static bool staged;
    static Vector3 centre;
    static double nextPoll;
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    static SteamShotSession()
    {
        EditorApplication.update += Poll;
        AssemblyReloadEvents.beforeAssemblyReload += Restore;
        EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.ExitingPlayMode) Restore(); };
    }

    static void Poll()
    {
        if (EditorApplication.isCompiling || EditorApplication.timeSinceStartup < nextPoll) return;
        nextPoll = EditorApplication.timeSinceStartup + 0.5;
        string request = Path.Combine(Folder, "request.txt");
        if (!File.Exists(request)) return;
        string command = File.ReadAllText(request).Trim();
        File.Delete(request);
        try
        {
            string[] parts = command.Split(' ');
            switch (parts[0])
            {
                case "inspect": Inspect(); break;
                case "play": if (!EditorApplication.isPlaying) EditorApplication.isPlaying = true; break;
                case "ladder": Ladder(); break;
                case "raid": Raid(); break;
                case "coopchaos": CoopChaos(); break;
                case "capture": Capture(parts[1], float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture), parts.Length > 5 ? int.Parse(parts[5]) : 1920); break;
                case "restore": Restore(); break;
                default: throw new InvalidOperationException("Unknown shot command");
            }
            File.WriteAllText(Path.Combine(Folder, "status.txt"), DateTime.Now + " OK " + command);
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(Folder, "status.txt"), command + "\n" + e);
            Debug.LogException(e);
            Restore();
        }
    }

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Fields).GetValue(target);
    static GameObject Player => GameObject.FindGameObjectWithTag("Player");
    static Bounds BoundsOf(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>().Where(r => r.enabled && !(r is ParticleSystemRenderer)).ToArray();
        var b = renderers.Length > 0 ? renderers[0].bounds : new Bounds(go.transform.position, Vector3.zero);
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b;
    }
    static void Inspect()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Playing=" + Application.isPlaying + " Paused=" + EditorApplication.isPaused);
        foreach (var p in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) sb.AppendLine("Camera " + p.name + " " + p.transform.position);
        if (Player != null)
        {
            sb.AppendLine("Player " + Player.name + " " + Player.transform.position + " bounds=" + BoundsOf(Player));
            foreach (var a in Player.GetComponentsInChildren<Animator>()) sb.AppendLine("Animator " + a.name + " human=" + a.isHuman + " controller=" + a.runtimeAnimatorController);
        }
        foreach (var l in Object.FindObjectsByType<LadderController>(FindObjectsSortMode.None)) sb.AppendLine("Ladder " + l.name + " " + l.transform.position + " rot=" + l.transform.eulerAngles + " bounds=" + BoundsOf(l.gameObject));
        foreach (var t in Object.FindObjectsByType<ReachTruckController>(FindObjectsSortMode.None)) sb.AppendLine("Truck " + t.name + " " + t.transform.position + " bounds=" + BoundsOf(t.gameObject) + " seat=" + Field<Transform>(t,"_driverSeat")?.position);
        File.WriteAllText(Path.Combine(Folder, "inspect.txt"), sb.ToString());
    }

    static void Begin()
    {
        Restore();
        if (!Application.isPlaying || Player == null || Camera.main == null) throw new InvalidOperationException("Enter the warehouse in Play Mode first.");
        previousTime = Time.timeScale;
        staged = true;
        Time.timeScale = 0;
        stage = new GameObject("~SteamShotStaging");
        stage.hideFlags = HideFlags.DontSave;
        var camGO = new GameObject("~SteamShotCamera");
        camGO.transform.SetParent(stage.transform);
        camera = camGO.AddComponent<Camera>();
        camera.CopyFrom(Camera.main);
        camera.enabled = false;
        camera.fieldOfView = 48;
        camera.aspect = 16f / 9f;
        camera.nearClipPlane = 0.05f;
        var sourceData = Camera.main.GetComponent<UniversalAdditionalCameraData>();
        var data = camGO.AddComponent<UniversalAdditionalCameraData>();
        if (sourceData != null)
        {
            EditorUtility.CopySerialized(sourceData, data);
            data.cameraStack.Clear();
        }
        data.renderType = CameraRenderType.Base;
    }

    static GameObject Visual(GameObject source, string name)
    {
        // Inactive parent prevents Awake/OnEnable on gameplay and network components.
        var holder = new GameObject("~InactiveCloneRoot");
        holder.transform.SetParent(stage.transform);
        holder.SetActive(false);
        var clone = Object.Instantiate(source, holder.transform);
        clone.name = name;
        foreach (var c in clone.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(c);
        foreach (var c in clone.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        foreach (var c in clone.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(c);
        foreach (var c in clone.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true)) Object.DestroyImmediate(c);
        foreach (var c in clone.GetComponentsInChildren<Camera>(true)) Object.DestroyImmediate(c);
        foreach (var c in clone.GetComponentsInChildren<AudioSource>(true)) Object.DestroyImmediate(c);
        foreach (var c in clone.GetComponentsInChildren<AudioListener>(true)) Object.DestroyImmediate(c);
        foreach (var t in clone.GetComponentsInChildren<Transform>(true)) { t.gameObject.tag = "Untagged"; t.gameObject.isStatic = false; }
        clone.transform.SetParent(stage.transform, true);
        clone.SetActive(true);
        Object.DestroyImmediate(holder);
        return clone;
    }

    static Animator Worker(string name, Vector3 position, Quaternion rotation, PlayerRoleType role, float speed, bool climb = false, RuntimeAnimatorController controller = null)
    {
        var go = Visual(Player, name);
        go.transform.SetPositionAndRotation(position, rotation);
        var animator = go.GetComponentsInChildren<Animator>().First(a => a.isHuman);
        if (controller != null) animator.runtimeAnimatorController = controller;
        animator.enabled = true;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.Rebind();
        foreach (var p in animator.parameters)
        {
            if (p.name == "Speed") animator.SetFloat(p.name, speed);
            if (p.name == "MotionSpeed") animator.SetFloat(p.name, 1);
            if (p.name == "Grounded") animator.SetBool(p.name, true);
            if (p.name == "IsClimbing") animator.SetBool(p.name, climb);
            if (p.name == "ClimbSpeed") animator.SetFloat(p.name, climb ? 1 : 0);
        }
        for (int i = 0; i < 35; i++) animator.Update(1f/60f);
        animator.enabled = false;
        var skin = Resources.LoadAll<PlayerSkinSet>("Catalog/PlayerSkins").FirstOrDefault(s => !s.Solo && s.Role == role);
        if (skin != null)
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                if (r.sharedMaterials.Length == skin.Materials.Length) r.sharedMaterials = skin.Materials;
        return animator;
    }

    // Two-bone geometric posing on the cloned skeleton only.
    static void Arm(Animator a, bool right, Vector3 target)
    {
        var upper = a.GetBoneTransform(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
        var lower = a.GetBoneTransform(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
        var hand = a.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        if (upper == null || lower == null || hand == null) return;
        float l1 = Vector3.Distance(upper.position, lower.position), l2 = Vector3.Distance(lower.position, hand.position);
        Vector3 d = target - upper.position;
        float dist = Mathf.Clamp(d.magnitude, Mathf.Abs(l1-l2)+0.01f, l1+l2-0.01f);
        Vector3 dir = d.normalized;
        Vector3 bend = Vector3.ProjectOnPlane(-a.transform.forward + (right ? a.transform.right : -a.transform.right)*0.6f, dir).normalized;
        float along = (l1*l1 + dist*dist - l2*l2)/(2*dist);
        Vector3 elbow = upper.position + dir*along + bend*Mathf.Sqrt(Mathf.Max(0,l1*l1-along*along));
        upper.rotation = Quaternion.FromToRotation(lower.position-upper.position, elbow-upper.position)*upper.rotation;
        lower.rotation = Quaternion.FromToRotation(hand.position-lower.position, target-lower.position)*lower.rotation;
    }

    static void Ladder()
    {
        Begin();
        var ladderSource = Object.FindObjectsByType<LadderController>(FindObjectsSortMode.None).First();
        var truckSource = Object.FindObjectsByType<ReachTruckController>(FindObjectsSortMode.None).First(t => !Field<bool>(t,"_groundOnlyFork"));
        Vector3 origin = Player.transform.position + Player.transform.forward*5;
        origin.y = Player.transform.position.y;
        Quaternion rotation = Quaternion.Euler(0, 35, 0);
        var carrier = Worker("Carrier",origin,rotation,PlayerRoleType.Picker,2f);
        var ladder = Visual(ladderSource.gameObject,"CarriedLadder");
        ladder.transform.SetPositionAndRotation(origin + rotation*Field<Vector3>(ladderSource,"_carryLocalOffset"), rotation*Quaternion.Euler(Field<Vector3>(ladderSource,"_carryLocalRotation")));
        // Copy the actual game's carry and climb geometry.
        Vector3 localRight = ladderSource.transform.InverseTransformPoint(ladderSource.RightHandTarget.position);
        Vector3 localLeft = ladderSource.transform.InverseTransformPoint(ladderSource.LeftHandTarget.position);
        Arm(carrier,true,ladder.transform.TransformPoint(localRight));
        Arm(carrier,false,ladder.transform.TransformPoint(localLeft));
        float height = 2.328f;
        Vector3 climbPos = ladder.transform.position + ladder.transform.up*height - ladder.transform.forward*Field<float>(ladderSource,"_climbStandoff") + ladder.transform.right*Field<float>(ladderSource,"_climbLateralOffset");
        var climber = Worker("LadderPassenger",climbPos,ladder.transform.rotation,PlayerRoleType.Assembler,0,true);
        Arm(climber,true,ladder.transform.TransformPoint(new Vector3(0.65f,height+1.552f,0)));
        Arm(climber,false,ladder.transform.TransformPoint(new Vector3(0.05f,height+1.164f,0)));
        var truck = Visual(truckSource.gameObject,"WatchingForklift");
        Quaternion truckRot = Quaternion.Euler(0, -55, 0);
        truck.transform.SetPositionAndRotation(origin + new Vector3(4,0,1.8f),truckRot);
        var seat = Field<Transform>(truckSource,"_driverSeat");
        var seatPosition = truck.transform.TransformPoint(truckSource.transform.InverseTransformPoint(seat.position));
        var seatRotation = truckRot * Quaternion.Inverse(truckSource.transform.rotation)*seat.rotation;
        var driver = Worker("WatchingDriver",seatPosition,seatRotation,PlayerRoleType.ForkLiftOperator,0,false,Field<RuntimeAnimatorController>(truckSource,"_sittingController"));
        var head = driver.GetBoneTransform(HumanBodyBones.Head);
        if (head != null) head.rotation = Quaternion.LookRotation(climbPos + Vector3.up - head.position);
        foreach (bool right in new[]{true,false})
        {
            var target = Field<Transform>(truckSource,right ? "_steerHandRight" : "_steerHandLeft");
            if (target != null) Arm(driver,right,truck.transform.TransformPoint(truckSource.transform.InverseTransformPoint(target.position)));
        }
        centre = origin + new Vector3(1.5f,2.5f,0.7f);
        File.WriteAllText(Path.Combine(Folder,"staging.txt"), "origin="+origin+" ladder="+BoundsOf(ladder)+" truck="+BoundsOf(truck)+" carrier="+BoundsOf(carrier.gameObject)+" climber="+BoundsOf(climber.gameObject)+" driver="+BoundsOf(driver.gameObject));
    }

    static void Raid()
    {
        Begin();
        Vector3 origin = Player.transform.position + Player.transform.forward*5;
        var runner = Worker("WorkerFleeing",origin,Quaternion.Euler(0,225,0),PlayerRoleType.Picker,5);
        var zombieSource = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Thief.prefab");
        for (int i=0;i<2;i++)
        {
            var z = Visual(zombieSource,"Raider"+i);
            z.transform.SetPositionAndRotation(origin + new Vector3(1.5f+i*1.6f,0,2.8f+i),Quaternion.Euler(0,225,0));
            var a=z.GetComponentInChildren<Animator>();
            a.enabled=true; a.cullingMode=AnimatorCullingMode.AlwaysAnimate; a.applyRootMotion=false; a.Rebind();
            foreach(var p in a.parameters) { if(p.name=="Speed")a.SetFloat(p.name,4); if(p.name=="MotionSpeed")a.SetFloat(p.name,1); if(p.name=="Grounded")a.SetBool(p.name,true); }
            for(int k=0;k<35+i*13;k++)a.Update(1f/60f);
            a.enabled=false;
        }
        centre=origin+new Vector3(1.2f,1.3f,1.4f);
    }

    static void CoopChaos()
    {
        if (!Application.isPlaying)
            throw new InvalidOperationException("Enter Play Mode before staging the co-op chaos set.");
        EditorCoroutineUtility.StartCoroutineOwnerless(CoopChaosRoutine());
    }

    static IEnumerator CoopChaosRoutine()
    {
        // Multiplayer starts in-place, so it preserves the warehouse while enabling the complete HUD.
        if (MainMenuController.Instance != null)
            MainMenuController.Instance.StartMultiplayer();

        yield return new EditorWaitForSeconds(2.5f);
        TutorialManager.Instance?.Skip();
        HUDController.Instance?.HideTutorial();

        Begin();
        BuildCoopChaosStage();

        previousMainCamera = Camera.main;
        previousMainCameraEnabled = previousMainCamera != null && previousMainCamera.enabled;
        if (previousMainCamera != null) previousMainCamera.enabled = false;
        camera.enabled = true;

        var hud = HUDController.Instance;
        hud?.SetActiveTask("RUSH ORDER — COLD CHAIN", "4/7 crates loaded  •  00:42 remaining");
        hud?.AddNotification("TEAMMATE DOWN", "MIRA needs assistance near rack 04", HUDController.NotifType.Warning);
        hud?.AddNotification("UNSTABLE LOAD", "Pallet is slipping from the upper forks", HUDController.NotifType.Warning);
        hud?.AddNotification("RAIDERS INSIDE", "Multiple contacts near the loading lane", HUDController.NotifType.Warning);

        Directory.CreateDirectory("Screenshots/SteamSession");
        coopFolder = Path.Combine("Screenshots", "SteamSession", "coop_multiplayer_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(coopFolder);
        File.WriteAllText(Path.Combine("Screenshots", "SteamSession", "LATEST_COOP_MULTIPLAYER.txt"), Path.GetFullPath(coopFolder));

        var shots = new (string name, Vector3 subject, float yaw, float pitch, float distance)[]
        {
            ("01_full_team_chaos", centre + new Vector3(0f, .8f, 0f),  34f, 15f, 13.5f),
            ("02_forklift_teamwork", centre + new Vector3(-2.8f, 1.2f, 1.2f), 312f, 10f, 8.2f),
            ("03_teammate_down", centre + new Vector3(.4f, .6f, -1.8f), 118f, 10f, 7.2f),
            ("04_zombie_melee", centre + new Vector3(3.2f, 1f, 1.1f), 218f, 8f, 8.4f),
            ("05_surrounded_team", centre + new Vector3(1.8f, 1f, .2f), 72f, 12f, 10.2f),
            ("06_overhead_shift_crisis", centre + new Vector3(0f, .2f, .3f), 28f, 34f, 15.5f),
        };

        foreach (var shot in shots)
        {
            PositionCoopCamera(shot.subject, shot.yaw, shot.pitch, shot.distance);
            yield return null;
            yield return null;
            yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(coopFolder, shot.name + ".png"), 2);
            yield return new EditorWaitForSeconds(1.4f);
        }

        File.WriteAllText(Path.Combine(coopFolder, "CAPTURE_COMPLETE.txt"),
            "Co-op chaos set captured from Unity at " + DateTime.Now.ToString("O"));
        Debug.Log("[SteamShotSession] Co-op multiplayer chaos set complete: " + Path.GetFullPath(coopFolder));
        Restore();
    }

    static void BuildCoopChaosStage()
    {
        coopNameTags.Clear();
        Vector3 forward = Player.transform.forward;
        Vector3 right = Player.transform.right;
        Vector3 origin = Player.transform.position + forward * 8f;
        origin.y = Player.transform.position.y;
        centre = origin + Vector3.up * 1.2f;

        Quaternion truckRot = Quaternion.LookRotation(forward + right * .18f);
        var truckSource = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/WarehouseDemo/Assets/Prefabs/Vehicles/ShopForklift.prefab");
        var truck = Visual(truckSource, "ChaosForklift");
        truck.transform.SetPositionAndRotation(origin - right * 3.1f + forward * .8f, truckRot);

        var loadedPalletSource = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/env/pallet_box_pack_prefab.prefab");
        var loadedPallet = Visual(loadedPalletSource, "TiltingLoadedPallet");
        loadedPallet.transform.SetPositionAndRotation(
            truck.transform.position + truck.transform.forward * 1.65f + Vector3.up * 2.45f,
            truckRot * Quaternion.Euler(0f, 0f, -11f));

        var boxSource = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/env/box_med_prefab.prefab");
        for (int i = 0; i < 4; i++)
        {
            var box = Visual(boxSource, "FallingCargo_" + i);
            box.transform.SetPositionAndRotation(
                loadedPallet.transform.position + right * (.9f + i * .32f) + Vector3.down * (.2f + i * .55f) + forward * ((i & 1) == 0 ? .35f : -.25f),
                Quaternion.Euler(18f + i * 17f, 26f + i * 29f, 12f - i * 14f));
        }

        var driver = Worker("ForkliftOperator", truck.transform.position + Vector3.up * 1.45f - truck.transform.forward * .3f,
            truckRot, PlayerRoleType.ForkLiftOperator, 0f);
        AddNameTag(driver, "NOVA  •  FORKLIFT", new Color(.95f, .62f, .12f));

        var guide = Worker("GroundGuide", origin - right * .75f + forward * 1.8f,
            Quaternion.LookRotation(-right - forward * .2f), PlayerRoleType.Dispatcher, 1.4f);
        Arm(guide, true, loadedPallet.transform.position + right * .8f);
        Arm(guide, false, guide.transform.position + Vector3.up * 1.1f - right * .4f);
        AddNameTag(guide, "ECHO  •  DISPATCHER", new Color(.35f, .8f, 1f));

        var falling = Worker("FallingPicker", origin + right * .15f - forward * 1.8f + Vector3.up * .58f,
            Quaternion.LookRotation(forward), PlayerRoleType.Picker, 0f);
        falling.transform.rotation *= Quaternion.Euler(0f, 0f, 72f);
        Arm(falling, true, falling.transform.position + right * 1.1f + Vector3.up * 1.1f);
        Arm(falling, false, falling.transform.position - right * .8f + Vector3.up * 1.35f);
        AddNameTag(falling, "MIRA  •  PICKER  [DOWN]", new Color(1f, .28f, .25f));

        var helper = Worker("HelpingAssembler", origin - right * .6f - forward * 1.4f,
            Quaternion.LookRotation(right + forward * .2f), PlayerRoleType.Assembler, 0f);
        var helperSpine = helper.GetBoneTransform(HumanBodyBones.Spine);
        if (helperSpine != null) helperSpine.rotation *= Quaternion.Euler(28f, 0f, 0f);
        Arm(helper, true, falling.transform.position + Vector3.up * .45f);
        Arm(helper, false, falling.transform.position + Vector3.up * .75f - forward * .2f);
        AddNameTag(helper, "BOLT  •  ASSEMBLER", new Color(.65f, 1f, .4f));

        var fighterA = Worker("FrontlinePicker", origin + right * 2.1f + forward * .7f,
            Quaternion.LookRotation(forward + right * .2f), PlayerRoleType.Picker, 0f);
        PoseFighter(fighterA, true);
        AttachWeapon(fighterA, "Assets/Resources/Catalog/Weapons/Models/weapon_model_bat.prefab");
        AddNameTag(fighterA, "RIVET  •  PICKER", new Color(.95f, .75f, .25f));

        var fighterB = Worker("FrontlineAssembler", origin + right * 3.35f - forward * .3f,
            Quaternion.LookRotation(forward - right * .25f), PlayerRoleType.Assembler, 0f);
        PoseFighter(fighterB, false);
        AttachWeapon(fighterB, "Assets/Resources/Catalog/Weapons/Models/weapon_model_crowbar.prefab");
        AddNameTag(fighterB, "SPARK  •  ASSEMBLER", new Color(.55f, 1f, .45f));

        var zombieSource = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Thief.prefab");
        Vector3[] zombieOffsets =
        {
            right * 2.4f + forward * 3.1f,
            right * 4.25f + forward * 2.25f,
            right * 5.0f + forward * .45f,
            right * 1.25f + forward * 3.9f,
            right * 3.8f - forward * 2.2f,
        };
        for (int i = 0; i < zombieOffsets.Length; i++)
        {
            Vector3 pos = origin + zombieOffsets[i];
            var zombie = Visual(zombieSource, "Raider_" + i);
            zombie.transform.SetPositionAndRotation(pos, Quaternion.LookRotation((origin + right * 2.7f) - pos));
            PoseZombie(zombie, 2.4f, .16f + i * .13f);
        }

        var downZombie = Visual(zombieSource, "DownedRaider");
        downZombie.transform.SetPositionAndRotation(origin + right * 3.15f + forward * 1.5f + Vector3.up * .35f,
            Quaternion.LookRotation(right) * Quaternion.Euler(0f, 0f, 86f));
        PoseZombie(downZombie, 0f, .5f);
    }

    static void PoseFighter(Animator fighter, bool highSwing)
    {
        Vector3 p = fighter.transform.position;
        Vector3 f = fighter.transform.forward;
        Vector3 r = fighter.transform.right;
        Arm(fighter, true, p + Vector3.up * (highSwing ? 2.05f : 1.65f) + f * .7f + r * .35f);
        Arm(fighter, false, p + Vector3.up * 1.35f + f * .35f - r * .15f);
        var chest = fighter.GetBoneTransform(HumanBodyBones.Chest);
        if (chest != null) chest.rotation *= Quaternion.Euler(highSwing ? -18f : 10f, highSwing ? 22f : -18f, 0f);
    }

    static void AttachWeapon(Animator fighter, string assetPath)
    {
        var hand = fighter.GetBoneTransform(HumanBodyBones.RightHand);
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (hand == null || source == null) return;
        var weapon = Visual(source, "StagedWeapon");
        weapon.transform.SetParent(hand, false);
        weapon.transform.localPosition = Vector3.zero;
        weapon.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
    }

    static void PoseZombie(GameObject zombie, float speed, float phase)
    {
        var animator = zombie.GetComponentInChildren<Animator>();
        if (animator == null) return;
        animator.enabled = true;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.applyRootMotion = false;
        animator.Rebind();
        foreach (var p in animator.parameters)
        {
            if (p.name == "Speed") animator.SetFloat(p.name, speed);
            if (p.name == "MotionSpeed") animator.SetFloat(p.name, 1f);
            if (p.name == "Grounded") animator.SetBool(p.name, true);
        }
        for (int i = 0; i < 18 + Mathf.RoundToInt(phase * 30f); i++) animator.Update(1f / 60f);
        animator.enabled = false;
    }

    static void AddNameTag(Animator worker, string text, Color color)
    {
        var head = worker.GetBoneTransform(HumanBodyBones.Head);
        var go = new GameObject("NameTag_" + text);
        go.transform.SetParent(stage.transform);
        go.transform.position = (head != null ? head.position : worker.transform.position + Vector3.up * 2f) + Vector3.up * .38f;
        var label = go.AddComponent<TextMeshPro>();
        label.text = text;
        label.fontSize = 2.2f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = color;
        label.outlineColor = new Color(0f, 0f, 0f, .95f);
        label.outlineWidth = .28f;
        label.rectTransform.sizeDelta = new Vector2(6f, .8f);
        coopNameTags.Add(label);
    }

    static void PositionCoopCamera(Vector3 subject, float yaw, float pitch, float distance)
    {
        camera.transform.position = subject + Quaternion.Euler(pitch, yaw, 0f) * new Vector3(0f, 0f, -distance);
        camera.transform.LookAt(subject);
        foreach (var tag in coopNameTags)
        {
            if (tag == null) continue;
            tag.transform.LookAt(camera.transform.position);
            tag.transform.Rotate(0f, 180f, 0f);
        }
    }

    static void Capture(string name, float yaw, float pitch, float distance, int width)
    {
        if(camera==null)throw new InvalidOperationException("Stage a shot first");
        camera.transform.position=centre+Quaternion.Euler(pitch,yaw,0)*new Vector3(0,0,-distance);
        camera.transform.LookAt(centre);
        int height=width*9/16;
        var rt = new RenderTexture(width,height,24,RenderTextureFormat.ARGB32);
        var old=RenderTexture.active;
        var tex=new Texture2D(width,height,TextureFormat.RGB24,false);
        try
        {
            camera.targetTexture=rt;
            camera.Render();
            RenderTexture.active=rt;
            tex.ReadPixels(new Rect(0,0,width,height),0,0); tex.Apply();
            File.WriteAllBytes(Path.Combine(Folder,name+".png"),tex.EncodeToPNG());
        }
        finally { camera.targetTexture=null; RenderTexture.active=old; rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(tex); }
    }

    static void Restore()
    {
        if(previousMainCamera!=null)previousMainCamera.enabled=previousMainCameraEnabled;
        previousMainCamera=null;
        coopNameTags.Clear();
        if(stage!=null)Object.DestroyImmediate(stage);
        camera=null;
        if(staged)Time.timeScale=previousTime;
        staged=false;
    }
}
