using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Tools → Warehouse Demo → Add Floor Highlight Targets
///
/// Finds every AssemblyBench and OrderBoard in the active scene and adds a
/// FloorHighlightTarget component if one is not already present.
/// Run once after adding the component to the project.
public static class AddFloorHighlightTargets
{
    [MenuItem("Tools/Warehouse Demo/Add Floor Highlight Targets")]
    public static void Execute()
    {
        int added = 0;

        foreach (var bench in Object.FindObjectsByType<AssemblyBench>(FindObjectsSortMode.None))
        {
            if (bench.GetComponent<FloorHighlightTarget>() == null)
            {
                Undo.AddComponent<FloorHighlightTarget>(bench.gameObject);
                added++;
                Debug.Log($"[FloorHighlight] Added to AssemblyBench on '{bench.gameObject.name}'");
            }
        }

        foreach (var board in Object.FindObjectsByType<OrderBoard>(FindObjectsSortMode.None))
        {
            if (board.GetComponent<FloorHighlightTarget>() == null)
            {
                Undo.AddComponent<FloorHighlightTarget>(board.gameObject);
                added++;
                Debug.Log($"[FloorHighlight] Added to OrderBoard on '{board.gameObject.name}'");
            }
        }

        if (added == 0)
        {
            Debug.Log("[FloorHighlight] Nothing to add — components already present or no matching objects found.");
            return;
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log($"[FloorHighlight] Done — added FloorHighlightTarget to {added} object(s). Scene saved.");
    }
}
