using UnityEditor.SceneManagement;
using UnityEngine;

public class SaveScene
{
    public static void Execute()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        bool ok = EditorSceneManager.SaveScene(scene);
        Debug.Log(ok ? $"[SaveScene] Saved: {scene.path}" : "[SaveScene] Save FAILED");
    }
}
