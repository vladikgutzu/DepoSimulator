using UnityEngine;

/// <summary>
/// Short procedural metal-latch confirmation used when forks successfully secure a pallet.
/// The clip is generated once so both vehicle types work without prefab or scene references.
/// </summary>
public static class PalletLiftSfx
{
    const int SampleRate = 44100;
    const float Duration = 0.12f;

    static AudioClip _clip;

    public static void Play(Vector3 worldPosition)
    {
        if (_clip == null)
            _clip = BuildClip();

        var holder = new GameObject("Pallet Lift Latch SFX");
        holder.transform.position = worldPosition;

        var source = holder.AddComponent<AudioSource>();
        source.clip = _clip;
        source.playOnAwake = false;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 1.5f;
        source.maxDistance = 16f;
        source.volume = 0.72f;
        source.pitch = Random.Range(0.97f, 1.03f);
        source.Play();

        Object.Destroy(holder, Duration / Mathf.Abs(source.pitch) + 0.1f);
    }

    static AudioClip BuildClip()
    {
        int sampleCount = Mathf.CeilToInt(Duration * SampleRate);
        var samples = new float[sampleCount];
        uint noiseState = 0x6D2B79F5u;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)SampleRate;

            // A sharp latch impact followed by two quickly damped metal resonances.
            float attack = Mathf.Min(1f, t / 0.0015f);
            float impactEnvelope = attack * Mathf.Exp(-t * 72f);
            float ringEnvelope = attack * Mathf.Exp(-t * 34f);

            noiseState = noiseState * 1664525u + 1013904223u;
            float noise = ((noiseState >> 8) / 8388607.5f) - 1f;

            float impact = noise * impactEnvelope * 0.42f;
            float lowBody = Mathf.Sin(2f * Mathf.PI * 310f * t) * impactEnvelope * 0.35f;
            float metal = (
                Mathf.Sin(2f * Mathf.PI * 1680f * t)
                + Mathf.Sin(2f * Mathf.PI * 2470f * t) * 0.55f) * ringEnvelope * 0.30f;

            samples[i] = Mathf.Clamp((impact + lowBody + metal) * 0.82f, -1f, 1f);
        }

        var clip = AudioClip.Create("PalletLiftLatch", sampleCount, 1, SampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
