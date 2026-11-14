using UnityEngine;
using UnityEditor;

public class AssignPackedBoxPrefab
{
    public static void Execute()
    {
        var bench = Object.FindFirstObjectByType<AssemblyBench>();
        if (bench == null) { Debug.LogWarning("[BoxPrefab] AssemblyBench not found."); return; }

        var boxSmall  = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/env/box_med_prefab.prefab");
        var boxMedium = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/env/box_long_flat_prefab.prefab");
        var boxLarge  = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/env/box_large_prefab.prefab");

        var so = new SerializedObject(bench);

        void Assign(string field, GameObject prefab)
        {
            var prop = so.FindProperty(field);
            if (prop == null) { Debug.LogWarning($"[BoxPrefab] Field '{field}' not found."); return; }
            prop.objectReferenceValue = prefab;
            Debug.Log($"[BoxPrefab] {field} = {prefab?.name ?? "null"}");
        }

        Assign("_boxSmall",  boxSmall);
        Assign("_boxMedium", boxMedium);
        Assign("_boxLarge",  boxLarge);

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(bench);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[BoxPrefab] Done: small=box_med, medium=box_long_flat, large=box_large");
    }
}
