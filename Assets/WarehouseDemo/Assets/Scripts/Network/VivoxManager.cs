using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.InputSystem;

/// Manages Vivox voice chat for co-op sessions.
/// Requires Vivox enabled in UGS Dashboard (Unity Gaming Services → Vivox).
/// Place on a persistent GameObject alongside NetworkBootstrapper.
public class VivoxManager : MonoBehaviour
{
    public static VivoxManager Instance { get; private set; }

    // Maps Vivox PlayerId → is currently speaking
    readonly Dictionary<string, bool> _speaking = new();

    // Maps Vivox PlayerId → the distortion filter on that remote participant's audio tap.
    // Only incoming (remote) voice is ever distorted — our own outgoing mic feed is untouched.
    readonly Dictionary<string, RadioVoiceDistortionFilter> _distortionFilters = new();
    bool _lastRadioInterfering;

    public IReadOnlyDictionary<string, bool> SpeakingStates => _speaking;
    public bool IsLoggedIn    { get; private set; }
    public bool IsPushToTalk  { get; private set; }

    // Fired on main thread: (vivoxPlayerId, isSpeaking)
    public event Action<string, bool> OnSpeakingChanged;

    string _currentChannelName;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task LoginAsync(string displayName)
    {
        try
        {
            if (VivoxService.Instance.IsLoggedIn) { IsLoggedIn = true; return; }
            await VivoxService.Instance.LoginAsync(new LoginOptions { DisplayName = displayName });
            IsLoggedIn = true;
            SubscribeServiceEvents();
            Debug.Log($"[Vivox] Logged in as {displayName}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Vivox] LoginAsync failed: {ex.Message}");
        }
    }

    /// Join a group audio channel named after the lobby code.
    public async Task JoinChannelAsync(string lobbyCode)
    {
        _currentChannelName = $"depot{lobbyCode}".ToLower().Replace("-", "");
        try
        {
            await VivoxService.Instance.JoinGroupChannelAsync(
                _currentChannelName, ChatCapability.AudioOnly);
            // Start muted — player unmutes by holding V (push-to-talk)
            VivoxService.Instance.MuteInputDevice();
            Debug.Log($"[Vivox] Joined channel {_currentChannelName}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Vivox] JoinChannelAsync failed: {ex.Message}");
        }
    }

