using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class WrapOrderBoardVisual
{
    public static void Execute()
    {
        // Find objects by traversing hierarchy
        var orderBoard = GameObject.Find("GameplayRoot")
            ?.transform.Find("OrderBoard")?.gameObject;
        if (orderBoard == null) { Debug.LogError("OrderBoard not found"); return; }

        var visual = orderBoard.transform.Find("OrderBoardVisual")?.gameObject;
        if (visual == null) { Debug.LogError("OrderBoardVisual not found"); return; }

        // Create wrapper at same world position as the GLB visual
        var wrapper = new GameObject("OrderBoardModel");
        wrapper.transform.SetParent(orderBoard.transform, false);
        wrapper.transform.localPosition = Vector3.zero;
        wrapper.transform.localRotation = Quaternion.identity;
        wrapper.transform.localScale    = new Vector3(0.25f, 0.25f, 0.25f);

        // Move GLB under wrapper (world position stays)
        visual.transform.SetParent(wrapper.transform, true);

        // Reset visual local transform so wrapper drives everything
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        visual.transform.localScale    = Vector3.one;

        EditorUtility.SetDirty(orderBoard);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"[WrapOrderBoardVisual] Done. Wrapper scale=0.25. GLB bounds: {visual.GetComponentInChildren<Renderer>()?.bounds}");
    }
}
