using System.Collections;
using UnityEngine;
using UnityEngine.Audio;

public enum GameAudioContext
{
    MainMenu,
    Gameplay
}

public class AudioManager : MonoBehaviour
{
    const string DefaultCatalogResourcePath = "Audio/GameAudioCatalog";
    const string DefaultMixerResourcePath = "Audio/WarehouseAudioMixer";
    const string DefaultMenuMusicResourcePath = "Audio/Music/MenuMusic";
    const string DefaultGameplayMusicResourcePath = "Audio/Music/GameplayMusic";
    const string DefaultWarehouseAmbienceResourcePath = "Audio/Ambience/WarehouseAmbience";
    const string DefaultConveyorAmbienceResourcePath = "Audio/Ambience/ConveyorAmbience";
    const string DefaultChargingHumResourcePath = "Audio/SFX/EnergyHum";
    const string DefaultChargingCompleteResourcePath = "Audio/SFX/ChargingComplete";

    const string MasterVolumeParameter = "MasterVolume";
    const string MusicVolumeParameter = "MusicVolume";
    const string AmbienceVolumeParameter = "AmbienceVolume";
    const string SfxVolumeParameter = "SFXVolume";
    const string UiVolumeParameter = "UIVolume";
    const string FoleyVolumeParameter = "FoleyVolume";
    const string VehiclesVolumeParameter = "VehiclesVolume";

    static GameAudioContext _requestedContext = GameAudioContext.MainMenu;

    public static AudioManager Instance { get; private set; }

    [Header("Data-driven audio")]
    [SerializeField] GameAudioCatalog _catalog;

    [Header("Music and mixer")]
    [Tooltip("Optional. If empty, Resources/Audio/WarehouseAudioMixer is used.")]
    [SerializeField] AudioMixer _mixer;
    [SerializeField] AudioMixerGroup _musicOutput;
    [SerializeField] AudioClip _menuMusic;
    [SerializeField] AudioClip _gameplayMusic;
    [SerializeField, Range(0f, 1f)] float _musicSourceVolume = 1f;
    [SerializeField, Range(0f, 1f)] float _gameplayMusicVolume = 0.5f;

    [Header("Gameplay ambience")]
    [SerializeField] AudioMixerGroup _ambienceOutput;
    [SerializeField] AudioClip _warehouseAmbience;
    [SerializeField] AudioClip _conveyorAmbience;
    [SerializeField, Range(0f, 1f)] float _warehouseAmbienceVolume = 0.28f;
    [SerializeField, Range(0f, 1f)] float _conveyorAmbienceVolume = 0.16f;

    [Header("Robot charging")]
    [SerializeField] AudioMixerGroup _sfxOutput;
    [SerializeField] AudioClip _chargingHum;
    [SerializeField] AudioClip _chargingComplete;
    [SerializeField, Range(0f, 1f)] float _chargingHumVolume = 0.28f;
    [SerializeField, Range(0.05f, 2f)] float _chargingHumFadeSeconds = 0.4f;
    [SerializeField, Range(0f, 1f)] float _chargingCompleteVolume = 0.55f;

    [SerializeField] private AudioClip _clipPickup;
    [SerializeField] private AudioClip _clipPlacePart;
    [SerializeField] private AudioClip _clipReturnPart;
    [SerializeField] private AudioClip _clipOrderAccepted;
    [SerializeField] private AudioClip _clipOrderComplete;
    [SerializeField] private AudioClip _clipOrderExpired;
    [SerializeField] private AudioClip _clipDemoComplete;
    [SerializeField] private AudioClip _clipError;
    [SerializeField] private AudioClip _clipPing;
    [SerializeField] private AudioClip _clipDrop;
    [SerializeField] [Range(0f, 1f)] private float _sfxVolume = 1f;

    private AudioSource _source;
    AudioSource _musicSource;
    AudioSource _warehouseAmbienceSource;
    AudioSource _conveyorAmbienceSource;
    AudioSource _chargingHumSource;
    AudioSource _chargingCompleteSource;
    Coroutine _chargingHumFade;
    AudioOneShotPool _oneShotPool;

    public GameAudioCatalog Catalog => _catalog;

    // Cached delegates so OnDestroy can unsubscribe the exact same instances.
    System.Action<OrderDefinition> _onAccepted;
    System.Action<OrderDefinition> _onCompleted;
    System.Action<OrderDefinition> _onExpired;

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        _source = gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.spatialBlend = 0f;

