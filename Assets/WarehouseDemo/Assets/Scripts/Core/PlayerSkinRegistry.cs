using System.Collections.Generic;
using UnityEngine;

/// Finds the PlayerSkinSet for a role. Same shape as PlayerSkinRegistry's neighbours
/// (PartRegistry, MeleeWeaponRegistry): load a Resources catalog once, cache it, drop the cache
/// between play sessions. Nothing in a scene has to reference the skins.
public static class PlayerSkinRegistry
{
    const string CatalogPath = "Catalog/PlayerSkins";

    static Dictionary<PlayerRoleType, PlayerSkinSet> _byRole;
    static PlayerSkinSet _solo;
    static bool _warned;

    /// The skin for a role in co-op.
    public static PlayerSkinSet For(PlayerRoleType role)
    {
        Build();
        return _byRole.TryGetValue(role, out var s) ? s : null;
    }

    /// The skin worn when there are no roles — single player.
    public static PlayerSkinSet Solo
    {
        get { Build(); return _solo; }
    }

    static void Build()
    {
        if (_byRole != null) return;
        _byRole = new Dictionary<PlayerRoleType, PlayerSkinSet>();

        foreach (var set in Resources.LoadAll<PlayerSkinSet>(CatalogPath))
        {
            if (set == null || set.Materials == null || set.Materials.Length == 0) continue;

            if (set.Solo) { _solo ??= set; continue; }

            // First asset for a role wins, and a second is named rather than left to load order
            // to decide which colour the team sees.
            if (_byRole.ContainsKey(set.Role))
            {
                Debug.LogWarning($"[PlayerSkinRegistry] Two skins for {set.Role} " +
                                 $"({_byRole[set.Role].name} and {set.name}) - keeping the first.");
                continue;
            }

            _byRole[set.Role] = set;
        }

        if (_byRole.Count == 0 && _solo == null && !_warned)
        {
            _warned = true;
            Debug.LogWarning($"[PlayerSkinRegistry] No skins under Resources/{CatalogPath} - " +
                             "players keep the model's own materials. Run " +
                             "Tools/Warehouse/Create Player Skins.");
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Reset() { _byRole = null; _solo = null; _warned = false; }
}
