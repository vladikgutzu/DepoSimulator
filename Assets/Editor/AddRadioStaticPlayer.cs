using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public class AddRadioStaticPlayer
{
    public static string Execute()
    {
        // Check if already exists
        var existing = Object.FindFirstObjectByType<RadioStaticPlayer>();
        if (existing != null)
            return $"RadioStaticPlayer already exists on '{existing.gameObject.name}'";

        var go = new GameObject("RadioStaticPlayer");
        go.AddComponent<RadioStaticPlayer>();

        // Parent under AudioManager if it exists
        var am = Object.FindFirstObjectByType<AudioManager>();
        if (am != null)
            go.transform.SetParent(am.transform);

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();

        return $"RadioStaticPlayer created under '{go.transform.parent?.name ?? "scene root"}'";
    }
}
