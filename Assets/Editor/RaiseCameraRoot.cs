using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class RaiseCameraRoot
{
    public static void Execute()
    {
        var player = GameObject.Find("Player");
        if (player == null) { Debug.LogError("[CamRoot] Player not found"); return; }

        var camRoot = player.transform.Find("PlayerCameraRoot");
        if (camRoot == null) { Debug.LogError("[CamRoot] PlayerCameraRoot not found under Player"); return; }

        var oldY = camRoot.localPosition.y;
        camRoot.localPosition = new Vector3(0f, 1.7f, 0f);

        EditorUtility.SetDirty(camRoot.gameObject);
        PrefabUtility.RecordPrefabInstancePropertyModifications(camRoot);
        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"[CamRoot] Y: {oldY} -> {camRoot.localPosition.y}");
    }
}
