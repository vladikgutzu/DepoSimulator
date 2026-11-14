using UnityEngine;
using UnityEngine.Audio;

public enum GameAudioEvent
{
    None = 0,
    Pickup,
    PlacePart,
    ReturnPart,
    OrderAccepted,
    OrderComplete,
    OrderExpired,
    DemoComplete,
    Error,
    Ping,
    BoxDrop,
    BoxImpact,
    BoxSlide,
    PalletImpact,
    ForkliftStart,
    ForkliftIdleLoop,
    ForkliftDriveLoop,
    ForkliftHydraulicLoop,
    ForkliftReverseLoop,
    ForkliftStop,
    ForkliftImpact,
    ForkliftHorn,
    AssemblyTool,
    AssemblyTape,
    MeleeSwing,
    MetalImpact,
    RobotFall,
    LimbPickup,
    LimbInstall,
    PowerOutage,
    PowerRestore,
    ShortCircuit,
    LobbyJoin,
    LobbyLeave,
    LobbyRoleSelected,
    PurchaseSuccess,
    UiButtonClick,
    ConveyorLoop,
    PlayerFootstepWalk,
    PlayerFootstepRun,
    PlayerJump,
    PlayerLand,
    BoxRelease,
    BoxThrow,
    InteractConfirm,
    RobotPunchImpact,
    RobotDamageImpact,
}

/// <summary>
/// Data-only description of one gameplay sound. Runtime systems reference the event id while
/// designers can replace clips and tune playback without changing gameplay code.
/// </summary>
[CreateAssetMenu(fileName = "AudioCue", menuName = "Warehouse/Audio/Audio Cue")]
public sealed class AudioCue : ScriptableObject
{
    [SerializeField] GameAudioEvent _event;
    [SerializeField] AudioClip[] _clips;
    [SerializeField] AudioMixerGroup _output;

    [Header("Playback")]
    [SerializeField, Range(0f, 1f)] float _volumeMin = 0.9f;
    [SerializeField, Range(0f, 1f)] float _volumeMax = 1f;
    [SerializeField, Range(-3f, 3f)] float _pitchMin = 0.96f;
    [SerializeField, Range(-3f, 3f)] float _pitchMax = 1.04f;
    [SerializeField] bool _loop;
    [SerializeField, Min(0f)] float _cooldownSeconds = 0.04f;
    [SerializeField, Min(0f)] float _loopReleaseSeconds = 0.12f;

    [Header("3D")]
    [SerializeField, Range(0f, 1f)] float _spatialBlend;
    [SerializeField, Min(0.01f)] float _minDistance = 1.5f;
    [SerializeField, Min(0.02f)] float _maxDistance = 18f;
    [SerializeField] AudioRolloffMode _rolloffMode = AudioRolloffMode.Logarithmic;

    [Header("Intensity response")]
    [Tooltip("Events below this value stay silent. Collision events use relative speed.")]
    [SerializeField, Min(0f)] float _minimumIntensity;
    [Tooltip("Intensity that reaches the cue's maximum volume/pitch. Zero disables scaling.")]
    [SerializeField, Min(0f)] float _fullIntensity;

    public GameAudioEvent Event => _event;
    public AudioMixerGroup Output => _output;
    public bool Loop => _loop;
    public float CooldownSeconds => _cooldownSeconds;
    public float LoopReleaseSeconds => _loopReleaseSeconds;
    public float SpatialBlend => _spatialBlend;
    public bool HasClips => _clips != null && _clips.Length > 0;

    public bool AcceptsIntensity(float intensity) => intensity >= _minimumIntensity;

    public AudioClip SelectClip()
    {
        if (_clips == null || _clips.Length == 0) return null;
        if (_clips.Length == 1) return _clips[0];
        return _clips[Random.Range(0, _clips.Length)];
    }

    public float RandomVolume(float intensity = 1f)
    {
        float response = EvaluateIntensity(intensity);
        return Mathf.Lerp(_volumeMin, Random.Range(_volumeMin, _volumeMax), response);
    }

    public float RandomPitch(float intensity = 1f)
    {
        float response = EvaluateIntensity(intensity);
        return Mathf.Lerp(_pitchMin, Random.Range(_pitchMin, _pitchMax), response);
    }

    public float LoopVolume(float normalizedIntensity) =>
        Mathf.Lerp(_volumeMin, _volumeMax, Mathf.Clamp01(normalizedIntensity));

    public float LoopPitch(float normalizedIntensity) =>
        Mathf.Lerp(_pitchMin, _pitchMax, Mathf.Clamp01(normalizedIntensity));

    public void ApplyRouting(AudioSource source)
    {
        source.outputAudioMixerGroup = _output;
        source.spatialBlend = _spatialBlend;
        source.minDistance = _minDistance;
        source.maxDistance = Mathf.Max(_minDistance, _maxDistance);
        source.rolloffMode = _rolloffMode;
        source.dopplerLevel = 0f;
    }

    float EvaluateIntensity(float intensity)
    {
        if (_fullIntensity <= _minimumIntensity) return 1f;
        return Mathf.InverseLerp(_minimumIntensity, _fullIntensity, intensity);
    }

#if UNITY_EDITOR
    public void EditorConfigure(GameAudioEvent audioEvent, AudioClip[] clips, bool loop,
        float spatialBlend, float volumeMin, float volumeMax, float pitchMin, float pitchMax,
        float cooldownSeconds, float minimumIntensity = 0f, float fullIntensity = 0f,
        AudioMixerGroup output = null)
    {
        _event = audioEvent;
        _clips = clips;
        _loop = loop;
        _spatialBlend = Mathf.Clamp01(spatialBlend);
        _volumeMin = Mathf.Clamp01(volumeMin);
        _volumeMax = Mathf.Clamp01(volumeMax);
        _pitchMin = pitchMin;
        _pitchMax = pitchMax;
        _cooldownSeconds = Mathf.Max(0f, cooldownSeconds);
        _minimumIntensity = Mathf.Max(0f, minimumIntensity);
        _fullIntensity = Mathf.Max(0f, fullIntensity);
        _output = output;
    }
#endif

    void OnValidate()
    {
        if (_volumeMax < _volumeMin) _volumeMax = _volumeMin;
        if (_pitchMax < _pitchMin) _pitchMax = _pitchMin;
        if (_maxDistance < _minDistance) _maxDistance = _minDistance;
        if (_fullIntensity > 0f && _fullIntensity < _minimumIntensity)
            _fullIntensity = _minimumIntensity;
    }
}
