using System.Reflection;
using UnityEngine;

public class TutorialVerifyHelper
{
    public static void StartSingleplayer()
    {
        var menu = Object.FindFirstObjectByType<MainMenuController>();
        if (menu == null) { Debug.LogError("[Verify] MainMenuController not found"); return; }

        var method = typeof(MainMenuController)
            .GetMethod("StartSingleplayer", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) { Debug.LogError("[Verify] StartSingleplayer method not found"); return; }

        method.Invoke(menu, null);
        Debug.Log("[Verify] StartSingleplayer invoked");
    }

    public static void SkipToOrderBoard()
    {
        var tm = Object.FindFirstObjectByType<TutorialManager>();
        if (tm == null) { Debug.LogError("[Verify] TutorialManager not found"); return; }

        // Skip to OrderBoardIntro step (step 4) via reflection
        var method = typeof(TutorialManager)
            .GetMethod("GoTo", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) { Debug.LogError("[Verify] GoTo not found"); return; }

        // Step.OrderBoardIntro = 4
        var stepType = typeof(TutorialManager).GetNestedType("Step", BindingFlags.NonPublic);
        var step = System.Enum.ToObject(stepType, 4);
        method.Invoke(tm, new[] { step });
        Debug.Log("[Verify] Jumped to OrderBoardIntro");
    }

    public static void SkipToFindParts()
    {
        var tm = Object.FindFirstObjectByType<TutorialManager>();
        if (tm == null) { Debug.LogError("[Verify] TutorialManager not found"); return; }

        var method = typeof(TutorialManager)
            .GetMethod("GoTo", BindingFlags.NonPublic | BindingFlags.Instance);
        var stepType = typeof(TutorialManager).GetNestedType("Step", BindingFlags.NonPublic);
        // Step.FindParts = 7
        var step = System.Enum.ToObject(stepType, 7);
        method.Invoke(tm, new[] { step });

        // Also call HighlightAllRequiredParts
        var highlight = typeof(TutorialManager)
            .GetMethod("HighlightAllRequiredParts", BindingFlags.NonPublic | BindingFlags.Instance);
        highlight?.Invoke(tm, null);
        Debug.Log("[Verify] Jumped to FindParts + highlighted parts");
    }

    public static void SkipToBench()
    {
        var tm = Object.FindFirstObjectByType<TutorialManager>();
        if (tm == null) { Debug.LogError("[Verify] TutorialManager not found"); return; }

        var method = typeof(TutorialManager)
            .GetMethod("GoTo", BindingFlags.NonPublic | BindingFlags.Instance);
        var stepType = typeof(TutorialManager).GetNestedType("Step", BindingFlags.NonPublic);
        // Step.BenchIntro = 10
        var step = System.Enum.ToObject(stepType, 10);
        method.Invoke(tm, new[] { step });
        Debug.Log("[Verify] Jumped to BenchIntro");
    }
}
