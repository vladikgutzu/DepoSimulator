using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class RevertCarryRotation
{
    [MenuItem("Tools/Warehouse Demo/Revert Carry Rotation to Prefab")]
    public static void Execute()
    {
        var all = Object.FindObjectsByType<BoxPhysicsInteractable>(FindObjectsInactive.Include);
        int reverted = 0;

        foreach (var bpi in all)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(bpi)) continue;

            var so   = new SerializedObject(bpi);
            var prop = so.FindProperty("_carryRotation");
            if (prop == null) continue;

            PrefabUtility.RevertPropertyOverride(prop, InteractionMode.AutomatedAction);
            reverted++;
        }

        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log($"[RevertRot] Reverted _carryRotation on {reverted} instances — now using prefab value.");
    }
}
