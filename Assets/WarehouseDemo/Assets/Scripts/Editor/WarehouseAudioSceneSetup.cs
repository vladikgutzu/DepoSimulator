#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class WarehouseAudioSceneSetup
{
    const string ScenePath = "Assets/urp_scene_with_training 1.unity";

    [MenuItem("Tools/Warehouse Demo/Audio/Audit Conveyor Candidates")]
    public static void AuditConveyorCandidates()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        foreach (GameObject root in scene.GetRootGameObjects())
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            string key = ((filter.name ?? "") + " " +
                          (filter.sharedMesh != null ? filter.sharedMesh.name : "")).ToLowerInvariant();
            if (!key.Contains("conveyor") && !key.Contains("roller") && !key.Contains("belt"))
                continue;
            Debug.Log($"[WarehouseAudioCandidate] {HierarchyPath(filter.transform)} | " +
                      $"mesh={filter.sharedMesh?.name} | pos={filter.transform.position}");
        }
    }

    static string HierarchyPath(Transform transform)
    {
        string result = transform.name;
        while (transform.parent != null)
        {
            transform = transform.parent;
            result = transform.name + "/" + result;
        }
        return result;
    }
}
#endif
