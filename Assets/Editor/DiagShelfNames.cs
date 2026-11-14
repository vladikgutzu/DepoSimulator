using UnityEngine;
using UnityEditor;

public class DiagShelfNames
{
    public static void Execute()
    {
        // Find shelfLarge objects and print their parent chain
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (t.name != "shelfLarge") continue;
            // Print up to 4 levels of parents
            string chain = t.name;
            Transform cur = t.parent;
            for (int i = 0; i < 4 && cur != null; i++, cur = cur.parent)
                chain = cur.name + " > " + chain;
            if (seen.Add(chain))
                Debug.Log($"[ShelfNames] {chain}");
        }

        // Also find any GOs with "shelf" in name that have Renderers
        Debug.Log("[ShelfNames] --- GameObjects with 'shelf' in name that have children ---");
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            if (!go.name.ToLower().Contains("shelf")) continue;
            if (go.transform.childCount == 0) continue;
            if (go.GetComponentsInChildren<Renderer>(false).Length == 0) continue;
            Debug.Log($"[ShelfNames] '{go.name}'  parent='{go.transform.parent?.name}'  children={go.transform.childCount}");
        }
    }
}
