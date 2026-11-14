using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// Keeps URP's additional-light shadow atlas within budget in the warehouse scene.
///
/// The ceiling prefabs are repeated throughout the building. A shadow-casting point light costs
/// six atlas faces, so enabling one on every repeated ceiling section still overflows an 8192px
/// atlas. Illumination remains enabled everywhere; only the two ceiling sections nearest the
/// player retain realtime shadows. The three deliberately shadowed extraBakedLights are left
/// untouched.
public static class WarehouseShadowBudget
{
    const string WarehouseScene = "urp_scene_with_training 1";
    const int CeilingSectionsWithShadows = 2;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Apply()
    {
        if (SceneManager.GetActiveScene().name != WarehouseScene) return;

        var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
        var ceilingRoots = new HashSet<Transform>();

        foreach (var light in lights)
        {
            if (light == null || light.shadows == LightShadows.None) continue;
            var root = FindNamedAncestor(light.transform, "ceiling_GRP_type_01_prefab",
                "ceiling_GRP_type_02_prefab");
            if (root != null) ceilingRoots.Add(root);
        }

        Transform anchor = null;
        try { anchor = GameObject.FindGameObjectWithTag("Player")?.transform; }
        catch (UnityException) { }
        Vector3 origin = anchor != null ? anchor.position : Vector3.zero;

        var ordered = new List<Transform>(ceilingRoots);
        ordered.Sort((a, b) =>
        {
            int distance = (a.position - origin).sqrMagnitude.CompareTo(
                (b.position - origin).sqrMagnitude);
            return distance != 0 ? distance : a.GetInstanceID().CompareTo(b.GetInstanceID());
        });

        var allowed = new HashSet<Transform>();
        for (int i = 0; i < Mathf.Min(CeilingSectionsWithShadows, ordered.Count); i++)
            allowed.Add(ordered[i]);

        int disabled = 0;
        foreach (var light in lights)
        {
            if (light == null || light.shadows == LightShadows.None) continue;
            var root = FindNamedAncestor(light.transform, "ceiling_GRP_type_01_prefab",
                "ceiling_GRP_type_02_prefab");
            if (root == null || allowed.Contains(root)) continue;

            light.shadows = LightShadows.None;
            disabled++;
        }

        Debug.Log($"[ShadowBudget] Realtime ceiling shadows kept on {allowed.Count} sections; " +
                  $"disabled on {disabled} repeated lights.");
    }

    static Transform FindNamedAncestor(Transform current, string firstPrefix, string secondPrefix)
    {
        while (current != null)
        {
            if (current.name.StartsWith(firstPrefix, System.StringComparison.Ordinal)
                || current.name.StartsWith(secondPrefix, System.StringComparison.Ordinal))
                return current;
            current = current.parent;
        }
        return null;
    }
}
