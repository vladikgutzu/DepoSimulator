using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// Rush order — a handful of random boxes scattered around the warehouse start glowing;
/// grab and carry each one to the shipping zone within the time limit for a bonus per box.
/// Standalone fetch-and-deliver, like UrgentPartEvent but for literal carried boxes instead
/// of an inventory item — doesn't touch the order/assembly/packing system at all.
public class RushOrderEvent : IGameEvent
{
    public string Name        => "RushOrder";
    public string Description => "Rush order: grab the glowing boxes and rush them to the purple shipping zone";

    /// Currently-undelivered target boxes — read by MinimapController to give them a dot,
    /// since they're plain BoxPhysicsInteractable (not BoxWithPart), so the normal
    /// part-dot scan never picks them up.
    public static readonly HashSet<BoxPhysicsInteractable> ActiveTargets = new();

    // Same defensive reset pattern as BoxPhysicsInteractable.ResetStatics/ResetNetworkIds —
    // without it, stale (destroyed) box references from a previous scene/session linger in
    // this static set when domain reload is disabled on Play Mode entry.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetStatics() => ActiveTargets.Clear();

    const float Duration       = 120f;
    const int   MinBoxes       = 2;
    const int   MaxBoxes       = 4;
    const int   BonusPerBox    = 50;
    const float DeliveryRadius = 3f;   // how close to the shipping zone counts as delivered
    const float WatchInterval  = 0.3f;

    // Warehouse pack shader emission properties (Shader Graphs/shdr_basic_01) — same
    // technique PartHighlighter uses, applied directly here since a rush-order box isn't
    // necessarily a part-bearing BoxWithPart that PartHighlighter scans.
    static readonly int PowerID    = Shader.PropertyToID("_Power");
    static readonly int ColorMapID = Shader.PropertyToID("_Color_Map");
    static readonly int AnimateID  = Shader.PropertyToID("_animate");
    static readonly int EmissionMapID = Shader.PropertyToID("_Emmision_Map"); // sic: shader property name
    static readonly int ScaleID       = Shader.PropertyToID("_Scale");

    readonly string[] _forcedNetBoxIds;
    readonly int      _forcedGateIndex;

    MonoBehaviour                _host;
    Coroutine                    _timer;
    Coroutine                    _watcher;
    List<BoxPhysicsInteractable> _targetBoxes;
    // Authoritative progress tracking — by NetBoxId, not by live object reference. A remote
    // client can receive the box-destroy broadcast (NetworkPhysBoxManager) before this
    // event's own delivery broadcast; matching against a possibly-already-destroyed
    // BoxPhysicsInteractable would silently drop the delivery (Unity's overridden `!= null`
    // check treats a destroyed object as null), so completion state lives here instead.
    readonly HashSet<ulong>      _remainingBoxIds = new();
    int                          _totalCount;
    int                          _deliveredCount;
    MaterialPropertyBlock        _mpb;
    int                          _deliveryGateIndex = -1;

    public RushOrderEvent(string[] forcedNetBoxIds = null, int forcedGateIndex = -1)
    {
        _forcedNetBoxIds = forcedNetBoxIds;
        _forcedGateIndex = forcedGateIndex;
    }