        _musicSource = gameObject.AddComponent<AudioSource>();
        _musicSource.playOnAwake = false;
        _musicSource.loop = true;
        _musicSource.spatialBlend = 0f;
        _musicSource.volume = _musicSourceVolume;

        _warehouseAmbienceSource = CreateLoopSource(_warehouseAmbienceVolume);
        _conveyorAmbienceSource = CreateLoopSource(_conveyorAmbienceVolume);
        _chargingHumSource = CreateLoopSource(0f);
        _chargingCompleteSource = gameObject.AddComponent<AudioSource>();
        _chargingCompleteSource.playOnAwake = false;
        _chargingCompleteSource.spatialBlend = 0f;

        if (_catalog == null)
            _catalog = Resources.Load<GameAudioCatalog>(DefaultCatalogResourcePath);

        if (_mixer == null)
            _mixer = Resources.Load<AudioMixer>(DefaultMixerResourcePath);
        if (_menuMusic == null)
            _menuMusic = Resources.Load<AudioClip>(DefaultMenuMusicResourcePath);
        if (_gameplayMusic == null)
            _gameplayMusic = Resources.Load<AudioClip>(DefaultGameplayMusicResourcePath);
        if (_warehouseAmbience == null)
            _warehouseAmbience = Resources.Load<AudioClip>(DefaultWarehouseAmbienceResourcePath);
        if (_conveyorAmbience == null)
            _conveyorAmbience = Resources.Load<AudioClip>(DefaultConveyorAmbienceResourcePath);
        if (_chargingHum == null)
            _chargingHum = Resources.Load<AudioClip>(DefaultChargingHumResourcePath);
        if (_chargingComplete == null)
            _chargingComplete = Resources.Load<AudioClip>(DefaultChargingCompleteResourcePath);
        if (_musicOutput == null && _mixer != null)
        {
            AudioMixerGroup[] groups = _mixer.FindMatchingGroups("Music");
            if (groups != null && groups.Length > 0) _musicOutput = groups[0];
        }
        if (_ambienceOutput == null && _mixer != null)
        {
            AudioMixerGroup[] groups = _mixer.FindMatchingGroups("Ambience");
            if (groups != null && groups.Length > 0) _ambienceOutput = groups[0];
        }
        if (_sfxOutput == null && _mixer != null)
        {
            AudioMixerGroup[] groups = _mixer.FindMatchingGroups("SFX");
            if (groups != null && groups.Length > 0) _sfxOutput = groups[0];
        }
        _musicSource.outputAudioMixerGroup = _musicOutput;
        ConfigureAmbienceSource(_warehouseAmbienceSource, _warehouseAmbience);
        ConfigureAmbienceSource(_conveyorAmbienceSource, _conveyorAmbience);
        _chargingHumSource.clip = _chargingHum;
        _chargingHumSource.outputAudioMixerGroup = _sfxOutput;
        _chargingCompleteSource.clip = _chargingComplete;
        _chargingCompleteSource.outputAudioMixerGroup = _sfxOutput;
        DisableLegacyAmbientSource();

        _oneShotPool = GetComponent<AudioOneShotPool>();
        if (_oneShotPool == null) _oneShotPool = gameObject.AddComponent<AudioOneShotPool>();
        _oneShotPool.Initialize(_catalog != null ? _catalog.OneShotVoiceCount : 20);

