using UnityEngine;
using UnityEditor;

// Tools > Warehouse Demo > Setup Pallet Behaviors
//
// Adds PalletBehavior to both pallet prefab assets so every scene instance
// automatically gets the component.  Safe to re-run (skips if already present).
public static class SetupPalletBehaviors
{
    static readonly string[] s_paths =
    {
        "Assets/Prefabs/env/pallet_boxes_prefab.prefab",
        "Assets/Prefabs/env/pallet_box_pack_prefab.prefab",
    };

    // Also patch existing scene instances whose root lacks the component.
    static readonly string[] s_rootNames =
    {
        "pallet_boxes_prefab",
        "pallet_box_pack_prefab",
    };

    [MenuItem("Tools/Warehouse Demo/Setup Pallet Behaviors")]
    public static void Execute()
    {
        int prefabCount = PatchPrefabs();
        int sceneCount  = PatchSceneInstances();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[PalletBehavior] Prefabs patched: {prefabCount}  " +
                  $"Scene instances patched: {sceneCount}");
        EditorUtility.DisplayDialog("Pallet Behaviors",
            $"Готово!\n\nПрефабов обновлено: {prefabCount}\n" +
            $"Экземпляров в сцене обновлено: {sceneCount}\n\n" +
            "Все паллеты теперь имеют PalletBehavior.\n" +
            "Сохраните сцену (Ctrl+S).", "OK");
    }

    static int PatchPrefabs()
    {
        int count = 0;
        foreach (var path in s_paths)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
            {
                Debug.LogWarning($"[PalletBehavior] Prefab not found: {path}");
                continue;
            }
            try
            {
                var existing = root.GetComponents<PalletBehavior>();
                for (int i = 1; i < existing.Length; i++)
                    Object.DestroyImmediate(existing[i]);
                if (existing.Length == 0)
                    root.AddComponent<PalletBehavior>();

                AssignGrabCollider(root);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                count++;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
        return count;
    }

    static readonly string[] s_palletChildNames = { "pallet_prefab", "pallet" };

    static void AssignGrabCollider(GameObject root)
    {
        var pb = root.GetComponent<PalletBehavior>();
        if (pb == null) return;

        var so   = new SerializedObject(pb);
        var prop = so.FindProperty("_grabCollider");

        // Prefer the pallet-board child: its min.y sits at the actual fork-entry
        // height (floor or shelf level), while the root BoxCollider starts 0.32m
        // above the root — causing the Y check to fail when grabbing from shelves.
        foreach (var n in s_palletChildNames)
        {
            var child = FindDeep(root.transform, n);
            if (child == null) continue;

            // Always use a trigger on the pallet child — consistent across all
            // prefabs regardless of what colliders were already there.
            // Remove any existing BoxColliders (solid or trigger) and add one trigger.
            foreach (var c in child.GetComponents<BoxCollider>())
                Object.DestroyImmediate(c);

            Collider col = AddPalletBoardTrigger(child.gameObject);

            if (col != null)
            {
                prop.objectReferenceValue = col;
                so.ApplyModifiedProperties();
                return;
            }
        }

        // Fallback — root collider when no named pallet child exists.
        foreach (var col in root.GetComponents<Collider>())
        {
            if (!col.isTrigger) { prop.objectReferenceValue = col; so.ApplyModifiedProperties(); return; }
        }
    }

    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var result = FindDeep(t.GetChild(i), name);
            if (result != null) return result;
        }
        return null;
    }

    // Adds a trigger-only BoxCollider sized to the pallet board mesh.
    // Trigger = no physics contact; used only for bounds in ScanForPallet.
    static BoxCollider AddPalletBoardTrigger(GameObject palletChild)
    {
        var rends = palletChild.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return null;

        Bounds b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);

        var bc       = palletChild.AddComponent<BoxCollider>();
        bc.isTrigger = true;
        bc.center    = palletChild.transform.InverseTransformPoint(b.center);
        bc.size      = b.size;
        return bc;
    }

    static int PatchSceneInstances()
    {
        int count = 0;
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            bool nameMatch = false;
            foreach (var n in s_rootNames)
                if (go.name == n || go.name.StartsWith(n + " (")) { nameMatch = true; break; }

            if (!nameMatch) continue;

            var comps = go.GetComponents<PalletBehavior>();
            for (int i = 1; i < comps.Length; i++)
                Undo.DestroyObjectImmediate(comps[i]);
            if (comps.Length == 0)
                Undo.AddComponent<PalletBehavior>(go);

            // Force-update GrabCollider on every instance — scene instances keep
            // old overrides even after the prefab is patched, so we reset explicitly.
            AssignGrabCollider(go);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            count++;
        }
        return count;
    }
}
