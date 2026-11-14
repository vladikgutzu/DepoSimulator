using UnityEngine;

/// Finds the local player's Transform — shared by systems (minimap, chaos events) that
/// need "where is the player" without caring which specific controller type is active.
public static class PlayerFinder
{
    public static Transform Find()
    {
        var tpc = Object.FindFirstObjectByType<StarterAssets.ThirdPersonController>();
        if (tpc != null) return tpc.transform;
        var fpc = Object.FindFirstObjectByType<FirstPersonController>();
        if (fpc != null) return fpc.transform;
        var go = GameObject.FindWithTag("Player");
        return go != null ? go.transform : null;
    }
}
