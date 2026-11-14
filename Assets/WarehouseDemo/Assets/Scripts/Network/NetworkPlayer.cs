using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// NetworkBehaviour placed on each player's NetworkObject prefab.
/// Syncs: display name, role and speaking indicator for the co-op HUD.
/// Soft enforcement: roles give bonuses, not hard restrictions.
public class NetworkPlayer : NetworkBehaviour
{
    // ── Singleton for local player ─────────────────────────────────────────────
    public static NetworkPlayer Local { get; private set; }

    // Registry of all connected players (client + server)
    static readonly List<NetworkPlayer> _all = new();
    public static IReadOnlyList<NetworkPlayer> All => _all;

    public static event System.Action OnRosterChanged;

    // ── Synced state (everyone can read; only server writes except where noted) ─
    public NetworkVariable<FixedString64Bytes> DisplayName = new(
        new FixedString64Bytes("Player"),
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<int> RoleIndex = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<float> BatteryPercentNet = new(
        100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<float> BatteryCapacityNet = new(
        100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<bool> IsChargingNet = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<bool> FastChargingNet = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<int> ChargingStationNet = new(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<bool> IsSpeaking = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Confirmed via the Lobby "Ready" button. Reset to false whenever the role changes,
    // so a stale confirmation can never let the host start with unapplied changes.
    public NetworkVariable<bool> IsReady = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// What this player is holding, as MeleeWeaponKind. State rather than an event, so a client
    /// joining mid-shift sees the bats already in the right hands.
    public NetworkVariable<int> WeaponKind = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Crushed-by-shelf death state. Server-authoritative so it survives late joins (unlike a
    // one-shot ClientRpc, a NetworkVariable replays its current value to anyone who connects
    // after the death already happened).
    public NetworkVariable<bool> IsDeadNet = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<Vector3> CrushPositionNet = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Canonical player-condition state. Clients may predict the local presentation, but only the
    // server publishes the state consumed by AI, late joiners and the other players.
    public NetworkVariable<bool> IsRagdolledNet = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<Vector3> RagdollImpulseNet = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<Vector3> RagdollHitPointNet = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<uint> RagdollVersionNet = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // ── Injuries ──────────────────────────────────────────────────────────────
    //
    // What is broken on this player, packed into one int: two bits per body part. The owner still
    // performs the local classification/presentation, then submits a monotonically-versioned
    // proposal; the server validates and publishes the persistent result.
    //
    // State rather than an event, and that is the point of a NetworkVariable here: somebody
    // joining mid-shift sees the arm that came off ten minutes ago, which a one-shot RPC could
    // never tell them.
    public NetworkVariable<int> InjuryMask = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Which parts of this body were fitted out of somebody else's, and whose livery they wear:
    // three bits per part, holding a PlayerRoleType plus one so zero can mean "its own paint".
    //
    // Beside the injury mask rather than inside it, because the two answer different questions and
    // move at different times — a part can be grafted and then broken off again — and because a
    // late joiner needs both answers independently. State, for the same reason as the mask: the
    // beige arm on the violet robot has to still be beige for somebody who connects an hour in.
    public NetworkVariable<int> GraftMask = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // True while a leg is gone and this player is hopping rather than walking. Separate from the
    // mask, even though it could be derived from it, because the puppet reacts to it completely
    // differently: the mask repaints a body, this swaps its locomotion.
    public NetworkVariable<bool> IsHobbling = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<uint> InjuryVersionNet = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<uint> LifeStateVersionNet = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<double> RespawnDeadlineNet = new(
        0d,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // ── Convenience accessors ─────────────────────────────────────────────────
    public PlayerRoleType RoleType => (PlayerRoleType)RoleIndex.Value;
    public string PlayerName      => DisplayName.Value.ToString();

    [Header("TPS Camera")]
    [SerializeField] private GameObject _cameraRoot;

    // Visible character body shown to other players. Hidden for the local owner
    // (first-person — you don't see your own body).
    [Header("Character Model")]
    [SerializeField] private GameObject _characterModel;

    NetworkPlayerNameplate _nameplate;

    const float MaxRagdollImpulse = 65f;
    const float RagdollPointRange = 8f;
    const float MinRagdollServerDuration = 0.5f;
    const double MaxRagdollServerDuration = 8d;
    const float RescueRange = 3.25f;
    const float RescueHoldServerSeconds = 2.75f;
    const float MultiplayerAutoRespawnSeconds = 90f;
    const ulong NoRescueTarget = ulong.MaxValue;

    double _ragdollStartedServerTime;
    uint _lastInjuryProposal;
    double _serverNextRepairAllowed;
    uint _nextInjuryIncidentToken;
    uint _pendingInjuryIncidentToken;
    BodyPart _pendingInjuryPart;
    RemoteInjuryVisual.State _pendingInjuryMaxState;
    double _pendingInjuryIncidentExpires;
    ulong _rescueTargetId = NoRescueTarget;
    double _rescueStartedServerTime;
    Vector3 _serverSpawnPoint;

    uint _serverChargeSequence;
    uint _serverSwingSequence;
    double _serverChargeStarted;
    double _serverSwingHitOpens;
    double _serverSwingExpires;
    double _serverNextSwingAllowed;
    double _lastPingTokenTime;
    float _pingTokens = PingBurst;

    const float PingBurst = 3f;
    const double PingTokenInterval = 0.5d;
    int _serverSwingWeaponKind;
    bool _serverSwingHeavy;
    Vector3 _serverSwingAim;
    MeleeCombat.NetworkWeaponStats _serverSwingStats;
    int _serverSwingHitsRemaining;
    readonly HashSet<ulong> _serverSwingTargets = new();

    // The owner's visible, controllable character is the scene Player rig. The NetworkPlayer
    // object also carries a RagdollController for its remote/network representation, but that
    // representation is deliberately hidden on the owning client. Driving the component on this
    // object therefore made every observer see the owner fall while the owner stayed upright.
    RagdollController _ownerRagdollController;
    InjurySystem _ownerInjurySystem;
    MeleeCombat _ownerMeleeCombat;

    /// Finds a component on the scene's visible, controllable player rig rather than on one of
    /// the NetworkPlayer representations. CharacterVisual is also tagged "Player", so
    /// FindGameObjectWithTag was nondeterministic: depending on hierarchy order it returned the
    /// hidden owner puppet and locally held items were parented to an invisible skeleton.
    static T FindOwnerGameplayComponent<T>() where T : Component
    {
        foreach (var component in Object.FindObjectsByType<T>(FindObjectsInactive.Exclude))
        {
            if (component == null) continue;
            if (component.GetComponentInParent<NetworkPlayer>() != null) continue;
            return component;
        }

        return null;
    }

    RagdollController ResolveOwnerRagdollController()
    {
        if (!IsOwner) return null;
        if (_ownerRagdollController != null) return _ownerRagdollController;

        _ownerRagdollController = FindOwnerGameplayComponent<RagdollController>();

        // Offline tests and prefabs that use one object for both roles still work.
        if (_ownerRagdollController == null)
            _ownerRagdollController = GetComponent<RagdollController>();

        return _ownerRagdollController;
    }

    InjurySystem ResolveOwnerInjurySystem()
    {
        if (!IsOwner) return null;
        if (_ownerInjurySystem != null) return _ownerInjurySystem;

        // As with ragdoll, the authoritative NetworkPlayer has an InjurySystem for its network
        // representation, while the owner actually sees and controls the scene Player rig. The
        // server-issued incident token must reach the latter or its locally predicted severing can
        // never publish InjuryMask back to the server.
        _ownerInjurySystem = FindOwnerGameplayComponent<InjurySystem>();

        if (_ownerInjurySystem == null)
            _ownerInjurySystem = GetComponent<InjurySystem>();

        return _ownerInjurySystem;
    }

    MeleeCombat ResolveOwnerMeleeCombat()
    {
        if (!IsOwner) return null;
        if (_ownerMeleeCombat != null) return _ownerMeleeCombat;

        // The owner controls and sees the scene Player rig. The MeleeCombat on this NetworkPlayer
        // belongs to its hidden network representation and remains useful to the server for combat
        // timing, but parenting a held item to it makes that item invisible only to its owner.
        _ownerMeleeCombat = FindOwnerGameplayComponent<MeleeCombat>();

        // Offline tests and prefabs that combine both roles still use the component here.
        if (_ownerMeleeCombat == null)
            _ownerMeleeCombat = GetComponent<MeleeCombat>();

        return _ownerMeleeCombat;
    }


    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public override void OnNetworkSpawn()
    {
        _all.Add(this);
        if (IsOwner)
        {
            Local = this;
            if (GetComponent<RobotBatteryController>() == null)
                gameObject.AddComponent<RobotBatteryController>();
            PlayerDeathManager.BindLocal(GetComponent<PlayerDeathManager>());
            RagdollController.BindLocal(ResolveOwnerRagdollController());
            InjurySystem.BindLocal(ResolveOwnerInjurySystem());
            MeleeCombat.BindLocal(ResolveOwnerMeleeCombat());
        }
        OnRosterChanged?.Invoke();

        if (_cameraRoot != null)
            _cameraRoot.SetActive(IsOwner);

        // For local owner: keep CharacterVisual ACTIVE so NetworkAnimator can sync
        // animation state (NGO checks Animator.enabled before sending — disabled = no sync).
        // Hide only the renderers so the local player doesn't see a duplicate body.
        if (_characterModel != null && IsOwner)
        {
            foreach (var r in _characterModel.GetComponentsInChildren<Renderer>(true))
                r.enabled = false;
        }

        _nextVisibilityCheck = Time.time + VisibilityCheckInterval;

        // Fires with the current value too, so a puppet that was already armed when we connected
        // gets its weapon rather than waiting for the next time it changes hands.
        WeaponKind.OnValueChanged += OnWeaponChanged;
        OnWeaponChanged(0, WeaponKind.Value);

        // Rebuild HUD roster on all clients whenever any player's role or ready state changes
        DisplayName.OnValueChanged += OnDisplayNameChanged;
        RoleIndex.OnValueChanged   += OnRoleChanged;
        IsReady.OnValueChanged     += OnReadyChanged;

        // Death state: everyone (including late joiners, via the initial value sync) reflects
        // this player's crushed-marker visibility. Fires with the already-current value too.
        IsDeadNet.OnValueChanged += OnDeadStateChanged;
        OnDeadStateChanged(false, IsDeadNet.Value);

        IsRagdolledNet.OnValueChanged += OnRagdollStateChanged;
        OnRagdollStateChanged(false, IsRagdolledNet.Value);

        if (IsServer)
            _serverSpawnPoint = transform.position;

        if (!IsOwner)
        {
            // These components own local controls/physics/state. Remote bodies are represented by
            // NetworkVariables plus RemoteRagdollVisual/RemoteInjuryVisual instead.
            var localRagdoll = GetComponent<RagdollController>();
            if (localRagdoll != null) localRagdoll.enabled = false;
            var localInjuries = GetComponent<InjurySystem>();
            if (localInjuries != null) localInjuries.enabled = false;
            var localDeath = GetComponent<PlayerDeathManager>();
            if (localDeath != null) localDeath.enabled = false;
            var localCombat = GetComponent<MeleeCombat>();
            if (localCombat != null) localCombat.enabled = false;

            // Kill input and movement on remote players so they don't steal local input
            var playerInput = GetComponent<UnityEngine.InputSystem.PlayerInput>();
            if (playerInput != null) playerInput.enabled = false;

            foreach (var mb in GetComponents<MonoBehaviour>())
            {
                var t = mb.GetType().Name;
                if (t is "FirstPersonController" or "ThirdPersonController" or "StarterAssetsInputs" or "TPSCameraController")
                    mb.enabled = false;
            }

            var interaction = GetComponent<PlayerInteraction>();
            if (interaction != null) interaction.enabled = false;
            var highlighter  = GetComponent<PartHighlighter>();
            if (highlighter  != null) highlighter.enabled  = false;

            _nameplate = GetComponent<NetworkPlayerNameplate>();
            if (_nameplate == null) _nameplate = gameObject.AddComponent<NetworkPlayerNameplate>();
            _nameplate.Initialize(this);
            return;
        }

        Local = this;

        // Move the scene's single Player rig to a per-client spawn point before anything else
        // runs -- without this every client shares the rig's one fixed scene position, so all
        // players spawn stacked on top of each other.
        var spawnPoint = PlayerSpawnPoints.Instance?.GetPointFor(OwnerClientId);
        if (spawnPoint != null)
        {
            var playerRig = ResolveOwnerMeleeCombat()?.gameObject;
            if (playerRig != null)
            {
                var cc = playerRig.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                playerRig.transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);
                if (cc != null) cc.enabled = true;
            }
        }

        // Push local player name to server
        string name = PlayerPrefs.GetString("PlayerName", "Player");
        SetDisplayNameServerRpc(name);

        // Push role selected in lobby (in-memory on LobbyController -- not PlayerPrefs, which
        // is shared registry storage per machine/product and would collide across instances
        // running on the same PC, e.g. under Multiplayer Play Mode).
        int roleIdx = LobbyController.SelectedRoleIndex;
        if (roleIdx > 0) SetRoleServerRpc(roleIdx);

    }

    // ── Team-mate visibility watchdog ─────────────────────────────────────────
    //
    // "I don't always see the other player — his box floats through the air on its own" is a
    // report with no known cause in this code: nothing here hides a team-mate's body, and only the
    // owner's own copy is ever made invisible. So rather than guess, this checks the one thing that
    // would produce exactly that picture — a puppet whose body is switched off — puts it back, and
    // says so in the log, which is the evidence the next session needs.
    const float VisibilityCheckInterval = 1f;
    float _nextVisibilityCheck;
    float _batteryTickAccumulator;
    float _chargingBillingRemainder;
    Animator _chargingPoseAnimator;
    float _chargingPoseWeight;
    static readonly int ChargingPoseSpeedId = Animator.StringToHash("Speed");

    void Update()
    {
        if (IsSpawned && IsServer) TickBatteryServer();

        // A world impact is server-authoritative, but recovery is normally reported by the
        // owning client after its local ragdoll animation completes. If that presentation RPC is
        // ever missed (or the local controller cannot start), leaving this bit set forever makes
        // the player "inactive" to every server transaction -- including picking up boxes. The
        // deadline is only a safety net; a healthy owner clears the state much earlier.
        if (IsSpawned && IsServer && IsRagdolledNet.Value &&
            NetworkManager.ServerTime.Time - _ragdollStartedServerTime >=
            MaxRagdollServerDuration)
        {
            IsRagdolledNet.Value = false;
            LifeStateVersionNet.Value++;
        }

        if (IsSpawned && IsServer && IsDeadNet.Value && RespawnDeadlineNet.Value > 0d
            && NetworkManager.ServerTime.Time >= RespawnDeadlineNet.Value)
        {
            ApplyAutoRespawnPenaltyClientRpc(0.50f);
            ServerCompleteRespawn(_serverSpawnPoint, viaForklift: false, autoRespawn: true);
        }

        if (!IsSpawned || IsOwner || _characterModel == null) return;
        if (Time.time < _nextVisibilityCheck) return;
        _nextVisibilityCheck = Time.time + VisibilityCheckInterval;

        bool wasInactive = !_characterModel.activeSelf;
        if (wasInactive) _characterModel.SetActive(true);

        var renderers = _characterModel.GetComponentsInChildren<Renderer>(true);
        int off = 0;
        foreach (var r in renderers) if (!r.enabled) off++;

        // Only when the WHOLE body is dark. A limb that came off is hidden on purpose (see
        // BotDamageVisual), and one piece missing is a game state, not a fault — every piece
        // missing is not something any code here does to a team-mate's puppet.
        bool allDark = renderers.Length > 0 && off == renderers.Length;
        if (!wasInactive && !allDark) return;

        foreach (var r in renderers) r.enabled = true;

        Debug.LogWarning($"[NetworkPlayer] '{PlayerName}' (client {OwnerClientId}) was invisible: " +
                         $"body inactive={wasInactive}, renderers off={off}/{renderers.Length}. Put back.");
    }

    void LateUpdate()
    {
        if (!IsSpawned) return;
        if (_chargingPoseAnimator == null)
        {
            var visualRoot = _characterModel != null ? _characterModel : gameObject;
            _chargingPoseAnimator = visualRoot.GetComponentInChildren<Animator>(true);
        }
        if (_chargingPoseAnimator == null) return;

        bool docked = IsChargingNet.Value && _chargingPoseAnimator.GetFloat(ChargingPoseSpeedId) < 0.1f;
        float step = Time.unscaledDeltaTime / ChargingPoseUtility.BlendSeconds;
        _chargingPoseWeight = Mathf.MoveTowards(_chargingPoseWeight, docked ? 1f : 0f, step);
        ChargingPoseUtility.Apply(_chargingPoseAnimator, _chargingPoseWeight);
    }

    void TickBatteryServer()
    {
        _batteryTickAccumulator += Time.deltaTime;
        if (_batteryTickAccumulator < 0.25f) return;
        float dt = _batteryTickAccumulator;
        _batteryTickAccumulator = 0f;

        if (!IsChargingNet.Value)
        {
            if (GameManager.Instance?.GameStarted == true)
                BatteryPercentNet.Value = Mathf.Max(0f,
                    BatteryPercentNet.Value - RobotBatteryController.OfflineDrainPerSecond * dt);
            return;
        }

        var station = ChargingStation.ByIndex(ChargingStationNet.Value);
        if (station == null || Vector3.Distance(transform.position, station.transform.position) > 7f)
        {
            StopChargingServer();
            return;
        }

        float rate = FastChargingNet.Value
            ? RobotBatteryController.FastRatePerSecond
            : RobotBatteryController.SlowRatePerSecond;
        int price = FastChargingNet.Value
            ? RobotBatteryController.FastPricePerPercent
            : RobotBatteryController.SlowPricePerPercent;
        float gained = Mathf.Min(BatteryCapacityNet.Value - BatteryPercentNet.Value, rate * dt);
        if (gained <= 0f) { StopChargingServer(); return; }

        _chargingBillingRemainder += gained * price;
        int bill = Mathf.FloorToInt(_chargingBillingRemainder);
        if (bill > 0)
        {
            if (GameManager.Instance == null || !GameManager.Instance.TrySpend(bill))
            {
                StopChargingServer();
                ChargingRejectedClientRpc("Team budget is too low.", OwnerOnly());
                return;
            }
            _chargingBillingRemainder -= bill;
        }

        BatteryPercentNet.Value = Mathf.Min(BatteryCapacityNet.Value, BatteryPercentNet.Value + gained);
        if (BatteryPercentNet.Value >= BatteryCapacityNet.Value - 0.001f) StopChargingServer();
    }

    [ServerRpc]
    public void RequestStartChargingServerRpc(int stationIndex, bool fast)
    {
        if (IsChargingNet.Value || BatteryPercentNet.Value >= BatteryCapacityNet.Value - 0.1f) return;
        int minimumChargeCost = fast
            ? RobotBatteryController.FastPricePerPercent
            : RobotBatteryController.SlowPricePerPercent;
        if (GameManager.Instance == null || GameManager.Instance.Score < minimumChargeCost)
        {
            ChargingRejectedClientRpc(
                $"Not enough money. {(fast ? "Fast" : "Slow")} charge requires at least ${minimumChargeCost:N0}.",
                OwnerOnly());
            return;
        }

        var station = ChargingStation.ByIndex(stationIndex);
        if (station == null || Vector3.Distance(transform.position, station.transform.position) > 4.5f)
        {
            ChargingRejectedClientRpc("Move closer to the charging station.", OwnerOnly());
            return;
        }

        foreach (var player in All)
        {
            if (player == null || player == this) continue;
            if (player.IsChargingNet.Value && player.ChargingStationNet.Value == stationIndex)
            {
                ChargingRejectedClientRpc("This charging station is occupied.", OwnerOnly());
                return;
            }
        }

        ChargingStationNet.Value = stationIndex;
        FastChargingNet.Value = fast;
        _chargingBillingRemainder = 0f;
        IsChargingNet.Value = true;
    }

    [ServerRpc]
    public void RequestStopChargingServerRpc() => StopChargingServer();

    [ServerRpc]
    public void SetBatteryForTestingServerRpc(float percent, ServerRpcParams rpcParams = default)
    {
        // Host-only QA hook: connected clients must not be able to rewrite their own battery.
        if (rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId) return;
        StopChargingServer();
        BatteryPercentNet.Value = Mathf.Clamp(percent, 0f, BatteryCapacityNet.Value);
    }

    public bool SetBatteryForTestingByHost(float percent)
    {
        if (!IsServer) return false;
        StopChargingServer();
        BatteryPercentNet.Value = Mathf.Clamp(percent, 0f, BatteryCapacityNet.Value);
        return true;
    }

    public float AddBatteryCapacityServer(float amount)
    {
        if (!IsServer) return BatteryCapacityNet.Value;
        BatteryCapacityNet.Value = Mathf.Clamp(BatteryCapacityNet.Value + Mathf.Max(0f, amount), 100f, 200f);
        return BatteryCapacityNet.Value;
    }

    void StopChargingServer()
    {
        IsChargingNet.Value = false;
        FastChargingNet.Value = false;
        ChargingStationNet.Value = -1;
        _chargingBillingRemainder = 0f;
    }

    [ClientRpc]
    void ChargingRejectedClientRpc(string reason, ClientRpcParams rpcParams = default)
    {
        HUDController.Instance?.AddNotification("Charging unavailable", reason,
            HUDController.NotifType.Warning);
    }

    ClientRpcParams OwnerOnly() => new()
    {
        Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
    };

    public override void OnNetworkDespawn()
    {
        _all.Remove(this);
        OnRosterChanged?.Invoke();

        WeaponKind.OnValueChanged  -= OnWeaponChanged;
        DisplayName.OnValueChanged -= OnDisplayNameChanged;
        RoleIndex.OnValueChanged   -= OnRoleChanged;
        IsReady.OnValueChanged     -= OnReadyChanged;
        IsDeadNet.OnValueChanged   -= OnDeadStateChanged;
        IsRagdolledNet.OnValueChanged -= OnRagdollStateChanged;

        if (!IsOwner) return;
        PlayerDeathManager.UnbindLocal(GetComponent<PlayerDeathManager>());
        RagdollController.UnbindLocal(_ownerRagdollController);
        InjurySystem.UnbindLocal(_ownerInjurySystem);
        MeleeCombat.UnbindLocal(_ownerMeleeCombat);
        _ownerRagdollController = null;
        _ownerInjurySystem = null;
        _ownerMeleeCombat = null;
        Local = null;
    }

    // ── ServerRpcs ─────────────────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void SetDisplayNameServerRpc(string name, ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;

        name = (name ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();
        if (name.Length == 0) name = "Player";
        if (name.Length > 32) name = name[..32];
        DisplayName.Value = new FixedString64Bytes(name);
    }

    [ServerRpc(RequireOwnership = false)]
    public void SetRoleServerRpc(int roleIndex, ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;

        var requested = System.Enum.IsDefined(typeof(PlayerRoleType), roleIndex)
            ? (PlayerRoleType)roleIndex
            : PlayerRoleType.None;

        if (requested != PlayerRoleType.None && RoleTakenByAnotherPlayer(requested))
            requested = FirstAvailableRole();

        RoleIndex.Value = (int)requested;
        // Role changed -- any earlier "Ready" confirmation no longer covers the current pick.
        IsReady.Value = false;
        // Notify Dispatcher HUD that team roster changed
        OnRosterChanged?.Invoke();
    }

    bool RoleTakenByAnotherPlayer(PlayerRoleType role)
    {
        foreach (var player in All)
            if (player != null && player != this && player.RoleType == role) return true;
        return false;
    }

    PlayerRoleType FirstAvailableRole()
    {
        PlayerRoleType[] roles =
        {
            PlayerRoleType.Picker,
            PlayerRoleType.Assembler,
            PlayerRoleType.Dispatcher,
            PlayerRoleType.ForkLiftOperator
        };

        foreach (var role in roles)
            if (!RoleTakenByAnotherPlayer(role)) return role;
        return PlayerRoleType.None;
    }

    /// Confirms the currently-selected role, clicked via the Lobby "Ready" button.
    [ServerRpc(RequireOwnership = false)]
    public void SetReadyServerRpc(ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId ||
            RoleType == PlayerRoleType.None)
            return;

        IsReady.Value = true;
        OnRosterChanged?.Invoke();
    }

    [ServerRpc(RequireOwnership = false)]
    public void SetSpeakingServerRpc(bool speaking, ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        IsSpeaking.Value = speaking;
    }

    // ── Injuries ──────────────────────────────────────────────────────────────

    /// Asks the server to put a toolkit in the warehouse, because this player has just been hurt
    /// and cannot repair himself without one.
    ///
    /// It has to be a request rather than a local spawn, and the reason is worth spelling out. The
    /// toolkit is a parallel-local object: every peer instantiates its own copy at a position the
    /// server chose and sent, which is how PowerFailureEvent and the gate breach already do it.
    /// Spawning one directly from InjurySystem broke that in the least visible way possible — the
    /// position comes out of Random, and an injury happens at a different moment on every machine,
    /// so each player would have found a toolkit somewhere else and none of them would have been
    /// looking at the same object.
    ///
    /// Copying the gate's `if (IsClientOnly) return` guard instead would have been worse: injuries
    /// only ever exist on the machine of the player they happened to, so a guard would leave a
    /// client-only player hurt with no tool anywhere and no way back.
    [ServerRpc(RequireOwnership = false)]
    public void RequestToolkitServerRpc(ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId || IsDeadNet.Value ||
            InjuryMask.Value == 0)
            return;

        // One in the building at a time. Held is checked on the server's own copy, which is the
        // only one that can answer for everybody.
        if (ToolkitItem.IsHeld) return;
        if (FindAnyObjectByType<ToolkitItem>() != null) return;

        SpawnToolkitClientRpc(ToolkitItem.PickSpawnPosition());
    }

    [ServerRpc(RequireOwnership = false)]
    public void PublishInjuryStateServerRpc(int injuryMask, int graftMask, bool hobbling,
        uint proposalVersion, uint incidentToken, ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId || IsDeadNet.Value) return;
        if (proposalVersion <= _lastInjuryProposal) return;
        if (!ValidInjuryMask(injuryMask) || !ValidGraftMask(graftMask)) return;
        if (incidentToken == 0u || incidentToken != _pendingInjuryIncidentToken ||
            NetworkManager.ServerTime.Time > _pendingInjuryIncidentExpires)
            return;

        // Exactly one previously-healthy part may change, and only as far as this server-issued
        // incident permits. Healing and repainting are separate server transactions below.
        BodyPart changedPart = BodyPart.None;
        RemoteInjuryVisual.State changedState = RemoteInjuryVisual.State.Fine;
        foreach (var part in RemoteInjuryVisual.Parts)
        {
            var before = RemoteInjuryVisual.Read(InjuryMask.Value, part);
            var after = RemoteInjuryVisual.Read(injuryMask, part);
            if (after == before) continue;
            if (changedPart != BodyPart.None || before != RemoteInjuryVisual.State.Fine ||
                after <= before)
                return;
            changedPart = part;
            changedState = after;
        }
        if (changedPart == BodyPart.None || graftMask != GraftMask.Value ||
            (_pendingInjuryPart != BodyPart.None && changedPart != _pendingInjuryPart) ||
            (int)changedState > (int)_pendingInjuryMaxState)
            return;

        bool bothArmsGone = RemoteInjuryVisual.Read(injuryMask, BodyPart.LeftArm) == RemoteInjuryVisual.State.Severed &&
                            RemoteInjuryVisual.Read(injuryMask, BodyPart.RightArm) == RemoteInjuryVisual.State.Severed;
        bool bothLegsGone = RemoteInjuryVisual.Read(injuryMask, BodyPart.LeftLeg) == RemoteInjuryVisual.State.Severed &&
                            RemoteInjuryVisual.Read(injuryMask, BodyPart.RightLeg) == RemoteInjuryVisual.State.Severed;
        if (bothArmsGone || bothLegsGone) return;

        bool hasSeveredLeg = RemoteInjuryVisual.Read(injuryMask, BodyPart.LeftLeg)
                              == RemoteInjuryVisual.State.Severed
                           || RemoteInjuryVisual.Read(injuryMask, BodyPart.RightLeg)
                              == RemoteInjuryVisual.State.Severed;
        if (hobbling != hasSeveredLeg) return;

        _lastInjuryProposal = proposalVersion;
        _pendingInjuryIncidentToken = 0u;
        _pendingInjuryIncidentExpires = 0d;
        InjuryMask.Value = injuryMask;
        GraftMask.Value = graftMask;
        IsHobbling.Value = hobbling;
        InjuryVersionNet.Value++;
        LifeStateVersionNet.Value++;
    }

    uint ServerOpenInjuryIncident(float impulseMagnitude, string boneName, bool allowAnyPart)
    {
        if (!IsServer || float.IsNaN(impulseMagnitude) || float.IsInfinity(impulseMagnitude) ||
            impulseMagnitude < 0f)
            return 0u;

        _nextInjuryIncidentToken++;
        if (_nextInjuryIncidentToken == 0u) _nextInjuryIncidentToken++;
        _pendingInjuryIncidentToken = _nextInjuryIncidentToken;
        _pendingInjuryPart = allowAnyPart
            ? BodyPart.None
            : BotDamageVisual.PartForBone(boneName ?? string.Empty);
        _pendingInjuryMaxState = allowAnyPart
            ? impulseMagnitude < 25f
                ? RemoteInjuryVisual.State.Dented
                : RemoteInjuryVisual.State.Severed
            : ServerClassifyInjury(impulseMagnitude, boneName ?? string.Empty);
        _pendingInjuryIncidentExpires = NetworkManager.ServerTime.Time + 5d;
        return _pendingInjuryIncidentToken;
    }

    static RemoteInjuryVisual.State ServerClassifyInjury(float impulse, string bone)
    {
        if (impulse < 25f) return RemoteInjuryVisual.State.Dented;
        bool limb = bone.Contains("Arm") || bone.Contains("Hand") || bone.Contains("Shoulder") ||
                    bone.Contains("Leg") || bone.Contains("Foot") || bone.Contains("Toes");
        return impulse < 45f && !limb
            ? RemoteInjuryVisual.State.Dented
            : RemoteInjuryVisual.State.Severed;
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestRepairServerRpc(int encodedPart, bool usesLimb, ulong limbOwnerId,
        bool limbOrphan, uint expectedLimbVersion, ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (sender != OwnerClientId || IsDeadNet.Value || IsRagdolledNet.Value) return;

        var part = (BodyPart)encodedPart;
        if (System.Array.IndexOf(RemoteInjuryVisual.Parts, part) < 0) return;
        var state = RemoteInjuryVisual.Read(InjuryMask.Value, part);
        if (state == RemoteInjuryVisual.State.Fine) return;

        double now = NetworkManager.ServerTime.Time;
        if (now < _serverNextRepairAllowed) return;

        bool severed = state == RemoteInjuryVisual.State.Severed;
        NetworkLimbState limb = default;
        var coop = CoopGameManager.Instance;
        if (severed && (!usesLimb || coop == null ||
            !coop.ServerTryGetHeldLimb(encodedPart, limbOwnerId, limbOrphan, sender,
                expectedLimbVersion, out limb)))
            return;
        if (!severed && usesLimb) return;

        var shop = NetworkShopManager.Instance;
        if (shop == null || !shop.ServerHasRepairKit) return;

        // All validation is complete before either resource is changed. Server RPCs execute
        // serially, so the held limb and kit cannot be claimed by a competing repair in between.
        if (severed && !coop.ServerTryConsumeHeldLimb(encodedPart, limbOwnerId, limbOrphan,
                sender, expectedLimbVersion, out limb))
            return;
        if (!shop.TryConsumeRepairKitServer()) return;

        PlayerRoleType livery = severed ? (PlayerRoleType)limb.Livery : RoleType;
        InjuryMask.Value = RemoteInjuryVisual.With(
            InjuryMask.Value, part, RemoteInjuryVisual.State.Fine);
        GraftMask.Value = SetServerGraft(GraftMask.Value, part, livery, RoleType);
        IsHobbling.Value =
            RemoteInjuryVisual.Read(InjuryMask.Value, BodyPart.LeftLeg) == RemoteInjuryVisual.State.Severed ||
            RemoteInjuryVisual.Read(InjuryMask.Value, BodyPart.RightLeg) == RemoteInjuryVisual.State.Severed;
        InjuryVersionNet.Value++;
        LifeStateVersionNet.Value++;
        _serverNextRepairAllowed = now + 0.75d;

        RepairConfirmedClientRpc(encodedPart, (int)livery, SingleClient(sender));
    }

    static int SetServerGraft(int mask, BodyPart part, PlayerRoleType livery,
        PlayerRoleType ownLivery)
    {
        int slot = System.Array.IndexOf(RemoteInjuryVisual.Parts, part);
        if (slot < 0) return mask;
        int shift = slot * RemoteInjuryVisual.GraftBitsPerPart;
        mask &= ~(0b111 << shift);
        return livery == ownLivery || livery == PlayerRoleType.None ? mask :
            RemoteInjuryVisual.WithGraft(mask, part, livery);
    }

    [ClientRpc]
    void RepairConfirmedClientRpc(int encodedPart, int encodedLivery,
        ClientRpcParams _ = default)
    {
        if (!IsOwner || !System.Enum.IsDefined(typeof(PlayerRoleType), encodedLivery)) return;
        if (InjurySystem.Instance?.ApplyServerConfirmedRepair(
                (BodyPart)encodedPart, (PlayerRoleType)encodedLivery) == true)
            AudioManager.Instance?.PlayPing();
    }

    static bool ValidInjuryMask(int mask)
    {
        int usedBits = RemoteInjuryVisual.Parts.Length * RemoteInjuryVisual.BitsPerPart;
        int allowed = (1 << usedBits) - 1;
        if ((mask & ~allowed) != 0) return false;
        foreach (var part in RemoteInjuryVisual.Parts)
            if ((int)RemoteInjuryVisual.Read(mask, part) > (int)RemoteInjuryVisual.State.Severed)
                return false;
        return true;
    }

    static bool ValidGraftMask(int mask)
    {
        int usedBits = RemoteInjuryVisual.Parts.Length * RemoteInjuryVisual.GraftBitsPerPart;
        int allowed = (1 << usedBits) - 1;
        if ((mask & ~allowed) != 0) return false;

        int maxEncodedRole = (int)PlayerRoleType.Dispatcher + 1;
        for (int slot = 0; slot < RemoteInjuryVisual.Parts.Length; slot++)
        {
            int value = (mask >> (slot * RemoteInjuryVisual.GraftBitsPerPart)) & 0b111;
            if (value > maxEncodedRole) return false;
        }
        return true;
    }

    [ClientRpc]
    void SpawnToolkitClientRpc(Vector3 position)
    {
        // The server has already been told there is none; a client may still have one lying about
        // from an event that ended differently on its side.
        if (ToolkitItem.IsHeld) return;
        if (FindAnyObjectByType<ToolkitItem>() != null) return;

        ToolkitItem.Spawn(position);

        HUDController.Instance?.AddNotification(
            "Toolkit dropped",
            "There is a toolkit somewhere in the warehouse",
            HUDController.NotifType.Warning);
    }

    /// Drops a copy of a lost limb on every machine, at the spot it came off on the owner's.
    ///
    /// Parallel-local like the toolkit rather than a NetworkObject: the limb is built out of the
    /// player's own meshes at the moment of the cut (see BotDamageVisual.BuildSeveredLimb), so
    /// there is nothing to spawn from a prefab. Every peer builds its own from the same body and
    /// the same part, which they all already have.
    [ServerRpc(RequireOwnership = false)]
    public void DropLimbServerRpc(int part, Vector3 position, int livery,
        ServerRpcParams rpcParams = default)
    {
        CoopGameManager.Instance?.ServerCreateLimb(
            this, part, position, livery, rpcParams.Receive.SenderClientId);
    }

    [ClientRpc]
    void DropLimbClientRpc(int part, Vector3 position, int livery)
    {
        // The owner already has his, built from his own body at the instant it was cut — rebuilding
        // it here would leave two arms on the floor for one injury.
        if (IsOwner) return;

        var visual = transform.Find("CharacterVisual");
        var body   = visual != null ? visual.gameObject : null;
        if (body == null) return;

        var limb = BotDamageVisual.Ensure(body)?.BuildSeveredLimb((BodyPart)part);
        if (limb == null) return;

        limb.transform.position = position;

        // Stamped with whose it is and what colour it is. Every peer holds a copy of everybody's
        // severed limbs in one list, so the name is what tells two identical left arms apart — and
        // the livery travels with it because anybody missing that part may now bolt it on, and it
        // stays the colour of the body it was cut from. See SeveredLimbItem.
        SeveredLimbItem.Attach(limb, (BodyPart)part, this, (PlayerRoleType)livery);
    }

    /// Mirrors picking the limb up and putting it down again onto every other machine.
    ///
    /// The drop already travelled; nothing else did. So a team-mate who found his arm, carried it
    /// across the warehouse and bolted it back on was seen, by everybody else, walking empty-handed
    /// past an arm that never moved — and the arm stayed on their floor for the rest of the shift,
    /// because the repair was local too (see RemoteInjuryVisual, which now clears it off the mask).
    // ── Who has claimed which limb ────────────────────────────────────────────
    //
    // A limb is a spare part any robot missing that side can use, and until now nothing decided
    // between two of them reaching for the same one: each machine picked it up locally, each
    // carried its own copy to a toolkit, and both got a working arm out of one. Boxes have never
    // had that problem because a box is claimed through the server before it moves — see
    // NetworkPhysBoxManager.RequestGrabServerRpc — and this is the same arrangement for limbs.
    //
    // Server-side only, and a plain static rather than a NetworkVariable: a claim is not something
    // anybody has to *see*, it only has to be arbitrated, and one machine deciding is the whole
    // mechanism. The dictionary lives in the server's process; on a host that is the same process
    // as a client, which costs nothing because only the ServerRpc bodies below ever touch it.
    //
    // Keyed the same way limbs are addressed everywhere else — connection, part, orphaned — so a
    // spare left behind by somebody who logged out is claimed exactly like anybody else's arm.
    static readonly Dictionary<(ulong owner, int part, bool orphan), ulong> s_limbClaims = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetLimbClaims() => s_limbClaims.Clear();

    /// Asks to pick a limb up. The answer comes back as a grant, and only to the winner.
    ///
    /// First come, first served, and deliberately not the "take it out of his hands" rule boxes
    /// use: a box changing hands is a hand-off, while a limb is halfway through an errand — its
    /// carrier is walking it to a toolkit, and losing it at the toolbox door would be the errand
    /// failing for no reason the player can see.
    [ServerRpc(RequireOwnership = false)]
    public void RequestLimbServerRpc(int part, ulong ownerId, bool orphan,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        bool granted = CoopGameManager.Instance != null &&
                       CoopGameManager.Instance.ServerTryClaimLimb(part, ownerId, orphan, sender);
        if (granted)
            GrantLimbClientRpc(part, ownerId, orphan, SingleClient(sender));
        else
            RejectLimbClientRpc(part, ownerId, orphan, SingleClient(sender));
    }

    // Only the winner receives this.
    [ClientRpc]
    void GrantLimbClientRpc(int part, ulong ownerId, bool orphan, ClientRpcParams _ = default)
    {
        var limb = SeveredLimbItem.Find(ownerId, (BodyPart)part, orphan);
        if (limb != null) limb.ClaimGranted();
    }

    [ClientRpc]
    void RejectLimbClientRpc(int part, ulong ownerId, bool orphan, ClientRpcParams _ = default)
    {
        SeveredLimbItem.Find(ownerId, (BodyPart)part, orphan)?.ClaimRejected();
    }

    static bool StillConnected(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return false;

        foreach (ulong id in nm.ConnectedClientsIds)
            if (id == clientId) return true;

        return false;
    }

    static ClientRpcParams SingleClient(ulong clientId) => new()
    {
        Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
    };

    /// Sent on the object of the player the limb was cut off, and stamped with the player who is
    /// holding it. The two are no longer the same: a left arm fits any robot missing a left arm,
    /// so the arm on the floor is a spare part and whoever needs it carries it.
    [ServerRpc(RequireOwnership = false)]
    public void CarryLimbServerRpc(int part, bool inHand, Vector3 position, ulong holder,
        ulong ownerId, bool orphan, ServerRpcParams rpcParams = default)
    {
        // `holder` is retained in the wire signature for compatibility but never trusted. The
        // transport-authenticated sender is the only actor allowed to move this state.
        ulong sender = rpcParams.Receive.SenderClientId;
        if (holder != sender) return;
        CoopGameManager.Instance?.ServerSetLimbCarry(
            part, inHand, position, ownerId, orphan, sender);
    }

    [ClientRpc]
    void CarryLimbClientRpc(int part, bool inHand, Vector3 position, ulong holder,
        ulong ownerId, bool orphan)
    {
        // The machine that picked it up already has it in the right fist. Tested on the holder and
        // no longer on ownership: a team-mate carrying *my* arm is news to me, and the old guard
        // threw exactly that message away on the one machine that needed it most.
        var me = Local;
        if (me != null && me.OwnerClientId == holder) return;

        // Addressed by the connection it was cut off rather than by this object, so a spare part
        // whose owner has left the session can still be picked up and put down like any other.
        var limb = SeveredLimbItem.Find(ownerId, (BodyPart)part, orphan);
        if (limb == null) return;

        if (!inHand) { limb.ShowOnGround(position); return; }

        var carrier = ByClient(holder);
        if (carrier == null) return;

        var visual = carrier.transform.Find("CharacterVisual");
        var anim   = visual != null ? visual.GetComponent<Animator>() : null;

        // Which hand is free is read off the *carrier's* mask — he is the one whose arms decide
        // where it can go — and never off the local InjurySystem, which answers for another body.
        limb.ShowInHand(anim, carrier.Severed(BodyPart.LeftArm), carrier.Severed(BodyPart.RightArm));
    }

    /// Takes a limb out of the world everywhere: it has been bolted onto somebody.
    ///
    /// Addressed to the player it was cut off, because that is who every peer files its copy under.
    /// The repair itself is local — InjurySystem.Mend — and the mask it publishes only says that
    /// *the wearer* is whole again, which on its own would leave the donor's arm lying there for
    /// the rest of the shift.
    [ServerRpc(RequireOwnership = false)]
    public void ConsumeLimbServerRpc(int part, ulong ownerId, bool orphan,
        ServerRpcParams rpcParams = default)
    {
        CoopGameManager.Instance?.ServerConsumeLimb(
            part, ownerId, orphan, rpcParams.Receive.SenderClientId);
    }

    [ClientRpc]
    void ConsumeLimbClientRpc(int part, ulong ownerId, bool orphan)
    {
        var limb = SeveredLimbItem.Find(ownerId, (BodyPart)part, orphan);
        if (limb != null) Destroy(limb.gameObject);
    }

    /// The player on a connection, or null if he has gone.
    static NetworkPlayer ByClient(ulong clientId)
    {
        foreach (var player in All)
            if (player != null && player.OwnerClientId == clientId) return player;

        return null;
    }

    /// True when this player's named part is gone rather than merely dented, according to the mask
    /// he publishes. The observer's view of InjurySystem.IsSevered.
    public bool Severed(BodyPart part) =>
        RemoteInjuryVisual.Read(InjuryMask.Value, part) == RemoteInjuryVisual.State.Severed;

    // ── Local helpers ─────────────────────────────────────────────────────────

    void OnDisplayNameChanged(FixedString64Bytes _, FixedString64Bytes __)
    {
        _nameplate?.Refresh();
        OnRosterChanged?.Invoke();
    }

    void OnRoleChanged(int _, int __)
    {
        _nameplate?.Refresh();
        OnRosterChanged?.Invoke();
    }

    void OnReadyChanged(bool _, bool __) => OnRosterChanged?.Invoke();

    /// Convenience: apply role picked in lobby UI, then push to server.
    public void ApplyRole(PlayerRoleType role)
    {
        SetRoleServerRpc((int)role);
    }

    // ── Team pings ──────────────────────────────────────────────────────────────────────

    [ServerRpc]
    public void PlacePingServerRpc(Vector3 position, ServerRpcParams rpcParams = default)
    {
        if (!IsSpawned || rpcParams.Receive.SenderClientId != OwnerClientId ||
            IsDeadNet.Value || IsRagdolledNet.Value || !ServerTakePingToken())
            return;
        if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z)
            || float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z))
            return;

        // The client ray is capped at 50 m. A small margin allows for player movement between the
        // request and server validation while rejecting malformed or modified coordinates.
        if ((position - transform.position).sqrMagnitude > 60f * 60f) return;

        ShowPingClientRpc(position, (int)RoleType);
    }

