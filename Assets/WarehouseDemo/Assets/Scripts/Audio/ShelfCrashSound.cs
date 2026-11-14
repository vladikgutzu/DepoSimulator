using UnityEngine;

/// Procedural shelf-crash sound — generated into an AudioClip buffer at startup.
/// Self-instantiates via RuntimeInitializeOnLoadMethod; no prefab or audio file needed.
/// Call Play(worldPos) for a 3D one-shot at the crash location.
public class ShelfCrashSound : MonoBehaviour
{
    public static ShelfCrashSound Instance { get; private set; }

    [SerializeField] [Range(0f, 1f)] float _volume = 0.9f;

    AudioClip _clip;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoCreate()
    {
        if (Instance != null) return;
        var go = new GameObject("[ShelfCrashSound]");
        DontDestroyOnLoad(go);
        go.AddComponent<ShelfCrashSound>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        _clip = BuildCrashClip();
    }

    public void Play(Vector3 worldPosition)
    {
        if (_clip == null) return;
        AudioSource.PlayClipAtPoint(_clip, worldPosition, _volume);
    }

    // ── Clip generation ───────────────────────────────────────────────────────

    static AudioClip BuildCrashClip()
    {
        const int   SampleRate = 44100;
        const float Duration   = 2.2f;
        int         total      = (int)(SampleRate * Duration);

        float[] data = new float[total];

        // Deterministic RNG so the clip is always the same (seed = 0xCAFE).
        var rng = new System.Random(0xCAFE);

        // Band-pass filter states: two one-pole low-pass, difference = band
        // lp800 - lp150 → metallic resonance band ~150–800 Hz
        float lp800 = 0f, lp150 = 0f;
        float a800  = 1f - Mathf.Exp(-2f * Mathf.PI * 800f  / SampleRate);
        float a150  = 1f - Mathf.Exp(-2f * Mathf.PI * 150f  / SampleRate);

        // Second band for higher-freq metal ring ~800–3000 Hz
        float lpH = 0f, lpM = 0f;
        float aH  = 1f - Mathf.Exp(-2f * Mathf.PI * 3000f / SampleRate);
        float aM  = 1f - Mathf.Exp(-2f * Mathf.PI *  800f / SampleRate);

        for (int i = 0; i < total; i++)
        {
            float t     = (float)i / SampleRate;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);

            // Low-freq metallic bang (150–800 Hz)
            lp800 += a800 * (noise - lp800);
            lp150 += a150 * (noise - lp150);
            float metalLow = (lp800 - lp150);

            // High-freq ring / scrape (800–3000 Hz)
            lpH += aH * (noise - lpH);
            lpM += aM * (noise - lpM);
            float metalHigh = (lpH - lpM);

            // ── Envelopes ────────────────────────────────────────────────────
            // Impact: sharp peak at t=0, decays in ~80 ms
            float impactEnv = Mathf.Exp(-t * 40f);

            // Body crash: peaks around 80 ms, decays over ~600 ms
            float bodyEnv = (t < 0.08f)
                ? t / 0.08f
                : Mathf.Exp(-(t - 0.08f) * 5f);

            // Rattle tail: random metallic debris, 0.2–1.8 s
            float rattleEnv = 0f;
            if (t > 0.2f && t < 1.8f)
                rattleEnv = Mathf.Exp(-(t - 0.2f) * 2.5f);

            // Occasional rattle spikes (random debris clinks)
            float spike = rng.NextDouble() < 0.015
                ? (float)(rng.NextDouble() * 2.0 - 1.0) * rattleEnv * 0.35f
                : 0f;

            // ── Mix ──────────────────────────────────────────────────────────
            float sample =
                noise     * impactEnv  * 0.80f +   // raw impact thump
                metalLow  * bodyEnv    * 0.55f +   // low metallic crash body
                metalHigh * bodyEnv    * 0.30f +   // high-freq ring
                metalLow  * rattleEnv  * 0.20f +   // rattle tail
                spike;

            data[i] = Mathf.Clamp(sample, -1f, 1f);
        }

        var clip = AudioClip.Create("ShelfCrash", total, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
