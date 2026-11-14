using System.Collections;
using UnityEngine;

/// Broken forklift — degrades handling for 45 seconds on ONE specific vehicle (reach
/// truck or pallet truck/rohlya — whichever ReachTruckController.All picks). The reach
/// truck and the pallet truck break and get repaired independently of each other.
/// Spawns a ToolkitItem somewhere in the warehouse: players can grab it and repair early.
/// ForkLiftOperator repairs in 10 sec, others in 25 sec.
public class BrokenForkliftEvent : IGameEvent
{
    public string Name        => "BrokenForklift";
    public string Description => "Forklift malfunctioning: hold [E] at it to repair";

    const float AutoFixDuration = 45f;
    const float DamageMult      = 0.35f;
    const float ExtraDrift      = 2.5f;

    readonly bool    _hasKitPos;
    readonly Vector3 _kitPos;
    readonly bool    _hasTruckIndex;
    readonly int     _truckIndex;

    MonoBehaviour        _host;
    Coroutine            _timer;
    GameObject           _kitInstance;
    ReachTruckController _target;
    bool                 _fixed;

    public BrokenForkliftEvent() { }

    public BrokenForkliftEvent(Vector3 kitPos, int truckIndex)
    {
        _hasKitPos     = true;
        _kitPos        = kitPos;
        _hasTruckIndex = true;
        _truckIndex    = truckIndex;
    }

    public string GetNetworkPayload(MonoBehaviour host)
    {
        int idx = ReachTruckController.All.Count > 0 ? Random.Range(0, ReachTruckController.All.Count) : 0;
        return $"{GameEventManager.FormatVec3(ToolkitItem.PickSpawnPosition())}:{idx}";
    }

    public void Apply(MonoBehaviour host)
    {
        _host = host;

        var trucks = ReachTruckController.All;
        if (trucks.Count == 0)
        {
            // No vehicle in the scene to break — nothing to do.
            GameEventManager.Instance?.MarkEventDone(Name);
            return;
        }
        _target = _hasTruckIndex && _truckIndex < trucks.Count ? trucks[_truckIndex] : trucks[Random.Range(0, trucks.Count)];
        _target.ApplyForkliftDamage(DamageMult, ExtraDrift);
        _target.OwnerForkliftEvent = this;

        _kitInstance = _hasKitPos ? ToolkitItem.Spawn(_kitPos) : ToolkitItem.Spawn();

        HUDController.Instance?.AddNotification(
            "FORKLIFT MALFUNCTION",
            $"{_target.DisplayName} malfunctioning — hold [E] at it to repair",
            HUDController.NotifType.Danger);

        _timer = host.StartCoroutine(AutoFixTimer());
    }

    public void Remove()
    {
        if (_fixed) return;
        _fixed = true;

        if (_target != null)
        {
            _target.ClearForkliftDamage();
            _target.OwnerForkliftEvent = null;
        }

        ToolkitItem.ConsumeKit();

        if (_kitInstance != null)
            Object.Destroy(_kitInstance);

        if (_host != null && _timer != null)
            _host.StopCoroutine(_timer);

        GameEventManager.Instance?.MarkEventDone(Name);

        HUDController.Instance?.AddNotification(
            "Forklift repaired",
            "Handling restored",
            HUDController.NotifType.Success);
    }

    IEnumerator AutoFixTimer()
    {
        yield return new WaitForSeconds(AutoFixDuration);
        Remove();
    }
}
