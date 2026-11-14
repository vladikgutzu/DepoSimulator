using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class TestEventRunner
{
    // Step 1: load the warehouse scene directly
    public static void LoadWarehouseScene()
    {
        // Try common scene names
        string[] candidates = { "WarehouseDemo", "Warehouse", "Main", "Game", "Demo", "SampleScene" };
        foreach (var name in candidates)
        {
            try { SceneManager.LoadScene(name); Debug.Log("[Test] Loaded scene: " + name); return; }
            catch { }
        }

        // Fallback: load scene index 1 (0 is usually main menu)
        if (SceneManager.sceneCountInBuildSettings > 1)
        {
            SceneManager.LoadScene(1);
            Debug.Log("[Test] Loaded scene index 1");
        }
        else
        {
            Debug.LogWarning("[Test] Could not find warehouse scene");
        }
    }

    // Step 2: fire all three new events in sequence
    public static void FirePowerFailure()
    {
        var mgr = Object.FindFirstObjectByType<GameEventManager>();
        if (mgr == null) { Debug.LogWarning("[Test] GameEventManager not found"); return; }
        new PowerFailureEvent().Apply(mgr);
        Debug.Log("[Test] PowerFailureEvent fired");
    }

    public static void FireFireAlarm()
    {
        var mgr = Object.FindFirstObjectByType<GameEventManager>();
        if (mgr == null) { Debug.LogWarning("[Test] GameEventManager not found"); return; }
        new FireAlarmEvent().Apply(mgr);
        Debug.Log("[Test] FireAlarmEvent fired");
    }

    public static void FireWrongOrder()
    {
        var mgr = Object.FindFirstObjectByType<GameEventManager>();
        if (mgr == null) { Debug.LogWarning("[Test] GameEventManager not found"); return; }
        new WrongOrderEvent().Apply(mgr);
        Debug.Log("[Test] WrongOrderEvent fired");
    }

    // Run all three one after another (logs results)
    public static void FireAll()
    {
        var mgr = Object.FindFirstObjectByType<GameEventManager>();
        if (mgr == null) { Debug.LogWarning("[Test] GameEventManager not found in scene"); return; }

        int fired = 0;
        try { new PowerFailureEvent().Apply(mgr); fired++; Debug.Log("[Test] ✓ PowerFailureEvent"); }
        catch (System.Exception e) { Debug.LogError("[Test] ✗ PowerFailureEvent: " + e.Message); }

        try { new FireAlarmEvent().Apply(mgr); fired++; Debug.Log("[Test] ✓ FireAlarmEvent"); }
        catch (System.Exception e) { Debug.LogError("[Test] ✗ FireAlarmEvent: " + e.Message); }

        try { new WrongOrderEvent().Apply(mgr); fired++; Debug.Log("[Test] ✓ WrongOrderEvent"); }
        catch (System.Exception e) { Debug.LogError("[Test] ✗ WrongOrderEvent: " + e.Message); }

        Debug.Log($"[Test] Done: {fired}/3 events fired successfully");
    }

    // Check chaos scaling state
    public static void CheckScaling()
    {
        var mgr = Object.FindFirstObjectByType<GameEventManager>();
        if (mgr == null) { Debug.LogWarning("[Test] GameEventManager not found"); return; }
        Debug.Log($"[Test] GameEventManager found — active events: check inspector. EnvironmentSpeedMult={GameEventManager.EnvironmentSpeedMult:F2}");
    }
}
