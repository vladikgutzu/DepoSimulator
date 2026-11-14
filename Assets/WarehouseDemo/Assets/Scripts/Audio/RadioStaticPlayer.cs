using UnityEngine;

/// Procedural walkie-talkie radio static.
/// Self-instantiates at runtime — no need to add to scene manually.
/// Call Play() to trigger a burst.
/// Generates band-pass filtered white noise via OnAudioFilterRead — no audio file needed.
[RequireComponent(typeof(AudioSource))]
public class RadioStaticPlayer : MonoBehaviour
{
    public static RadioStaticPlayer Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoCreate()
    {
        if (Instance != null) return;
        var go = new UnityEngine.GameObject("[RadioStaticPlayer]");
        DontDestroyOnLoad(go);
        go.AddComponent<RadioStaticPlayer>();
    }

    [SerializeField] [Range(0f, 1f)] float _volume      = 0.35f;
    [SerializeField]                  float _burstLength = 1.2f;  // seconds of static per burst
    [SerializeField]                  float _clickVolume = 0.6f;  // PTT click loudness

    AudioSource _src;

    // ── Noise generation state (runs on audio thread) ────────────────────────
    float _remaining;       // seconds left in current burst
    float _sampleRate;

    // System.Random is used instead of UnityEngine.Random because OnAudioFilterRead
    // runs on the audio thread, where Unity API calls are forbidden.
    readonly System.Random _rng = new System.Random();

    // Simple one-pole band-pass via two low-pass stages at different cutoffs
    // Low-pass A (high cutoff ~3 kHz) minus Low-pass B (low cutoff ~300 Hz) = band
    float _lpA, _lpB;       // filter states

    // Envelope
    float _envAttack  = 0.02f;   // seconds
    float _envRelease = 0.08f;   // seconds tail after burst ends
    float _env;                  // current envelope value [0..1]

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        _src = GetComponent<AudioSource>();
        _src.playOnAwake = false;
        _src.loop        = true;   // we drive output manually in OnAudioFilterRead
        _src.volume      = 1f;
        _src.spatialBlend = 0f;    // 2D — radio is "in ear"
        _sampleRate = AudioSettings.outputSampleRate;
    }

    /// Trigger a burst of radio static (click + static + click).
    public void Play() => Play(_burstLength);

    public void Play(float duration)
    {
        if (!_src.isPlaying) _src.Play();
        // Extend or start a new burst
        _remaining = Mathf.Max(_remaining, duration);
    }

    public void Stop()
    {
        _remaining = 0f;
    }

    // Called by Unity audio thread — generates samples directly.
    void OnAudioFilterRead(float[] data, int channels)
    {
        if (_remaining <= 0f && _env <= 0.001f)
        {
            // Silence: clear buffer and return
            for (int i = 0; i < data.Length; i++) data[i] = 0f;
            return;
        }

        float dt          = 1f / _sampleRate;
        float attackRate  = dt / Mathf.Max(_envAttack,  0.001f);
        float releaseRate = dt / Mathf.Max(_envRelease, 0.001f);

        // Band-pass coefficients (one-pole IIR)
        // lpA passes ~0–3000 Hz, lpB passes ~0–300 Hz → difference is ~300–3000 Hz band
        float alphaA = 1f - Mathf.Exp(-2f * Mathf.PI * 3000f * dt);
        float alphaB = 1f - Mathf.Exp(-2f * Mathf.PI *  300f * dt);

        int samplesPerChannel = data.Length / channels;

        for (int s = 0; s < samplesPerChannel; s++)
        {
            // Envelope
            if (_remaining > 0f)
            {
                _env = Mathf.Min(1f, _env + attackRate);
                _remaining -= dt;
            }
            else
            {
                _env = Mathf.Max(0f, _env - releaseRate);
            }

            // White noise → band-pass filter
            float noise = (float)(_rng.NextDouble() * 2.0 - 1.0);
            _lpA += alphaA * (noise - _lpA);
            _lpB += alphaB * (noise - _lpB);
            float bandNoise = (_lpA - _lpB) * _volume * _env;

            // Slight crackle: occasional amplitude spike
            if (_rng.NextDouble() < 0.003)
                bandNoise *= 1.5f + (float)(_rng.NextDouble() * 1.5);

            bandNoise = Mathf.Clamp(bandNoise, -1f, 1f);

            for (int c = 0; c < channels; c++)
                data[s * channels + c] = bandNoise;
        }
    }
}