    public string GetNetworkPayload(MonoBehaviour host)
    {
        var boxes = PickRandomBoxes(RollCount());
        if (boxes.Count == 0) return "";

        var ids = new string[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
            ids[i] = boxes[i].NetBoxId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int gate = ShippingZone.Instance?.PickEventGateIndex() ?? -1;
        return $"{gate};{string.Join("|", ids)}";
    }

    public void Apply(MonoBehaviour host)
    {
        _host           = host;
        _deliveredCount = 0;
        _mpb            = new MaterialPropertyBlock();

        _targetBoxes = new List<BoxPhysicsInteractable>();
        if (_forcedNetBoxIds != null)
        {
            foreach (var s in _forcedNetBoxIds)
            {
                if (!ulong.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)) continue;
                var box = BoxPhysicsInteractable.FindByNetId(id);
                if (box != null) _targetBoxes.Add(box);
            }
        }
        else
        {
            _targetBoxes = PickRandomBoxes(RollCount());
        }

        if (_targetBoxes.Count == 0) { GameEventManager.Instance?.MarkEventDone(Name); return; }

        _totalCount = _targetBoxes.Count;
        foreach (var box in _targetBoxes)
        {
            SetHighlight(box, true);
            ActiveTargets.Add(box);
            _remainingBoxIds.Add(box.NetBoxId);
        }

        _deliveryGateIndex = ShippingZone.Instance?.ShowEventTarget(
            Name, new Color(0.75f, 0.35f, 0.95f), _forcedGateIndex) ?? -1;
        if (_deliveryGateIndex < 0)
        {
            Remove();
            return;
        }

        HUDController.Instance?.AddNotification(
            "RUSH ORDER",
            $"Grab the {_totalCount} glowing boxes and deliver them to the purple shipping zone within {Duration:0} sec!",
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

        if (_targetBoxes != null)
            foreach (var box in _targetBoxes)
            {
                if (box != null) SetHighlight(box, false);
                ActiveTargets.Remove(box);
            }

        ShippingZone.Instance?.HideEventTarget(Name);
        HUDController.Instance?.HideEventTimer(Name);

        GameEventManager.Instance?.MarkEventDone(Name);
    }

    IEnumerator WatchForDelivery()
    {
        while (true)
        {
            yield return new WaitForSeconds(WatchInterval);

            var carried = BoxPhysicsInteractable.GrabbedBox;
            if (carried == null || !_remainingBoxIds.Contains(carried.NetBoxId)) continue;

            var zone     = ShippingZone.Instance;
            var playerTf = PlayerFinder.Find();
            if (zone == null || playerTf == null) continue;
            if (!zone.TryGetEventDeliveryPosition(Name, out var destination)) continue;
            if (Vector3.Distance(playerTf.position, destination) > DeliveryRadius) continue;

            if (Deliver(carried)) yield break;
        }
    }

    // Returns true when the delivery completed the whole order (solo only — in MP the echo does).
    bool Deliver(BoxPhysicsInteractable carried)
    {
        ulong netBoxId = carried.NetBoxId;
        bool isMP     = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;

        if (isMP)
        {
            // The server verifies ownership, version, target membership and distance, then
            // consumes the held box and advances the event as one transaction.  Do not destroy
            // the local copy optimistically: a rejected/stale request must leave it in the hand.
            CoopGameManager.Instance?.NotifyRushBoxDeliveredServerRpc(netBoxId, carried.NetVersion);
            return false;
        }

        carried.SilentRelease();
        Object.Destroy(carried.gameObject);
        return ApplyDelivery(netBoxId);
    }

    /// Server half of a rush delivery.  All facts used here are canonical server state; the
    /// client's request contains only the identity/version of the box it believes it is holding.
    public bool ServerTryDeliver(ulong senderId, ulong netBoxId, uint expectedVersion)
    {
        var network = Unity.Netcode.NetworkManager.Singleton;
        if (network == null || !network.IsServer || !_remainingBoxIds.Contains(netBoxId))
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
            !boxes.ServerTryGetHeldBox(netBoxId, senderId, expectedVersion, out _, out _) ||
            !boxes.ServerTryConsumeHeldBox(netBoxId, senderId, expectedVersion, out _))
            return false;

        ApplyDelivery(netBoxId);
        return true;
    }

