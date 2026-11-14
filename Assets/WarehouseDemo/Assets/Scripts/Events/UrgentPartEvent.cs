using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// Urgent part — pick up a random part from any order in the demo's order list and bring
/// it to the shipping zone within 75 seconds. Doesn't touch the assembly bench at all —
/// this is a standalone fetch-and-deliver task, independent of whatever order (if any) is
/// currently active.
public class UrgentPartEvent : IGameEvent
{
    public string Name        => "UrgentPart";
    public string Description => "Urgent part: grab it and bring it to the shipping zone within 75 seconds";

    const float Duration        = 75f;
    const int   BonusScore      = 75;
    const float DeliveryRadius  = 3f;   // how close to the shipping zone counts as delivered
    const float WatchInterval   = 0.3f;

    /// The part currently being chased under this event, if any — read by
    /// PartHighlighter/MinimapController to give it a distinct highlight color.
    public static PartDefinition ActiveTargetPart { get; private set; }

    readonly string _forcedPartId;
    readonly int    _forcedGateIndex;
    MonoBehaviour   _host;
    Coroutine       _timer;
    Coroutine       _watcher;
    PartDefinition  _targetPart;
    GameObject      _plantedBox;     // the crate this event put its part in, if any
    bool            _delivered;
    int             _deliveryGateIndex = -1;

    public UrgentPartEvent(string forcedPartId = null, int forcedGateIndex = -1)
    {
        _forcedPartId = forcedPartId;
        _forcedGateIndex = forcedGateIndex;
    }

    public string GetNetworkPayload(MonoBehaviour host)
    {
        int gate = ShippingZone.Instance?.PickEventGateIndex() ?? -1;
        return $"{gate};{PickRandomPartFromAnyOrder()?.PartID ?? ""}";
    }

    public void Apply(MonoBehaviour host)
    {
        _host      = host;
        _delivered = false;

        _targetPart = _forcedPartId != null ? PartRegistry.Get(_forcedPartId) : PickRandomPartFromAnyOrder();
        if (_targetPart == null) { GameEventManager.Instance?.MarkEventDone(Name); return; }

        // The errand brings its own goods. PartSpawner lays out the active order's parts and
        // nothing else, so without this the target was either absent from the warehouse entirely
        // — an impossible fetch that just ran out its clock — or it was the order's only copy,
        // and handing that in killed the order the player was already working on.
        //
        // Null in multiplayer on a client (the server plants it and the assignment arrives over
        // NetworkBoxManager) and null when the pool has no free crate; in both cases the event
        // still runs, because a part from the order list may well be lying about anyway.
        _plantedBox = PartSpawner.Instance?.PlaceExtraPart(_targetPart);

        ActiveTargetPart = _targetPart;
        PartHighlighter.RefreshAll();
        _deliveryGateIndex = ShippingZone.Instance?.ShowEventTarget(
            Name, new Color(0.90f, 0.20f, 0.20f), _forcedGateIndex) ?? -1;
        if (_deliveryGateIndex < 0)
        {
            Remove();
            return;
        }

        HUDController.Instance?.AddNotification(
            "URGENT PART",
            $"Grab «{_targetPart.PartName}» and bring it to the shipping zone within {Duration:0} sec!",
            HUDController.NotifType.Warning);

        _timer   = host.StartCoroutine(Countdown());
        _watcher = host.StartCoroutine(WatchForDelivery());
    }

    public void Remove()
    {
        if (_host != null)
        {
            if (_timer   != null) _host.StopCoroutine(_timer);
            if (_watcher != null) _host.StopCoroutine(_watcher);
        }

        if (ActiveTargetPart == _targetPart)
            ActiveTargetPart = null;

        // Whether it was delivered or the clock ran out, the crate this event planted is not part
        // of anyone's order — leaving it would litter the shift with parts nobody asked for, and
        // by the third or fourth event the warehouse reads as noise. Untouched if the player
        // picked it up (the component is gone with the box) or if it holds a required part.
        if (_plantedBox != null)
        {
            PartSpawner.Instance?.RemoveExtraPart(_plantedBox);
            _plantedBox = null;
        }

        PartHighlighter.RefreshAll();
        ShippingZone.Instance?.HideEventTarget(Name);
        HUDController.Instance?.HideEventTimer(Name);

        GameEventManager.Instance?.MarkEventDone(Name);
    }

