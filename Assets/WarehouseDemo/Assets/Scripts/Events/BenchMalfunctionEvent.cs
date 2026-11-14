using System.Collections;
using UnityEngine;

/// Bench malfunction — blocks it for 30 seconds.
/// Carries over the old logic from GameEventManager.
public class BenchMalfunctionEvent : IGameEvent
{
    public string Name        => "BenchMalfunction";
    public string Description => "Bench blocked for 30 seconds";

    const float Duration = 30f;

    MonoBehaviour _host;
    Coroutine     _timer;

    public void Apply(MonoBehaviour host)
    {
        _host = host;
        GameEventManager.Instance.BlockBench();

        HUDController.Instance?.AddNotification(
            "MALFUNCTION",
            "Bench blocked for 30 seconds",
            HUDController.NotifType.Danger);

        _timer = host.StartCoroutine(Timer());
    }

    public void Remove()
    {
        if (_host != null && _timer != null)
            _host.StopCoroutine(_timer);

        GameEventManager.Instance?.UnblockBench();

        GameEventManager.Instance?.MarkEventDone(Name);

        HUDController.Instance?.AddNotification(
            "Bench restored",
            "You can continue working",
            HUDController.NotifType.Success);
    }

    IEnumerator Timer()
    {
        yield return new WaitForSeconds(Duration);
        Remove();
    }
}
