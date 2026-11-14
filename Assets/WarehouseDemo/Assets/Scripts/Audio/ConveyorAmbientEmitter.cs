using UnityEngine;

/// Localised warehouse machinery bed. One emitter represents a whole conveyor section; it does
/// not poll or search the scene and follows the global menu/gameplay audio context through the
/// Ambience mixer group.
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public sealed class ConveyorAmbientEmitter : MonoBehaviour
{
    [SerializeField] AudioClip _clip;
    [SerializeField, Range(0f, 1f)] float _volume = 0.18f;
    [SerializeField] float _minDistance = 3f;
    [SerializeField] float _maxDistance = 24f;

    AudioSource _source;

    void Awake()
    {
        _source = GetComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = true;
        _source.spatialBlend = 1f;
        _source.rolloffMode = AudioRolloffMode.Logarithmic;
        _source.minDistance = Mathf.Max(0.1f, _minDistance);
        _source.maxDistance = Mathf.Max(_source.minDistance, _maxDistance);
        _source.dopplerLevel = 0f;
        _source.priority = 192;
        _source.volume = _volume;
        if (_clip != null) _source.clip = _clip;
    }

    void Start()
    {
        if (_source.outputAudioMixerGroup == null)
            _source.outputAudioMixerGroup = AudioManager.Instance?.GetMixerGroup("Ambience");
        if (_source.clip == null) return;
        if (_source.clip.length > 0.05f)
            _source.time = Random.Range(0f, _source.clip.length - 0.01f);
        _source.Play();
    }

#if UNITY_EDITOR
    public void EditorConfigure(AudioClip clip, float volume = 0.18f)
    {
        _clip = clip;
        _volume = Mathf.Clamp01(volume);
    }
#endif
}
