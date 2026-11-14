using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

public static class SetupOcclusionFader
{
    public static void Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) { Debug.LogError("[OcclusionFader] Player not found"); return; }

        var fader = player.GetComponent<CameraOcclusionFader>();
        if (fader == null)
            fader = player.AddComponent<CameraOcclusionFader>();

        // Set occlusion mask: check Default layer (0) only — shelves/walls.
        // Player is on layer 8 ("Outline"), so it won't be in the raycast.
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        LayerMask mask = LayerMask.GetMask("Default");
        typeof(CameraOcclusionFader)
            .GetField("_occlusionMask", flags)
            ?.SetValue(fader, mask);

        typeof(CameraOcclusionFader)
            .GetField("_fadeAlpha", flags)
            ?.SetValue(fader, 0.15f);

        typeof(CameraOcclusionFader)
            .GetField("_fadeSpeed", flags)
            ?.SetValue(fader, 10f);

        typeof(CameraOcclusionFader)
            .GetField("_headHeight", flags)
            ?.SetValue(fader, 1.4f);

        EditorUtility.SetDirty(player);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            SceneManager.GetActiveScene());
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(
            SceneManager.GetActiveScene());

        Debug.Log("[OcclusionFader] CameraOcclusionFader added to Player. Layer mask = Default.");
    }
}
