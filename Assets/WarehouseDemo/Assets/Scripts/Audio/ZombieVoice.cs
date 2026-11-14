using UnityEngine;

/// The raider's voice and the meat of his fists: an idle growl on a timer, a roar when he
/// swings, a thud when a blow lands on him, and a rattling groan when he goes down.
///
/// Same shape as FireAlarmSirenPlayer — assign real clips and they win; leave them empty and
/// the sounds are synthesized at runtime, so a raid is never silent just because no audio
/// asset has been hooked up yet. ThiefController adds this itself, so it also works on a
/// prefab that was built before the component existed.
[RequireComponent(typeof(AudioSource))]
public class ZombieVoice : MonoBehaviour
{
    [Header("Clips (optional — synthesized when empty)")]
    [SerializeField] AudioClip[] _growls  = new AudioClip[0];
    [SerializeField] AudioClip[] _attacks = new AudioClip[0];
    [SerializeField] AudioClip[] _hits    = new AudioClip[0];
    [SerializeField] AudioClip   _death;

    [Header("Mix")]
    [SerializeField] [Range(0f, 1f)] float _volume     = 0.75f;
    [SerializeField] float _hearingRange = 26f;

    [Header("Idle growling")]
    [Tooltip("Seconds of silence between growls while he is going about his business. The gap " +
             "after a growl finishes, not the period — see ScheduleGrowl.")]
    [SerializeField] Vector2 _growlInterval    = new Vector2(5f, 11f);
    [Tooltip("Silence between growls once he is fighting — he gets louder about it.")]
    [SerializeField] Vector2 _aggressiveInterval = new Vector2(2f, 4f);

    /// Set by ThiefController: a zombie in a fight growls more often.
    public bool Aggressive { get; set; }

    AudioSource _src;
    float       _nextGrowl;
    bool        _silenced;   // after death: nothing more comes out of him

    // Synthesized fallbacks are built once and shared by every raider in the scene.
    static AudioClip[] _synthGrowls;
    static AudioClip[] _synthAttacks;
    static AudioClip[] _synthHits;
    static AudioClip   _synthDeath;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetStatics()
    {
        _synthGrowls = _synthAttacks = _synthHits = null;
        _synthDeath  = null;
    }

    void Awake()
    {
        _src = GetComponent<AudioSource>();
        _src.playOnAwake   = false;
        _src.loop          = false;
        _src.spatialBlend  = 1f;                       // positional: you hear where he is
        _src.rolloffMode   = AudioRolloffMode.Linear;
        _src.minDistance   = 2.5f;
        _src.maxDistance   = _hearingRange;
        _src.dopplerLevel  = 0f;

        ScheduleGrowl();
    }

    void Update()
    {
        if (_silenced) return;

        _nextGrowl -= Time.deltaTime;
        if (_nextGrowl > 0f) return;

        Growl();
        ScheduleGrowl();
    }

    /// The interval is a gap *after* the growl, not a period.
    ///
    /// Play uses PlayOneShot, so a growl longer than the interval does not cut its predecessor off,
    /// it stacks on top of it. The recorded growls run 5 to 8 seconds against an aggressive
    /// interval of 2 to 4, which had one raider talking over himself three times at once. Adding
    /// the clip's own length means the tuning values mean what their names say whatever is
    /// assigned, instead of being silently coupled to how long somebody's recording happens to be.
    void ScheduleGrowl()
    {
        var range = Aggressive ? _aggressiveInterval : _growlInterval;
        _nextGrowl = _lastClipLength + Random.Range(range.x, range.y);
    }

    float _lastClipLength;

    // ── The four sounds ───────────────────────────────────────────────────────

    public void Growl()
    {
        if (_silenced) return;
        Play(Pick(_growls, SynthGrowls()), Aggressive ? 1f : 0.8f);
    }

    /// Played as he swings — the roar is the audible half of the wind-up telegraph.
    public void Attack()
    {
        if (_silenced) return;
        Play(Pick(_attacks, SynthAttacks()), 1f);

        // No idle growl right on top of the roar.
        _nextGrowl = Mathf.Max(_nextGrowl, 1.5f);
    }

    /// A blow connecting — used both when he takes one and when he lands one.
    public void Hit()
    {
        if (_silenced) return;
        Play(Pick(_hits, SynthHits()), 1f);
    }

    public void Death()
    {
        if (_silenced) return;
        Play(_death != null ? _death : SynthDeath(), 1f);
        _silenced = true;   // the growl timer stops with him
    }

    void Play(AudioClip clip, float scale)
    {
        if (clip == null || _src == null) return;

        float pitch = Random.Range(0.9f, 1.1f);
        _src.pitch = pitch;
        _src.PlayOneShot(clip, _volume * scale);

        // Divided by pitch because that is what actually stretches it: played at 0.9 an eight
        // second growl runs for nearly nine.
        _lastClipLength = clip.length / Mathf.Max(0.01f, pitch);
    }

