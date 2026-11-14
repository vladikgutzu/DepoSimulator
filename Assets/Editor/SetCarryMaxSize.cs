using UnityEngine;
using UnityEditor;

public class SetCarryMaxSize
{
    public static void Execute()
    {
        int changed = 0;

        // Update only prefabs with "box" in their name
        var guids = AssetDatabase.FindAssets("box t:Prefab");
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var go   = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;

            var comps = go.GetComponentsInChildren<BoxPhysicsInteractable>(true);
            if (comps.Length == 0) continue;

            bool dirty = false;
            foreach (var comp in comps)
            {
                var so   = new SerializedObject(comp);
                var prop = so.FindProperty("_carryMaxSize");
                if (prop == null) continue;
                if (Mathf.Approximately(prop.floatValue, 0.55f)) continue;

                prop.floatValue = 0.55f;
                so.ApplyModifiedPropertiesWithoutUndo();
                dirty = true;
                changed++;
                Debug.Log($"[SetCarryMaxSize] {path} → _carryMaxSize = 0.55");
            }

            if (dirty)
                EditorUtility.SetDirty(go);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[SetCarryMaxSize] Done. Updated {changed} component(s).");
    }
}
