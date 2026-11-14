using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;

public static class RunSetupPallets
{
    static readonly string[] s_paths =
    {
        "Assets/Prefabs/env/pallet_boxes_prefab.prefab",
        "Assets/Prefabs/env/pallet_box_pack_prefab.prefab",
    };

    static readonly string[] s_rootNames =
    {
        "pallet_boxes_prefab",
        "pallet_box_pack_prefab",
    };

    static readonly string[] s_palletChildNames = { "pallet_prefab", "pallet" };

    public static void Execute()
    {
        int prefabCount = PatchPrefabs();
        int sceneCount  = PatchSceneInstances();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[SetupPallets] Done. Prefabs: {prefabCount}, Scene instances: {sceneCount}");
    }

    static int PatchPrefabs()
    {
        int count = 0;
        foreach (var path in s_paths)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            if (root == null) { Debug.LogWarning($"Not found: {path}"); continue; }
            try
            {
                var existing = root.GetComponents<PalletBehavior>();
                for (int i = 1; i < existing.Length; i++) Object.DestroyImmediate(existing[i]);
                if (existing.Length == 0) root.AddComponent<PalletBehavior>();
                AssignGrabCollider(root);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                count++;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        return count;
    }

    static int PatchSceneInstances()
    {
        int count = 0;
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            bool match = false;
            foreach (var n in s_rootNames)
                if (go.name == n || go.name.StartsWith(n + " (")) { match = true; break; }
            if (!match) continue;

            var comps = go.GetComponents<PalletBehavior>();
            for (int i = 1; i < comps.Length; i++) Undo.DestroyObjectImmediate(comps[i]);
            if (comps.Length == 0) Undo.AddComponent<PalletBehavior>(go);

            AssignGrabCollider(go);
            EditorSceneManager.MarkSceneDirty(go.scene);
            count++;
        }
        return count;
    }

    static void AssignGrabCollider(GameObject root)
    {
        var pb = root.GetComponent<PalletBehavior>();
        if (pb == null) return;
        var so   = new SerializedObject(pb);
        var prop = so.FindProperty("_grabCollider");

        foreach (var n in s_palletChildNames)
        {
            var child = FindDeep(root.transform, n);
            if (child == null) continue;

            foreach (var c in child.GetComponents<BoxCollider>())
                Object.DestroyImmediate(c);

            var bc       = child.gameObject.AddComponent<BoxCollider>();
            bc.isTrigger = true;
            var rends    = child.GetComponentsInChildren<Renderer>(true);
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                bc.center = child.transform.InverseTransformPoint(b.center);
                bc.size   = b.size;
            }

            prop.objectReferenceValue = bc;
            so.ApplyModifiedProperties();
            return;
        }

        foreach (var col in root.GetComponents<Collider>())
            if (!col.isTrigger) { prop.objectReferenceValue = col; so.ApplyModifiedProperties(); return; }
    }

    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var r = FindDeep(t.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }
}