    static AudioClip Pick(AudioClip[] authored, AudioClip[] fallback)
    {
        var set = authored != null && authored.Length > 0 ? authored : fallback;
        if (set == null || set.Length == 0) return null;

        // An authored array with holes in it should not produce silence.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var clip = set[Random.Range(0, set.Length)];
            if (clip != null) return clip;
        }
        return null;
    }

    // ── Synthesis ─────────────────────────────────────────────────────────────
    //
    // The Synth* builders are public so Tools → Warehouse Demo → Export Zombie Voice Clips
    // can render them to .wav — the only way to actually listen to them before deciding
    // whether to keep them or drop real recordings into the clip slots above.
    //
    // Growls are a low harmonic tone (the voice) plus low-passed noise (the rasp), chopped
    // by a slow amplitude modulation — that flutter is what makes a drone read as a growl.
    // Impacts are a short pitch-dropping thump under a burst of noise.

    const int SampleRate = 44100;

    public static AudioClip[] SynthGrowls()
    {
        if (_synthGrowls != null) return _synthGrowls;

        _synthGrowls = new[]
        {
            BuildVoice("ZombieGrowlA", seed: 11, length: 1.9f, startHz: 74f, endHz: 68f, rattleHz: 27f, rasp: 0.45f),
            BuildVoice("ZombieGrowlB", seed: 27, length: 2.3f, startHz: 62f, endHz: 71f, rattleHz: 22f, rasp: 0.55f),
            BuildVoice("ZombieGrowlC", seed: 43, length: 1.5f, startHz: 88f, endHz: 79f, rattleHz: 31f, rasp: 0.40f),
        };
        return _synthGrowls;
    }

    public static AudioClip[] SynthAttacks()
    {
        if (_synthAttacks != null) return _synthAttacks;

        // Higher, and rising instead of sagging: a lunge rather than a mutter.
        _synthAttacks = new[]
        {
            BuildVoice("ZombieRoarA", seed: 61, length: 1.1f, startHz: 96f, endHz: 132f, rattleHz: 34f, rasp: 0.65f, bite: true),
            BuildVoice("ZombieRoarB", seed: 83, length: 1.3f, startHz: 108f, endHz: 148f, rattleHz: 29f, rasp: 0.70f, bite: true),
        };
        return _synthAttacks;
    }

    public static AudioClip[] SynthHits()
    {
        if (_synthHits != null) return _synthHits;

        _synthHits = new[]
        {
            BuildImpact("ZombieHitA", seed: 5,  thumpHz: 92f),
            BuildImpact("ZombieHitB", seed: 19, thumpHz: 78f),
        };
        return _synthHits;
    }

    public static AudioClip SynthDeath()
    {
        if (_synthDeath != null) return _synthDeath;

        // A long groan sliding down to nothing.
        _synthDeath = BuildVoice("ZombieDeath", seed: 97, length: 2.4f,
                                 startHz: 92f, endHz: 44f, rattleHz: 18f, rasp: 0.75f);
        return _synthDeath;
    }

    /// One vocalisation: harmonic tone + rasp noise + rattle AM, under a soft envelope.
    static AudioClip BuildVoice(string name, int seed, float length, float startHz, float endHz,
                                float rattleHz, float rasp, bool bite = false)
    {
        int   count   = Mathf.RoundToInt(SampleRate * length);
        var   data    = new float[count];
        var   rng     = new System.Random(seed);
        double phase  = 0.0;
        float  noiseLp = 0f;
        float  peak    = 0f;

        // A hard-edged start for a roar, an easing one for a mutter.
        float attack = bite ? 0.03f : 0.18f;
        float releas = bite ? 0.35f : 0.45f;

        for (int i = 0; i < count; i++)
        {
            float t = i / (float)SampleRate;
            float u = t / length;

            float hz = Mathf.Lerp(startHz, endHz, u) * (1f + 0.05f * Mathf.Sin(2f * Mathf.PI * 5.2f * t));
            phase += 2.0 * Mathf.PI * hz / SampleRate;

            float p = (float)phase;
            float tone = Mathf.Sin(p) * 0.60f
                       + Mathf.Sin(p * 2f) * 0.26f
                       + Mathf.Sin(p * 3f) * 0.14f
                       + Mathf.Sin(p * 5f) * 0.07f;

            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            noiseLp += (noise - noiseLp) * 0.10f;       // one-pole low-pass: breath, not hiss

            float rattle = 0.60f + 0.40f * Mathf.Sin(2f * Mathf.PI * rattleHz * t);
            float env    = Envelope(u, attack, releas);

            float s = (tone * (1f - rasp * 0.5f) + noiseLp * rasp * 2.2f) * rattle * env;
            data[i] = s;
            peak = Mathf.Max(peak, Mathf.Abs(s));
        }

        return Finish(name, data, peak);
    }

    /// A fist landing: a short thump whose pitch drops away, with a noise crack on top.
    static AudioClip BuildImpact(string name, int seed, float thumpHz)
    {
        const float Length = 0.34f;

        int   count = Mathf.RoundToInt(SampleRate * Length);
        var   data  = new float[count];
        var   rng   = new System.Random(seed);
        double phase = 0.0;
        float  peak  = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = i / (float)SampleRate;

            float hz = thumpHz * Mathf.Lerp(1f, 0.45f, t / Length);
            phase += 2.0 * Mathf.PI * hz / SampleRate;

            float body  = Mathf.Sin((float)phase) * Mathf.Exp(-t * 16f);
            float crack = (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-t * 42f);

            float s = body * 0.85f + crack * 0.55f;
            data[i] = s;
            peak = Mathf.Max(peak, Mathf.Abs(s));
        }

        return Finish(name, data, peak);
    }

    /// Fade in, hold, fade out — u is normalized position through the clip.
    static float Envelope(float u, float attack, float release)
    {
        if (u < attack)         return u / attack;
        if (u > 1f - release)   return (1f - u) / release;
        return 1f;
    }

    static AudioClip Finish(string name, float[] data, float peak)
    {
        if (peak > 0.0001f)
        {
            float gain = 0.9f / peak;
            for (int i = 0; i < data.Length; i++) data[i] *= gain;
        }

        var clip = AudioClip.Create(name, data.Length, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
