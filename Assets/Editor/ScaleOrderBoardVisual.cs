using UnityEditor;
using UnityEngine;

public class ScaleOrderBoardVisual
{
    public static void Execute()
    {
        var go = GameObject.Find("OrderBoardVisual");
        if (go == null) { Debug.LogError("OrderBoardVisual not found"); return; }

        // Apply scale and position on the root
        go.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);

        // Position so bottom of model sits at floor (Y=0 world)
        // Parent OrderBoard is at world Y=1.5, so local Y = -1.5
        go.transform.localPosition = new Vector3(0f, -1.5f, 0f);

        EditorUtility.SetDirty(go);
        Debug.Log($"[ScaleOrderBoardVisual] Done. Bounds: {go.GetComponentInChildren<Renderer>()?.bounds}");
    }
}
