using UnityEngine;

[DisallowMultipleComponent]
public sealed class AudioOneShotPool : MonoBehaviour
{
    AudioSource[] _voices;
    float[] _startedAt;
    int _cursor;

    public void Initialize(int voiceCount)
    {
        voiceCount = Mathf.Clamp(voiceCount, 4, 64);
        if (_voices != null && _voices.Length == voiceCount) return;

        if (_voices != null)
        {
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i] != null) Destroy(_voices[i].gameObject);
        }

        _voices = new AudioSource[voiceCount];
        _startedAt = new float[voiceCount];
        for (int i = 0; i < voiceCount; i++)
        {
            var voice = new GameObject($"OneShot_{i:00}");
            voice.transform.SetParent(transform, false);
            AudioSource source = voice.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            _voices[i] = source;
        }
    }

    public bool Play(AudioCue cue, Vector3 position, float intensity = 1f)
    {
        if (cue == null || !cue.AcceptsIntensity(intensity)) return false;
        AudioClip clip = cue.SelectClip();
        if (clip == null) return false;
        if (_voices == null || _voices.Length == 0) Initialize(20);

        int index = FindVoice();
        AudioSource source = _voices[index];
        source.Stop();
        source.transform.position = position;
        cue.ApplyRouting(source);
        source.loop = false;
        source.clip = clip;
        source.volume = cue.RandomVolume(intensity);
        source.pitch = cue.RandomPitch(intensity);
        source.Play();
        _startedAt[index] = Time.unscaledTime;
        _cursor = (index + 1) % _voices.Length;
        return true;
    }

    int FindVoice()
    {
        int oldest = _cursor;
        float oldestTime = float.PositiveInfinity;
        for (int offset = 0; offset < _voices.Length; offset++)
        {
            int index = (_cursor + offset) % _voices.Length;
            AudioSource source = _voices[index];
            if (!source.isPlaying) return index;
            if (_startedAt[index] < oldestTime)
            {
                oldestTime = _startedAt[index];
                oldest = index;
            }
        }
        return oldest;
    }
}
