using UnityEngine;
using System.Collections.Generic;

public class FindTutorialBoxes
{
    public static void Execute()
    {
        var spawner = Object.FindAnyObjectByType<PartSpawner>(FindObjectsInactive.Include);
        if (spawner == null) { Debug.LogError("[FindTutorialBoxes] PartSpawner not found"); return; }
        Debug.Log($"[FindTutorialBoxes] PartSpawner on: '{spawner.gameObject.name}'");

        // Search via roots only — avoid scanning every GO in a huge scene
        var roots = UnityEngine.SceneManagement.SceneManager
                        .GetActiveScene().GetRootGameObjects();

        var candidates = new List<(GameObject go, Vector3 center, float maxY)>();

        foreach (var root in roots)
            ScanChildren(root.transform, candidates);

        candidates.Sort((a, b) => a.maxY.CompareTo(b.maxY));

        Debug.Log($"[FindTutorialBoxes] Box candidates (ground-level first): {candidates.Count}");
        int shown = 0;
        foreach (var (go, center, maxY) in candidates)
        {
            if (shown++ >= 15) break;
            string path = GetPath(go.transform);
            Debug.Log($"[FindTutorialBoxes] maxY={maxY:F2}  pos=({center.x:F1},{center.y:F1},{center.z:F1})  id={go.GetInstanceID()}  {path}");
        }
    }

    static void ScanChildren(Transform t, List<(GameObject, Vector3, float)> result)
    {
        string lower = t.gameObject.name.ToLower();
        if (lower.Contains("box_med_prefab") || lower.Contains("box_large_prefab"))
        {
            if (t.GetComponent<BoxWithPart>() == null)
            {
                var rends = t.GetComponentsInChildren<Renderer>(false);
                if (rends.Length > 0)
                {
                    var b = rends[0].bounds;
                    for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                    result.Add((t.gameObject, b.center, b.max.y));
                    return; // don't recurse into matched prefab
                }
            }
        }
        for (int i = 0; i < t.childCount; i++)
            ScanChildren(t.GetChild(i), result);
    }

    static string GetPath(Transform t)
    {
        string p = t.name;
        var par = t.parent;
        int depth = 0;
        while (par != null && depth++ < 4) { p = par.name + "/" + p; par = par.parent; }
        return p;
    }
}
