using System.Collections.Generic;
using UnityEngine;

/// Lookup table for PartDefinition assets by PartID.
/// Used by networking code that sends PartID strings over the wire.
public static class PartRegistry
{
    static Dictionary<string, PartDefinition> _cache;

    public static PartDefinition Get(string partId)
    {
        BuildCacheIfNeeded();
        return _cache.TryGetValue(partId, out var p) ? p : null;
    }

    public static string GetName(string partId)
    {
        var def = Get(partId);
        return def != null ? def.PartName : partId;
    }

    static void BuildCacheIfNeeded()
    {
        if (_cache != null) return;
        _cache = new Dictionary<string, PartDefinition>();

        // Every PartDefinition under Resources, not just Catalog/Parts. The old lookup read
        // MotoParts only when Catalog/Parts was empty — and it never is — so all fourteen parts of
        // the full AGV order were unknown by id. Anything that crosses the network as an id then
        // failed silently: the server dropped "install claw at the bench" (the carried box was
        // already destroyed, the count stayed 0/1), and clients never tagged those parts' boxes,
        // so they neither glowed for the Picker nor installed.
        foreach (var p in Resources.LoadAll<PartDefinition>(""))
        {
            if (p == null || string.IsNullOrEmpty(p.PartID)) continue;
            if (_cache.TryGetValue(p.PartID, out var existing) && existing != p)
            {
                Debug.LogWarning($"[PartRegistry] PartID '{p.PartID}' is used by both '{existing.name}' and " +
                                 $"'{p.name}' — keeping the first.");
                continue;
            }
            _cache[p.PartID] = p;
        }
    }

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetCache() => _cache = null;
}
