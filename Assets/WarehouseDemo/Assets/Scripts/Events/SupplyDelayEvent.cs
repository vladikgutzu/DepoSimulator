using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// Supply delay — 1–2 part types become temporarily unavailable to pick up (60 sec).
/// Triggers when a Dispatcher is on the team — complicates coordination.
public class SupplyDelayEvent : IGameEvent
{
    public string Name        => "SupplyDelay";
    public string Description => "Supply delay: some parts are temporarily unavailable";

    const float Duration   = 60f;
    const int   MaxBlocked = 2;

    readonly string[] _forcedIds;
    MonoBehaviour _host;
    Coroutine     _timer;
    readonly List<string> _blockedIds = new();

    public SupplyDelayEvent(string[] forcedIds = null)
    {
        _forcedIds = forcedIds;
    }

    public string GetNetworkPayload(MonoBehaviour host)
    {
        // Pick blocked part IDs on server; encode as pipe-separated string.
        var bench = AssemblyBench.Instance;
        var order = bench?.ActiveOrder;
        if (order == null) return "";

        var candidates = new List<string>();
        foreach (var req in order.RequiredParts)
        {
            if (req.Part == null) continue;
            if (bench.GetPlacedCount(req.Part) < req.Count)
                candidates.Add(req.Part.PartID);
        }
        if (candidates.Count == 0) return "";

        var picked = new List<string>();
        int count = Mathf.Min(MaxBlocked, candidates.Count);
        for (int i = 0; i < count; i++)
        {
            int idx = Random.Range(i, candidates.Count);
            (candidates[i], candidates[idx]) = (candidates[idx], candidates[i]);
            picked.Add(candidates[i]);
        }
        return string.Join("|", picked);
    }

    public void Apply(MonoBehaviour host)
    {
        _host = host;
        _blockedIds.Clear();

        var bench = AssemblyBench.Instance;
        var order = bench?.ActiveOrder;
        if (order == null || order.RequiredParts == null || order.RequiredParts.Length == 0)
        {
            GameEventManager.Instance?.MarkEventDone(Name);
            return;
        }

        if (_forcedIds != null && _forcedIds.Length > 0)
        {
            // MP path: use server-chosen IDs.
            foreach (var id in _forcedIds)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    _blockedIds.Add(id);
                    GameEventManager.BlockedPartIDs.Add(id);
                }
            }
        }
        else
        {
            // SP path: pick locally.
            var candidates = new List<string>();
            foreach (var req in order.RequiredParts)
            {
                if (req.Part == null) continue;
                if (bench.GetPlacedCount(req.Part) < req.Count)
                    candidates.Add(req.Part.PartID);
            }
            if (candidates.Count == 0) { GameEventManager.Instance?.MarkEventDone(Name); return; }

            int count = Mathf.Min(MaxBlocked, candidates.Count);
            for (int i = 0; i < count; i++)
            {
                int idx = Random.Range(i, candidates.Count);
                (candidates[i], candidates[idx]) = (candidates[idx], candidates[i]);
                _blockedIds.Add(candidates[i]);
                GameEventManager.BlockedPartIDs.Add(candidates[i]);
            }
        }

        if (_blockedIds.Count == 0) { GameEventManager.Instance?.MarkEventDone(Name); return; }

        var names = new List<string>();
        foreach (var req in order.RequiredParts)
            if (req.Part != null && _blockedIds.Contains(req.Part.PartID))
                names.Add(req.Part.PartName);

        string nameList = string.Join(", ", names);
        HUDController.Instance?.AddNotification(
            "SUPPLY DELAY",
            $"Unavailable for {Duration:0} sec: {nameList}",
            HUDController.NotifType.Danger);

        _timer = host.StartCoroutine(Timer());
    }

    public void Remove()
    {
        foreach (var id in _blockedIds)
            GameEventManager.BlockedPartIDs.Remove(id);
        _blockedIds.Clear();

        if (_host != null && _timer != null)
            _host.StopCoroutine(_timer);

        GameEventManager.Instance?.MarkEventDone(Name);

        HUDController.Instance?.AddNotification(
            "Supplies resumed",
            "All parts available again",
            HUDController.NotifType.Success);
    }

    IEnumerator Timer()
    {
        yield return new WaitForSeconds(Duration);
        Remove();
    }
}