    /// Applied directly in solo. In multiplayer, applied on every client via CoopGameManager's
    /// ClientRpc echo — never called straight from WatchForDelivery() there. Returns true if
    /// this delivery completed the whole rush order (caller should stop watching).
    public bool ApplyDelivery(ulong netBoxId)
    {
        if (!_remainingBoxIds.Remove(netBoxId)) return false; // not ours, or already applied

        _deliveredCount++;

        // Best-effort visual cleanup — the box may already be destroyed on this client if the
        // NetworkPhysBoxManager destroy broadcast arrived first; nothing to undo in that case.
        BoxPhysicsInteractable box = null;
        if (_targetBoxes != null)
            foreach (var b in _targetBoxes)
                if (b != null && b.NetBoxId == netBoxId) { box = b; break; }
        if (box != null)
        {
            SetHighlight(box, false);
            _targetBoxes.Remove(box);
            ActiveTargets.Remove(box);
        }

        bool isServer = Unity.Netcode.NetworkManager.Singleton?.IsServer ?? true;
        if (isServer)
            GameManager.Instance?.AddBonusScore(BonusPerBox);

        if (_remainingBoxIds.Count == 0)
        {
            HUDController.Instance?.AddNotification(
                "Rush order complete!",
                $"All {_totalCount} boxes delivered — +{BonusPerBox * _totalCount} total",
                HUDController.NotifType.Success);
            Remove();
            return true;
        }

        HUDController.Instance?.AddNotification(
            "Box delivered",
            $"{_deliveredCount}/{_totalCount} delivered — +{BonusPerBox}",
            HUDController.NotifType.Success);
        return false;
    }

    IEnumerator Countdown()
    {
        float remaining = Duration;
        while (remaining > 0f)
        {
            HUDController.Instance?.UpdateEventTimer(
                Name, "RUSH ORDER", remaining, Duration,
                $"Deliver {_deliveredCount}/{_totalCount} boxes",
                new Color(0.75f, 0.35f, 0.95f));
            yield return null;
            remaining -= Time.deltaTime;
        }

        HUDController.Instance?.AddNotification(
            "Time's up",
            _deliveredCount > 0
                ? $"Rush order partially completed — {_deliveredCount}/{_totalCount} boxes delivered"
                : "Rush order boxes were not delivered in time",
            HUDController.NotifType.Danger);
        Remove();
    }

    static int RollCount() => Random.Range(MinBoxes, MaxBoxes + 1);

    static List<BoxPhysicsInteractable> PickRandomBoxes(int count)
    {
        var boxes = Object.FindObjectsByType<BoxPhysicsInteractable>(FindObjectsSortMode.None);
        var pool  = new List<BoxPhysicsInteractable>(boxes.Length);
        foreach (var b in boxes)
        {
            if (b == BoxPhysicsInteractable.GrabbedBox) continue;
            // Never a box that already means something else: taking an order's part crate (or a
            // vehicle part / packed order) as a rush target glows it in the same gold and then
            // eats it on delivery, so the order it belonged to can no longer be finished.
            var bwp = b.GetComponent<BoxWithPart>();
            if (bwp != null && bwp.Part != null) continue;
            if (b.GetComponent<VehiclePartBox>() != null || b.GetComponent<PackedOrderBox>() != null) continue;
            pool.Add(b);
        }

        var picked = new List<BoxPhysicsInteractable>(count);
        for (int i = 0; i < count && pool.Count > 0; i++)
        {
            int idx = Random.Range(0, pool.Count);
            picked.Add(pool[idx]);
            pool.RemoveAt(idx);
        }
        return picked;
    }

    void SetHighlight(BoxPhysicsInteractable box, bool on)
    {
        foreach (var r in box.GetComponentsInChildren<Renderer>(true))
        {
            var materials = r.sharedMaterials;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                var material = materials[materialIndex];
                if (material == null || !material.HasProperty(PowerID)
                                     || !material.HasProperty(ColorMapID)) continue;

                _mpb.Clear();
                r.GetPropertyBlock(_mpb, materialIndex);
                if (on)
                {
                    _mpb.SetFloat(AnimateID, 5f);
                    _mpb.SetColor(ColorMapID, PartHighlighter.RushGlowColor);
                    _mpb.SetFloat(PowerID, PartHighlighter.RequiredGlowPower);
                    if (material.HasProperty(EmissionMapID))
                        _mpb.SetTexture(EmissionMapID, Texture2D.whiteTexture);
                    if (material.HasProperty(ScaleID))
                        _mpb.SetFloat(ScaleID, 1f);
                }
                else
                {
                    _mpb.Clear();
                }
                r.SetPropertyBlock(_mpb, materialIndex);
            }
        }
    }
}
