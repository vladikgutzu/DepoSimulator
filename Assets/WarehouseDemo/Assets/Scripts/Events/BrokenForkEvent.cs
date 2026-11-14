using UnityEngine;

/// The forklift's fork jams — can't be raised or lowered. Applies to ONE specific vehicle
/// (reach truck or pallet truck/rohlya — whichever ReachTruckController.All picks); they
/// jam and get repaired independently of each other.
/// Doesn't recover on its own: find the toolkit and hold [E] at the vehicle to repair.
/// Triggers when a ForkLiftOperator is on the team.
public class BrokenForkEvent : IGameEvent
{
    public string Name        => "BrokenFork";
    public string Description => "Forklift fork is jammed: raising and lowering are blocked";

    readonly bool    _hasKitPos;
    readonly Vector3 _kitPos;
    readonly bool    _hasTruckIndex;
    readonly int     _truckIndex;

    GameObject           _kitInstance;
    ReachTruckController _target;
    bool                 _fixed;

    public BrokenForkEvent() { }

    public BrokenForkEvent(Vector3 kitPos, int truckIndex)
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
        var trucks = ReachTruckController.All;
        if (trucks.Count == 0)
        {
            GameEventManager.Instance?.MarkEventDone(Name);
            return;
        }
        _target = _hasTruckIndex && _truckIndex < trucks.Count ? trucks[_truckIndex] : trucks[Random.Range(0, trucks.Count)];
        _target.SetForkJammed(true);
        _target.OwnerForkEvent = this;

        _kitInstance = _hasKitPos ? ToolkitItem.Spawn(_kitPos) : ToolkitItem.Spawn();

        HUDController.Instance?.AddNotification(
            "FORK JAMMED",
            $"{_target.DisplayName}: hydraulics failed — hold [E] at it to repair",
            HUDController.NotifType.Danger);
    }

    public void Remove()
    {
        if (_fixed) return;
        _fixed = true;

        if (_target != null)
        {
            _target.SetForkJammed(false);
            _target.OwnerForkEvent = null;
        }

        ToolkitItem.ConsumeKit();

        if (_kitInstance != null)
            Object.Destroy(_kitInstance);

        GameEventManager.Instance?.MarkEventDone(Name);

        HUDController.Instance?.AddNotification(
            "Fork repaired",
            "Hydraulics restored — raising and lowering work again",
            HUDController.NotifType.Success);
    }
}
