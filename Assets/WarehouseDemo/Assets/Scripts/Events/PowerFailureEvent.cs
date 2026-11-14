using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// Power failure — switches post-processing to the Night profile and disables the bench.
/// Picks 3 random power boxes (powerBox_06 / powerBox_07) as repair targets; the player must find the toolkit
/// and hold [E] for 5 sec at EACH of them. The toolkit isn't consumed until all 3 are
/// fixed. Only ends when every target is repaired — there is no automatic recovery.
public class PowerFailureEvent : IGameEvent
{
    public string Name        => "PowerFailure";
    public string Description => "Power failure: repair all 3 power boxes";

    const int TargetCount = 3;

    readonly bool  _hasKitPos;
    readonly Vector3 _kitPos;
    readonly bool  _hasBoxIndices;
    readonly int[] _boxIndices;

    GameObject _kitInstance;
    readonly List<PowerBoxRepairTarget> _repairTargets = new();
    int  _repairedCount;
    bool _fixed;

    // Post-process state
    Volume        _globalVolume;
    VolumeProfile _savedProfile;

    public PowerFailureEvent() { }

    public PowerFailureEvent(Vector3 kitPos, int[] boxIndices)
    {
        _hasKitPos     = true;
        _kitPos        = kitPos;
        _hasBoxIndices = true;
        _boxIndices    = boxIndices;
    }

    public string GetNetworkPayload(MonoBehaviour host)
    {
        var boxes   = PowerBoxRegistry.GetAll();
        var indices = PowerBoxRegistry.PickRandomIndices(boxes.Count, TargetCount);
        string idxPart = string.Join(":", indices);
        return $"{GameEventManager.FormatVec3(ToolkitItem.PickSpawnPosition())}:{idxPart}";
    }

    public void Apply(MonoBehaviour host)
    {
        // Switch to Night post-process profile
        _globalVolume = FindGlobalVolume();
        if (_globalVolume != null)
        {
            _savedProfile = _globalVolume.sharedProfile;
            var night = GameEventManager.Instance?.NightProfile;
            if (night != null)
                _globalVolume.sharedProfile = night;
        }

        // Disable assembly bench
        GameEventManager.Instance?.BlockBench();

        // Pick the repair targets — 3 random power boxes (or the network-assigned ones).
        var boxes = PowerBoxRegistry.GetAll();
        var indices = _hasBoxIndices ? new List<int>(_boxIndices)
                                      : PowerBoxRegistry.PickRandomIndices(boxes.Count, TargetCount);
        foreach (var idx in indices)
        {
            if (idx < 0 || idx >= boxes.Count) continue;
            var go = boxes[idx];
            // Skip boxes already claimed by another concurrently-active event (e.g. FireAlarm).
            if (go.GetComponent<PowerBoxRepairTarget>() != null) continue;
            var target = go.AddComponent<PowerBoxRepairTarget>();
            target.Configure(requireToolkit: true, boxIndex: idx);
            target.OnRepaired = NotifyBoxRepaired;
            _repairTargets.Add(target);
        }

        // Another event can already own every power box. Starting an outage with zero repair
        // targets creates an unwinnable event: the task looks complete, but nothing can call
        // NotifyBoxRepaired and therefore the lighting/bench never recover.
        if (_repairTargets.Count == 0)
        {
            Remove();
            return;
        }

        _kitInstance = _hasKitPos ? ToolkitItem.Spawn(_kitPos) : ToolkitItem.Spawn();

        GameEventManager.TriggerShake(0.4f);

        HUDController.Instance?.AddNotification(
            "POWER FAILURE",
            $"The lights went out! Bench disabled. Hold [E] at each of the {_repairTargets.Count} glowing power boxes to repair them.",
            HUDController.NotifType.Danger);

        AudioManager.Instance?.PlayError();
    }

    /// Called by a PowerBoxRepairTarget once its own hold-repair completes.
    public void NotifyBoxRepaired(PowerBoxRepairTarget box)
    {
        _repairedCount++;

        if (_repairedCount < _repairTargets.Count)
        {
            HUDController.Instance?.AddNotification(
                "Power box repaired",
                $"{_repairedCount}/{_repairTargets.Count} fixed — find the rest",
                HUDController.NotifType.Warning);
        }
        else
        {
            Remove();
        }
    }

    public void Remove()
    {
        if (_fixed) return;
        _fixed = true;

        // Restore previous (Day) post-process profile
        if (_globalVolume != null)
        {
            var restoreProfile = _savedProfile ?? GameEventManager.Instance?.DayProfile;
            if (restoreProfile != null) _globalVolume.sharedProfile = restoreProfile;
        }

        // Re-enable bench
        GameEventManager.Instance?.UnblockBench();

        foreach (var t in _repairTargets)
            if (t != null) Object.Destroy(t);
        _repairTargets.Clear();

        if (_kitInstance != null)
            Object.Destroy(_kitInstance);
        ToolkitItem.ConsumeKit();

        GameEventManager.Instance?.MarkEventDone(Name);

        HUDController.Instance?.AddNotification(
            "Power restored",
            "All power boxes fixed — the bench is working again",
            HUDController.NotifType.Success);
    }

    static Volume FindGlobalVolume()
    {
        Volume fallback = null;
        var manager = GameEventManager.Instance;
        foreach (var v in Object.FindObjectsByType<Volume>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!v.isGlobal) continue;
            fallback ??= v;

            // FireAlarmEvent creates its own global overlay. Never replace that profile with the
            // night profile: the warehouse day/night volume is the one whose current profile is
            // one of the configured lighting profiles.
            if (v.sharedProfile == manager?.DayProfile || v.sharedProfile == manager?.NightProfile)
                return v;
        }
        return fallback;
    }
}
