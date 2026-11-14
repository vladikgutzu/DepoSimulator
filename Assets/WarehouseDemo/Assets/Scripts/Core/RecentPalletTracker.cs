using System.Collections.Generic;
using UnityEngine;

/// Tracks pallets recently hit by a chaos event (ShelfCollapse, PalletDrop) so a
/// freshly-triggered event doesn't immediately re-target the same pallet the player just
/// saw get hit — shared across event types since most boxes in the warehouse are pallet
/// cargo, making the two events' target pools overlap heavily.
public static class RecentPalletTracker
{
    const float CooldownDuration = 30f; // seconds before a hit pallet is eligible again

    static readonly Dictionary<PalletBehavior, float> _hitAt = new();

    public static void MarkHit(PalletBehavior pallet)
    {
        if (pallet == null) return;
        _hitAt[pallet] = Time.time;
    }

    public static bool IsOnCooldown(PalletBehavior pallet)
    {
        if (pallet == null) return false;
        return _hitAt.TryGetValue(pallet, out float t) && Time.time - t < CooldownDuration;
    }
}
