using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Assigns MotoOrders ScriptableObjects to GameManager._availableOrders.
// Run once if the OrderBoard shows no orders.
// Menu: Tools → Warehouse Demo → Fix GameManager Orders
public static class FixGameManagerOrders
{
    [MenuItem("Tools/Warehouse Demo/Fix GameManager Orders")]
    public static void Execute()
    {
        var gm = Object.FindObjectOfType<GameManager>();
        if (gm == null) { Debug.LogError("[FixOrders] GameManager not found."); return; }

        var orders = Resources.LoadAll<OrderDefinition>("MotoOrders");
        if (orders.Length == 0) { Debug.LogError("[FixOrders] No OrderDefinition found in Resources/MotoOrders/"); return; }

        System.Array.Sort(orders, (a, b) => a.Reward.CompareTo(b.Reward));

        var so  = new SerializedObject(gm);
        var arr = so.FindProperty("_availableOrders");
        arr.arraySize = orders.Length;
        for (int i = 0; i < orders.Length; i++)
            arr.GetArrayElementAtIndex(i).objectReferenceValue = orders[i];
        so.ApplyModifiedProperties();

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[FixOrders] Assigned {orders.Length} orders to GameManager. Save scene (Ctrl+S).");
    }
}
