using UnityEditor;
using UnityEngine;

public class AssignGhostPrefabs
{
    [MenuItem("Tools/Warehouse Demo/Assign Ghost Prefabs")]
    public static void Execute()
    {
        var guids   = AssetDatabase.FindAssets("t:PartDefinition");
        int changed = 0;

        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var part    = AssetDatabase.LoadAssetAtPath<PartDefinition>(path);
            if (part == null) continue;

            var so        = new SerializedObject(part);
            var ghostProp = so.FindProperty("GhostPrefab");
            if (ghostProp == null) continue;

            // Try direct: ghost_{partID}
            string ghostPath = $"Assets/Prefabs/GhostParts/ghost_{part.PartID}.prefab";
            var ghost = AssetDatabase.LoadAssetAtPath<GameObject>(ghostPath);

            // Fallback: strip _left / _right suffix (gliders_left / gliders_right → ghost_gliders)
            if (ghost == null)
            {
                string baseID = part.PartID
                    .Replace("_left",  "")
                    .Replace("_right", "");
                ghostPath = $"Assets/Prefabs/GhostParts/ghost_{baseID}.prefab";
                ghost = AssetDatabase.LoadAssetAtPath<GameObject>(ghostPath);
            }

            if (ghost != null && ghostProp.objectReferenceValue != ghost)
            {
                ghostProp.objectReferenceValue = ghost;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(part);
                changed++;
                Debug.Log($"[AssignGhostPrefabs] {part.name} → {ghost.name}");
            }
            else if (ghost == null)
            {
                Debug.Log($"[AssignGhostPrefabs] No ghost found for: {part.PartID} — GhostPrefab left null");
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[AssignGhostPrefabs] Done. {changed} part(s) updated.");
    }
}
