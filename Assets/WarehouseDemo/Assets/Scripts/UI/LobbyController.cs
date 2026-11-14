using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Services.Authentication;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using UnityEngine.UIElements;

/// Manages the multiplayer lobby UI panel embedded inside MainMenu.uxml.
/// Attach to the same GameObject as MainMenuController.
[RequireComponent(typeof(UIDocument))]
public class LobbyController : MonoBehaviour
{
    public static LobbyController Instance { get; private set; }

    // Root elements
    VisualElement _mainCard;
    VisualElement _lobbyCard;
    VisualElement _viewConnect;
    VisualElement _viewRoom;

    // Connect view
    TextField _inputName;
    TextField _inputCode;
    Label     _errorLabel;
    Button    _createBtn;
    Button    _joinBtn;
    Button    _backBtn;

    // Room view
    Label         _codeDisplay;
    Label         _playerCount;
    VisualElement _playerList;
    Label         _statusLabel;
    Label         _roleHint;
    Label         _rolePerkName;
    Label         _rolePerkSummary;
    Label[]       _rolePerkItems;
    Label         _teamBonus;
    Button        _startBtn;
    Button        _leaveBtn;
    Button        _readyBtn;
    Label         _readyHint;
    Button[]      _roleBtns;

    int _lastPlayerCount;

    static readonly string[] RoleNames        = { "PICKER", "ASSEMBLER", "DISPATCHER", "FORKLIFT" };
    static readonly string[] RoleDisplayNames = { "Picker", "Assembler", "Dispatcher", "Forklift" };
    static readonly string[] RolePerkSummaries =
    {
        "Fast warehouse runner focused on locating and retrieving order parts.",
        "Bench specialist who installs and packs order components without delay.",
        "Team coordinator with exclusive planning tools and full warehouse awareness.",
        "Heavy-cargo specialist with precise vehicle control and faster repairs.",
    };
    static readonly string[][] RolePerks =
    {
        new[] { "+25% movement speed", "Required boxes highlighted within 20 m", "Specialist for Urgent Part events" },
        new[] { "Instant part placement at the assembly bench", "No 1.2 second non-specialist placement delay", "Primary assembly and packing role" },
        new[] { "Accepts orders while a Dispatcher is present", "Exclusive warehouse minimap and team pings", "Repairs radio interference events" },
        new[] { "+10% movement speed", "Full forklift speed and turning control", "Repairs vehicle faults in 10 seconds instead of 25" },
    };

    // Maps button index (UXML order: Picker, Assembler, Dispatcher, ForkLiftOperator)
    // to PlayerRoleType enum value (None=0, Picker=1, Assembler=2, ForkLiftOperator=3, Dispatcher=4).
    // Enum puts ForkLift before Dispatcher, UI puts Dispatcher before ForkLift — hence explicit map.
    static readonly int[] RoleButtonToEnumValue = { 1, 2, 4, 3 };

    // In-memory, not PlayerPrefs: PlayerPrefs is shared registry storage per machine/product,
    // so under MPPM (or any two instances on the same PC) every instance would read the same
    // value regardless of what it picked locally. A static field is scoped to this process.
    public static int SelectedRoleIndex { get; private set; }

    int  _selectedRole;
    bool _busy;

    void Awake()
    {
        if (Instance != null) { Destroy(this); return; }
        Instance = this;
    }

    void Start() => StartCoroutine(InitWhenReady());

