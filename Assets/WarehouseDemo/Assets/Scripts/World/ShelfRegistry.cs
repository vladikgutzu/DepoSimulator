using System.Collections.Generic;
using UnityEngine;

/// Single source of truth for shelf identification numbers (1..N), shared by
/// MinimapController (minimap zone labels) and ShelfStickerNumberer (in-world
/// sticker decals) so both always agree on which shelf is which number.
public static class ShelfRegistry
{
    // Name substrings that identify shelf-row container objects.
    static readonly string[] Patterns = {
        "shelflarge_grp_row", "shelflarge_grp_0", "shelflarge_grp_right",
        "shelflarge_grp_03",  "shelf_forklift_grp"
    };

    public readonly struct ShelfEntry
    {
        public readonly GameObject GO;
        public readonly Bounds     Bounds;
        public readonly int        Number;

        public ShelfEntry(GameObject go, Bounds bounds, int number)
        {
            GO = go; Bounds = bounds; Number = number;
        }
    }

    /// Finds all shelf-row objects and numbers them in a stable, spatially
    /// intuitive order (left-to-right, then front-to-back).
    public static List<ShelfEntry> GetOrderedShelves()
    {
        var matches = new List<(GameObject go, Bounds bounds)>();

        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            string lower = go.name.ToLower();
            bool match = false;
            foreach (var p in Patterns) if (lower.Contains(p)) { match = true; break; }
            if (!match) continue;

            var rends = go.GetComponentsInChildren<Renderer>(false);
            if (rends.Length == 0) continue;

            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            matches.Add((go, b));
        }

        matches.Sort((a, b) =>
        {
            int cmp = a.bounds.center.x.CompareTo(b.bounds.center.x);
            return cmp != 0 ? cmp : a.bounds.center.z.CompareTo(b.bounds.center.z);
        });

        var result = new List<ShelfEntry>(matches.Count);
        for (int i = 0; i < matches.Count; i++)
            result.Add(new ShelfEntry(matches[i].go, matches[i].bounds, i + 1));
        return result;
    }
}
