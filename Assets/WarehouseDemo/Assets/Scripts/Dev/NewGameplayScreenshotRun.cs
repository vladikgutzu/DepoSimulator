#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot capture session requested by Temp/NewGameplayScreenshots.request.
/// Every frame is captured from live play mode while the regular gameplay systems run.
/// </summary>
[InitializeOnLoad]
public static class NewGameplayScreenshotBootstrap
{
    const string RequestFile = "Temp/NewGameplayScreenshots.request";
    const string SessionKey = "SteamShots.NewGameplaySessionRunning";

    static NewGameplayScreenshotBootstrap()
    {
        string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
        string request = Path.Combine(root, RequestFile);
        if (!File.Exists(request)) return;

        File.Delete(request);
        SessionState.SetBool(SessionKey, true);
        EditorApplication.update -= StartWhenEditorIsReady;
        EditorApplication.update += StartWhenEditorIsReady;
    }

    static void StartWhenEditorIsReady()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        EditorApplication.update -= StartWhenEditorIsReady;
        StartCaptureFromMenu();
    }

    [MenuItem("Tools/Warehouse Demo/Capture New Gameplay Screenshots")]
    public static void StartCaptureFromMenu()
    {
        SessionState.SetBool(SessionKey, true);
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;

        if (EditorApplication.isPlaying)
        {
            CreateRunner();
            return;
        }

        EditorApplication.isPlaying = true;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode) return;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        CreateRunner();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void RestoreRunnerAfterPlayModeReload()
    {
        if (!SessionState.GetBool(SessionKey, false)) return;
        CreateRunner();
    }

    static void CreateRunner()
    {
        if (UnityEngine.Object.FindFirstObjectByType<NewGameplayScreenshotRun>() != null) return;
        var go = new GameObject("~NewGameplayScreenshotRun");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<NewGameplayScreenshotRun>();
    }
}

public sealed class NewGameplayScreenshotRun : MonoBehaviour
{
    string _folder;

    IEnumerator Start()
    {
        yield return WaitFor(() => MainMenuController.Instance != null, 20f);
        SteamGameplayPilot.Ensure();
        SteamGameplayPilot.Solo();

        yield return new WaitForSecondsRealtime(5f);
        yield return WaitFor(() => GameObject.FindGameObjectWithTag("Player") != null
                                  && TutorialManager.Instance != null
                                  && HUDController.Instance != null, 25f);
        yield return new WaitForSecondsRealtime(2f);

        SteamGameplayPilot.Ensure();
        SteamGameplayPilot.Prep();
        SteamGameplayPilot.HideHud(false);
        HideTutorialNow();
        yield return new WaitForSecondsRealtime(1f);
        HideTutorialNow();

        string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
        _folder = Path.Combine(root, "Screenshots", "SteamSession",
            "new_gameplay_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(root, "Screenshots", "SteamSession", "LATEST_NEW_GAMEPLAY.txt"), _folder);

        // Genuine player movement through the active warehouse shift.
        SteamGameplayPilot.View(18f, 9f);
        SteamGameplayPilot.Hold(0.12f, 1f, true);
        yield return new WaitForSecondsRealtime(1.4f);
        yield return Capture("01_shift_run.png");

        SteamGameplayPilot.View(62f, 13f);
        SteamGameplayPilot.Hold(-0.22f, 1f, false);
        yield return new WaitForSecondsRealtime(1.2f);
        yield return Capture("02_warehouse_work.png");
        SteamGameplayPilot.Stop();

        // Board and drive a real reach truck, using the same controller as the player.
        var truck = FindObjectsByType<ReachTruckController>(FindObjectsSortMode.None).FirstOrDefault();
        var playerObject = GameObject.FindGameObjectWithTag("Player");
        var player = playerObject != null ? playerObject.transform.root.gameObject : null;
        if (truck != null && player != null)
        {
            truck.Interact(player);
            yield return new WaitForSecondsRealtime(1.5f);
            SteamGameplayPilot.KeyDown("W", true);
            yield return new WaitForSecondsRealtime(1.4f);
            yield return Capture("03_forklift_drive.png");
            SteamGameplayPilot.KeyDown("W", false);
            SteamGameplayPilot.KeyDown("Q", true);
            yield return new WaitForSecondsRealtime(1.8f);
            yield return Capture("04_forklift_forks.png");
            SteamGameplayPilot.KeyDown("Q", false);
            SteamGameplayPilot.Key("E");
            yield return new WaitForSecondsRealtime(1.5f);
        }

        // Real timed order event with its normal HUD and warehouse markers.
        SteamGameplayPilot.Event("RushOrder");
        yield return new WaitForSecondsRealtime(1.2f);
        SteamGameplayPilot.Key("Enter");
        yield return new WaitForSecondsRealtime(2f);
        SteamGameplayPilot.Hold(0.15f, 1f, true);
        yield return new WaitForSecondsRealtime(1f);
        yield return Capture("05_rush_order.png");
        SteamGameplayPilot.Stop();

        ClearEvent("RushOrder");
        SteamGameplayPilot.Event("Thief");
        yield return new WaitForSecondsRealtime(2f);
        SteamGameplayPilot.Key("Enter");
        yield return new WaitForSecondsRealtime(1f);
        SteamGameplayPilot.Chase("nearest", 2.2f, true);
        SteamGameplayPilot.Face("nearest", 7f, -10f);
        yield return WaitForRaider(3.2f, 30f);
        yield return Capture("06_raider_chase.png");
        SteamGameplayPilot.Click(0, 0.15f);
        yield return new WaitForSecondsRealtime(0.18f);
        yield return Capture("07_raider_combat.png");

        SteamGameplayPilot.Stop();
        SteamGameplayPilot.FaceOff();
        File.WriteAllText(Path.Combine(_folder, "CAPTURE_COMPLETE.txt"),
            "New live gameplay capture completed at " + DateTime.Now.ToString("O"));
        Debug.Log("[NewGameplayScreenshots] COMPLETE: " + _folder);
        SessionState.SetBool("SteamShots.NewGameplaySessionRunning", false);
    }

    static IEnumerator WaitFor(Func<bool> predicate, float timeout)
    {
        float started = Time.realtimeSinceStartup;
        while (!predicate() && Time.realtimeSinceStartup - started < timeout)
            yield return null;
    }

    static IEnumerator WaitForRaider(float range, float timeout)
    {
        float started = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - started < timeout)
        {
            var playerObject = GameObject.FindGameObjectWithTag("Player");
            var raider = ThiefController.All.FirstOrDefault(t => t != null && t.Current != ThiefController.State.Down);
            if (playerObject != null && raider != null &&
                Vector3.Distance(playerObject.transform.root.position, raider.transform.position) <= range)
                yield break;
            yield return null;
        }
    }

    IEnumerator Capture(string fileName)
    {
        HideTutorialNow();
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(_folder, fileName), 1);
        yield return new WaitForSecondsRealtime(1f);
    }

    static void HideTutorialNow()
    {
        TutorialManager.Instance?.Skip();
        HUDController.Instance?.HideTutorial();
    }

    static void ClearEvent(string eventName)
    {
        var manager = GameEventManager.Instance;
        if (manager == null) return;
        var active = manager.GetActiveEvent(eventName);
        if (active != null) active.Remove();
        if (manager.IsEventActive(eventName)) manager.MarkEventDone(eventName);
    }
}
#endif
