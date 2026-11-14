using UnityEngine;
using UnityEditor;

public class WireMinimapFull
{
    public static void Execute()
    {
        var gmGO = GameObject.Find("GameManager");
        if (gmGO == null) { Debug.LogError("[WireMinimapFull] GameManager not found"); return; }

        var mm = gmGO.GetComponent<MinimapController>();
        if (mm == null) { Debug.LogError("[WireMinimapFull] MinimapController not found"); return; }

        var so = new SerializedObject(mm);

        // World bounds
        so.FindProperty("_worldCenter").vector2Value = new Vector2(6f, -6f);
        so.FindProperty("_worldSize").floatValue = 32f;

        // Use explicit paths — GameplayRoot contains the actual demo objects,
        // Warehouse/ contains training-scene duplicates with the same scripts.
        WireTransform(so, "_orderBoardTransform", "GameplayRoot/OrderBoard");
        WireTransform(so, "_benchTransform",      "GameplayRoot/AssemblyBench");
        WireTransform(so, "_shippingZoneTransform","GameplayRoot/ShippingZone");

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(mm);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log("[WireMinimapFull] Done.");
    }

    static void WireTransform(SerializedObject so, string prop, string path)
    {
        var go = GameObject.Find(path);
        if (go != null)
        {
            so.FindProperty(prop).objectReferenceValue = go.transform;
            Debug.Log($"[WireMinimapFull] Wired {prop} → {path}");
        }
        else
        {
            Debug.LogWarning($"[WireMinimapFull] Not found: {path}");
        }
    }
}
