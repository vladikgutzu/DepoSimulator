using UnityEngine;
using UnityEditor;

public static class AddPlayerCarryLayer
{
    public static void Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) { Debug.LogError("[CarryLayer] Player не найден!"); return; }

        if (player.GetComponent<PlayerCarryLayer>() == null)
        {
            Undo.AddComponent<PlayerCarryLayer>(player);
            Debug.Log("[CarryLayer] PlayerCarryLayer добавлен на Player.");
        }
        else
            Debug.Log("[CarryLayer] PlayerCarryLayer уже есть.");

        if (!Application.isPlaying)
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }
}
