using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Clears the static flags on the moving part of every wall gate.
///
/// This is why the gate looked like it never opened. The gates are authored fully Static, and a
/// Batching Static renderer is merged into one combined mesh at load and then drawn from where it
/// was baked, whatever its transform does afterwards. So the animation was running and the
/// transform really was rising 3.05 m — measured — while the shutter on screen never moved, and the
/// raiders walked through a door that was open in the data and shut on screen.
///
/// Only the leaf group is cleared, not the wall around it: the frame, the wall panels and the
/// pipework never move and should stay batched and contributing to the lightmap. What is lost is
/// the leaf's own contribution to baked GI, which is unavoidable — a thing that moves cannot be
/// baked in place.
public static class UnstaticGates
{
    [MenuItem("Tools/Warehouse/Unstatic Gate Leaves")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var gates = Object.FindObjectsByType<GateAnimator>(FindObjectsInactive.Include,
                                                          FindObjectsSortMode.None);
        if (gates.Length == 0) return "no gates in the open scene — open the warehouse scene first";

        int cleared = 0;

        foreach (var gate in gates)
        {
            foreach (var t in gate.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                if (GameObjectUtility.GetStaticEditorFlags(go) == 0) continue;

                Undo.RecordObject(go, "Unstatic gate leaf");
                GameObjectUtility.SetStaticEditorFlags(go, 0);
                EditorUtility.SetDirty(go);
                cleared++;
            }
        }

        var scene = gates[0].gameObject.scene;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        int marked = gates.Count(g => g.IsZombieSpawn);

        return $"cleared static flags on {cleared} object(s) under {gates.Length} gate(s) " +
               $"({marked} of them marked for zombie spawns) and saved {scene.name}.\n" +
               "The gate leaves now render from their transform, so the open animation is visible. " +
               "They no longer contribute to baked GI — re-bake lighting if the doorways look off.";
    }
}
