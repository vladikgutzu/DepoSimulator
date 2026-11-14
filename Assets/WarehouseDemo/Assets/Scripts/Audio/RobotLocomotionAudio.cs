using UnityEngine;

/// Plays robot locomotion one-shots for both the controllable rig and network puppets.
/// Animation events provide step/land timing, so no step RPC or polling timer is needed.
[DisallowMultipleComponent]
public sealed class RobotLocomotionAudio : MonoBehaviour
{
    [Header("Optional overrides")]
    [SerializeField] AudioSource _source;
    [SerializeField] AudioClip[] _footsteps;
    [SerializeField] AudioClip _jump;
    [SerializeField] AudioClip _land;

    [Header("Mix")]
    [SerializeField, Range(0f, 1f)] float _walkVolume = 0.30f;
    [SerializeField, Range(0f, 1f)] float _runVolume = 0.48f;
    [SerializeField, Range(0f, 1f)] float _jumpVolume = 0.28f;
    [SerializeField, Range(0f, 1f)] float _landVolume = 0.45f;

    static AudioClip[] s_FallbackFootsteps;
    static AudioClip s_FallbackLand;
    static float s_FallbackFootstepVolume = 0.3f;

    int _lastFootstepIndex = -1;
    float _nextFootstepTime;
    float _nextJumpTime;
    float _nextLandTime;
    bool _isRunning;
    bool _controllerDriven;
    bool _moving;

    void Awake()
    {
        if (_source == null) _source = GetComponent<AudioSource>();
        if (_source == null) _source = gameObject.AddComponent<AudioSource>();

        _source.playOnAwake = false;
        _source.loop = false;
        _source.spatialBlend = 1f;
        _source.rolloffMode = AudioRolloffMode.Linear;
        _source.minDistance = 1f;
        _source.maxDistance = 15f;
        _source.dopplerLevel = 0f;
    }

    void Start()
    {
        if (_source.outputAudioMixerGroup == null)
            _source.outputAudioMixerGroup = AudioManager.Instance?.GetMixerGroup("Foley");
    }

    /// Registers the Starter Assets clips as a project-safe fallback. Network puppets can then
    /// use the same clips without serializing another copy of the list into their prefab.
    public void ConfigureFallbacks(AudioClip[] footsteps, AudioClip landing, float footstepVolume)
    {
        if (footsteps != null && footsteps.Length != 0)
        {
            _footsteps = footsteps;
            s_FallbackFootsteps = footsteps;
        }

        if (landing != null)
        {
            _land = landing;
            s_FallbackLand = landing;
        }

        _walkVolume = Mathf.Clamp01(footstepVolume);
        _runVolume = Mathf.Clamp01(footstepVolume * 1.55f);
        s_FallbackFootstepVolume = Mathf.Clamp01(footstepVolume);
    }

    public void PlayFootstep()
    {
        // Locomotion clips already contain OnFootstep animation events. Use that exact animation
        // beat for local and remote characters instead of a movement timer that drifts from gait.
        if (_controllerDriven && !_moving) return;
        PlayFootstepNow();
    }

    void PlayFootstepNow()
    {
        float now = Time.unscaledTime;
        if (now < _nextFootstepTime) return;

        AudioClip[] clips = _footsteps != null && _footsteps.Length != 0
            ? _footsteps
            : s_FallbackFootsteps;
        if (clips == null || clips.Length == 0) return;

        int index = PickNonRepeatingIndex(clips.Length);
        AudioClip clip = clips[index];
        if (clip == null) return;

        bool running = _controllerDriven || _isRunning;
        GameAudioEvent audioEvent = running
            ? GameAudioEvent.PlayerFootstepRun
            : GameAudioEvent.PlayerFootstepWalk;
        if (AudioManager.Instance?.PlayAt(audioEvent, transform.position) == true)
        {
            _nextFootstepTime = now + 0.08f;
            return;
        }

        float fallbackScale = _footsteps == null || _footsteps.Length == 0
            ? s_FallbackFootstepVolume / Mathf.Max(0.01f, _walkVolume)
            : 1f;
        _source.pitch = Random.Range(0.96f, 1.04f);
        _source.PlayOneShot(clip, (running ? _runVolume : _walkVolume) * fallbackScale);
        _nextFootstepTime = now + 0.08f;
    }

    public void SetLocomotionState(bool moving, bool sprinting)
    {
        _controllerDriven = true;
        _isRunning = moving;
        _moving = moving;
    }

    public void PlayJump()
    {
        float now = Time.unscaledTime;
        if (now < _nextJumpTime) return;

        if (AudioManager.Instance?.PlayAt(GameAudioEvent.PlayerJump, transform.position) == true)
        {
            _nextJumpTime = now + 0.15f;
            return;
        }

        // Until a dedicated take-off clip is imported, the short landing clip is a safe metallic
        // fallback at a higher pitch and lower level.
        AudioClip clip = _jump != null ? _jump : (_land != null ? _land : s_FallbackLand);
        if (clip == null) return;

        _source.pitch = 1.18f;
        _source.PlayOneShot(clip, _jumpVolume);
        _nextJumpTime = now + 0.15f;
    }

    public void PlayLand()
    {
        float now = Time.unscaledTime;
        if (now < _nextLandTime) return;

        if (AudioManager.Instance?.PlayAt(GameAudioEvent.PlayerLand, transform.position) == true)
        {
            _nextLandTime = now + 0.15f;
            return;
        }

        AudioClip clip = _land != null ? _land : s_FallbackLand;
        if (clip == null) return;

        _source.pitch = Random.Range(0.96f, 1.02f);
        _source.PlayOneShot(clip, _landVolume);
        _nextLandTime = now + 0.15f;
    }

    int PickNonRepeatingIndex(int count)
    {
        if (count <= 1)
        {
            _lastFootstepIndex = 0;
            return 0;
        }

        int index = Random.Range(0, count - 1);
        if (index >= _lastFootstepIndex) index++;
        _lastFootstepIndex = index;
        return index;
    }
}
