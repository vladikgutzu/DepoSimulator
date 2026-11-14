using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// Order mix-up — the dispatcher reports a part that doesn't actually exist.
/// Players waste time searching, then a correction arrives.
/// Works as a pure information event: the real order never changes.
public class WrongOrderEvent : IGameEvent
{
    public string Name        => "WrongOrder";
    public string Description => "Dispatcher error: a fake component listed on the order";

    const float Duration = 75f;

    static readonly string[] FakePartNames =
    {
        "Hydraulic fitting",
        "Support axle #7",
        "Gearbox seal",
        "Pressure control sensor",
        "Mounting bracket XL",
        "Vibration damper",
        "Lock nut M24",
    };

    readonly string _forcedFakePart;
    MonoBehaviour   _host;
    Coroutine       _timer;

    public WrongOrderEvent(string forcedFakePart = null)
    {
        _forcedFakePart = forcedFakePart;
    }

    public string GetNetworkPayload(MonoBehaviour host)
    {
        // Pick a fake part name on the server and pass it to all clients.
        var orderedNames = new HashSet<string>();
        var order = GameManager.Instance?.ActiveOrder;
        if (order != null)
            foreach (var p in order.RequiredParts)
                if (p.Part != null) orderedNames.Add(p.Part.PartName);

        foreach (var name in FakePartNames)
            if (!orderedNames.Contains(name)) return name;

        return FakePartNames[Random.Range(0, FakePartNames.Length)];
    }

    public void Apply(MonoBehaviour host)
    {
        _host = host;

        string fakePart = _forcedFakePart;
        if (fakePart == null)
        {
            // SP path: pick locally.
            var orderedNames = new HashSet<string>();
            var order = GameManager.Instance?.ActiveOrder;
            if (order != null)
                foreach (var p in order.RequiredParts)
                    if (p.Part != null) orderedNames.Add(p.Part.PartName);

            fakePart = FakePartNames[Random.Range(0, FakePartNames.Length)];
            foreach (var name in FakePartNames)
                if (!orderedNames.Contains(name)) { fakePart = name; break; }
        }

        HUDController.Instance?.AddNotification(
            "DISPATCHER — URGENT",
            $"Additionally required: «{fakePart}». Find it in the warehouse!",
            HUDController.NotifType.Warning);

        AudioManager.Instance?.PlayError();
        _timer = host.StartCoroutine(Correction(fakePart));
    }

    public void Remove()
    {
        if (_host != null && _timer != null)
            _host.StopCoroutine(_timer);
        GameEventManager.Instance?.MarkEventDone(Name);
    }

    IEnumerator Correction(string fakePart)
    {
        yield return new WaitForSeconds(Duration);

        HUDController.Instance?.AddNotification(
            "DISPATCHER CORRECTION",
            $"Order mix-up! «{fakePart}» isn't needed — our apologies.",
            HUDController.NotifType.Danger);

        AudioManager.Instance?.PlayError();
        GameEventManager.Instance?.MarkEventDone(Name);
    }
}
