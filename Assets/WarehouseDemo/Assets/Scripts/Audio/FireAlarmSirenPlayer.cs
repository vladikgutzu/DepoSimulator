using UnityEngine;
using UnityEngine.SceneManagement;

/// Warehouse fire-alarm siren. Plays a real AudioClip (set via SetClip, e.g. from
/// GameEventManager.FireAlarmSirenClip) looping if one is assigned; otherwise falls back
/// to a procedurally synthesized wailing tone (frequency-modulated sine via
/// OnAudioFilterRead, same pattern as RadioStaticPlayer's procedural noise) so the alarm
/// still works even without an audio asset. Self-instantiates at runtime.
[RequireComponent(typeof(AudioSource))]
public class FireAlarmSirenPlayer : MonoBehaviour
{
    public static FireAlarmSirenPlayer Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoCreate()
    {
        if (Instance != null) return;
        var go = new GameObject("[FireAlarmSirenPlayer]");
        DontDestroyOnLoad(go);
        go.AddComponent<FireAlarmSirenPlayer>();
    }

    [SerializeField] [Range(0f, 1f)] float _volume     = 0.3f;
    [SerializeField] float _minFreq     = 500f; // Hz — low point of the wail
    [SerializeField] float _maxFreq     = 950f; // Hz — high point of the wail
    [SerializeField] float _sweepPeriod = 1.4f; // seconds per low->high->low cycle

    const float EnvAttack  = 0.15f; // seconds to fade in when Play() is called
    const float EnvRelease = 0.3f;  // seconds to fade out when Stop() is called

    AudioSource _src;
    AudioClip   _clip;  // real asset, if provided — takes priority over synthesis

    // Whether a clip is assigned, as a plain bool the audio thread may read.
    //
    // Comparing _clip against null looks harmless but is not: UnityEngine.Object's == asks the
    // engine whether the native object is still alive, and that is main-thread only. Doing it in
    // OnAudioFilterRead threw once per audio buffer — a steady stream of errors for the whole
    // session. Same reason RadioVoiceDistortionFilter caches its sample rate instead of reading it.
    volatile bool _hasClip;
    double _sampleRate;
    double _phase;      // running phase of the audible tone (radians)
    double _sweepPhase; // running phase of the frequency-sweep LFO (radians)
    bool   _playing;
    float  _env;        // current envelope value [0..1] — read/written on audio thread only

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        _src = GetComponent<AudioSource>();
        _src.playOnAwake  = false;
        _src.loop         = true; // clip mode loops normally; synthesis mode is driven manually
        _src.volume       = 1f;
        _src.spatialBlend = 0f;   // 2D — alarm is ambient, not positional
        _sampleRate = AudioSettings.outputSampleRate;

        // This object survives scene loads. Its playback state must not: otherwise leaving a
        // shift while an alarm is active carries the siren into the menu and the next new game.
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (Instance == this) Instance = null;
    }

    void OnSceneLoaded(Scene _, LoadSceneMode __)
    {
        Stop();
        SetClip(null);
        _env = 0f;
        _phase = 0d;
        _sweepPhase = 0d;
    }

    /// Assign a real siren clip to use instead of the procedural fallback. Pass null to
    /// go back to synthesis.
    public void SetClip(AudioClip clip)
    {
        _clip     = clip;
        _hasClip  = clip != null;   // on the main thread, where asking is allowed
        _src.clip = clip;
    }

    public void Play()
    {
        _playing = true;
        if (_clip != null) _src.volume = _volume;
        if (!_src.isPlaying) _src.Play();
    }

    public void Stop()
    {
        _playing = false;
        if (_clip != null) _src.Stop(); // synthesis mode fades out on its own via _env
    }

    // Called by Unity's audio thread. With a real clip assigned, the buffer already holds
    // its samples — leave it untouched. Otherwise generate the synthesized wail directly.
    void OnAudioFilterRead(float[] data, int channels)
    {
        if (_hasClip) return;

        if (!_playing && _env <= 0.0001f)
        {
            for (int i = 0; i < data.Length; i++) data[i] = 0f;
            return;
        }

        double dt = 1.0 / _sampleRate;
        float attackRate  = (float)(dt / EnvAttack);
        float releaseRate = (float)(dt / EnvRelease);
        double sweepFreq  = 1.0 / _sweepPeriod;

        int samplesPerChannel = data.Length / channels;
        for (int s = 0; s < samplesPerChannel; s++)
        {
            _env = _playing
                ? Mathf.Min(1f, _env + attackRate)
                : Mathf.Max(0f, _env - releaseRate);

            // Smooth sine LFO sweeps the tone's frequency between min/max — the classic wail.
            _sweepPhase += 2.0 * System.Math.PI * sweepFreq * dt;
            if (_sweepPhase > 2.0 * System.Math.PI) _sweepPhase -= 2.0 * System.Math.PI;
            double lfo  = (System.Math.Sin(_sweepPhase) + 1.0) * 0.5; // 0..1
            double freq = _minFreq + (_maxFreq - _minFreq) * lfo;

            // Integrate phase from instantaneous frequency (not sin(freq*t) directly) so
            // the pitch changes smoothly with no clicks/discontinuities.
            _phase += 2.0 * System.Math.PI * freq * dt;
            if (_phase > 2.0 * System.Math.PI) _phase -= 2.0 * System.Math.PI;

            float sample = (float)System.Math.Sin(_phase) * _volume * _env;
            for (int c = 0; c < channels; c++)
                data[s * channels + c] = sample;
        }
    }
}
