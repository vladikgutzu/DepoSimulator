using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// Tools → Warehouse Demo → Add Player Death Manager
/// Adds PlayerDeathManager to the player GameObject in the current scene.
public static class AddPlayerDeathManager
{
    [MenuItem("Tools/Warehouse Demo/Add Player Death Manager")]
    static void Run()
    {
        var fpc = Object.FindFirstObjectByType<FirstPersonController>();
        if (fpc == null)
        {
            EditorUtility.DisplayDialog("Error", "No FirstPersonController found in scene.", "OK");
            return;
        }

        var go = fpc.gameObject;
        if (go.GetComponent<PlayerDeathManager>() != null)
        {
            EditorUtility.DisplayDialog("Already done",
                $"PlayerDeathManager already exists on '{go.name}'.", "OK");
            return;
        }

        Undo.AddComponent<PlayerDeathManager>(go);
        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"[PlayerDeath] Added PlayerDeathManager to '{go.name}'", go);
        EditorUtility.DisplayDialog("Done",
            $"PlayerDeathManager added to '{go.name}'.\n\nAlso run:\nTools → Warehouse Demo → Setup Physics Shelves\nto enable shelf tipping.",
            "OK");
    }
}
