using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;
using System.Linq;

public class AssignTutorialBoxes
{
    [MenuItem("Tools/Warehouse Demo/Assign Tutorial Boxes")]
    public static void Execute()
    {
        var spawner = Object.FindFirstObjectByType<PartSpawner>();
        if (spawner == null) { Debug.LogError("[Assign] PartSpawner not found"); return; }

        // Count parts in first order
        var gm = Object.FindFirstObjectByType<GameManager>();
        int needed = 3;
        if (gm != null)
        {
            var f = typeof(GameManager).GetField("_availableOrders",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var orders = f?.GetValue(gm) as OrderDefinition[];
            if (orders != null && orders.Length > 0 && orders[0] != null)
            {
                needed = orders[0].RequiredParts.Sum(r => r.Count);
                Debug.Log($"[Assign] First order '{orders[0].name}' needs {needed} parts.");
            }
        }

        // Collect boxes via Renderer query — no recursive traversal needed.
        var candidates = new List<(GameObject root, Vector3 center)>();
        var renderers  = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
        var seen       = new HashSet<GameObject>();

        foreach (var r in renderers)
        {
            // Walk up to find the box root (name matches pattern)
            var t = r.transform;
            while (t != null)
            {
                string lower = t.gameObject.name.ToLower();
                if (lower.Contains("box_med_prefab") || lower.Contains("box_large_prefab"))
                {
                    var go = t.gameObject;
                    if (!seen.Add(go)) break;
                    if (go.GetComponent<BoxWithPart>() != null) break;

                    var b = r.bounds; // fast: single renderer bounds
                    if (b.max.y < 1.0f)
                        candidates.Add((go, b.center));
                    break;
                }
                t = t.parent;
            }
        }

        Debug.Log($"[Assign] Found {candidates.Count} ground-level boxes.");

        if (candidates.Count < needed)
        {
            Debug.LogError($"[Assign] Not enough boxes ({candidates.Count}) for {needed} parts.");
            return;
        }

        // Sort by Z, pick one per band.
        candidates.Sort((a, b) => a.center.z.CompareTo(b.center.z));
        float zMin = candidates[0].center.z;
        float zMax = candidates[candidates.Count - 1].center.z;
        float band  = (zMax - zMin) / needed;

        var chosen = new GameObject[needed];
        var usedGo = new HashSet<GameObject>();
        for (int i = 0; i < needed; i++)
        {
            float lo  = zMin + band * i;
            float hi  = lo + band;
            float mid = (lo + hi) * 0.5f;
            float bestD = float.MaxValue;
            GameObject best = null;
            foreach (var (go, center) in candidates)
            {
                if (usedGo.Contains(go)) continue;
                if (center.z < lo || center.z > hi) continue;
                float d = Mathf.Abs(center.z - mid);
                if (d < bestD) { bestD = d; best = go; }
            }
            if (best == null)
            {
                // fallback: nearest unused
                foreach (var (go, center) in candidates)
                {
                    if (usedGo.Contains(go)) continue;
                    float d = Mathf.Abs(center.z - mid);
                    if (d < bestD) { bestD = d; best = go; }
                }
            }
            chosen[i] = best;
            usedGo.Add(best);
            Debug.Log($"[Assign] Box {i}: '{best.name}' at {best.transform.position}");
        }

        var so   = new SerializedObject(spawner);
        var prop = so.FindProperty("_tutorialBoxes");
        prop.arraySize = needed;
        for (int i = 0; i < needed; i++)
            prop.GetArrayElementAtIndex(i).objectReferenceValue = chosen[i];
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(spawner);
        EditorSceneManager.MarkSceneDirty(spawner.gameObject.scene);
        Debug.Log($"[Assign] Done. Press Ctrl+S to save.");
    }
}
