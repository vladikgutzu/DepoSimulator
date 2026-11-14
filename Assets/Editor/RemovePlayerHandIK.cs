using UnityEngine;
using UnityEditor;

public static class RemovePlayerHandIK
{
    public static void Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) { Debug.LogError("[Cleanup] Player не найден!"); return; }

        var ik = player.GetComponent<PlayerHandIK>();
        if (ik != null)
        {
            Undo.DestroyObjectImmediate(ik);
            Debug.Log("[Cleanup] PlayerHandIK удалён с Player.");
        }
        else
            Debug.Log("[Cleanup] PlayerHandIK и так отсутствует.");

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }
}
