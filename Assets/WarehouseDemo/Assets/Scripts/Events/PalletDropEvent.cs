using System.Collections;
using UnityEngine;

/// Pallet explosion — boxes fly in every direction, nearby players get ragdolled.
public class PalletDropEvent : IGameEvent
{
    public string Name        => "PalletDrop";
    public string Description => "Pallet explosion: boxes fly everywhere, stay clear!";

    const float ExplosionForce  = 26f;
    const float ExplosionRadius = 5f;
    const float RagdollRadius   = 3.5f;
    const float RagdollImpulse  = 24f;
    const float WarnDelay       = 1.0f;

    readonly bool    _hasOrigin;
    readonly Vector3 _forcedOrigin;

    public PalletDropEvent() { }

    public PalletDropEvent(Vector3 origin)
    {
        _hasOrigin     = true;
        _forcedOrigin  = origin;
    }

    public string GetNetworkPayload(MonoBehaviour host)
    {
        // Pick the pallet closest to the (server's) local player so the blast actually
        // happens somewhere visible — picking an arbitrary one could land anywhere across
        // the whole warehouse. Pass its position so all clients aim at the same one.
        var target = ClosestUnliftedPallet();
        return target != null ? GameEventManager.FormatVec3(target.transform.position) : "";
    }

    public void Apply(MonoBehaviour host)
    {
        PalletBehavior target;

        if (_hasOrigin)
        {
            // Find pallet closest to the server-chosen origin.
            target = null;
            float bestDist = float.MaxValue;
            foreach (var p in PalletBehavior.All)
            {
                float d = Vector3.Distance(p.transform.position, _forcedOrigin);
                if (!p.IsLifted && d < bestDist) { bestDist = d; target = p; }
            }
        }
        else
        {
            target = ClosestUnliftedPallet();
        }

        if (target == null)
        {
            GameEventManager.Instance?.MarkEventDone(Name);
            return;
        }

        host.StartCoroutine(BlastSequence(target));
    }

    public void Remove() { }

    // Prefers a pallet not on RecentPalletTracker's cooldown, so this doesn't immediately
    // re-target one that ShelfCollapse (or a previous PalletDrop) just hit. Falls back to
    // the closest one regardless of cooldown if every nearby pallet is cooling down.
    static PalletBehavior ClosestUnliftedPallet()
    {
        var playerTf = PlayerFinder.Find();

        PalletBehavior best = null, bestAny = null;
        float bestDist = float.MaxValue, bestAnyDist = float.MaxValue;

        foreach (var p in PalletBehavior.All)
        {
            if (p.IsLifted) continue;
            float d = playerTf != null ? Vector3.Distance(p.transform.position, playerTf.position) : 0f;

            if (d < bestAnyDist) { bestAnyDist = d; bestAny = p; }

            if (RecentPalletTracker.IsOnCooldown(p)) continue;
            if (d < bestDist) { bestDist = d; best = p; }
        }

        return best ?? bestAny;
    }

    IEnumerator BlastSequence(PalletBehavior pallet)
    {
        Vector3 blastOrigin = pallet.transform.position;

        HUDController.Instance?.AddNotification(
            "DANGER!",
            "The pallet is about to burst — run!",
            HUDController.NotifType.Danger);

        yield return new WaitForSeconds(WarnDelay);

        if (pallet == null) { GameEventManager.Instance?.MarkEventDone(Name); yield break; }

        pallet.Explode(ExplosionForce, ExplosionRadius);
        CameraShake.AddTrauma(1f);
        RecentPalletTracker.MarkHit(pallet);

        var ragdolls = Object.FindObjectsByType<RagdollController>(FindObjectsSortMode.None);
        foreach (var ragdoll in ragdolls)
        {
            if (ragdoll.IsRagdoll) continue;
            float dist = Vector3.Distance(ragdoll.transform.position, blastOrigin);
            if (dist > RagdollRadius) continue;

            Vector3 dir = (ragdoll.transform.position - blastOrigin).normalized;
            dir.y = 0.4f;
            ragdoll.TriggerRagdoll(dir.normalized * RagdollImpulse, blastOrigin);
        }

        HUDController.Instance?.AddNotification(
            "Pallet exploded!",
            "Boxes scattered across the warehouse",
            HUDController.NotifType.Warning);

        GameEventManager.Instance?.MarkEventDone(Name);
    }
}
