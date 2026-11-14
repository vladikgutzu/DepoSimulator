using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// Fire alarm — smooth red post-process pulse (pale red breathing up to saturated red
/// and back, no hard cuts/strobing) and the assembly bench is blocked. Picks a random
/// number of power boxes (powerBox_06 / powerBox_07) as repair targets — no toolkit needed, just hold [E] at
/// each one. Only ends when every targeted box is repaired — there is no automatic recovery.
public class FireAlarmEvent : IGameEvent
{
    public string Name        => "FireAlarm";
    public string Description => "Fire alarm: bench blocked, repair the power boxes to clear it";

    const float PulsePeriod = 2.5f;  // seconds per full pale->red->pale breathing cycle
    const float MinWeight   = 0.15f; // pale red / near-normal lighting at the low point
    const float MaxWeight   = 1f;    // fully saturated red at the peak

    readonly bool  _hasBoxIndices;
    readonly int[] _boxIndices;

    MonoBehaviour _host;
    Coroutine     _flicker;

    GameObject _volumeGO;
    Volume     _volume;
    bool       _fixed;

    readonly List<PowerBoxRepairTarget> _repairTargets = new();
    int _repairedCount;

    public FireAlarmEvent() { }

    public FireAlarmEvent(int[] boxIndices)
    {
        _hasBoxIndices = true;
        _boxIndices    = boxIndices;
    }

    public string GetNetworkPayload(MonoBehaviour host)
    {
        var boxes = PowerBoxRegistry.GetAll();
        int count = boxes.Count > 0 ? Random.Range(1, boxes.Count + 1) : 0;
        var indices = PowerBoxRegistry.PickRandomIndices(boxes.Count, count);
        return string.Join(":", indices);
    }

    public void Apply(MonoBehaviour host)
    {
        _host = host;

        // Pick the repair targets — a random number of power boxes (or the
        // network-assigned ones). No toolkit required, unlike PowerFailureEvent.
        var boxes = PowerBoxRegistry.GetAll();
        List<int> indices;
        if (_hasBoxIndices)
        {
            indices = new List<int>(_boxIndices);
        }
        else
        {
            int count = boxes.Count > 0 ? Random.Range(1, boxes.Count + 1) : 0;
            indices = PowerBoxRegistry.PickRandomIndices(boxes.Count, count);
        }

        foreach (var idx in indices)
        {
            if (idx < 0 || idx >= boxes.Count) continue;
            var go = boxes[idx];
            // Skip boxes already claimed by another concurrently-active event (e.g. PowerFailure).
            if (go.GetComponent<PowerBoxRepairTarget>() != null) continue;
            var target = go.AddComponent<PowerBoxRepairTarget>();
            target.Configure(requireToolkit: false, boxIndex: idx);
            target.OnRepaired = NotifyBoxRepaired;
            _repairTargets.Add(target);
        }

        if (_repairTargets.Count == 0)
        {
            // Nothing available to target (all boxes already claimed elsewhere, or none
            // exist) — bail out cleanly instead of starting an alarm nobody can clear.
            GameEventManager.Instance?.MarkEventDone(Name);
            return;
        }

        // Dedicated overlay Volume (separate from the Day/Night Global Volume used by
        // PowerFailureEvent) so the red pulse layers on top regardless of whatever
        // lighting state is currently active, and never fights over the same profile slot.
        var profile = GameEventManager.Instance?.FireAlarmProfile;
        if (profile != null)
        {
            _volumeGO = new GameObject("FireAlarmVolume");
            _volume = _volumeGO.AddComponent<Volume>();
            _volume.isGlobal      = true;
            _volume.priority      = 10f; // blends on top of the base Global Volume
            _volume.weight        = MinWeight;
            _volume.sharedProfile = profile;
            _flicker = host.StartCoroutine(FlickerRoutine());
        }

        GameEventManager.Instance?.BlockBench();

        HUDController.Instance?.AddNotification(
            "FIRE ALARM",
            $"Alarm! Bench disabled. Repair all {_repairTargets.Count} power boxes to clear it.",
            HUDController.NotifType.Danger);

        GameEventManager.TriggerShake(0.25f);

        FireAlarmSirenPlayer.Instance?.SetClip(GameEventManager.Instance?.FireAlarmSirenClip);
        FireAlarmSirenPlayer.Instance?.Play();
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

        if (_host != null && _flicker != null)
            _host.StopCoroutine(_flicker);

        FireAlarmSirenPlayer.Instance?.Stop();

        if (_volumeGO != null)
            Object.Destroy(_volumeGO);

        foreach (var t in _repairTargets)
            if (t != null) Object.Destroy(t);
        _repairTargets.Clear();

        GameEventManager.Instance?.UnblockBench();
        GameEventManager.Instance?.MarkEventDone(Name);

        HUDController.Instance?.AddNotification(
            "All clear",
            "Fire alarm lifted — bench back online",
            HUDController.NotifType.Success);
    }

    IEnumerator FlickerRoutine()
    {
        float t = 0f;
        while (true)
        {
            t += Time.deltaTime;
            // Continuous sine breathing, not a hard on/off toggle — smooth and seizure-safe.
            float phase = (Mathf.Sin(t * (2f * Mathf.PI / PulsePeriod)) + 1f) * 0.5f; // 0..1
            _volume.weight = Mathf.Lerp(MinWeight, MaxWeight, phase);
            yield return null;
        }
    }

}