    IEnumerator InitWhenReady()
    {
        var doc = GetComponent<UIDocument>();
        while (doc.rootVisualElement == null) yield return null;
        var root = doc.rootVisualElement;
        UiAudioFeedback.Bind(root);

        // ── Root elements ────────────────────────────────────────────────────
        _mainCard    = root.Q<VisualElement>("main-card");
        _lobbyCard   = root.Q<VisualElement>("lobby-card");
        _viewConnect = root.Q<VisualElement>("view-connect");
        _viewRoom    = root.Q<VisualElement>("view-room");

        // ── Connect view ─────────────────────────────────────────────────────
        _inputName  = root.Q<TextField>("input-name");
        _inputCode  = root.Q<TextField>("input-code");
        _errorLabel = root.Q<Label>("lobby-error");
        _createBtn  = root.Q<Button>("btn-create");
        _joinBtn    = root.Q<Button>("btn-join");
        _backBtn    = root.Q<Button>("btn-back");

        _inputName.value = PlayerPrefs.GetString("PlayerName", "");
        _inputCode.value = "";

        _createBtn?.RegisterCallback<ClickEvent>(_ => OnCreateClicked());
        _joinBtn?.RegisterCallback<ClickEvent>(_ => OnJoinClicked());
        _backBtn?.RegisterCallback<ClickEvent>(_ => Hide());

        // ── Room view ────────────────────────────────────────────────────────
        _codeDisplay = root.Q<Label>("code-display");
        _playerCount = root.Q<Label>("player-count");
        _playerList  = root.Q<VisualElement>("player-list");
        _statusLabel = root.Q<Label>("lobby-status");
        _roleHint    = root.Q<Label>("role-hint");
        _rolePerkName = root.Q<Label>("role-perk-name");
        _rolePerkSummary = root.Q<Label>("role-perk-summary");
        _rolePerkItems = new Label[3];
        for (int i = 0; i < _rolePerkItems.Length; i++)
            _rolePerkItems[i] = root.Q<Label>($"role-perk-{i}");
        _teamBonus   = root.Q<Label>("team-bonus");
        _startBtn    = root.Q<Button>("btn-start");
        _leaveBtn    = root.Q<Button>("btn-leave");
        _readyBtn    = root.Q<Button>("btn-ready");
        _readyHint   = root.Q<Label>("ready-hint");

        root.Q<Button>("btn-copy-code")?.RegisterCallback<ClickEvent>(_ => CopyCode());
        _startBtn?.RegisterCallback<ClickEvent>(_ => OnStartClicked());
        _leaveBtn?.RegisterCallback<ClickEvent>(_ => OnLeaveClicked());
        _readyBtn?.RegisterCallback<ClickEvent>(_ => OnReadyClicked());

        // ── Role buttons ──────────────────────────────────────────────────────
        _roleBtns = new Button[4];
        for (int i = 0; i < 4; i++)
        {
            var idx = i;
            _roleBtns[i] = root.Q<Button>($"role-btn-{idx}");
            _roleBtns[i]?.RegisterCallback<ClickEvent>(_ => SelectRole(idx));
        }

        // The pick survives the reload even though this controller does not — SelectedRoleIndex is
        // static. Coming up on 0 again quietly turned a joining Dispatcher back into a Picker.
        int restore = System.Array.IndexOf(RoleButtonToEnumValue, SelectedRoleIndex);
        SelectRole(restore >= 0 ? restore : 0);

        // ── Lobby events ──────────────────────────────────────────────────────
        var lm = NetworkLobbyManager.Instance;
        if (lm != null)
        {
            lm.OnPlayersUpdated += OnPlayersUpdated;
            lm.OnGameStarted    += OnGameStarted;
            lm.OnError          += ShowError;
        }

        // Ready state and role both live on NetworkPlayer (NetworkVariables) -- refresh the
        // ready button and the host's start-button-enabled state whenever either changes,
        // on any player, not just the local one.
        NetworkPlayer.OnRosterChanged += OnRosterChangedUI;

        // Already in a room? Then this is a fresh controller in a scene NGO has just reloaded
        // under a joining client, and the room view it was showing died with the old one.
        //
        // The lobby, the menu and the NetworkManager all live in the warehouse scene, so a client
        // being synchronised has the scene — and every piece of this UI — rebuilt beneath him. The
        // join flow's own ShowRoomView() then ran on a controller that no longer existed, painting
        // an orphaned visual tree, while what the player actually had on screen was a fresh main
        // menu. His READY button was one nobody was listening to.
        if (NetworkLobbyManager.Instance?.CurrentLobby != null)
        {
            _mainCard?.AddToClassList("hidden");
            _lobbyCard?.RemoveFromClassList("hidden");
            ShowRoomView();
        }
    }

