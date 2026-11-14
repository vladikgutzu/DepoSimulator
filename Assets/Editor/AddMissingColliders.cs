using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// Tools > Warehouse Demo > Add Missing Colliders
///
/// Two-pass tool:
///   1. Scene pass  — finds every prefab-instance root (or plain hierarchy root) that
///      has MeshRenderer children but no Collider anywhere in its hierarchy, and adds
///      a single BoxCollider sized to the combined renderer bounds.
///   2. Prefab pass — same logic applied directly to known env-prefab assets on disk
///      so newly placed instances already come with colliders.
public static class AddMissingColliders
{
    static readonly string[] EnvPrefabPaths =
    {
        "Assets/Prefabs/env/shelfLarge_prefab.prefab",
        "Assets/Prefabs/env/shelfLarge_GRP_01_prefab.prefab",
        "Assets/Prefabs/env/shelfLarge_GRP_02_prefab.prefab",
        "Assets/Prefabs/env/shelf_forklift_prefab.prefab",
        "Assets/Prefabs/env/shelf_forklift_GRP_01_prefab.prefab",
        "Assets/Prefabs/env/pallet_prefab.prefab",
        "Assets/Prefabs/env/pallet_boxes_prefab.prefab",
        "Assets/Prefabs/env/pallet_box_pack_prefab.prefab",
        "Assets/Prefabs/env/barrel_prefab.prefab",
        "Assets/Prefabs/env/plastic_container_prefab.prefab",
        "Assets/Prefabs/env/large_container_prefab.prefab",
        "Assets/Prefabs/env/barrier_prefab.prefab",
        "Assets/Prefabs/env/steelBarrier_prefab.prefab",
        "Assets/Prefabs/env/cornerFence_prefab.prefab",
        "Assets/Prefabs/env/workbench.prefab",
        "Assets/Prefabs/env/truck_trailer_prefab.prefab",
        "Assets/Prefabs/env/box_med_prefab.prefab",
        "Assets/Prefabs/env/box_large_prefab.prefab",
        "Assets/Prefabs/env/box_long_flat_prefab.prefab",
        "Assets/Prefabs/env/box_wide_flat_prefab.prefab",
    };

    [MenuItem("Tools/Warehouse Demo/Add Missing Colliders (Scene + Prefabs)")]
    static void Execute()
    {
        int sceneAdded  = ProcessScene();
        int prefabAdded = ProcessPrefabs();

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

        string msg = $"Сцена: +{sceneAdded} BoxCollider(s)\nПрефабы: +{prefabAdded}\n\nСохраните сцену (Ctrl+S).";
        Debug.Log($"[AddMissingColliders] Done. {msg}");
        EditorUtility.DisplayDialog("Коллайдеры добавлены", msg, "OK");
    }

    // ── Scene pass ────────────────────────────────────────────────────────────

    static int ProcessScene()
    {
        int added = 0;
        var visited = new HashSet<int>(); // instance IDs of already-processed units

        // FindObjectsByType returns ALL active + inactive MeshRenderers in loaded scenes.
        var renderers = Object.FindObjectsByType<MeshRenderer>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (var mr in renderers)
        {
            // Determine the "unit root": prefab instance root if applicable, else hierarchy root.
            var unit = PrefabUtility.GetNearestPrefabInstanceRoot(mr.gameObject)
                       ?? HierarchyRoot(mr.gameObject);

            if (!visited.Add(unit.GetInstanceID())) continue;

            if (ShouldSkip(unit))       continue;
            if (HasCollider(unit))      continue;

            if (FitBoxCollider(unit))
            {
                EditorUtility.SetDirty(unit);
                added++;
                Debug.Log($"[AddMissingColliders] Scene: +BoxCollider → {unit.name}");
            }
        }

        return added;
    }

    // ── Prefab pass ───────────────────────────────────────────────────────────

    static int ProcessPrefabs()
    {
        int added = 0;

        foreach (var path in EnvPrefabPaths)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) continue;

            using var scope = new PrefabUtility.EditPrefabContentsScope(path);
            var root = scope.prefabContentsRoot;

            if (HasCollider(root)) continue;

            if (FitBoxCollider(root))
            {
                added++;
                Debug.Log($"[AddMissingColliders] Prefab: +BoxCollider → {path}");
            }
        }

        return added;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static bool ShouldSkip(GameObject go) =>
        // Never touch the player or controllable vehicles — they manage their own colliders.
        go.GetComponentInChildren<FirstPersonController>(true)  != null ||
        go.GetComponentInChildren<ReachTruckController>(true)   != null ||
        go.GetComponentInChildren<CartController>(true)         != null ||
        go.GetComponentInChildren<ScissorLift>(true)            != null;

    static bool HasCollider(GameObject go) =>
        go.GetComponentInChildren<Collider>(true) != null;

    // Calculates the combined world bounds of all MeshRenderers in the hierarchy,
    // converts to root-local space, and adds a BoxCollider.
    static bool FitBoxCollider(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length == 0) return false;

        var bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);

        var box    = go.AddComponent<BoxCollider>();
        box.center = go.transform.InverseTransformPoint(bounds.center);
        box.size   = Vector3.Scale(bounds.size, InvScale(go.transform.lossyScale));
        return true;
    }

    static GameObject HierarchyRoot(GameObject go)
    {
        var t = go.transform;
        while (t.parent != null) t = t.parent;
        return t.gameObject;
    }

    static Vector3 InvScale(Vector3 s) =>
        new(s.x != 0f ? 1f / s.x : 1f,
            s.y != 0f ? 1f / s.y : 1f,
            s.z != 0f ? 1f / s.z : 1f);
}
