using System.Collections.Generic;
using UnityEngine;

/// Looks up MeleeWeaponDefinition assets by kind. Same shape as PartRegistry: load everything
/// under a Resources catalog folder once, cache it, and drop the cache between play sessions.
///
/// Nothing in a scene has to point at these, which is the point — a weapon is defined entirely
/// by its asset, and adding one means adding an enum value and an asset rather than editing
/// four switch statements.
public static class MeleeWeaponRegistry
{
    const string CatalogPath = "Catalog/Weapons";

    static Dictionary<MeleeWeaponKind, MeleeWeaponDefinition> _cache;
    static bool _warned;

    public static MeleeWeaponDefinition Get(MeleeWeaponKind kind)
    {
        BuildCacheIfNeeded();
        return _cache.TryGetValue(kind, out var def) ? def : null;
    }

    static void BuildCacheIfNeeded()
    {
        if (_cache != null) return;
        _cache = new Dictionary<MeleeWeaponKind, MeleeWeaponDefinition>();

        foreach (var def in Resources.LoadAll<MeleeWeaponDefinition>(CatalogPath))
        {
            if (def == null) continue;

            // First asset for a kind wins; a second is a authoring mistake worth naming rather
            // than silently letting load order decide which numbers the game runs on.
            if (_cache.ContainsKey(def.Kind))
            {
                Debug.LogWarning($"[MeleeWeaponRegistry] Two definitions for {def.Kind} " +
                                 $"({_cache[def.Kind].name} and {def.name}) — keeping the first.");
                continue;
            }

            _cache[def.Kind] = def;
        }

        if (_cache.Count == 0 && !_warned)
        {
            _warned = true;
            Debug.LogWarning($"[MeleeWeaponRegistry] No weapon definitions under " +
                             $"Resources/{CatalogPath} — every weapon will fall back to bare-hand " +
                             $"numbers. Run Tools/Warehouse/Create Melee Weapon Assets.");
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetCache() { _cache = null; _warned = false; }
}
