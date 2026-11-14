using UnityEngine;

/// The sounds a steel roller shutter makes, synthesised once and shared by every gate.
///
/// Procedural for the same reason ShelfCrashSound is: no file to source, license or ship, and the
/// three sounds a gate needs are all noise pushed through filters and envelopes. Built once into
/// AudioClip buffers on the main thread — deliberately not OnAudioFilterRead, which runs on the
/// audio thread where touching a UnityEngine.Object throws. FireAlarmSirenPlayer was doing exactly
/// that and producing an exception per buffer.
public static class GateSfx
{
    const int SampleRate = 44100;

    /// Three of them, because a gate struck eighteen times with one clip is a machine, not a crowd.
    const int ImpactVariants = 3;

    static AudioClip[] _impacts;
    static AudioClip   _rattle;
    static AudioClip   _roller;

    /// Built at load so the cost lands behind the loading screen rather than on the frame a raid
    /// starts. EnsureBuilt still guards every accessor: the attribute fires once per process, and
    /// NGO loads the scene again underneath it — the same trap the scene id pass fell into.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Prewarm() => EnsureBuilt();

    public static AudioClip Impact(int variant)
    {
        EnsureBuilt();
        return _impacts[Mathf.Abs(variant) % ImpactVariants];
    }

    public static AudioClip Rattle { get { EnsureBuilt(); return _rattle; } }
    public static AudioClip Roller { get { EnsureBuilt(); return _roller; } }

    static void EnsureBuilt()
    {
        // Compared against null rather than cached in a bool: a clip nothing in a scene references
        // can be collected by UnloadUnusedAssets, leaving the static field holding a dead object.
        if (_impacts != null && _impacts[0] != null && _rattle != null && _roller != null) return;

        _impacts = new AudioClip[ImpactVariants];
        for (int i = 0; i < ImpactVariants; i++)
            _impacts[i] = BuildImpact(0x6A7E + i * 977);

        _rattle = BuildRattle(0x1D3F);
        _roller = BuildRoller(0x51C2);
    }

    // ── Impact ────────────────────────────────────────────────────────────────

    /// A body hitting a corrugated shutter: a slap, the panel booming like a drumhead, and the
    /// slats rattling in their guides for a second afterwards.
    ///
    /// The boom is what makes it read as a big sheet of steel rather than as a door. It is two
    /// decaying partials rather than filtered noise, because a panel that size rings at a pitch,
    /// and noise alone came out as a bin lid.
    static AudioClip BuildImpact(int seed)
    {
        const float Duration = 1.6f;
        int total = (int)(SampleRate * Duration);

        var data = new float[total];
        var rng  = new System.Random(seed);

        // Band-pass pairs, same one-pole difference trick ShelfCrashSound uses.
        float lpHi = 0f, lpLo = 0f;                                   // 250..1200, the metal body
        float aHi  = Coef(1200f), aLo = Coef(250f);

        float lpSlatHi = 0f, lpSlatLo = 0f;                           // 900..5000, the slats
        float aSlatHi  = Coef(5000f), aSlatLo = Coef(900f);

        // The two ringing partials. Detuned per variant so the three do not sound like one clip
        // played three times.
        float ringA = 64f  + (float)rng.NextDouble() * 14f;
        float ringB = 173f + (float)rng.NextDouble() * 40f;

        for (int i = 0; i < total; i++)
        {
            float t     = (float)i / SampleRate;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);

            lpHi += aHi * (noise - lpHi);
            lpLo += aLo * (noise - lpLo);
            float body = lpHi - lpLo;

            lpSlatHi += aSlatHi * (noise - lpSlatHi);
            lpSlatLo += aSlatLo * (noise - lpSlatLo);
            float slats = lpSlatHi - lpSlatLo;

            // The slap itself: broadband, gone in 30 ms.
            float slap = Mathf.Exp(-t * 55f);

            // The panel booming. Drops a little in pitch as it decays, the way a struck sheet does.
            float boomEnv = Mathf.Exp(-t * 6.5f);
            float sag     = 1f - 0.10f * (1f - boomEnv);
            float boom    = Mathf.Sin(2f * Mathf.PI * ringA * sag * t) * 0.60f
                          + Mathf.Sin(2f * Mathf.PI * ringB * sag * t) * 0.28f;

            // Metal body, under the boom and shorter.
            float bodyEnv = t < 0.012f ? t / 0.012f : Mathf.Exp(-(t - 0.012f) * 11f);

            // Slats knocking about in their guides, well after the blow.
            float rattleEnv = t > 0.03f ? Mathf.Exp(-(t - 0.03f) * 3.2f) : 0f;

            // Individual slats catching, rather than a smooth hiss. Sparse on purpose: at one
            // chance in a hundred samples this fires four hundred times a second, which is not a
            // handful of slats knocking, it is white noise with extra steps.
            float clack = rng.NextDouble() < 0.0006
                ? (float)(rng.NextDouble() * 2.0 - 1.0) * rattleEnv * 0.5f
                : 0f;

            float sample =
                noise * slap      * 0.55f +
                boom  * boomEnv   * 0.75f +
                body  * bodyEnv   * 0.45f +
                slats * rattleEnv * 0.16f +
                clack;

            data[i] = Mathf.Clamp(sample, -1f, 1f);
        }

