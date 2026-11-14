using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// Grabs press screenshots out of the running game.
///
/// Deliberately captures the Game view, not the Scene view: the Scene view has no HUD, its own
/// camera and its own lighting, so a shot from it is a picture of the editor rather than of the
/// game. ScreenCapture.CaptureScreenshot renders exactly what the player is looking at.
///
/// Files land outside Assets/ so Unity never imports them — a listing screenshot is not a game
/// asset, and importing a pile of 4K PNGs only slows the project down.
public static class GameplayScreenshot
{
    const string Folder = "Screenshots";

    /// 1 = native game-view resolution. Higher renders the frame that many times larger, which
    /// is how you get a 4K shot out of a 1080p editor window. UI Toolkit HUD scales with it,
    /// but check the result: some overlays are laid out in pixels and will look thin.
    const int SuperSize = 2;

    [MenuItem("Tools/Warehouse/Capture Gameplay Screenshot _F9", priority = 100)]
    public static void Capture()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Screenshot] Only works in play mode — that is the whole point: " +
                             "the Game view is what a player sees.");
            return;
        }

        Directory.CreateDirectory(Folder);

        string path = Path.Combine(Folder, $"depo_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        // Lands on the next rendered frame, not this one, so the file appears a moment later.
        ScreenCapture.CaptureScreenshot(path, SuperSize);

        Debug.Log($"[Screenshot] {path} (x{SuperSize})");
    }

    /// Something has to be happening for a screenshot to be worth taking. This drops a raid on
    /// the warehouse on demand rather than waiting out the event timer.
    [MenuItem("Tools/Warehouse/Force Raid Now _F10", priority = 101)]
    public static void ForceRaid()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Screenshot] Play mode only.");
            return;
        }

        var gem = GameEventManager.Instance;
        if (gem == null) { Debug.LogWarning("[Screenshot] No GameEventManager in the scene."); return; }

        Debug.Log(gem.ForceStartEvent("Thief")
            ? "[Screenshot] Raid started — raiders walk in a few seconds apart."
            : "[Screenshot] Raid refused (already running, or nothing loaded to raid).");
    }
}
