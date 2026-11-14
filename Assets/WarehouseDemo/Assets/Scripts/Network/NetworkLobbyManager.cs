using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

// Manages Unity Lobby + Relay lifecycle.
// Place on a persistent GO alongside NetworkBootstrapper.
// Requires: NetworkManager GO with UnityTransport component in the scene.
public class NetworkLobbyManager : MonoBehaviour
{
    public static NetworkLobbyManager Instance { get; private set; }

    // Lobby data keys
    const string KEY_RELAY   = "relay";
    const string KEY_STARTED = "started";
    const string KEY_NAME    = "name";
    const string KEY_ROLE    = "role";

    const int   MAX_PLAYERS        = 4;
    const float HEARTBEAT_INTERVAL = 15f;
    const float POLL_INTERVAL      = 1.5f;

    public Lobby  CurrentLobby { get; private set; }
    public bool   IsHost       { get; private set; }
    public string LobbyCode    => CurrentLobby?.LobbyCode;

    public event Action<List<Player>> OnPlayersUpdated;
    public event Action               OnGameStarted;
    public event Action<string>       OnError;

    private Coroutine _heartbeat;
    private Coroutine _poll;
    private string    _myName = "Player";

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void SetMyName(string name) =>
        _myName = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<bool> CreateAsync()
    {
        try
        {
            // Relay: allocate slots for up to (MAX_PLAYERS - 1) clients
            Allocation alloc = await RelayService.Instance.CreateAllocationAsync(MAX_PLAYERS - 1);
            string relayCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);

            CurrentLobby = await LobbyService.Instance.CreateLobbyAsync(
                SteamBranding.GameName,
                MAX_PLAYERS,
                new CreateLobbyOptions
                {
                    IsPrivate = false,
                    Data = new Dictionary<string, DataObject>
                    {
                        { KEY_RELAY,   new DataObject(DataObject.VisibilityOptions.Member, relayCode) },
                        { KEY_STARTED, new DataObject(DataObject.VisibilityOptions.Member, "0") }
                    },
                    Player = MakePlayer("—")
                }
            );
            IsHost = true;

            ApplyRelayHost(alloc);
            NetworkManager.Singleton.StartHost();

            _heartbeat = StartCoroutine(HeartbeatLoop());
            _poll      = StartCoroutine(PollLoop());
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Lobby] CreateAsync failed: {ex.Message}");
            OnError?.Invoke("Failed to create game");
            return false;
        }
    }

    // ── Join ──────────────────────────────────────────────────────────────────

    public async Task<bool> JoinAsync(string lobbyCode)
    {
        try
        {
            CurrentLobby = await LobbyService.Instance.JoinLobbyByCodeAsync(
                lobbyCode.Trim().ToUpper(),
                new JoinLobbyByCodeOptions { Player = MakePlayer("—") }
            );
            IsHost = false;

            string relayCode = CurrentLobby.Data[KEY_RELAY].Value;
            JoinAllocation joinAlloc = await RelayService.Instance.JoinAllocationAsync(relayCode);

            ApplyRelayClient(joinAlloc);
            NetworkManager.Singleton.StartClient();

            _poll = StartCoroutine(PollLoop());
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Lobby] JoinAsync failed: {ex.Message}");
            OnError?.Invoke("Invalid code or game unavailable");
            return false;
        }
    }

    // ── Role sync ─────────────────────────────────────────────────────────────

    public async Task SetRoleAsync(string roleDisplayName)
    {
        if (CurrentLobby == null) return;
        try
        {
            CurrentLobby = await LobbyService.Instance.UpdatePlayerAsync(
                CurrentLobby.Id,
                AuthenticationService.Instance.PlayerId,
                new UpdatePlayerOptions
                {
                    Data = new Dictionary<string, PlayerDataObject>
                    {
                        { KEY_NAME, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, _myName) },
                        { KEY_ROLE, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, roleDisplayName) }
                    }
                }
            );
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Lobby] SetRoleAsync failed: {ex.Message}");
        }
    }

    // ── Start game (host only) ────────────────────────────────────────────────

    /// Starts the shift: tells the lobby, then walks the host in. Voice chat comes last and has
    /// no say in either.
    ///
    /// The order is the whole point, and it used to be wrong in a way that produced a very strange
    /// picture: the flag went up, then `await JoinVivoxAsync()`, then the host's own transition.
    /// Vivox failing — not signed in, no network, an expired token — threw straight past that
    /// transition into a catch that logged and swallowed it. By then "started" was already public,
    /// so every client's poll saw it and walked into the warehouse alone, while the host sat in
    /// the lobby looking at a start button that had apparently done nothing.
    ///
    /// Now there are two outcomes and both are consistent: the flag is published and everybody
    /// goes, or publishing fails and nobody does. Whether people can talk to each other is not
    /// something that gets a vote on whether the game has begun.
    public async Task StartGameAsync()
    {
        if (!IsHost || CurrentLobby == null) return;

        try
        {
            await LobbyService.Instance.UpdateLobbyAsync(CurrentLobby.Id, new UpdateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>
                {
                    { KEY_STARTED, new DataObject(DataObject.VisibilityOptions.Member, "1") }
                }
            });
        }
        catch (Exception ex)
        {
            // Nobody moves. The clients never saw a start, and neither does the host.
            Debug.LogError($"[Lobby] StartGameAsync could not publish the start: {ex.Message}");
            OnError?.Invoke("Could not start the game");
            return;
        }

        // Told, and now walking in. Clients pick the same moment up from their poll.
        OnGameStarted?.Invoke();

        // Last, and in its own net: a shift that has begun has begun whether or not anybody can
        // hear each other.
        try
        {
            await JoinVivoxAsync();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Lobby] voice chat unavailable: {ex.Message}");
        }
    }

    /// Puts the start flag back down.
    ///
    /// It only ever went up. Set to "0" when the lobby is created and to "1" when the host starts,
    /// and nothing in between — so a lobby that has been played once kept pulling every new joiner
    /// straight into a game, on the strength of a flag left over from the last one. Called whenever
    /// a host is looking at the lobby room, which is by definition a moment when the game has not
    /// started.
    public async Task ResetStartedAsync()
    {
        if (!IsHost || CurrentLobby == null) return;

        try
        {
            await LobbyService.Instance.UpdateLobbyAsync(CurrentLobby.Id, new UpdateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>
                {
                    { KEY_STARTED, new DataObject(DataObject.VisibilityOptions.Member, "0") }
                }
            });
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Lobby] could not clear the start flag: {ex.Message}");
        }
    }

    // ── Vivox integration ─────────────────────────────────────────────────────

    async Task JoinVivoxAsync()
    {
        var vivox = VivoxManager.Instance;
        if (vivox == null) return;

        string displayName = _myName;
        await vivox.LoginAsync(displayName);

        if (CurrentLobby != null)
            await vivox.JoinChannelAsync(CurrentLobby.LobbyCode);
    }

    // ── Leave ─────────────────────────────────────────────────────────────────

    public async void LeaveAsync()
    {
        if (_heartbeat != null) StopCoroutine(_heartbeat);
        if (_poll      != null) StopCoroutine(_poll);

        if (CurrentLobby != null)
        {
            try
            {
                if (IsHost)
                    await LobbyService.Instance.DeleteLobbyAsync(CurrentLobby.Id);
                else
                    await LobbyService.Instance.RemovePlayerAsync(
                        CurrentLobby.Id, AuthenticationService.Instance.PlayerId);
            }
            catch { /* lobby may already be gone */ }
            CurrentLobby = null;
        }

        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.Shutdown();
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    private void ApplyRelayHost(Allocation alloc)
    {
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetRelayServerData(new RelayServerData(alloc, "dtls"));
    }

    private void ApplyRelayClient(JoinAllocation joinAlloc)
    {
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetRelayServerData(new RelayServerData(joinAlloc, "dtls"));
    }

    // ── Coroutines ────────────────────────────────────────────────────────────

    private IEnumerator HeartbeatLoop()
    {
        var delay = new WaitForSeconds(HEARTBEAT_INTERVAL);
        while (CurrentLobby != null)
        {
            LobbyService.Instance.SendHeartbeatPingAsync(CurrentLobby.Id);
            yield return delay;
        }
    }

    /// How long a client will wait, after seeing the start flag, for its own connection to come
    /// up before giving up and saying so. Several polls: Relay can take a moment.
    const float START_CONNECT_TIMEOUT = 10f;

    private IEnumerator PollLoop()
    {
        var   delay        = new WaitForSeconds(POLL_INTERVAL);
        float waitingSince = 0f;
        while (CurrentLobby != null)
        {
            yield return delay;
            var task = LobbyService.Instance.GetLobbyAsync(CurrentLobby.Id);
            yield return new WaitUntil(() => task.IsCompleted);
            if (task.Exception != null) continue;

            CurrentLobby = task.Result;
            OnPlayersUpdated?.Invoke(CurrentLobby.Players);

            // Clients watch for host start signal
            if (!IsHost
                && CurrentLobby.Data.TryGetValue(KEY_STARTED, out var v)
                && v.Value == "1")
            {
                // The flag says the game has begun; it says nothing about whether this machine has
                // a session to join. Walking in on an unconnected client puts him in an empty
                // warehouse that never fills, which reads as the game being broken rather than as
                // a connection that has not finished.
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsConnectedClient)
                {
                    waitingSince += POLL_INTERVAL;
                    if (waitingSince < START_CONNECT_TIMEOUT) continue;

                    Debug.LogError("[Lobby] the game started but this client never connected");
                    OnError?.Invoke("Could not connect to the game");
                    yield break;
                }

                // Client also joins Vivox when host starts the game
                StartCoroutine(JoinVivoxCoroutine());
                OnGameStarted?.Invoke();
                yield break;
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    System.Collections.IEnumerator JoinVivoxCoroutine()
    {
        var task = JoinVivoxAsync();
        yield return new UnityEngine.WaitUntil(() => task.IsCompleted);
    }

    private Player MakePlayer(string role) =>
        new Player(
            id: AuthenticationService.Instance.PlayerId,
            data: new Dictionary<string, PlayerDataObject>
            {
                { KEY_NAME, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, _myName) },
                { KEY_ROLE, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, role) }
            }
        );

    public static string GetName(Player p) =>
        p.Data != null && p.Data.TryGetValue(KEY_NAME, out var v) ? v.Value : "???";

    public static string GetRole(Player p) =>
        p.Data != null && p.Data.TryGetValue(KEY_ROLE, out var v) ? v.Value : "—";
}