    bool ServerTakePingToken()
    {
        double now = NetworkManager.ServerTime.Time;
        if (_lastPingTokenTime <= 0d) _lastPingTokenTime = now;
        double elapsed = System.Math.Max(0d, now - _lastPingTokenTime);
        _lastPingTokenTime = now;
        _pingTokens = Mathf.Min(PingBurst,
            _pingTokens + (float)(elapsed / PingTokenInterval));
        if (_pingTokens < 1f) return false;
        _pingTokens -= 1f;
        return true;
    }

    [ClientRpc]
    void ShowPingClientRpc(Vector3 position, int roleIndex)
    {
        var role = System.Enum.IsDefined(typeof(PlayerRoleType), roleIndex)
            ? (PlayerRoleType)roleIndex
            : PlayerRoleType.None;
        bool dispatcher = role == PlayerRoleType.Dispatcher;
        Color color = dispatcher ? PlayerRoleColors.For(role) : new Color(1f, 0.55f, 0f);

        PingMarker.Spawn(position, color, dispatcher ? 14f : 8f);
        AudioManager.Instance?.PlayPing();
    }

    // ── Ragdoll sync ──────────────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void NotifyRagdollServerRpc(Vector3 impulse, Vector3 hitPoint,
        ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        if (!ServerApplyRagdoll(impulse, hitPoint, ownerAlreadyPredicted: true)) return;
        uint incident = ServerOpenInjuryIncident(impulse.magnitude, string.Empty, allowAnyPart: true);
        AuthorizePredictedInjuryClientRpc(incident, SingleClient(OwnerClientId));
    }

    public bool ServerApplyRagdoll(Vector3 impulse, Vector3 hitPoint,
        bool ownerAlreadyPredicted = false)
    {
        if (!IsServer || IsDeadNet.Value || IsRagdolledNet.Value) return false;
        if (!Finite(impulse) || !Finite(hitPoint)) return false;
        if ((hitPoint - transform.position).sqrMagnitude > RagdollPointRange * RagdollPointRange)
            return false;

        if (impulse.magnitude > MaxRagdollImpulse)
            impulse = impulse.normalized * MaxRagdollImpulse;

        RagdollImpulseNet.Value = impulse;
        RagdollHitPointNet.Value = hitPoint;
        RagdollVersionNet.Value++;
        _ragdollStartedServerTime = NetworkManager.ServerTime.Time;
        IsRagdolledNet.Value = true;
        LifeStateVersionNet.Value++;

        if (!ownerAlreadyPredicted)
            ApplyRagdollOwnerClientRpc(impulse, hitPoint, RagdollVersionNet.Value,
                SingleClient(OwnerClientId));
        return true;
    }

    [ClientRpc]
    void AuthorizePredictedInjuryClientRpc(uint incidentToken, ClientRpcParams _ = default)
    {
        if (!IsOwner) return;
        ResolveOwnerInjurySystem()?.AuthorizeNetworkInjury(incidentToken);
    }

    [ClientRpc]
    void ApplyRagdollOwnerClientRpc(Vector3 impulse, Vector3 hitPoint, uint version,
        ClientRpcParams _ = default)
    {
        if (!IsOwner || version < RagdollVersionNet.Value) return;
        ResolveOwnerRagdollController()?.TriggerNetworkRagdoll(impulse, hitPoint);
    }

    [ServerRpc(RequireOwnership = false)]
    public void NotifyRagdollRecoveredServerRpc(uint version,
        ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        if (!IsRagdolledNet.Value || version != RagdollVersionNet.Value) return;
        if (NetworkManager.ServerTime.Time - _ragdollStartedServerTime < MinRagdollServerDuration)
            return;

        IsRagdolledNet.Value = false;
        LifeStateVersionNet.Value++;
    }

    /// One entry point for server-simulated world hits. It publishes canonical ragdoll state,
    /// opens one narrowly-scoped injury transaction, then tells only the owner to play the body.
    public void ServerTakeWorldBlow(Vector3 impulse, Vector3 hitPoint, bool knockdown,
        float injuryImpulse, FixedString32Bytes bone)
    {
        if (!IsServer || IsDeadNet.Value || !Finite(impulse) || !Finite(hitPoint) ||
            float.IsNaN(injuryImpulse) || float.IsInfinity(injuryImpulse))
            return;

        if (impulse.magnitude > MaxRagdollImpulse)
            impulse = impulse.normalized * MaxRagdollImpulse;
        injuryImpulse = Mathf.Clamp(injuryImpulse, 0f, MaxRagdollImpulse);

        // A world hit was simulated by the server, not predicted by the victim. Send the direct
        // owner RPC as the fast path; IsRagdolledNet below remains the reliable state fallback.
        if (knockdown && !ServerApplyRagdoll(impulse, hitPoint, ownerAlreadyPredicted: false))
            return;

        float effectiveInjury = knockdown ? impulse.magnitude : injuryImpulse;
        uint incident = ServerOpenInjuryIncident(
            effectiveInjury, bone.ToString(), allowAnyPart: knockdown);
        TakeWorldBlowClientRpc(impulse, hitPoint, knockdown, injuryImpulse, bone, incident,
            SingleClient(OwnerClientId));
    }

    /// Server-confirmed consequence, addressed to the player who owns this body.
    [ClientRpc]
    public void TakeWorldBlowClientRpc(Vector3 impulse, Vector3 hitPoint, bool knockdown,
        float injuryImpulse, FixedString32Bytes bone, uint incidentToken,
        ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;

        var injuries = ResolveOwnerInjurySystem();
        injuries?.AuthorizeNetworkInjury(incidentToken);

        // Directional rather than a generic rumble: which way the camera lurches is how the
        // player learns where the blow came from without turning round to look.
        CameraShake.Kick(impulse, knockdown ? 0.6f : 0.4f);
        AudioManager.Instance?.PlayAt(GameAudioEvent.RobotDamageImpact, hitPoint);

        if (knockdown)
        {
            // The server already published the fall. This is presentation only and must not echo
            // another owner request back into the authoritative state.
            ResolveOwnerRagdollController()?.TriggerNetworkRagdoll(impulse, hitPoint);
            return;
        }

        injuries?.AddInjuryFromImpact(injuryImpulse, bone.ToString());
    }

    /// The flinch, addressed to everyone rather than to the man being hit.
    ///
    /// TakeWorldBlowClientRpc deliberately goes to the victim alone, because only that client may
    /// ragdoll or injure its own body. That left the blow invisible to the rest of the team: a
    /// raider swinging and a teammate standing there, unmarked. This is the half that everybody
    /// needs to see, and it touches nothing but bones, so it is safe to run on every copy.
    [ClientRpc]
    public void PlayHitReactionClientRpc(Vector3 direction, float strength)
    {
        HitReaction.Ensure(gameObject)?.Play(direction, strength);
        // The owner already hears the authoritative hit at the exact hit point through
        // TakeWorldBlowClientRpc. Other clients need one spatialized copy for this puppet.
        if (!IsOwner)
            AudioManager.Instance?.PlayAt(GameAudioEvent.RobotDamageImpact,
                transform.position + Vector3.up * 1.1f);
    }

    void OnRagdollStateChanged(bool _, bool down)
    {
        if (!down) return;

        // The owner used to ignore the authoritative state and relied entirely on one targeted
        // presentation RPC. If that RPC did not execute, every observer saw this player fall but
        // the player himself stayed upright and never reported recovery, leaving the server-side
        // ragdoll flag stuck forever. Triggering from state is idempotent: the direct RPC may have
        // already started the same controller, whose own IsRagdoll guard rejects the duplicate.
        if (IsOwner)
        {
            ResolveOwnerRagdollController()?.TriggerNetworkRagdoll(
                RagdollImpulseNet.Value, RagdollHitPointNet.Value);
            return;
        }

        var visual = transform.Find("CharacterVisual");
        visual?.GetComponent<RemoteRagdollVisual>()?
            .Trigger(RagdollImpulseNet.Value, RagdollHitPointNet.Value);
    }

    static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    // ── The trip ──────────────────────────────────────────────────────────────

    /// Plays the stumble on every copy of this player.
    ///
    /// The trip is the one animation in the game that no other road could carry. It is fired by a
    /// **trigger** — AnyState → Tripping, and it has to be a trigger, because a bool re-satisfies
    /// the condition every frame and restarts the clip from near its start (see
    /// PlayerSlipController). PlayerAnimatorBridge copies floats and bools, and a trigger is
    /// neither: it auto-consumes the moment it fires, so there is nothing left on the source
    /// animator for a copier to read. The `IsSlipping` bool that does travel gates no transition
    /// at all — which is why a team-mate hobbling and going down was seen standing still.
    ///
    /// NetworkAnimator is the road for exactly this. The prefab runs it in Owner authority, so the
    /// owner — the only machine where the fall actually happens — may raise the trigger, and NGO
    /// delivers it as a trigger rather than as a parameter poll.
    public void FireSlipTrigger()
    {
        if (!IsSpawned || !IsOwner) return;
        NetAnimator?.SetTrigger(SlipTrigger);
    }

    /// Takes the pending trip back off, for a fall cancelled before it played — a real impact
    /// landing mid-slip, which RagdollController resolves in favour of the ragdoll.
    public void ClearSlipTrigger()
    {
        if (!IsSpawned || !IsOwner) return;
        NetAnimator?.ResetTrigger(SlipTrigger);
    }

    /// The animator parameter the controller's AnyState → Tripping transition listens on.
    /// Named once, here, because PlayerSlipController hashes the same string.
    public const string SlipTrigger = "Slip";

    Unity.Netcode.Components.NetworkAnimator _netAnimator;

    Unity.Netcode.Components.NetworkAnimator NetAnimator
    {
        get
        {
            if (_netAnimator == null)
                _netAnimator = GetComponent<Unity.Netcode.Components.NetworkAnimator>();

            return _netAnimator;
        }
    }

    // ── Weapon in the puppet's hand ───────────────────────────────────────────

    GameObject _weaponVisual;

    /// The owner's own body is the scene rig, which MeleeCombat arms directly — this is only about
    /// the puppet everyone else looks at. Without it a player with a bat is seen empty-handed, and
    /// every bit of grip work on the local weapon is invisible to the team.
    void OnWeaponChanged(int _, int kind)
    {
        if (IsOwner) return;

        if (_weaponVisual != null) { Destroy(_weaponVisual); _weaponVisual = null; }

        var weapon = (MeleeWeaponKind)kind;
        // The real limb object is attached by SeveredLimbItem's replicated carry state. Building
        // a generic weapon visual here would duplicate it and discard the identity needed for repair.
        if (weapon == MeleeWeaponKind.Fists || weapon == MeleeWeaponKind.RobotLimb) return;

        var visual = transform.Find("CharacterVisual");
        var anim   = visual != null ? visual.GetComponent<Animator>() : null;
        if (anim == null || !anim.isHuman) return;

        _weaponVisual = MeleeWeaponItem.BuildVisual(weapon);
        _weaponVisual.name = $"Weapon_{weapon}";

        WeaponVisualPlacer.Hold(_weaponVisual);
        WeaponVisualPlacer.PlaceInHand(_weaponVisual, anim.GetBoneTransform(HumanBodyBones.RightHand), weapon);
    }

    // ── Melee swing sync ──────────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void BeginMeleeChargeServerRpc(uint sequence, ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        if (IsDeadNet.Value || IsRagdolledNet.Value || IsChargingNet.Value) return;
        if (sequence <= _serverSwingSequence || sequence <= _serverChargeSequence) return;

        _serverChargeSequence = sequence;
        _serverChargeStarted = NetworkManager.ServerTime.Time;
    }

    [ServerRpc(RequireOwnership = false)]
    public void CancelMeleeChargeServerRpc(uint sequence, ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        if (sequence != _serverChargeSequence) return;
        _serverChargeSequence = 0;
        _serverChargeStarted = 0d;
    }

    /// Accepts one new swing token. Animation timings and all gameplay stats are derived from the
    /// server-owned WeaponKind; the client supplies only intent, sequence and aim.
    [ServerRpc(RequireOwnership = false)]
    public void NotifySwingServerRpc(uint sequence, bool requestedHeavy, Vector3 aim,
        ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        if (IsDeadNet.Value || IsRagdolledNet.Value || IsChargingNet.Value) return;
        if (sequence == 0 || sequence != _serverChargeSequence
            || sequence <= _serverSwingSequence) return;
        if (!Finite(aim) || aim.sqrMagnitude < 0.001f) return;
        if (!MeleeCombat.TryGetNetworkStats(WeaponKind.Value, out var stats)) return;

        double now = NetworkManager.ServerTime.Time;
        if (now + 0.12d < _serverNextSwingAllowed) return;

        var combat = GetComponent<MeleeCombat>();
        float fullCharge = combat != null ? combat.FullChargeTime : 1.1f;
        bool heavy = requestedHeavy && now - _serverChargeStarted >= fullCharge - 0.12f;

        aim.y = 0f;
        if (aim.sqrMagnitude < 0.001f) aim = transform.forward;
        aim.Normalize();

        _serverSwingSequence = sequence;
        _serverSwingWeaponKind = WeaponKind.Value;
        _serverSwingHeavy = heavy;
        _serverSwingAim = aim;
        _serverSwingStats = stats;
        _serverSwingHitsRemaining = stats.MaxTargets;
        _serverSwingTargets.Clear();
        _serverSwingHitOpens = now + Mathf.Max(0f, stats.Windup - 0.25f);
        _serverSwingExpires = now + stats.Cooldown + 0.75f;
        _serverNextSwingAllowed = now + stats.Cooldown;
        _serverChargeSequence = 0;
        _serverChargeStarted = 0d;

        float left = Mathf.Max(0.02f, stats.Cooldown - stats.Windup);
        PlaySwingClientRpc(stats.Windup, left * 0.34f, left * 0.66f);
    }

    public bool TryConsumeServerMeleeHit(uint sequence, ulong targetNetworkId,
        Vector3 targetPosition, out int damage, out Vector3 direction, out bool heavy)
    {
        damage = 0;
        direction = Vector3.zero;
        heavy = false;

        if (!IsServer || sequence != _serverSwingSequence || _serverSwingHitsRemaining <= 0)
            return false;
        if (IsDeadNet.Value || IsRagdolledNet.Value || IsChargingNet.Value
            || WeaponKind.Value != _serverSwingWeaponKind) return false;

        double now = NetworkManager.ServerTime.Time;
        if (now < _serverSwingHitOpens || now > _serverSwingExpires) return false;
        if (_serverSwingTargets.Contains(targetNetworkId)) return false;
        if (!Finite(targetPosition)) return false;

        Vector3 origin = transform.position + Vector3.up * 1.1f;
        Vector3 to = targetPosition + Vector3.up - origin;
        to.y = 0f;
        float distance = to.magnitude;
        if (distance > _serverSwingStats.Reach + 0.65f) return false;
        if (distance > 0.15f
            && Vector3.Angle(_serverSwingAim, to) > _serverSwingStats.HalfAngle + 10f)
            return false;

        _serverSwingTargets.Add(targetNetworkId);
        _serverSwingHitsRemaining--;
        damage = Mathf.Max(1, _serverSwingStats.Damage);
        direction = _serverSwingAim;
        heavy = _serverSwingHeavy;
        return true;
    }

    [ClientRpc]
    void PlaySwingClientRpc(float windup, float strike, float recover)
    {
        if (IsOwner) return;   // already swinging locally, in real time

        var visual = transform.Find("CharacterVisual");
        if (visual == null) return;

        // Armed: the same clip the owner is playing, on the same masked layer — the puppet uses
        // the same controller, so it has both. Anything else means the blow that lands is one
        // animation on your screen and another on your teammate's.
        var anim = visual.GetComponent<Animator>();
        if (WeaponKind.Value != (int)MeleeWeaponKind.Fists && anim != null)
        {
            int layer = anim.GetLayerIndex(PlayerWeaponLayer.LayerName);
            if (layer >= 0 && anim.HasState(layer, AttackStateHash))
            {
                float total = Mathf.Max(0.05f, windup + strike + recover);
                anim.SetFloat(AttackSpeedHash, AttackClipDuration / total);
                anim.SetLayerWeight(layer, 1f);
                anim.CrossFadeInFixedTime(AttackStateHash, 0.06f, layer, 0f);

                StartCoroutine(LowerArmsLayer(anim, layer, total));
                return;
            }
        }

        // Bare hands, or a controller without the layer: the springs, which is all there ever was
        // before the clip existed.
        var arm = visual.GetComponent<MeleeSwingArm>();
        if (arm == null) arm = visual.gameObject.AddComponent<MeleeSwingArm>();

        arm.Begin(windup, strike, recover);
    }

    static readonly int AttackStateHash = Animator.StringToHash("WeaponAttack");
    static readonly int AttackSpeedHash = Animator.StringToHash("AttackSpeed");
    const  float AttackClipDuration = 2.27f;   // matches MeleeCombat's own figure

    /// Takes the arms layer back down after the blow, the way PlayerWeaponLayer does for the owner
    /// — a puppet has no such component, and a layer left up holds the arms out of the run cycle.
    System.Collections.IEnumerator LowerArmsLayer(Animator anim, int layer, float after)
    {
        yield return new WaitForSeconds(after);

        while (anim != null && anim.GetLayerWeight(layer) > 0.01f)
        {
            anim.SetLayerWeight(layer, Mathf.MoveTowards(anim.GetLayerWeight(layer), 0f, Time.deltaTime * 8f));
            yield return null;
        }
        if (anim != null) anim.SetLayerWeight(layer, 0f);
    }

    // ── Death / rescue sync ───────────────────────────────────────────────────

    /// Fallback for an owner-detected crush. PhysicsShelf normally invokes ServerApplyDeath
    /// directly on the server; sender and position validation keep this path from mutating a
    /// different player's state or inventing a remote death.
    [ServerRpc(RequireOwnership = false)]
    public void NotifyDeathServerRpc(Vector3 crushPosition, ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        if (!Finite(crushPosition) || (crushPosition - transform.position).sqrMagnitude > 16f)
            return;
        ServerApplyDeath(crushPosition);
    }

    public void ServerApplyDeath(Vector3 crushPosition)
    {
        if (!IsServer || IsDeadNet.Value || !Finite(crushPosition)) return;

        CrushPositionNet.Value = crushPosition;
        IsRagdolledNet.Value = false;
        IsDeadNet.Value = true;
        RespawnDeadlineNet.Value = NetworkManager.ServerTime.Time + MultiplayerAutoRespawnSeconds;
        LifeStateVersionNet.Value++;

        ApplyDeathOwnerClientRpc(crushPosition, LifeStateVersionNet.Value,
            SingleClient(OwnerClientId));
        BroadcastDeathClientRpc();
    }

    [ClientRpc]
    void ApplyDeathOwnerClientRpc(Vector3 crushPosition, uint version,
        ClientRpcParams _ = default)
    {
        if (!IsOwner || version < LifeStateVersionNet.Value) return;
        GetComponent<PlayerDeathManager>()?.ApplyNetworkDeath(crushPosition, RespawnDeadlineNet.Value);
    }

    [ClientRpc]
    void BroadcastDeathClientRpc(ClientRpcParams _ = default)
    {
        if (IsOwner) return; // the dying player already has their own death screen
        HUDController.Instance?.AddNotification(
            "Teammate down!",
            $"{PlayerName} was crushed by a shelf — go rescue them",
            HUDController.NotifType.Danger);
    }

    [ServerRpc(RequireOwnership = false)]
    public void NotifyRespawnServerRpc(ServerRpcParams rpcParams = default)
    {
        // Compatibility path for old scene/prefab code. A client cannot clear death early.
        if (rpcParams.Receive.SenderClientId != OwnerClientId || !IsDeadNet.Value) return;
        if (RespawnDeadlineNet.Value <= 0d
            || NetworkManager.ServerTime.Time < RespawnDeadlineNet.Value) return;
        ApplyAutoRespawnPenaltyClientRpc(0.50f);
        ServerCompleteRespawn(_serverSpawnPoint, viaForklift: false, autoRespawn: true);
    }

    [ServerRpc(RequireOwnership = false)]
    public void BeginRescueServerRpc(ulong deadClientId, ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        var target = ByClient(deadClientId);
        if (!CanRescueTarget(target)) return;

        _rescueTargetId = deadClientId;
        _rescueStartedServerTime = NetworkManager.ServerTime.Time;
    }

    [ServerRpc(RequireOwnership = false)]
    public void CancelRescueServerRpc(ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        _rescueTargetId = NoRescueTarget;
        _rescueStartedServerTime = 0d;
    }

    /// Called by whichever client performs the rescue interaction (teammate hold-E,
    /// or a forklift operator lifting the shelf) — deadClientId identifies the victim,
    /// since the marker interacted with lives on a different client than the victim.
    [ServerRpc(RequireOwnership = false)]
    public void RequestRescueServerRpc(ulong deadClientId, bool viaForklift,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (sender != OwnerClientId) return;

        var target = ByClient(deadClientId);
        if (target == null || !target.IsDeadNet.Value || target == this) return;

        Vector3 respawnPosition;
        if (viaForklift)
        {
            if (!ReachTruckController.TryGetServerRescuePosition(
                    sender, target.CrushPositionNet.Value, out respawnPosition)) return;
        }
        else
        {
            if (_rescueTargetId != deadClientId) return;
            if (NetworkManager.ServerTime.Time - _rescueStartedServerTime
                < RescueHoldServerSeconds) return;
            if (!CanRescueTarget(target)) return;
            respawnPosition = transform.position + transform.right * 1.2f;
        }

        _rescueTargetId = NoRescueTarget;
        _rescueStartedServerTime = 0d;
        target.ServerCompleteRespawn(respawnPosition, viaForklift, autoRespawn: false);
    }

    bool CanRescueTarget(NetworkPlayer target)
    {
        if (target == null || target == this || !target.IsDeadNet.Value) return false;
        return (transform.position - target.CrushPositionNet.Value).sqrMagnitude
               <= RescueRange * RescueRange;
    }

    void ServerCompleteRespawn(Vector3 position, bool viaForklift, bool autoRespawn)
    {
        if (!IsServer || !IsDeadNet.Value) return;
        IsDeadNet.Value = false;
        RespawnDeadlineNet.Value = 0d;
        LifeStateVersionNet.Value++;
        CompleteRespawnClientRpc(position, viaForklift, autoRespawn,
            LifeStateVersionNet.Value, SingleClient(OwnerClientId));
    }

    [ClientRpc]
    void CompleteRespawnClientRpc(Vector3 position, bool viaForklift, bool autoRespawn,
        uint version, ClientRpcParams _ = default)
    {
        if (!IsOwner || version < LifeStateVersionNet.Value) return;
        GetComponent<PlayerDeathManager>()?.ApplyNetworkRespawn(position, viaForklift, autoRespawn);
    }

    [ClientRpc]
    void ApplyAutoRespawnPenaltyClientRpc(float pct)
        => PlayerDeathManager.ApplySharedScorePenalty(pct);

    void OnDeadStateChanged(bool _, bool isDead)
    {
        OnRosterChanged?.Invoke();
        if (IsOwner) return; // local player's own PlayerDeathManager owns its own marker/UI

        if (isDead) CrushedPlayerMarker.SpawnRemote(CrushPositionNet.Value, OwnerClientId);
        else        CrushedPlayerMarker.DestroyMarkerFor(OwnerClientId);
    }
}
