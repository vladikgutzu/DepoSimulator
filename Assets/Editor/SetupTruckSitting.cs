using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine.SceneManagement;

public static class SetupTruckSitting
{
    const string ClipFbxPath     = "Assets/Animations/Sitting Idle.fbx";
    const string ControllerPath  = "Assets/Animations/SittingController.controller";

    public static void Execute()
    {
        // ── 1. Load clip, ensure it loops ────────────────────────────────────
        AnimationClip sittingClip = null;
        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(ClipFbxPath))
            if (a is AnimationClip ac && !ac.name.StartsWith("__preview__"))
            { sittingClip = ac; break; }

        if (sittingClip == null)
        { Debug.LogError("[TruckSitting] Sitting Idle clip not found at " + ClipFbxPath); return; }

        // Set loop on the FBX importer clip settings
        var importer = AssetImporter.GetAtPath(ClipFbxPath) as ModelImporter;
        if (importer != null)
        {
            var clips = importer.clipAnimations;
            if (clips == null || clips.Length == 0)
                clips = importer.defaultClipAnimations;

            bool changed = false;
            for (int i = 0; i < clips.Length; i++)
            {
                if (!clips[i].loopTime) { clips[i].loopTime = true; clips[i].loopPose = true; changed = true; }
            }
            if (changed)
            {
                importer.clipAnimations = clips;
                importer.SaveAndReimport();
                // Reload clip after reimport
                foreach (var a in AssetDatabase.LoadAllAssetsAtPath(ClipFbxPath))
                    if (a is AnimationClip ac && !ac.name.StartsWith("__preview__"))
                    { sittingClip = ac; break; }
                Debug.Log("[TruckSitting] Set loop=true on Sitting Idle clip");
            }
        }

        // ── 2. Create AnimatorController with one looping Sitting Idle state ─
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null)
        {
            controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            var sm    = controller.layers[0].stateMachine;
            var state = sm.AddState("Sitting Idle");
            state.motion = sittingClip;
            sm.defaultState = state;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log("[TruckSitting] Created SittingController at " + ControllerPath);
        }
        else
        {
            // Ensure the existing controller points to the correct clip
            var sm = controller.layers[0].stateMachine;
            foreach (var s in sm.states)
                if (s.state.name == "Sitting Idle") { s.state.motion = sittingClip; break; }
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log("[TruckSitting] Updated existing SittingController");
        }

        // ── 3. Add hand targets to Steering_Wheel ────────────────────────────
        var truck = GameObject.Find("Reach_Truck_01a_Rig_Prefab_01");
        if (truck == null) { Debug.LogError("[TruckSitting] Truck not found in scene"); return; }

        var wheel = truck.transform.Find("Reach_Truck_01a_Rig/Root/Body/Steering_Wheel");
        if (wheel == null) { Debug.LogError("[TruckSitting] Steering_Wheel bone not found"); return; }

        // Remove existing targets if re-running
        var exR = wheel.Find("SteerHandTarget_R");
        var exL = wheel.Find("SteerHandTarget_L");
        if (exR != null) Object.DestroyImmediate(exR.gameObject);
        if (exL != null) Object.DestroyImmediate(exL.gameObject);

        // Hands at ~3 o'clock (right) and ~9 o'clock (left), radius ~0.18m.
        // Local axes of the wheel bone: X = across the wheel face, Y = up/down the spoke.
        var handRGO = new GameObject("SteerHandTarget_R");
        handRGO.transform.SetParent(wheel, false);
        handRGO.transform.localPosition = new Vector3( 0.18f, 0f, 0f);
        handRGO.transform.localRotation = Quaternion.identity;

        var handLGO = new GameObject("SteerHandTarget_L");
        handLGO.transform.SetParent(wheel, false);
        handLGO.transform.localPosition = new Vector3(-0.18f, 0f, 0f);
        handLGO.transform.localRotation = Quaternion.identity;

        Debug.Log("[TruckSitting] Hand targets added to Steering_Wheel (adjust localPosition in Inspector if needed)");

        // ── 4. Assign refs to ReachTruckController ───────────────────────────
        var rtc = truck.GetComponent<ReachTruckController>();
        if (rtc == null) { Debug.LogWarning("[TruckSitting] ReachTruckController not on truck root"); }
        else
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var t     = typeof(ReachTruckController);
            t.GetField("_sittingController",  flags)?.SetValue(rtc, controller);
            t.GetField("_steeringWheelBone",  flags)?.SetValue(rtc, wheel);
            t.GetField("_steerHandRight",     flags)?.SetValue(rtc, handRGO.transform);
            t.GetField("_steerHandLeft",      flags)?.SetValue(rtc, handLGO.transform);
            EditorUtility.SetDirty(rtc);
            Debug.Log("[TruckSitting] ReachTruckController refs assigned");
        }

        EditorUtility.SetDirty(truck);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            SceneManager.GetActiveScene());
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(
            SceneManager.GetActiveScene());
        Debug.Log("[TruckSitting] Done. Tweak SteerHandTarget positions in Inspector if hands are off.");
    }
}
