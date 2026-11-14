using UnityEngine;
using UnityEditor;

public class ClearFillerCount
{
    public static void Execute()
    {
        var spawner = Object.FindFirstObjectByType<PartSpawner>();
        if (spawner == null) { Debug.LogWarning("[Filler] PartSpawner not found."); return; }

        var so   = new SerializedObject(spawner);
        var prop = so.FindProperty("_fillerCount");
        if (prop == null) { Debug.LogWarning("[Filler] _fillerCount not found."); return; }

        prop.intValue = 0;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(spawner);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[Filler] _fillerCount set to 0 — no filler boxes will spawn.");
    }
}
