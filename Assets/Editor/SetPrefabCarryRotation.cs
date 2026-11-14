using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Reflection;

public static class SetPrefabCarryRotation
{
    static readonly (string path, float holdDist, float handHeight, Vector3 rotation)[] Configs =
    {
        ("Assets/Prefabs/env/box_med_prefab.prefab",       0.15f, 0.85f, Vector3.zero),
        ("Assets/Prefabs/env/box_large_prefab.prefab",     0.15f, 0.85f, Vector3.zero),
        ("Assets/Prefabs/env/box_long_flat_prefab.prefab", 0.15f, 0.85f, new Vector3(-10f,  0f, 0f)),
        ("Assets/Prefabs/env/box_wide_flat_prefab.prefab", 0.15f, 0.85f, new Vector3(-100f, 0f, 0f)),
    };

    // Step 1: write values to prefab assets only
    public static void Execute()
    {
        var type = typeof(BoxPhysicsInteractable);
        var hdF  = type.GetField("_holdDistance",  BindingFlags.NonPublic | BindingFlags.Instance);
        var hhF  = type.GetField("_handHeight",    BindingFlags.NonPublic | BindingFlags.Instance);
        var crF  = type.GetField("_carryRotation", BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var (path, holdDist, handHeight, rotation) in Configs)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Debug.LogWarning($"Not found: {path}"); continue; }

            var bpi = prefab.GetComponent<BoxPhysicsInteractable>();
            if (bpi == null) continue;

            hdF?.SetValue(bpi, holdDist);
            hhF?.SetValue(bpi, handHeight);
            crF?.SetValue(bpi, rotation);
            EditorUtility.SetDirty(prefab);
            Debug.Log($"[PrefabCarry] {prefab.name}: dist={holdDist} height={handHeight} rot={rotation}");
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[PrefabCarry] Prefabs updated. Run RevertInstanceOverrides to clean scene instances.");
    }

    // Step 2: revert instance overrides in batches (run separately if needed)
    public static void RevertInstanceOverrides()
    {
        var all      = Object.FindObjectsByType<BoxPhysicsInteractable>(FindObjectsInactive.Include);
        var props    = new[] { "_holdDistance", "_handHeight", "_carryRotation" };
        int reverted = 0;

        foreach (var bpi in all)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(bpi)) continue;
            // Revert entire component overrides at once — much faster than per-property
            PrefabUtility.RevertObjectOverride(bpi, InteractionMode.AutomatedAction);
            reverted++;
        }

        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log($"[PrefabCarry] Reverted overrides on {reverted} instances.");
    }
}
