using System.Collections.Generic;
using UnityEngine;

/// Shelf collapse — a random pallet in the warehouse bursts, scattering its boxes.
/// No longer ragdolls the player — just a random box-scatter hazard.
public class ShelfCollapseEvent : IGameEvent
{
    public string Name        => "ShelfCollapse";
    public string Description => "Shelf collapse: a random pallet's boxes scatter";

    const float ExplosionForce  = 16f;
    const float ExplosionRadius = 3f;

    readonly bool    _hasOrigin;
    readonly Vector3 _forcedOrigin;

    public ShelfCollapseEvent() { }

    public ShelfCollapseEvent(Vector3 center)
    {
        _hasOrigin    = true;
        _forcedOrigin = center;
    }

    public string GetNetworkPayload(MonoBehaviour host)
    {
        // Pick the target pallet up front so all clients scatter the SAME one.
        var target = PickRandomPallet();
        return target != null ? GameEventManager.FormatVec3(target.transform.position) : "";
    }

    public void Apply(MonoBehaviour host)
    {
        PalletBehavior target = _hasOrigin ? ClosestPalletTo(_forcedOrigin) : PickRandomPallet();

        if (target == null)
        {
            GameEventManager.Instance?.MarkEventDone(Name);
            return;
        }

        target.Explode(ExplosionForce, ExplosionRadius);
        CameraShake.AddTrauma(0.7f);
        RecentPalletTracker.MarkHit(target);

        HUDController.Instance?.AddNotification(
            "COLLAPSE!",
            "A shelf collapsed — boxes scattered!",
            HUDController.NotifType.Danger);

        GameEventManager.Instance?.MarkEventDone(Name);
    }

    public void Remove() { }

    // Picks any non-lifted pallet at random — prefers one not on RecentPalletTracker's
    // cooldown so this doesn't immediately re-target the same pallet PalletDrop (or a
    // previous ShelfCollapse) just hit. Falls back to any pallet if all are cooling down.
    static PalletBehavior PickRandomPallet()
    {
        var candidates = new List<PalletBehavior>();
        var fallback    = new List<PalletBehavior>();
        foreach (var p in PalletBehavior.All)
        {
            if (p == null || p.IsLifted) continue;
            fallback.Add(p);
            if (!RecentPalletTracker.IsOnCooldown(p)) candidates.Add(p);
        }

        var pool = candidates.Count > 0 ? candidates : fallback;
        return pool.Count > 0 ? pool[Random.Range(0, pool.Count)] : null;
    }

    // Used by clients replaying a server-broadcast collapse — matches the pallet closest
    // to the world position the server sent (its transform.position at pick time).
    static PalletBehavior ClosestPalletTo(Vector3 pos)
    {
        PalletBehavior best = null;
        float bestDist = float.MaxValue;
        foreach (var p in PalletBehavior.All)
        {
            if (p == null) continue;
            float d = Vector3.Distance(p.transform.position, pos);
            if (d < bestDist) { bestDist = d; best = p; }
        }
        return best;
    }
}