        return Finish(data, $"GateImpact{seed:X}", total);
    }

    // ── Rattle ────────────────────────────────────────────────────────────────

    /// The continuous strain of a crowd leaning on the shutter, under the individual blows.
    ///
    /// Loops, so the ends are crossfaded into one another — a seam in a sound playing for twenty
    /// seconds straight is a tick once a second and impossible to unhear.
    static AudioClip BuildRattle(int seed)
    {
        const float Duration = 2f;
        int total = (int)(SampleRate * Duration);

        var data = new float[total];
        var rng  = new System.Random(seed);

        float lpHi = 0f, lpLo = 0f;
        float aHi  = Coef(2600f), aLo = Coef(320f);

        for (int i = 0; i < total; i++)
        {
            float t     = (float)i / SampleRate;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);

            lpHi += aHi * (noise - lpHi);
            lpLo += aLo * (noise - lpLo);
            float band = lpHi - lpLo;

            // Uneven pressure: several slow swells rather than a flat hiss.
            float swell = 0.55f
                        + 0.25f * Mathf.Sin(2f * Mathf.PI * 1.5f * t)
                        + 0.20f * Mathf.Sin(2f * Mathf.PI * 3.7f * t + 1.1f);

            // Same reasoning as the impact: a dozen a second reads as metal, two hundred as hiss.
            float clack = rng.NextDouble() < 0.0004
                ? (float)(rng.NextDouble() * 2.0 - 1.0) * 0.55f
                : 0f;

            data[i] = Mathf.Clamp(band * swell * 0.5f + clack, -1f, 1f);
        }

        CrossfadeLoop(data, (int)(SampleRate * 0.12f));

        return Finish(data, "GateRattle", total);
    }

    // ── Roller ────────────────────────────────────────────────────────────────

    /// The shutter travelling: a motor under a steady clatter of slats going over the drum.
    /// Runs a shade longer than GateAnimator's 2.2 s of travel so it does not stop before the gate.
    static AudioClip BuildRoller(int seed)
    {
        const float Duration = 2.6f;
        int total = (int)(SampleRate * Duration);

        var data = new float[total];
        var rng  = new System.Random(seed);

        float lpHi = 0f, lpLo = 0f;                    // rumble
        float aHi  = Coef(220f), aLo = Coef(45f);

        float lpCHi = 0f, lpCLo = 0f;                  // slat clatter
        float aCHi  = Coef(3400f), aCLo = Coef(1100f);

        // Slats passing the guide, about fourteen a second.
        const float SlatRate = 14f;
        float nextSlat = 0f;
        float slatEnv  = 0f;

        for (int i = 0; i < total; i++)
        {
            float t     = (float)i / SampleRate;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);

            lpHi += aHi * (noise - lpHi);
            lpLo += aLo * (noise - lpLo);
            float rumble = lpHi - lpLo;

            lpCHi += aCHi * (noise - lpCHi);
            lpCLo += aCLo * (noise - lpCLo);
            float clatter = lpCHi - lpCLo;

            // Jittered, or fourteen a second is a buzz at a pitch rather than a mechanism.
            if (t >= nextSlat)
            {
                slatEnv  = 1f;
                nextSlat = t + (1f / SlatRate) * (0.82f + (float)rng.NextDouble() * 0.36f);
            }
            slatEnv *= 0.9986f;

            float motor = Mathf.Sin(2f * Mathf.PI * 118f * t) * 0.22f
                        + Mathf.Sin(2f * Mathf.PI * 236f * t) * 0.09f;

            // Spins up and coasts down at the ends.
            float env = Mathf.Min(Mathf.Clamp01(t / 0.18f),
                                  Mathf.Clamp01((Duration - t) / 0.30f));

            float sample = (rumble  * 0.55f
                          + motor   * env
                          + clatter * slatEnv * 0.30f) * env;

            data[i] = Mathf.Clamp(sample, -1f, 1f);
        }

        return Finish(data, "GateRoller", total);
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    /// One-pole coefficient for a cutoff in Hz.
    static float Coef(float hz) => 1f - Mathf.Exp(-2f * Mathf.PI * hz / SampleRate);

    /// Folds the tail over the head so the clip meets itself without a click.
    static void CrossfadeLoop(float[] data, int fade)
    {
        if (fade <= 0 || fade * 2 >= data.Length) return;

        for (int i = 0; i < fade; i++)
        {
            float k = (float)i / fade;
            data[i] = data[i] * k + data[data.Length - fade + i] * (1f - k);
        }
    }

    /// Normalises to a predictable peak, so mixing these against each other is about the numbers
    /// in GateAudio rather than about whatever the synthesis happened to come out at.
    static AudioClip Finish(float[] data, string name, int total)
    {
        float peak = 0f;
        for (int i = 0; i < total; i++) peak = Mathf.Max(peak, Mathf.Abs(data[i]));

        if (peak > 0.0001f)
        {
            float gain = 0.92f / peak;
            for (int i = 0; i < total; i++) data[i] *= gain;
        }

        var clip = AudioClip.Create(name, total, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