    /// Anything about the roster changing — somebody joining, picking a role, pressing READY —
    /// redraws both the list and the buttons. The list is where "who is ready" is read, so it has
    /// to move on the same event the start button does.
    void OnRosterChangedUI()
    {
        SyncLocalRoleSelection();
        RefreshRoleAvailability();
        RefreshRoomState();
        RefreshPlayerList(null);
        RefreshTeamBonus();
    }

    void OnDestroy()
    {
        Instance = null;
        var lm = NetworkLobbyManager.Instance;
        if (lm != null)
        {
            lm.OnPlayersUpdated -= OnPlayersUpdated;
            lm.OnGameStarted    -= OnGameStarted;
            lm.OnError          -= ShowError;
        }

        NetworkPlayer.OnRosterChanged -= OnRosterChangedUI;
    }

    // ── Public show / hide ────────────────────────────────────────────────────

    public void Show()
    {
        _mainCard?.AddToClassList("hidden");
        _lobbyCard?.RemoveFromClassList("hidden");
        ShowConnectView();
    }

    public void Hide()
    {
        _lobbyCard?.AddToClassList("hidden");
        _mainCard?.RemoveFromClassList("hidden");
        ClearError();
    }

    // ── Internal view switches ────────────────────────────────────────────────

    void ShowConnectView()
    {
        _viewConnect?.RemoveFromClassList("hidden");
        _viewRoom?.AddToClassList("hidden");
        SetBusy(false);
        ClearError();
    }

