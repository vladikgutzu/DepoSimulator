using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class FixOrderBoardModel
{
    public static void Execute()
    {
        var orderBoard = GameObject.Find("GameplayRoot")
            ?.transform.Find("OrderBoard")?.gameObject;
        if (orderBoard == null) { Debug.LogError("OrderBoard not found"); return; }

        var model = orderBoard.transform.Find("OrderBoardModel")?.gameObject;
        if (model == null) { Debug.LogError("OrderBoardModel not found"); return; }

        // Scale 0.25 and drop so bottom sits on floor
        // Parent OrderBoard is at world Y=1.5
        // Model bounds min Y=1.65 → need to shift down by 1.65
        // local Y offset = -(1.65) relative to parent's Y
        model.transform.localPosition = new Vector3(0f, -1.65f, 0f);
        model.transform.localScale    = new Vector3(0.25f, 0.25f, 0.25f);

        // Also update OrderBoard BoxCollider to match new visual
        var col = orderBoard.GetComponent<BoxCollider>();
        if (col != null)
        {
            col.center = new Vector3(0f, -0.8f, -0.1f);
            col.size   = new Vector3(0.8f, 1.8f, 0.5f);
        }

        EditorUtility.SetDirty(orderBoard);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

        var rend = model.GetComponentInChildren<Renderer>();
        Debug.Log($"[FixOrderBoardModel] Done. Model bounds: {rend?.bounds}");
    }
}
