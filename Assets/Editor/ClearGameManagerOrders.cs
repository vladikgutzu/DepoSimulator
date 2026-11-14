using UnityEngine;
using UnityEditor;

public class ClearGameManagerOrders
{
    public static void Execute()
    {
        var gm = Object.FindFirstObjectByType<GameManager>();
        if (gm == null)
        {
            Debug.LogWarning("[ClearOrders] GameManager not found in scene.");
            return;
        }

        var so = new SerializedObject(gm);
        var prop = so.FindProperty("_availableOrders");
        if (prop == null)
        {
            Debug.LogWarning("[ClearOrders] _availableOrders property not found.");
            return;
        }

        prop.ClearArray();
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(gm);

        // Also mark scene dirty so it saves
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[ClearOrders] _availableOrders cleared — GameManager will now auto-load from Catalog/Orders.");
    }
}