    IEnumerator WatchForDelivery()
    {
        while (true)
        {
            yield return new WaitForSeconds(WatchInterval);

            // Parts only ever travel in a box in the hands, so that is what is checked.
            var carried = BoxPhysicsInteractable.GrabbedBox;
            var bwp     = carried != null ? carried.GetComponent<BoxWithPart>() : null;
            if (_targetPart == null || bwp == null || bwp.Part != _targetPart) continue;

            var zone     = ShippingZone.Instance;
            var playerTf = PlayerFinder.Find();
            if (zone == null || playerTf == null) continue;
            if (!zone.TryGetEventDeliveryPosition(Name, out var destination)) continue;
            if (Vector3.Distance(playerTf.position, destination) > DeliveryRadius) continue;

            bool  isMP     = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;
            ulong netBoxId = carried.NetBoxId;

            if (isMP)
            {
                // Server owns the hand-in transaction.  Keeping the local copy until confirmation
                // prevents another player's delivery (or a stale request) emptying this hand.
                CoopGameManager.Instance?.NotifyUrgentPartDeliveredServerRpc(netBoxId, carried.NetVersion);
                continue;
            }
            else
            {
                carried.SilentRelease();
                Object.Destroy(carried.gameObject);
                ForceComplete();
            }

            yield break;
        }
    }

    /// Atomically verifies and consumes the exact urgent-part box held by the requesting player.
    public bool ServerTryDeliver(ulong senderId, ulong netBoxId, uint expectedVersion)
    {
        var network = Unity.Netcode.NetworkManager.Singleton;
        if (network == null || !network.IsServer || _delivered || _targetPart == null)
            return false;

        NetworkPlayer sender = null;
        foreach (var player in NetworkPlayer.All)
            if (player != null && player.OwnerClientId == senderId) { sender = player; break; }

        var zone = ShippingZone.Instance;
        var boxes = NetworkPhysBoxManager.Instance;
        if (sender == null || sender.IsDeadNet.Value || sender.IsRagdolledNet.Value ||
            zone == null || boxes == null ||
            !zone.TryGetEventDeliveryPosition(Name, out var destination) ||
            (sender.transform.position - destination).sqrMagnitude > DeliveryRadius * DeliveryRadius ||
            !boxes.ServerTryGetHeldBox(netBoxId, senderId, expectedVersion, out var box, out _) ||
            box == null || box.GetComponent<BoxWithPart>()?.Part?.PartID != _targetPart.PartID ||
            !boxes.ServerTryConsumeHeldBox(netBoxId, senderId, expectedVersion, out _))
            return false;

        ForceComplete();
        return true;
    }

    /// Applied directly in solo. In multiplayer, applied on every client via CoopGameManager's
    /// ClientRpc echo — never called straight from WatchForDelivery() there.
    public void ForceComplete()
    {
        if (_delivered) return;
        _delivered = true;

        var part = _targetPart;
        Remove();

        bool isServer = Unity.Netcode.NetworkManager.Singleton?.IsServer ?? true;
        if (isServer)
            GameManager.Instance?.AddBonusScore(BonusScore);

        HUDController.Instance?.AddNotification(
            "Urgent part delivered!",
            $"+{BonusScore} bonus for «{part?.PartName}»",
            HUDController.NotifType.Success);
    }

    IEnumerator Countdown()
    {
        float remaining = Duration;
        while (remaining > 0f)
        {
            HUDController.Instance?.UpdateEventTimer(
                Name, "URGENT PART", remaining, Duration,
                $"Deliver: {_targetPart?.PartName}", new Color(0.90f, 0.20f, 0.20f));
            yield return null;
            remaining -= Time.deltaTime;
        }

        if (!_delivered)
        {
            HUDController.Instance?.AddNotification(
                "Time's up",
                $"Urgent part «{_targetPart?.PartName}» was not delivered",
                HUDController.NotifType.Danger);
            Remove();
        }
    }

    /// A part from the demo's order list — preferring one the CURRENT order does not need.
    ///
    /// The event plants its own copy, so the two tasks no longer fight over one crate. They can
    /// still collide in the player's hands, though: the delivery watcher below only asks whether
    /// the carried box holds the part, not which crate it is, so a target shared with the
    /// active order would let a walk past the shipping zone hand in the part the bench is waiting
    /// for. Choosing a different part removes that overlap entirely.
    ///
    /// Falls back to the full list when the active order happens to cover it — better a rare
    /// overlap than no urgent part at all.
    static PartDefinition PickRandomPartFromAnyOrder()
    {
        var orders = GameManager.Instance?.GetAllOrders();
        if (orders == null || orders.Length == 0) return null;

        var active = GameManager.Instance?.ActiveOrder;
        var wanted = new HashSet<PartDefinition>();
        if (active?.RequiredParts != null)
            foreach (var req in active.RequiredParts)
                if (req.Part != null) wanted.Add(req.Part);

        var candidates = new List<PartDefinition>();
        var fallback   = new List<PartDefinition>();

        foreach (var order in orders)
        {
            if (order?.RequiredParts == null) continue;
            foreach (var req in order.RequiredParts)
            {
                if (req.Part == null) continue;
                fallback.Add(req.Part);
                if (!wanted.Contains(req.Part)) candidates.Add(req.Part);
            }
        }

        if (candidates.Count > 0) return candidates[Random.Range(0, candidates.Count)];
        return fallback.Count > 0 ? fallback[Random.Range(0, fallback.Count)] : null;
    }
}