    void ShowRoomView()
    {
        _viewConnect?.AddToClassList("hidden");
        _viewRoom?.RemoveFromClassList("hidden");

        var lm = NetworkLobbyManager.Instance;

        // A host sitting in the room is, by definition, a game that has not started. Said out loud
        // because the flag in the lobby only ever went up: one played and re-opened lobby went on
        // announcing "started" to everybody who joined afterwards, and they dropped into the
        // warehouse while the host was still here choosing a role.
        if (lm != null && lm.IsHost) _ = lm.ResetStartedAsync();

        if (_codeDisplay != null)
            _codeDisplay.text = lm?.LobbyCode ?? "——";

        bool isHost = lm?.IsHost ?? false;
        if (_startBtn != null)
        {
            if (isHost) _startBtn.RemoveFromClassList("hidden");
            else        _startBtn.AddToClassList("hidden");
            _startBtn.SetEnabled(false);
        }

        // The host does not tell himself he is ready — starting the game is that statement, and
        // it is the only one he can make that the others cannot. His button goes; the line under
        // it says what he is waiting for instead.
        if (_readyBtn != null)
        {
            if (isHost) _readyBtn.AddToClassList("hidden");
            else        _readyBtn.RemoveFromClassList("hidden");
        }

        _lastPlayerCount = 0;
        RefreshRoomState();
        RefreshPlayerList(null);
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    async void OnCreateClicked()
    {
        if (_busy) return;
        ClearError();
        if (!TrySaveName()) return;
        SetBusy(true);
        LoadingScreen.Show("Signing in...");
        LoadingScreen.SetProgress(0.10f);

        if (NetworkBootstrapper.Instance != null)
            await NetworkBootstrapper.Instance.InitAsync();

        LoadingScreen.SetProgress(0.35f);
        LoadingScreen.SetStatus("Reserving a relay...");
        LoadingScreen.SetProgress(0.60f);
        bool ok = await NetworkLobbyManager.Instance.CreateAsync();
        SetBusy(false);

        if (!ok) { LoadingScreen.Hide(); return; }

        LoadingScreen.SetStatus("Preparing the lobby...");
        LoadingScreen.SetProgress(0.90f);
        await NetworkLobbyManager.Instance.SetRoleAsync(RoleDisplayNames[_selectedRole]);
        LoadingScreen.SetProgress(1f);
        LoadingScreen.Hide();
        ShowRoomView();
    }

    async void OnJoinClicked()
    {
        if (_busy) return;
        string code = _inputCode?.value?.Trim().ToUpper() ?? "";
        if (string.IsNullOrEmpty(code)) { ShowError("Enter room code"); return; }

        ClearError();
        if (!TrySaveName()) return;
        SetBusy(true);
        LoadingScreen.Show("Signing in...");
        LoadingScreen.SetProgress(0.10f);

        if (NetworkBootstrapper.Instance != null)
            await NetworkBootstrapper.Instance.InitAsync();

        LoadingScreen.SetProgress(0.35f);
        LoadingScreen.SetStatus("Connecting to the host...");
        LoadingScreen.SetProgress(0.55f);
        bool ok = await NetworkLobbyManager.Instance.JoinAsync(code);
        SetBusy(false);

        if (!ok) { LoadingScreen.Hide(); return; }

        // JoinAsync returns the moment StartClient is called, not when the client is in. What
        // follows is NGO synchronising this client — which reloads the scene under him and
        // destroys this controller — so the screen has to be handed over rather than awaited.
        LoadingScreen.SetProgress(0.65f);
        LoadingScreen.HoldUntilConnected("Synchronising with the host...");

        await NetworkLobbyManager.Instance.SetRoleAsync(RoleDisplayNames[_selectedRole]);
        ShowRoomView();
    }

    void OnLeaveClicked()
    {
        NetworkLobbyManager.Instance?.LeaveAsync();
        ShowConnectView();
    }

    async void OnStartClicked()
    {
        if (_startBtn != null) _startBtn.SetEnabled(false);
        await NetworkLobbyManager.Instance.StartGameAsync();
    }

    void CopyCode()
    {
        string code = NetworkLobbyManager.Instance?.LobbyCode;
        if (!string.IsNullOrEmpty(code))
            GUIUtility.systemCopyBuffer = code;
    }

    void SelectRole(int idx)
    {
        if (idx < 0 || idx >= RoleButtonToEnumValue.Length) return;

        var requested = (PlayerRoleType)RoleButtonToEnumValue[idx];
        bool occupied = NetworkPlayer.All.Any(np => np != null
                                                  && np != NetworkPlayer.Local
                                                  && np.RoleType == requested);
        if (occupied)
        {
            if (_roleHint != null)
                _roleHint.text = $"{RoleDisplayNames[idx]} is already assigned to another player.";
            RefreshRoleAvailability();
            return;
        }

        _selectedRole = idx;
        // +1 offset: PlayerRoleType enum starts at 0=None, index 0 = Picker = enum value 1
        SelectedRoleIndex = RoleButtonToEnumValue[idx];

        // Role buttons live in view-room, shown only after Create/Join already called
        // StartHost/StartClient -- NetworkPlayer has already spawned and read whatever
        // SelectedRoleIndex was at that point. Push the change to the live NetworkVariable
        // too, or picking a role here would silently only affect a future spawn that already happened.
        NetworkPlayer.Local?.ApplyRole((PlayerRoleType)SelectedRoleIndex);

        ApplyRoleSelectionVisual(idx);
        if (_roleHint != null) _roleHint.text = string.Empty;
        RefreshRoleAvailability();

        // Sync to lobby if already in room
        var lm = NetworkLobbyManager.Instance;
        if (lm?.CurrentLobby != null)
            _ = lm.SetRoleAsync(RoleDisplayNames[idx]);
    }

    void SyncLocalRoleSelection()
    {
        var local = NetworkPlayer.Local;
        if (local == null || local.RoleType == PlayerRoleType.None) return;

        int actual = (int)local.RoleType;
        int idx = System.Array.IndexOf(RoleButtonToEnumValue, actual);
        if (idx < 0) return;

        bool changedByServer = SelectedRoleIndex != actual;
        _selectedRole = idx;
        SelectedRoleIndex = actual;
        ApplyRoleSelectionVisual(idx);

        if (changedByServer && NetworkLobbyManager.Instance?.CurrentLobby != null)
            _ = NetworkLobbyManager.Instance.SetRoleAsync(RoleDisplayNames[idx]);
    }

    void ApplyRoleSelectionVisual(int idx)
    {
        for (int i = 0; i < _roleBtns.Length; i++)
        {
            if (_roleBtns[i] == null) continue;
            if (i == idx) _roleBtns[i].AddToClassList("role-btn--active");
            else          _roleBtns[i].RemoveFromClassList("role-btn--active");
        }

        if (_rolePerkName != null) _rolePerkName.text = RoleNames[idx];
        if (_rolePerkSummary != null) _rolePerkSummary.text = RolePerkSummaries[idx];
        if (_rolePerkItems != null)
        {
            for (int i = 0; i < _rolePerkItems.Length; i++)
                if (_rolePerkItems[i] != null)
                    _rolePerkItems[i].text = $"✓  {RolePerks[idx][i]}";
        }
    }

    void RefreshRoleAvailability()
    {
        if (_roleBtns == null) return;

        for (int i = 0; i < _roleBtns.Length; i++)
        {
            var button = _roleBtns[i];
            if (button == null) continue;

            var role = (PlayerRoleType)RoleButtonToEnumValue[i];
            bool selected = i == _selectedRole;
            bool occupied = NetworkPlayer.All.Any(np => np != null
                                                      && np != NetworkPlayer.Local
                                                      && np.RoleType == role);

            button.SetEnabled(!occupied || selected);
            button.EnableInClassList("role-btn--occupied", occupied && !selected);
            button.text = occupied && !selected ? $"{RoleNames[i]} · TAKEN" : RoleNames[i];
        }
    }

    // ── Lobby event handlers ──────────────────────────────────────────────────

    void OnPlayersUpdated(List<Player> players)
    {
        int count = players?.Count ?? 0;

        if (_playerCount != null)
            _playerCount.text = $"{count} / 4";

        if (_statusLabel != null)
        {
            _statusLabel.text = count < 2
                ? $"Waiting for players... ({count}/4)"
                : $"Ready to start — {count} player(s)";
        }

        _lastPlayerCount = count;
        RefreshRoomState();

        RefreshPlayerList(players);
        RefreshTeamBonus(players);
    }

    void RefreshTeamBonus(List<Player> lobbyPlayers = null)
    {
        if (_teamBonus == null) return;

        var uniqueRoles = new HashSet<string>();
        if (NetworkPlayer.All.Count > 0)
        {
            foreach (var player in NetworkPlayer.All)
                if (player != null && player.RoleType != PlayerRoleType.None)
                    uniqueRoles.Add(player.RoleType.ToString());
        }
        else if (lobbyPlayers != null)
        {
            foreach (var player in lobbyPlayers)
            {
                string role = NetworkLobbyManager.GetRole(player);
                if (!string.IsNullOrWhiteSpace(role) && role != "—") uniqueRoles.Add(role);
            }
        }

        int roleCount = Mathf.Clamp(uniqueRoles.Count, 0, 4);
        int bonusPercent = Mathf.Max(0, roleCount - 1) * 10;
        _teamBonus.text = bonusPercent > 0
            ? $"ROLE DIVERSITY BONUS  +{bonusPercent}%  ·  {roleCount}/4 SPECIALISTS"
            : "ROLE DIVERSITY BONUS  +0%  ·  CHOOSE DIFFERENT ROLES";
        _teamBonus.EnableInClassList("lobby-team-bonus--active", bonusPercent > 0 && roleCount < 4);
        _teamBonus.EnableInClassList("lobby-team-bonus--full", roleCount == 4);
    }

    /// Updates the "Ready" button label and the host's "Start Game" enabled state.
    /// Called whenever the Lobby's player count changes or any NetworkPlayer's role/ready
    /// state changes (roster, not just the local player -- everyone needs to be ready).
    void RefreshRoomState()
    {
        bool isHost     = NetworkLobbyManager.Instance?.IsHost ?? false;
        bool localReady = NetworkPlayer.Local != null && NetworkPlayer.Local.IsReady.Value;

        if (_readyBtn != null && !isHost)
        {
            _readyBtn.SetEnabled(!localReady);
            _readyBtn.text = localReady ? "✓ READY" : "READY";
        }

        // Everyone but the host. His own readiness is not a thing anybody waits for, and while it
        // was counted the start button stayed dead until he pressed a button meant for the guests.
        int guests = 0, guestsReady = 0;
        foreach (var np in NetworkPlayer.All)
        {
            if (np == null || IsHostPlayer(np)) continue;
            guests++;
            if (np.IsReady.Value) guestsReady++;
        }

        if (_startBtn != null && isHost)
            _startBtn.SetEnabled(guests == guestsReady);

        if (_readyHint != null)
        {
            if (!isHost) { _readyHint.AddToClassList("hidden"); }
            else
            {
                _readyHint.RemoveFromClassList("hidden");
                _readyHint.text = guests == 0
                    ? "Nobody else in the room — you can start alone."
                    : guests == guestsReady
                        ? $"Everyone is ready ({guestsReady}/{guests}) — start when you like."
                        : $"Waiting for READY: {guestsReady}/{guests}.";
            }
        }
    }

    /// The room's host, as the network sees him — NGO always gives the server client id 0.
    static bool IsHostPlayer(NetworkPlayer np) =>
        np.OwnerClientId == Unity.Netcode.NetworkManager.ServerClientId;

    void OnReadyClicked()
    {
        if (NetworkPlayer.Local != null) { NetworkPlayer.Local.SetReadyServerRpc(); return; }

        // Pressed before this client's own player object has spawned — which is exactly when a
        // joining player first looks at this button. It used to be dropped on the floor: the
        // null-conditional call did nothing and said nothing, and the button sat there saying
        // READY as if it had never been touched.
        if (_readyBtn != null) { _readyBtn.SetEnabled(false); _readyBtn.text = "CONNECTING..."; }
        StartCoroutine(SendReadyWhenSpawned());
    }

    IEnumerator SendReadyWhenSpawned()
    {
        const float timeout = 10f;
        float waited = 0f;

        while (NetworkPlayer.Local == null && waited < timeout)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        if (NetworkPlayer.Local != null)
        {
            NetworkPlayer.Local.SetReadyServerRpc();
            RefreshRoomState();
            yield break;
        }

        if (_readyBtn != null) { _readyBtn.SetEnabled(true); _readyBtn.text = "READY"; }
        ShowError("Still connecting to the host — try READY again in a moment");
    }

    void RefreshPlayerList(List<Player> players)
    {
        if (_playerList == null) return;

        // Built from the network roster whenever there is one, because that is where readiness
        // lives and it changes the instant somebody presses the button. The Lobby service's own
        // player list is a poll behind and knows nothing about READY; it is the fallback for the
        // seconds before the roster exists.
        if (NetworkPlayer.All.Count > 0) { RefreshPlayerListFromRoster(); return; }

        _playerList.Clear();

        if (players == null || players.Count == 0)
        {
            var empty = new Label("Waiting for connection...");
            empty.AddToClassList("lobby-player-empty");
            _playerList.Add(empty);
            return;
        }

        string myId = "";
        try { myId = AuthenticationService.Instance.PlayerId; } catch { }

        foreach (var p in players)
        {
            string name  = NetworkLobbyManager.GetName(p);
            string role  = NetworkLobbyManager.GetRole(p);
            bool   isMe  = p.Id == myId;

            var row = new VisualElement();
            row.AddToClassList("lobby-player-row");
            if (isMe) row.AddToClassList("lobby-player-row--me");

            var dot = new VisualElement();
            dot.AddToClassList("lobby-player-dot");

            var nameLbl = new Label(isMe ? $"{name}  (YOU)" : name);
            nameLbl.AddToClassList("lobby-player-name");

            var roleLbl = new Label(role == "—" ? "..." : role.ToUpper());
            roleLbl.AddToClassList("lobby-player-role");

            row.Add(dot);
            row.Add(nameLbl);
            row.Add(roleLbl);
            _playerList.Add(row);
        }
    }

    void RefreshPlayerListFromRoster()
    {
        _playerList.Clear();

        foreach (var np in NetworkPlayer.All)
        {
            if (np == null) continue;

            bool isMe   = np.IsOwner;
            bool isHost = IsHostPlayer(np);

            var row = new VisualElement();
            row.AddToClassList("lobby-player-row");
            if (isMe) row.AddToClassList("lobby-player-row--me");

            var dot = new VisualElement();
            dot.AddToClassList("lobby-player-dot");
            row.Add(dot);

            string name = string.IsNullOrEmpty(np.PlayerName) ? "Player" : np.PlayerName;
            var nameLbl = new Label(isMe ? $"{name}  (YOU)" : name);
            nameLbl.AddToClassList("lobby-player-name");
            row.Add(nameLbl);

            RoleLabelFor(np, out string roleText);
            var roleLbl = new Label(roleText);
            roleLbl.AddToClassList("lobby-player-role");
            row.Add(roleLbl);

            if (isHost)
            {
                var hostFlag = new Label("HOST");
                hostFlag.AddToClassList("lobby-player-flag");
                hostFlag.AddToClassList("lobby-player-flag--host");
                row.Add(hostFlag);
            }
            else
            {
                bool ready = np.IsReady.Value;
                var flag = new Label(ready ? "READY" : "NOT READY");
                flag.AddToClassList("lobby-player-flag");
                flag.AddToClassList(ready ? "lobby-player-flag--ready" : "lobby-player-flag--waiting");
                row.Add(flag);
            }

            _playerList.Add(row);
        }
    }

    static void RoleLabelFor(NetworkPlayer np, out string text)
    {
        text = np.RoleType switch
        {
            PlayerRoleType.Picker           => "PICKER",
            PlayerRoleType.Assembler        => "ASSEMBLER",
            PlayerRoleType.Dispatcher       => "DISPATCHER",
            PlayerRoleType.ForkLiftOperator => "FORKLIFT",
            _                               => "...",
        };
    }

    void OnGameStarted()
    {
        MainMenuController.Instance?.StartMultiplayer();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    bool TrySaveName()
    {
        string name = _inputName?.value?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            ShowError("Enter your nickname");
            _inputName?.Focus();
            return false;
        }
        PlayerPrefs.SetString("PlayerName", name);
        PlayerPrefs.Save();
        NetworkLobbyManager.Instance?.SetMyName(name);
        return true;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        if (_createBtn != null) _createBtn.SetEnabled(!busy);
        if (_joinBtn   != null) _joinBtn.SetEnabled(!busy);
        if (_statusLabel != null && _viewRoom != null && _viewRoom.ClassListContains("hidden"))
        {
            // show spinner hint only on connect view
        }
    }

    void ShowError(string msg)
    {
        if (_errorLabel == null) return;
        _errorLabel.text = msg;
        _errorLabel.RemoveFromClassList("hidden");
    }

    void ClearError() => _errorLabel?.AddToClassList("hidden");
}
