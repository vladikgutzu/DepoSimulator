using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class CleanBoxContainers
{
    // Pass 1: remove BoxPhysicsInteractable from containers (objects with child interactables)
    // Pass 2: remove BoxPhysicsInteractable from objects without "BoxInteract" tag
    [MenuItem("Tools/Warehouse Demo/Clean Box Containers")]
    public static void Execute()
    {
        var all = Object.FindObjectsByType<BoxPhysicsInteractable>(FindObjectsInactive.Include);
        int removedContainers = 0, removedUntagged = 0;

        foreach (var bpi in all)
        {
            if (bpi == null) continue;
            var go = bpi.gameObject;

            // Rule 1: containers (have child BoxPhysicsInteractable) → remove
            bool isContainer = false;
            foreach (var child in bpi.GetComponentsInChildren<BoxPhysicsInteractable>(true))
            {
                if (child == bpi) continue;
                isContainer = true;
                break;
            }

            // Rule 2: not tagged BoxInteract → remove (decals, stickers, sub-meshes)
            bool isUntagged = !go.CompareTag("BoxInteract");

            if (!isContainer && !isUntagged) continue;

            foreach (var bc in go.GetComponents<BoxCollider>())
                Object.DestroyImmediate(bc);

            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) Object.DestroyImmediate(rb);

            Object.DestroyImmediate(bpi);
            EditorUtility.SetDirty(go);

            if (isContainer) removedContainers++;
            else             removedUntagged++;
        }

        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log($"[CleanBoxes] Removed: {removedContainers} containers, {removedUntagged} untagged objects.");
    }
}
