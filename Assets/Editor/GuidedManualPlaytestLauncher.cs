#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Editor entry point and scene restoration for Guided Manual Playtest.</summary>
[InitializeOnLoad]
public static class GuidedManualPlaytestLauncher
{
    const string ScenePath = "Assets/urp_scene_with_training 1.unity";
    const string RestoreSceneKey = "GuidedQA.RestoreScene";

    static GuidedManualPlaytestLauncher()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    [MenuItem("Tools/QA/Start Guided Playtest", priority = 2)]
    public static void StartGuidedPlaytest()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[GuidedQA] Stop Play Mode before starting a new guided playtest.");
            return;
        }

        var current = EditorSceneManager.GetActiveScene();
        if (current.isDirty)
        {
            Debug.LogError("[GuidedQA] Save the active scene before starting the guided playtest.");
            return;
        }

        SessionState.SetString(RestoreSceneKey, current.path);
        GuidedManualPlaytest.BeginNewRun();
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Debug.Log("[GuidedQA] Training warehouse loaded; entering Play Mode.");
        EditorApplication.isPlaying = true;
    }

    [MenuItem("Tools/QA/Stop Guided Playtest", priority = 3)]
    public static void StopGuidedPlaytest()
    {
        PlayerPrefs.SetInt(GuidedManualPlaytest.ActiveKey, 0);
        PlayerPrefs.Save();
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode)
        {
            PlayerPrefs.SetInt(GuidedManualPlaytest.ActiveKey, 0);
            PlayerPrefs.Save();

            string restore = SessionState.GetString(RestoreSceneKey, string.Empty);
            SessionState.EraseString(RestoreSceneKey);
            if (!string.IsNullOrEmpty(restore) && restore != ScenePath)
                EditorApplication.delayCall += () => EditorSceneManager.OpenScene(restore, OpenSceneMode.Single);
        }
    }
}
#endif