        GameSettings.AudioSettingsChanged += OnAudioSettingsChanged;
        ApplyAudioSettings();
        RefreshMusic();
        RefreshAmbience();
    }

    private void Start()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;
        _onAccepted  = _ => PlayOrderAccepted();
        _onCompleted = _ => PlayOrderComplete();
        _onExpired   = _ => PlayOrderExpired();
        gm.OnOrderAccepted  += _onAccepted;
        gm.OnOrderCompleted += _onCompleted;
        gm.OnOrderExpired   += _onExpired;
        gm.OnDemoComplete   += PlayDemoComplete;
    }

    private void OnDestroy()
    {
        GameSettings.AudioSettingsChanged -= OnAudioSettingsChanged;
        var gm = GameManager.Instance;
        if (gm == null) return;
        gm.OnOrderAccepted  -= _onAccepted;
        gm.OnOrderCompleted -= _onCompleted;
        gm.OnOrderExpired   -= _onExpired;
        gm.OnDemoComplete   -= PlayDemoComplete;
    }

    public static void SetContext(GameAudioContext context)
    {
        _requestedContext = context;
        if (Instance == null) return;
        Instance.ApplyAudioSettings();
        Instance.RefreshMusic();
        Instance.RefreshAmbience();
    }

    public void PlayPickup()        => PlayConfigured(GameAudioEvent.Pickup, _clipPickup);
    public void PlayPlacePart()     => PlayConfigured(GameAudioEvent.PlacePart, _clipPlacePart);
    public void PlayReturnPart()    => PlayConfigured(GameAudioEvent.ReturnPart, _clipReturnPart);
    public void PlayOrderAccepted() => PlayConfigured(GameAudioEvent.OrderAccepted, _clipOrderAccepted);
    public void PlayOrderComplete() => PlayConfigured(GameAudioEvent.OrderComplete, _clipOrderComplete);
    public void PlayOrderExpired()  => PlayConfigured(GameAudioEvent.OrderExpired, _clipOrderExpired);
    public void PlayDemoComplete()  => PlayConfigured(GameAudioEvent.DemoComplete, _clipDemoComplete);
    public void PlayError()         => PlayConfigured(GameAudioEvent.Error, _clipError);
    public void PlayPing()          => PlayConfigured(GameAudioEvent.Ping, _clipPing);
    public void PlayDrop()          => PlayConfigured(GameAudioEvent.BoxDrop, _clipDrop);
    public void PlayPurchaseSuccess() => PlayConfigured(GameAudioEvent.PurchaseSuccess, null);
    public void PlayUiButtonClick()   => PlayUi(GameAudioEvent.UiButtonClick);
    public void PlayInteractConfirm() => PlayUi(GameAudioEvent.InteractConfirm);

    public void StartChargingHum()
    {
        if (_chargingHumSource == null || _chargingHumSource.clip == null ||
            _requestedContext != GameAudioContext.Gameplay) return;
        if (_chargingHumFade != null) StopCoroutine(_chargingHumFade);
        _chargingHumFade = null;
        _chargingHumSource.volume = _chargingHumVolume;
        if (!_chargingHumSource.isPlaying) _chargingHumSource.Play();
    }

    public void StopChargingHum(bool playFullChargeSignal)
    {
        if (_chargingHumFade != null) StopCoroutine(_chargingHumFade);
        _chargingHumFade = StartCoroutine(FadeOutChargingHum(playFullChargeSignal));
    }

    public void StopChargingHumImmediately()
    {
        if (_chargingHumFade != null) StopCoroutine(_chargingHumFade);
        _chargingHumFade = null;
        if (_chargingHumSource == null) return;
        _chargingHumSource.Stop();
        _chargingHumSource.volume = 0f;
    }

    IEnumerator FadeOutChargingHum(bool playFullChargeSignal)
    {
        if (playFullChargeSignal && _chargingCompleteSource != null &&
            _chargingCompleteSource.clip != null && _requestedContext == GameAudioContext.Gameplay)
            _chargingCompleteSource.PlayOneShot(_chargingCompleteSource.clip, _chargingCompleteVolume);

        if (_chargingHumSource != null && _chargingHumSource.isPlaying)
        {
            float startVolume = _chargingHumSource.volume;
            float elapsed = 0f;
            while (elapsed < _chargingHumFadeSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                _chargingHumSource.volume = Mathf.Lerp(startVolume, 0f,
                    Mathf.Clamp01(elapsed / _chargingHumFadeSeconds));
                yield return null;
            }
            _chargingHumSource.Stop();
            _chargingHumSource.volume = 0f;
        }
        _chargingHumFade = null;
    }

    public void PlayRadioStatic(float duration = 1.2f) =>
        RadioStaticPlayer.Instance?.Play(duration);

    public AudioCue GetCue(GameAudioEvent audioEvent)
    {
        return _catalog != null && _catalog.TryGet(audioEvent, out AudioCue cue) ? cue : null;
    }

    public AudioMixerGroup GetMixerGroup(string groupName)
    {
        if (_mixer == null || string.IsNullOrWhiteSpace(groupName)) return null;
        AudioMixerGroup[] groups = _mixer.FindMatchingGroups(groupName);
        return groups != null && groups.Length > 0 ? groups[0] : null;
    }

    public float GetCooldown(GameAudioEvent audioEvent)
    {
        AudioCue cue = GetCue(audioEvent);
        return cue != null ? cue.CooldownSeconds : 0f;
    }

    public bool PlayGlobal(GameAudioEvent audioEvent, float intensity = 1f)
    {
        if (_requestedContext == GameAudioContext.MainMenu) return false;
        AudioCue cue = GetCue(audioEvent);
        return cue != null && _oneShotPool != null
            && _oneShotPool.Play(cue, transform.position, intensity);
    }

    public bool PlayAt(GameAudioEvent audioEvent, Vector3 position, float intensity = 1f)
    {
        if (_requestedContext == GameAudioContext.MainMenu) return false;
        AudioCue cue = GetCue(audioEvent);
        return cue != null && _oneShotPool != null
            && _oneShotPool.Play(cue, position, intensity);
    }

    void PlayConfigured(GameAudioEvent audioEvent, AudioClip fallback)
    {
        if (PlayGlobal(audioEvent)) return;
        Play(fallback);
    }

    void PlayUi(GameAudioEvent audioEvent)
    {
        AudioCue cue = GetCue(audioEvent);
        if (cue != null && _oneShotPool != null)
            _oneShotPool.Play(cue, transform.position, 1f);
    }

    private void Play(AudioClip clip)
    {
        if (clip == null || _requestedContext == GameAudioContext.MainMenu) return;
        _source.PlayOneShot(clip, _sfxVolume);
    }

    void ApplyAudioSettings()
    {
        // Vivox/voice is deliberately not controlled here. Never use AudioListener.volume:
        // only the game's mixer groups are changed, leaving voice-chat output independent.
        SetMixerVolume(MasterVolumeParameter, GameSettings.MasterVolume);
        SetMixerVolume(MusicVolumeParameter, GameSettings.MusicVolume);

        bool gameplay = _requestedContext == GameAudioContext.Gameplay;
        float sounds = GameSettings.SoundVolume;
        SetMixerVolume(AmbienceVolumeParameter, gameplay ? sounds : 0f);
        SetMixerVolume(SfxVolumeParameter, gameplay ? sounds : 0f);
        SetMixerVolume(UiVolumeParameter, sounds);
        SetMixerVolume(FoleyVolumeParameter, gameplay ? sounds : 0f);
        SetMixerVolume(VehiclesVolumeParameter, gameplay ? sounds : 0f);

        if (_musicSource != null)
        {
            float contextVolume = _requestedContext == GameAudioContext.Gameplay
                ? _gameplayMusicVolume
                : _musicSourceVolume;
            // Fallback when the mixer asset/group has not been wired yet.
            _musicSource.mute = GameSettings.MusicVolume <= 0.0001f;
            _musicSource.volume = _musicOutput != null
                ? contextVolume
                : contextVolume * GameSettings.MasterVolume * GameSettings.MusicVolume;
        }
    }

    void OnAudioSettingsChanged()
    {
        ApplyAudioSettings();
        RefreshMusic();
        RefreshAmbience();
    }

    void RefreshMusic()
    {
        if (_musicSource == null) return;
        AudioClip wanted = _requestedContext == GameAudioContext.MainMenu
            ? _menuMusic
            : _gameplayMusic;

        if (_musicSource.clip == wanted && _musicSource.isPlaying) return;
        _musicSource.Stop();
        _musicSource.clip = wanted;
        if (wanted != null && GameSettings.MusicVolume > 0.0001f) _musicSource.Play();
    }

    void RefreshAmbience()
    {
        bool shouldPlay = _requestedContext == GameAudioContext.Gameplay;
        SetLoopPlaying(_warehouseAmbienceSource, shouldPlay);
        SetLoopPlaying(_conveyorAmbienceSource, shouldPlay);
    }

    AudioSource CreateLoopSource(float volume)
    {
        AudioSource source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialBlend = 0f;
        source.volume = volume;
        return source;
    }

    void ConfigureAmbienceSource(AudioSource source, AudioClip clip)
    {
        source.clip = clip;
        source.outputAudioMixerGroup = _ambienceOutput;
    }

    static void SetLoopPlaying(AudioSource source, bool shouldPlay)
    {
        if (source == null || source.clip == null) return;
        if (shouldPlay && !source.isPlaying) source.Play();
        else if (!shouldPlay && source.isPlaying) source.Stop();
    }

    static void DisableLegacyAmbientSource()
    {
        GameObject legacy = GameObject.Find("audioAmbient");
        if (legacy == null) return;
        AudioSource source = legacy.GetComponent<AudioSource>();
        if (source != null)
        {
            source.Stop();
            source.playOnAwake = false;
            source.enabled = false;
        }
    }

    void SetMixerVolume(string parameter, float linear)
    {
        if (_mixer == null) return;
        float decibels = linear <= 0.0001f ? -80f : Mathf.Log10(linear) * 20f;
        _mixer.SetFloat(parameter, decibels);
    }
}
