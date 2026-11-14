using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class RevertPhysicsLayers
{
    public static void Execute()
    {
        int forkliftLayer = LayerMask.NameToLayer("Forklift");
        int palletLayer   = LayerMask.NameToLayer("Pallet");

        // Re-enable collision between these layers so nothing is broken
        if (forkliftLayer >= 0 && palletLayer >= 0)
            Physics.IgnoreLayerCollision(forkliftLayer, palletLayer, false);

        int reverted = 0;

        // Restore ALL scene objects that ended up on Forklift or Pallet layer → Default (0)
        foreach (var go in Object.FindObjectsByType<GameObject>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (go.layer == forkliftLayer || go.layer == palletLayer)
            {
                go.layer = 0; // Default
                EditorUtility.SetDirty(go);
                reverted++;
            }
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"[RevertLayers] Restored {reverted} objects to Default layer. Save scene!");
    }
}
