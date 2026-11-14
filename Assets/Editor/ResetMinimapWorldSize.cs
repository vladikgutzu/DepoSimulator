using UnityEngine;
using UnityEditor;

public class ResetMinimapWorldSize
{
    public static void Execute()
    {
        var minimap = Object.FindAnyObjectByType<MinimapController>();
        if (minimap == null)
        {
            Debug.LogError("[ResetMinimap] MinimapController not found in scene.");
            return;
        }

        Undo.RecordObject(minimap, "Reset MinimapController worldSize");

        var so = new SerializedObject(minimap);
        var prop = so.FindProperty("_worldSize");
        if (prop != null)
        {
            prop.floatValue = 0f;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(minimap);
            Debug.Log($"[ResetMinimap] _worldSize set to 0 on '{minimap.gameObject.name}'. Save the scene to persist.");
        }
        else
        {
            Debug.LogError("[ResetMinimap] Property '_worldSize' not found.");
        }
    }
}