    public async Task LeaveSessionAsync()
    {
        try
        {
            UnsubscribeServiceEvents();
            if (VivoxService.Instance == null) return;
            if (VivoxService.Instance.ActiveChannels.Count > 0)
                await VivoxService.Instance.LeaveAllChannelsAsync();
            if (VivoxService.Instance.IsLoggedIn)
                await VivoxService.Instance.LogoutAsync();
            IsLoggedIn = false;
            _speaking.Clear();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Vivox] LeaveSessionAsync failed: {ex.Message}");
        }
    }

    // ── Push-to-talk (V key) ──────────────────────────────────────────────────

    void Update()
    {
        if (!IsLoggedIn) return;

        // Toggle voice distortion on every remote participant's tap when RadioInterferenceEvent
        // starts/ends. Polled here (rather than an event) since GameEventManager.IsRadioInterfering
        // is just a static bool with no change notification.
        bool interfering = GameEventManager.IsRadioInterfering;
        if (interfering != _lastRadioInterfering)
        {
            _lastRadioInterfering = interfering;
            foreach (var filter in _distortionFilters.Values)
                if (filter != null) filter.Distorting = interfering;
        }

        var kb = Keyboard.current;
        var gp = Gamepad.current;
        if (kb == null && gp == null) return;

        bool v = GameInputBindings.IsPressed(GameBindingAction.PushToTalk);
        if (v == IsPushToTalk) return;

        IsPushToTalk = v;
        if (IsPushToTalk)
        {
            RadioStaticPlayer.Instance?.Play(0.4f); // click at transmission start
            VivoxService.Instance.UnmuteInputDevice();
        }
        else
        {
            RadioStaticPlayer.Instance?.Play(0.25f); // squelch at transmission end
            VivoxService.Instance.MuteInputDevice();
        }

        // Said once per press, not per frame. It is the line that separates "my microphone never
        // opened" from "his voice never reached my ears" — two different bugs that look identical
        // from the sofa, and the build is where they have to be told apart.
        Debug.Log($"[Vivox] push-to-talk {(IsPushToTalk ? "ON — microphone unmuted" : "off — microphone muted")}, " +
                  $"participants in channel: {_speaking.Count}, taps: {_distortionFilters.Count}");

        NetworkPlayer.Local?.SetSpeakingServerRpc(IsPushToTalk);
    }

    // ── Vivox event subscriptions ─────────────────────────────────────────────

    void SubscribeServiceEvents()
    {
        VivoxService.Instance.ParticipantAddedToChannel     += OnParticipantAdded;
        VivoxService.Instance.ParticipantRemovedFromChannel += OnParticipantRemoved;
    }

    void UnsubscribeServiceEvents()
    {
        if (!IsLoggedIn || VivoxService.Instance == null) return;
        VivoxService.Instance.ParticipantAddedToChannel     -= OnParticipantAdded;
        VivoxService.Instance.ParticipantRemovedFromChannel -= OnParticipantRemoved;
    }

    void OnParticipantAdded(VivoxParticipant participant)
    {
        _speaking[participant.PlayerId] = false;
        // Subscribe per-participant so we know who is speaking
        participant.ParticipantSpeechDetected += () => OnVivoxSpeechDetected(participant);

        // Route this remote participant's decoded voice through our own AudioSource (muted
        // out of Vivox's normal channel mix) so RadioVoiceDistortionFilter can process it.
        // Not supported/needed for the local player's own (outgoing) voice.
        if (!participant.IsSelf)
        {
            var tapGO = participant.CreateVivoxParticipantTap(silenceInChannelAudioMix: true);
            if (tapGO != null)
            {
                ConfigureTapAudio(tapGO, participant.DisplayName);

                var filter = tapGO.AddComponent<RadioVoiceDistortionFilter>();
                filter.Distorting = _lastRadioInterfering; // pick up mid-interference late joins
                _distortionFilters[participant.PlayerId] = filter;
            }
        }
    }

    /// Makes the tap actually audible, and says out loud how it was set up.
    ///
    /// The tap is the only way a team-mate's voice reaches the ears now: asking for it silences
    /// Vivox's own channel mix, so whatever this AudioSource does is what the player hears. Vivox
    /// hands the object over with default settings and leaves it wherever it created it — and an
    /// AudioSource left at 3D sits at that spot in the warehouse, so the voice fades out with
    /// distance from a point nobody is standing on. Radio voice is not positional: it is in your
    /// ear, at full volume, wherever the speaker is.
    void ConfigureTapAudio(GameObject tapGO, string who)
    {
        var src = tapGO.GetComponent<AudioSource>();
        if (src == null)
        {
            Debug.LogWarning($"[Vivox] tap for '{who}' has no AudioSource — nothing will be heard.");
            return;
        }

        float wasBlend  = src.spatialBlend;
        float wasVolume = src.volume;
        bool  wasMuted  = src.mute;

        src.spatialBlend          = 0f;     // 2D: a radio, not a voice across the room
        src.volume                = 1f;
        src.mute                  = false;
        src.dopplerLevel          = 0f;
        src.bypassEffects         = false;  // the interference filter has to keep working
        src.bypassListenerEffects = false;
        src.ignoreListenerPause   = false;
        src.ignoreListenerVolume  = false;
        src.priority              = 0;      // voice outranks warehouse noise

        // One line per participant, and the one that answers "why is there no voice in the build
        // when the editor is fine": it names the state the tap arrived in.
        Debug.Log($"[Vivox] tap for '{who}': spatialBlend {wasBlend:0.00}->0, volume {wasVolume:0.00}->1, " +
                  $"muted {wasMuted}->False, playing={src.isPlaying}, at {tapGO.transform.position:F1}, " +
                  $"listeners={UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length}");
    }

    void OnParticipantRemoved(VivoxParticipant participant)
    {
        _speaking.Remove(participant.PlayerId);
        // The tap GameObject itself is destroyed by VivoxParticipant's own cleanup
        // (DestroyTap, wired to participant removal / logout) — just drop our reference.
        _distortionFilters.Remove(participant.PlayerId);
        OnSpeakingChanged?.Invoke(participant.PlayerId, false);
    }

    void OnVivoxSpeechDetected(VivoxParticipant participant)
    {
        bool speaking = participant.SpeechDetected;
        if (_speaking.TryGetValue(participant.PlayerId, out bool prev) && prev == speaking) return;
        _speaking[participant.PlayerId] = speaking;
        OnSpeakingChanged?.Invoke(participant.PlayerId, speaking);
    }

    void OnDestroy() => _ = LeaveSessionAsync();
}
