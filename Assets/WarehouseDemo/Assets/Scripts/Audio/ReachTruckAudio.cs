using UnityEngine;

/// <summary>
/// Local presentation for a reach truck. It observes replicated/local vehicle state and never
/// writes gameplay or network state, so host and clients produce the same loops without RPC spam.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ReachTruckController))]
public sealed class ReachTruckAudio : MonoBehaviour
{
    ReachTruckController _controller;
    NetworkVehicleSync _network;
    Rigidbody _body;
    AudioSource _idle;
    AudioSource _drive;
    AudioSource _hydraulic;
    AudioSource _reverse;
    AudioCue _idleCue;
    AudioCue _driveCue;
    AudioCue _hydraulicCue;
    AudioCue _reverseCue;
    Vector3 _lastPosition;
    float _lastFork;
    float _hydraulicUntil;
    bool _wasOccupied;
    bool _ready;

    void Awake()
    {
        _controller = GetComponent<ReachTruckController>();
        _network = GetComponent<NetworkVehicleSync>();
        _body = GetComponent<Rigidbody>();
        _lastPosition = transform.position;
    }

    void OnEnable()
    {
        _lastPosition = transform.position;
        _lastFork = CurrentFork;
        _wasOccupied = IsOccupied;
    }

    void Update()
    {
        if (!_ready && !TryInitialize()) return;

        bool occupied = IsOccupied;
        if (occupied != _wasOccupied)
        {
            AudioManager.Instance?.PlayAt(
                occupied ? GameAudioEvent.ForkliftStart : GameAudioEvent.ForkliftStop,
                transform.position);
            _wasOccupied = occupied;
        }

        float deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
        Vector3 frameVelocity = (transform.position - _lastPosition) / deltaTime;
        _lastPosition = transform.position;

        float physicalSpeed = _body != null && !_body.isKinematic
            ? _body.linearVelocity.magnitude
            : frameVelocity.magnitude;
        float speed01 = Mathf.Clamp01(physicalSpeed / _controller.NetworkMaxLinearSpeed);
        speed01 = Mathf.Max(speed01, Mathf.Abs(_controller.DriveCommand));

        SetLoop(_idle, _idleCue, occupied ? 1f - speed01 * 0.75f : 0f);
        SetLoop(_drive, _driveCue, occupied ? speed01 : 0f);

        float fork = CurrentFork;
        if (!_controller.IsGroundOnlyVehicle && Mathf.Abs(fork - _lastFork) > 0.0001f)
            _hydraulicUntil = Time.unscaledTime + (_hydraulicCue?.LoopReleaseSeconds ?? 0f);
        _lastFork = fork;
        bool hydraulicActive = occupied && Time.unscaledTime <= _hydraulicUntil;
        SetLoop(_hydraulic, _hydraulicCue, hydraulicActive ? 1f : 0f);

        float forwardVelocity = Vector3.Dot(frameVelocity, transform.forward);
        bool reversing = occupied && (_controller.DriveCommand < -0.05f || forwardVelocity < -0.1f);
        SetLoop(_reverse, _reverseCue, reversing ? 1f : 0f);
    }

    void OnDisable()
    {
        Stop(_idle);
        Stop(_drive);
        Stop(_hydraulic);
        Stop(_reverse);
    }

    bool TryInitialize()
    {
        AudioManager manager = AudioManager.Instance;
        if (manager == null || manager.Catalog == null) return false;

        _idleCue = manager.GetCue(GameAudioEvent.ForkliftIdleLoop);
        _driveCue = manager.GetCue(GameAudioEvent.ForkliftDriveLoop);
        _hydraulicCue = manager.GetCue(GameAudioEvent.ForkliftHydraulicLoop);
        _reverseCue = manager.GetCue(GameAudioEvent.ForkliftReverseLoop);
        _idle = CreateLoop("Audio_Idle", _idleCue);
        _drive = CreateLoop("Audio_Drive", _driveCue);
        _hydraulic = CreateLoop("Audio_Hydraulic", _hydraulicCue);
        _reverse = CreateLoop("Audio_Reverse", _reverseCue);
        _ready = true;
        return true;
    }

    AudioSource CreateLoop(string childName, AudioCue cue)
    {
        if (cue == null || !cue.HasClips) return null;
        var child = new GameObject(childName);
        child.transform.SetParent(transform, false);
        AudioSource source = child.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.clip = cue.SelectClip();
        source.volume = 0f;
        cue.ApplyRouting(source);
        return source;
    }

    static void SetLoop(AudioSource source, AudioCue cue, float intensity)
    {
        if (source == null || cue == null) return;
        if (intensity <= 0.001f)
        {
            if (source.isPlaying) source.Pause();
            return;
        }

        source.volume = cue.LoopVolume(intensity);
        source.pitch = cue.LoopPitch(intensity);
        if (!source.isPlaying) source.UnPause();
        if (!source.isPlaying) source.Play();
    }

    static void Stop(AudioSource source)
    {
        if (source != null) source.Stop();
    }

    bool IsOccupied => _network != null && _network.IsSpawned
        ? _network.IsOccupied
        : _controller.IsLocallyOccupied;

    float CurrentFork => _network != null && _network.IsSpawned
        ? _network.ForkT
        : _controller.ForkNormalized;
}
