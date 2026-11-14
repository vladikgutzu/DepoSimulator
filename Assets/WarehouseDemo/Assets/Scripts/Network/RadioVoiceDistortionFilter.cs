using UnityEngine;

/// Lo-fi "voice-over-radio" distortion applied to ONE remote player's decoded Vivox audio
/// while RadioInterferenceEvent is active — band-narrowing (radio-speaker timbre) + soft-clip
/// crunch + a touch of crackle. Attach to the same GameObject as a VivoxParticipantTap's
/// AudioSource (see VivoxManager.OnParticipantAdded): VivoxParticipantTap writes the decoded
/// voice into that AudioSource's clip and plays it, so Unity runs OnAudioFilterRead on every
/// component of this GameObject as part of that AudioSource's own DSP chain — no AudioMixer
/// asset needed, this processes the already-decoded PCM directly.
///
/// Runs on Unity's audio thread, not the main thread: no GC allocation, no calls into
/// UnityEngine.Random/GameEventManager/Vivox APIs here. VivoxManager polls
/// GameEventManager.IsRadioInterfering on the main thread and pushes it in via Distorting —
/// a plain bool set/read across threads is safe for this single-flag handoff (no torn reads).
public class RadioVoiceDistortionFilter : MonoBehaviour
{
    [Header("Band narrowing (radio speaker timbre)")]
    [Range(0.05f, 1f)] [SerializeField] float _lowpassAlpha  = 0.14f; // lower = darker/muffled
    [Range(0.5f, 1f)]  [SerializeField] float _highpassAlpha = 0.97f; // lower = thinner/tinnier

    [Header("Crunch")]
    [SerializeField] float _driveAmount   = 14f;   // soft-clip waveshaper intensity
    [SerializeField] float _crackleAmount = 0.16f; // additive noise level while wet

    [Header("Breaking up")]
    //
    // Timbre alone is not interference. A voice can be as tinny as you like and still be followed
    // word for word — and being understood is exactly what this event is supposed to take away.
    // What costs intelligibility is losing pieces of words, so the signal is chopped: it drops out
    // for a fraction of a second at a time, and what is left comes through squashed.
    [Tooltip("How often the signal drops out, in times per second.")]
    [SerializeField] float _dropoutsPerSecond = 3.2f;
    [Tooltip("How long one dropout lasts, in seconds.")]
    [SerializeField] float _dropoutLength     = 0.16f;
    [Tooltip("What is left of the voice during a dropout: 0 is silence, 1 is untouched.")]
    [Range(0f, 1f)] [SerializeField] float _dropoutFloor = 0.06f;

    [Header("Transition")]
    [SerializeField] float _mixRampPerSecond = 4f; // wet/dry crossfade speed, avoids clicks

    // Set from the main thread (VivoxManager), read on the audio thread.
    volatile bool _distorting;
    public bool Distorting { set => _distorting = value; }

    // Audio-thread-only state.
    float _wet;
    float _dropoutTimer;      // counts down the gap currently being cut out
    float _nextDropoutIn;     // and the wait until the next one
    float _lpState;
    float _hpPrevIn;
    float _hpPrevOut;
    uint  _noiseState = 0x9E3779B9u;

    // Cached on the main thread: AudioSettings.outputSampleRate is main-thread-only, and
    // reading it from OnAudioFilterRead threw "GetSampleRate can only be called from the main
    // thread" once per audio buffer -- an exception with a stack trace dozens of times a
    // second, on the audio thread, for the whole time a voice tap existed. Same pattern the
    // other synthesis filters already use (FireAlarmSirenPlayer, RadioStaticPlayer).
    int _sampleRate = 48000;

    void Awake() => _sampleRate = AudioSettings.outputSampleRate;

    void OnAudioFilterRead(float[] data, int channels)
    {
        if (channels <= 0) return;

        float wetTarget  = _distorting ? 1f : 0f;
        float wetStep    = _mixRampPerSecond / Mathf.Max(1, _sampleRate);

        float secondsPerFrame = 1f / Mathf.Max(1, _sampleRate);
        float dropoutGap      = _dropoutsPerSecond > 0.01f ? 1f / _dropoutsPerSecond : 999f;

        int frames = data.Length / channels;
        for (int i = 0; i < frames; i++)
        {
            _wet = Mathf.MoveTowards(_wet, wetTarget, wetStep);
            if (_wet <= 0f) continue; // fully dry — leave this frame's samples untouched

            // The signal cutting in and out. Counted in samples rather than driven from the main
            // thread, because this loop is all the clock the audio thread has.
            float gate = 1f;
            if (_dropoutTimer > 0f)
            {
                _dropoutTimer -= secondsPerFrame;
                gate = _dropoutFloor;
            }
            else
            {
                _nextDropoutIn -= secondsPerFrame;
                if (_nextDropoutIn <= 0f)
                {
                    // Uneven on purpose: a dropout every exactly-so often reads as a tremolo
                    // effect, and what this is imitating is a signal, not an effect.
                    _noiseState ^= _noiseState << 13;
                    _noiseState ^= _noiseState >> 17;
                    _noiseState ^= _noiseState << 5;
                    float r = _noiseState / (float)uint.MaxValue;

                    _dropoutTimer  = _dropoutLength * (0.5f + r);
                    _nextDropoutIn = dropoutGap * (0.5f + r);
                }
            }

            for (int c = 0; c < channels; c++)
            {
                int   idx = i * channels + c;
                float dry = data[idx];

                // Lowpass then highpass in series narrows the band toward a radio speaker's
                // range, cutting both the low rumble and the crisp highs of a clean mic.
                _lpState += _lowpassAlpha * (dry - _lpState);
                float afterLp = _lpState;
                float hp = _highpassAlpha * (_hpPrevOut + afterLp - _hpPrevIn);
                _hpPrevIn  = afterLp;
                _hpPrevOut = hp;

                // Soft-clip saturation for crunch.
                float driven = hp * _driveAmount;
                float shaped = driven / (1f + Mathf.Abs(driven));

                // Cheap deterministic crackle (xorshift32) — no allocation, no UnityEngine.Random
                // (not safe off the main thread).
                _noiseState ^= _noiseState << 13;
                _noiseState ^= _noiseState >> 17;
                _noiseState ^= _noiseState << 5;
                float noise = ((_noiseState / (float)uint.MaxValue) * 2f - 1f) * _crackleAmount;

                float wetSample = Mathf.Clamp(shaped * gate + noise, -1f, 1f);
                data[idx] = Mathf.Lerp(dry, wetSample, _wet);
            }
        }
    }
}
