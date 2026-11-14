using System.Collections.Generic;
using UnityEngine;

/// The one place a role's colour is decided.
///
/// It used to be decided in three: a RoleColor field on the PlayerRole asset, a dictionary in
/// CoopHUDExtension and another in MinimapController. Three copies of the same idea is three
/// chances for the dot on the minimap, the name in the HUD and the body in the world to disagree
/// about who the green one is — which is exactly the thing the colour exists to prevent.
public static class PlayerRoleColors
{
    static readonly Dictionary<PlayerRoleType, Color> Colors = new()
    {
        { PlayerRoleType.Picker,           new Color(0.30f, 0.85f, 0.40f) }, // green
        { PlayerRoleType.Assembler,        new Color(0.30f, 0.60f, 0.95f) }, // blue
        { PlayerRoleType.ForkLiftOperator, new Color(0.92f, 0.27f, 0.24f) }, // red - matches the
                                                                             // body's red livery
        { PlayerRoleType.Dispatcher,       new Color(0.85f, 0.45f, 0.95f) }, // violet
        { PlayerRoleType.None,             new Color(0.70f, 0.73f, 0.78f) }, // grey - no role yet
    };

    /// What a player on this role is coloured, everywhere it shows: the body, the minimap dot,
    /// the name in the co-op HUD.
    public static Color For(PlayerRoleType role) =>
        Colors.TryGetValue(role, out var c) ? c : Colors[PlayerRoleType.None];

    /// Worn in single player, where there are no roles to tell apart. Deliberately not one of
    /// the role colours: seeing it should never make a returning co-op player think they have
    /// been assigned something.
    public static readonly Color Solo = new(0.82f, 0.84f, 0.88f);   // pale steel
}
