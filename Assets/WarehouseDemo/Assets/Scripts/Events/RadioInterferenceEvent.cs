using System.Collections;
using UnityEngine;

/// Radio interference — continuous static noise disrupts voice comms.
/// The Dispatcher must find the toolkit and hold [E] at the radio panel to fix it.
/// Auto-clears after MaxDuration seconds if no Dispatcher is present (SP fallback).
public class RadioInterferenceEvent : IGameEvent
{
    public static RadioInterferenceEvent Active { get; private set; }

    public string Name        => "RadioInterference";
    public string Description => "Radio interference — the Dispatcher must repair it";

    const float MaxDuration      = 120f;  // failsafe auto-end if dispatcher absent
    const float StaticVolume     = 0.28f; // RadioStaticPlayer burst volume override
    const float LowPassCutoff    = 800f;  // Hz — muffle all audio during event
    const float NormalCutoff     = 22000f;

    readonly bool    _hasKitPos;
    readonly Vector3 _kitPos;

    MonoBehaviour _host;
    Coroutine     _timer;
    GameObject    _panel;
    GameObject    _kitInstance;
    AudioLowPassFilter _lpf;
    bool _fixed;

    public RadioInterferenceEvent() { }

    public RadioInterferenceEvent(Vector3 kitPos)
    {
        _hasKitPos = true;
        _kitPos    = kitPos;
    }

    public string GetNetworkPayload(MonoBehaviour host) =>
        GameEventManager.FormatVec3(ToolkitItem.PickSpawnPosition());

    // ── IGameEvent ──────────────────────────────────────────────────────────────

    public void Apply(MonoBehaviour host)
    {
        _host  = host;
        _fixed = false;
        Active = this;

        // The job goes onto the warehouse's own radio charger — see RadioRepairPanel.
        _panel = RadioRepairPanel.Spawn();

        _kitInstance = _hasKitPos ? ToolkitItem.Spawn(_kitPos) : ToolkitItem.Spawn();

        // Start continuous static bursts
        RadioStaticPlayer.Instance?.Play(MaxDuration);

        // Muffle audio listener to simulate garbled comms
        ApplyLowPass();

        HUDController.Instance?.AddNotification(
            "RADIO INTERFERENCE",
            "Comms disrupted. Dispatcher: hold [E] at the radio panel!",
            HUDController.NotifType.Danger);

        _timer = host.StartCoroutine(Intermittent());
    }

    public void Remove()
    {
        Active = null;
        if (_host != null && _timer != null)
            _host.StopCoroutine(_timer);

        RestoreLowPass();
        RadioStaticPlayer.Instance?.Stop();

        // Only the job is removed. The charger is part of the wall: destroying it would take a
        // piece of the warehouse with every repaired radio.
        if (_panel != null)
        {
            var panel = _panel.GetComponent<RadioRepairPanel>();
            if (panel != null) Object.Destroy(panel);
            _panel = null;
        }

        if (_kitInstance != null)
            Object.Destroy(_kitInstance);
        ToolkitItem.ConsumeKit();

        GameEventManager.Instance?.MarkEventDone(Name);
    }

    // Called by RadioRepairPanel.Interact() when dispatcher fixes the radio.
    public void Fix()
    {
        if (_fixed) return;
        _fixed = true;
        Remove();

        HUDController.Instance?.AddNotification(
            "Radio repaired",
            "Comms restored",
            HUDController.NotifType.Success);
    }

    // ── Audio helpers ───────────────────────────────────────────────────────────

    void ApplyLowPass()
    {
        var listener = Object.FindFirstObjectByType<AudioListener>();
        if (listener == null) return;

        _lpf = listener.GetComponent<AudioLowPassFilter>();
        if (_lpf == null) _lpf = listener.gameObject.AddComponent<AudioLowPassFilter>();
        _lpf.cutoffFrequency = LowPassCutoff;
    }

    void RestoreLowPass()
    {
        if (_lpf == null) return;
        _lpf.cutoffFrequency = NormalCutoff;
        // Don't destroy — may be needed by other effects. Just restore cutoff.
    }

    // ── Intermittent static bursts ───────────────────────────────────────────────

    IEnumerator Intermittent()
    {
        float elapsed = 0f;
        while (elapsed < MaxDuration && !_fixed)
        {
            // Static burst every 4–9 seconds
            float wait = Random.Range(4f, 9f);
            yield return new WaitForSeconds(wait);
            elapsed += wait;
            if (_fixed) break;

            // Brief burst: spike low-pass filter cutoff up then back down (crackle)
            if (_lpf != null) _lpf.cutoffFrequency = 4000f;
            RadioStaticPlayer.Instance?.Play(0.8f);
            yield return new WaitForSeconds(0.5f);
            if (_lpf != null) _lpf.cutoffFrequency = LowPassCutoff;

            // Periodically remind players
            if (elapsed % 30f < 10f)
            {
                HUDController.Instance?.AddNotification(
                    "Radio interference",
                    "Dispatcher: approach the radio panel!",
                    HUDController.NotifType.Warning);
            }
        }

        // Failsafe auto-end
        if (!_fixed) Remove();
    }
}
