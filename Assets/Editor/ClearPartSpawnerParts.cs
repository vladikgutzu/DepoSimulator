using UnityEngine;
using UnityEditor;

public class ClearPartSpawnerParts
{
    public static void Execute()
    {
        var spawner = Object.FindFirstObjectByType<PartSpawner>();
        if (spawner == null)
        {
            Debug.LogWarning("[ClearParts] PartSpawner not found in scene.");
            return;
        }

        var so   = new SerializedObject(spawner);
        var prop = so.FindProperty("_allParts");
        if (prop == null)
        {
            Debug.LogWarning("[ClearParts] _allParts property not found.");
            return;
        }

        int before = prop.arraySize;
        prop.ClearArray();
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(spawner);

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"[ClearParts] _allParts cleared ({before} entries removed) — " +
                  "PartSpawner will now auto-load from Catalog/Parts.");
    }
}
