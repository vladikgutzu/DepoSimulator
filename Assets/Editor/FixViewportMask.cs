using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;

// Fixes the OrderBoardPanel Viewport Image alpha=0 bug that clips all scroll content.
// Menu: Tools → Warehouse Demo → Fix Viewport Mask
public static class FixViewportMask
{
    [MenuItem("Tools/Warehouse Demo/Fix Viewport Mask")]
    public static void Execute()
    {
        var root = GameObject.Find("GameplayRoot");
        if (root == null) { Debug.LogError("[FixVP] GameplayRoot not found."); return; }

        var vpT = root.transform.Find("GameHUD/OrderBoardPanel/ScrollView/Viewport");
        if (vpT == null) { Debug.LogError("[FixVP] Viewport not found."); return; }

        var img = vpT.GetComponent<Image>();
        if (img == null) { Debug.LogError("[FixVP] No Image on Viewport."); return; }

        var so = new SerializedObject(img);
        var colProp = so.FindProperty("m_Color");
        colProp.colorValue = new Color(0f, 0f, 0f, 0.01f);
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[FixVP] Viewport Image alpha set to 0.01. Save scene (Ctrl+S) then Play.");
    }
}
