using UnityEngine;
using System.Reflection;

public class DiagScene
{
    public static void Execute()
    {
        // Try finding by name
        var goByName = GameObject.Find("AssemblyBench");
        Debug.Log($"[Diag] GameObject.Find('AssemblyBench') = {(goByName != null ? goByName.name + " active=" + goByName.activeInHierarchy : "NULL")}");

        // Try finding component via GetComponentInChildren on GameplayRoot
        var root = GameObject.Find("GameplayRoot");
        Debug.Log($"[Diag] GameplayRoot = {(root != null ? "found, active=" + root.activeInHierarchy : "NULL")}");
        if (root != null)
        {
            var benchFromRoot = root.GetComponentInChildren<AssemblyBench>(true);
            Debug.Log($"[Diag] AssemblyBench from GetComponentInChildren = {(benchFromRoot != null ? benchFromRoot.gameObject.name : "NULL")}");
        }

        // Try all MonoBehaviours
        var allMono = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int benchCount = 0;
        foreach (var m in allMono)
        {
            if (m.GetType().Name == "AssemblyBench")
            {
                benchCount++;
                Debug.Log($"[Diag] Found AssemblyBench via type name on '{m.gameObject.name}', selfActive={m.gameObject.activeSelf}, hierarchyActive={m.gameObject.activeInHierarchy}");
            }
        }
        if (benchCount == 0)
            Debug.LogWarning("[Diag] No AssemblyBench found in any MonoBehaviour scan");

        // Check IsPlayingOrWillChangePlaymode
        Debug.Log($"[Diag] EditorApplication.isPlaying={UnityEditor.EditorApplication.isPlaying}, isPlayingOrWillChange={UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode}");
    }
}
