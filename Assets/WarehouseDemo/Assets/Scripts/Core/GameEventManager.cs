using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Rendering;

/// Fires random warehouse mini-events once the first order is accepted.
/// In multiplayer the server is the sole scheduler; events are broadcast to all clients
/// via CoopGameManager.BroadcastEventStartClientRpc / BroadcastEventEndClientRpc.
public class GameEventManager : MonoBehaviour
{
    public static GameEventManager Instance { get; private set; }

    // ── Environment modifiers — set by active events, read by other systems ────
    public static float EnvironmentSpeedMult    { get; set; } = 1f;
    public static float RagdollThresholdMult    { get; set; } = 1f;

    // ── Supply delay — set by SupplyDelayEvent ────────────────────────────────
    public static HashSet<string> BlockedPartIDs { get; } = new();

    // ── Bench state (read by AssemblyBench) ───────────────────────────────────
    // Reference-counted, not a plain bool: BenchMalfunctionEvent, PowerFailureEvent and
    // FireAlarmEvent can all block the bench at once — one ending early must not unblock
    // it while another is still active.
    int  _benchMalfunctionCount;
    public bool IsBenchMalfunctioning => _benchMalfunctionCount > 0;
    public void BlockBench()   => _benchMalfunctionCount++;
    public void UnblockBench() => _benchMalfunctionCount = Mathf.Max(0, _benchMalfunctionCount - 1);

    // ── Radio state (read by RadioRepairPanel) ─────────────────────────────────
    public static bool IsRadioInterfering => RadioInterferenceEvent.Active != null;

    // ── Camera shake via Cinemachine Impulse ──────────────────────────────────
    CinemachineImpulseSource _impulse;

    public static void TriggerShake(float force)
    {
        if (Instance != null && Instance._impulse != null)
            Instance._impulse.GenerateImpulse(force);
    }

    // ── Inspector ─────────────────────────────────────────────────────────────
    [SerializeField] float _minInterval = 60f;
    [SerializeField] float _maxInterval = 120f;

    [Header("Toolkit Prefab (used by PowerFailure / BrokenForklift / BrokenFork / RadioInterference)")]
    [SerializeField] GameObject _toolkitPrefab;

    [Header("Post-process Profiles (used by PowerFailureEvent)")]
    [SerializeField] public VolumeProfile DayProfile;
    [SerializeField] public VolumeProfile NightProfile;

    [Header("Post-process Profile (used by FireAlarmEvent — red flicker overlay)")]
    [SerializeField] public VolumeProfile FireAlarmProfile;

    [Header("Siren Clip (used by FireAlarmEvent — falls back to a synthesized wail if unset)")]
    [SerializeField] public AudioClip FireAlarmSirenClip;

    [Header("Thief Prefabs (used by ThiefEvent — all optional, each has a primitive fallback)")]
    [SerializeField] GameObject _thiefPrefab;
    // The raiders are zombies and carry nothing, so the floor is where every weapon a player
    // ever holds comes from.
    //
    // Only used when the scene has no pickups of its own — see SpawnStartingWeapons. A dressed
    // warehouse ignores this list entirely.
    [Tooltip("Fallback layout, used only in a scene with no weapon pickups placed in it. " +
             "One pickup per entry; repeat a kind to put out more.")]
    [SerializeField] MeleeWeaponKind[] _startingWeapons =
    {
        MeleeWeaponKind.Bat,
        MeleeWeaponKind.Crowbar,
        MeleeWeaponKind.PipeWrench,
        MeleeWeaponKind.Plank,
        MeleeWeaponKind.RollingPin,
    };

    // ── Internal ──────────────────────────────────────────────────────────────
    struct WeightedEvent
    {
        public IGameEvent     evt;
        public float          weight;
        public PlayerRoleType requiredRole;
        public bool           multiplayerOnly;
    }

    List<WeightedEvent> _pool = new();
    readonly HashSet<string>               _active          = new();
    readonly Dictionary<string, IGameEvent> _activeInstances = new();
    // Prevents double-notification when server broadcasts "end" before a client's own timer fires.
    readonly HashSet<string> _completedByNetwork = new();

    bool  _running;
    float _nextEventTime;
    float _shiftTime;
    IGameEvent _pendingIntroducedEvent;
    bool       _waitingForEventIntro;
    readonly HashSet<string> _introducedEventTypes = new();

    // ── Dynamic Chaos Scaling ─────────────────────────────────────────────────
    int MaxConcurrent => _shiftTime switch
    {
        < 900f  => 1,
        < 1800f => 2,
        < 2700f => 3,
        _       => 4,
    };

    int EventPressureTier => CompletedOrders switch
    {
        < 3  => 0,
        < 6  => 1,
        < 10 => 2,
        < 14 => 3,
        _    => 4,
    };

    int CompletedOrders => GameManager.Instance != null
        ? GameManager.Instance.CompletedOrders
        : 0;

    float NextInterval() => EventPressureTier switch
    {
        0 => Random.Range(70f, 100f),
        1 => Random.Range(55f, 80f),
        2 => Random.Range(40f, 65f),
        3 => Random.Range(30f, 50f),
        _ => Random.Range(20f, 40f),
    };

    /// Fixed so the starting bats land identically for host and clients.
    const int WeaponSpawnSeed = 20260822;

    static bool IsNetworkServer =>
        Unity.Netcode.NetworkManager.Singleton?.IsServer ?? false;

    static bool IsMultiplayer =>
        Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        _impulse = GetComponent<CinemachineImpulseSource>();
        ToolkitItem.KitPrefab       = _toolkitPrefab;
        ThiefController.Prefab      = _thiefPrefab;
    }

    void Start()
    {
        _pool = new List<WeightedEvent>
        {
            new() { evt = new RushOrderEvent(),        weight = 3.0f, requiredRole = PlayerRoleType.None },
            new() { evt = new WetFloorEvent(),         weight = 2.0f, requiredRole = PlayerRoleType.None },
            new() { evt = new ShelfCollapseEvent(),    weight = 2.0f, requiredRole = PlayerRoleType.None },
            new() { evt = new BenchMalfunctionEvent(), weight = 1.0f, requiredRole = PlayerRoleType.None },
            new() { evt = new PowerFailureEvent(),     weight = 1.0f, requiredRole = PlayerRoleType.None },
            new() { evt = new FireAlarmEvent(),        weight = 1.5f, requiredRole = PlayerRoleType.None },
            new() { evt = new WrongOrderEvent(),       weight = 1.5f, requiredRole = PlayerRoleType.None },
            new() { evt = new ThiefEvent(),            weight = 2.0f, requiredRole = PlayerRoleType.None },
            new() { evt = new GateBreachEvent(),       weight = 1.5f, requiredRole = PlayerRoleType.None },

            new() { evt = new BrokenForkliftEvent(),  weight = 1.5f, requiredRole = PlayerRoleType.ForkLiftOperator },
            new() { evt = new BrokenForkEvent(),      weight = 2.0f, requiredRole = PlayerRoleType.ForkLiftOperator },
            new() { evt = new UrgentPartEvent(),      weight = 2.0f, requiredRole = PlayerRoleType.Picker },
            new() { evt = new SupplyDelayEvent(),         weight = 1.0f, requiredRole = PlayerRoleType.Dispatcher },
            new() { evt = new RadioInterferenceEvent(),  weight = 1.5f, requiredRole = PlayerRoleType.Dispatcher, multiplayerOnly = true },
        };

        if (GameManager.Instance != null)
        {
            GameManager.Instance.OnOrderAccepted += OnFirstOrder;
            GameManager.Instance.OnOrderCompleted += OnOrderCompleted;
        }

        SpawnStartingWeapons();
    }

    /// Seeded so every machine in a co-op session lays the same weapons on the same patches of
    /// floor without an RPC — the pickup itself has always been a local affair. Spawn order is
    /// the array order for the same reason: it has to be identical everywhere.
    void SpawnStartingWeapons()
    {
        // The level's own pickups win. Where a crowbar lies is a decision about the warehouse —
        // which corner, how far from the gate, whether the far end of the floor has anything at
        // all — and a random scatter cannot make it. This stays only as the fallback for a scene
        // that has none, so the mechanic still works in one nobody has dressed yet.
        if (MeleeWeaponItem.AnyInScene()) return;

        if (_startingWeapons == null || _startingWeapons.Length == 0) return;

        var state = Random.state;
        Random.InitState(WeaponSpawnSeed);

        foreach (var kind in _startingWeapons)
        {
            if (kind == MeleeWeaponKind.Fists) continue;   // nothing to lay on the floor
            MeleeWeaponItem.Spawn(kind);
        }

        Random.state = state;
    }

    void OnDestroy()
    {
        Instance = null;
        EnvironmentSpeedMult = 1f;
        RagdollThresholdMult = 1f;
        BlockedPartIDs.Clear();

        if (GameManager.Instance != null)
        {
            GameManager.Instance.OnOrderAccepted -= OnFirstOrder;
            GameManager.Instance.OnOrderCompleted -= OnOrderCompleted;
        }
    }

    void OnFirstOrder(OrderDefinition _)
    {
        GameManager.Instance.OnOrderAccepted -= OnFirstOrder;
        _running       = true;
        _nextEventTime = Time.time + NextInterval();
    }

    void OnOrderCompleted(OrderDefinition _)
    {
        if (!_running) return;

        int completedBefore = Mathf.Max(0, CompletedOrders - 1);
        int previousTier = completedBefore switch
        {
            < 3  => 0,
            < 6  => 1,
            < 10 => 2,
            < 14 => 3,
            _    => 4,
        };

        // Only a band transition changes an already planned incident. Starting a complete new
        // interval avoids an event popping immediately on top of the order-complete feedback.
        if (previousTier != EventPressureTier)
            _nextEventTime = Time.time + NextInterval();
    }

    void Update()
    {
        if (!_running) return;

        // In multiplayer only the server schedules events; clients wait for ClientRpc.
        if (IsMultiplayer && !IsNetworkServer) return;

        _shiftTime += Time.deltaTime;
        if (_waitingForEventIntro) return;

        // Do not let an incident replace a card from the base tutorial. Once its final card has
        // faded, the overdue event is selected immediately and receives its first-use teaching
        // card when appropriate. A player who skipped training opted out of these cards.
        var tutorial = TutorialManager.Instance;
        if (!IsMultiplayer && tutorial != null
            && !tutorial.TutorialSkipped && !tutorial.BaseTutorialPresentationComplete)
            return;

        if (Time.time < _nextEventTime) return;
        _nextEventTime = Time.time + NextInterval();
        TryFireEvent();
    }

    // ── Event dispatch ────────────────────────────────────────────────────────

    void TryFireEvent()
    {
        if (_pool == null || _pool.Count == 0) return;
        if (_active.Count >= MaxConcurrent) return;

        var candidates = new List<WeightedEvent>(_pool.Count);
        foreach (var entry in _pool)
        {
            if (entry.evt == null) continue;
            if (_active.Contains(entry.evt.Name)) continue;
            if (OtherTimedDeliveryIsBusy(entry.evt.Name)) continue;
            if (entry.multiplayerOnly && !IsMultiplayer) continue;
            if (!SpecializationPenalties.SoloMode
                && entry.requiredRole != PlayerRoleType.None
                && !TeamHasRole(entry.requiredRole)) continue;
            candidates.Add(entry);
        }

        if (candidates.Count == 0) return;

        float total = 0f;
        foreach (var c in candidates) total += c.weight;
        float roll = Random.Range(0f, total);
        float acc  = 0f;
        IGameEvent chosen = null;
        foreach (var c in candidates)
        {
            acc += c.weight;
            if (roll <= acc) { chosen = c.evt; break; }
        }
        chosen ??= candidates[candidates.Count - 1].evt;

        // In solo play each non-obvious event type gets one teaching card. Apply is postponed:
        // timers, spawned hazards, markers and notifications begin only after Continue.
        if (TryQueueFirstTypeIntroduction(chosen)) return;

        if (IsMultiplayer)
        {
            // Server: get payload (random picks, positions) then broadcast to all clients.
            // Reserve the slot immediately so rapid Update() frames can't double-fire.
            _active.Add(chosen.Name);
            string payload = chosen.GetNetworkPayload(this);
            CoopGameManager.Instance?.BroadcastEventStartClientRpc(chosen.Name, payload);
            // Don't apply locally here — ApplyEventFromNetwork fires on server too via ClientRpc.
        }
        else
        {
            _active.Add(chosen.Name);
            _activeInstances[chosen.Name] = chosen;
            chosen.Apply(this);
        }
    }

    bool TryQueueFirstTypeIntroduction(IGameEvent chosen)
    {
        if (chosen == null || IsMultiplayer || _waitingForEventIntro
            || _introducedEventTypes.Contains(chosen.Name))
            return false;

        var tutorial = TutorialManager.Instance;
        if (tutorial == null || tutorial.TutorialSkipped) return false;

        _pendingIntroducedEvent = chosen;
        _waitingForEventIntro = true;
        if (tutorial.TryShowEventIntro(chosen.Name))
        {
            _introducedEventTypes.Add(chosen.Name);
            return true;
        }

        // Obvious events intentionally return false and start normally. A temporarily unavailable
        // HUD follows the same safe fallback rather than leaving the scheduler stuck.
        _pendingIntroducedEvent = null;
        _waitingForEventIntro = false;
        return false;
    }

    /// Releases the solo event held behind its first-use tutorial card.
    /// Called only by TutorialManager after the player presses Continue.
    public void ContinueIntroducedEvent()
    {
        if (!_waitingForEventIntro) return;

        var chosen = _pendingIntroducedEvent;
        _pendingIntroducedEvent = null;
        _waitingForEventIntro = false;
        if (chosen == null) return;

        _active.Add(chosen.Name);
        _activeInstances[chosen.Name] = chosen;
        chosen.Apply(this);
    }

    /// Starts a named event outside the random scheduler — used when something in the world
    /// causes an incident rather than the timer picking one (ThiefController smashing a power
    /// box). Deliberately ignores MaxConcurrent: a consequence the players brought on
    /// themselves must land even on a busy shift. Returns false if that event is already
    /// running, or if this peer is a client (the server broadcasts it to everyone).
    public bool ForceStartEvent(string eventName)
    {
        if (string.IsNullOrEmpty(eventName)) return false;
        if (_active.Contains(eventName)) return false;
        if (_pendingIntroducedEvent?.Name == eventName) return false;
        if (OtherTimedDeliveryIsBusy(eventName)) return false;
        if (IsMultiplayer && !IsNetworkServer) return false;

        var evt = CreateEvent(eventName, null);
        if (evt == null) return false;

        if (IsMultiplayer)
        {
            // Same handshake as TryFireEvent: reserve the slot, then let the ClientRpc apply
            // it on every peer (server included) so all copies share the server's random picks.
            _active.Add(eventName);
            string payload = evt.GetNetworkPayload(this);
            CoopGameManager.Instance?.BroadcastEventStartClientRpc(eventName, payload);
            return true;
        }

        if (TryQueueFirstTypeIntroduction(evt)) return true;

        _active.Add(eventName);
        _activeInstances[eventName] = evt;
        evt.Apply(this);
        return true;
    }

    /// True while the named event is running — lets a caller pick a different kind of trouble
    /// instead of firing one that is already in progress.
    public bool IsEventActive(string eventName) => _active.Contains(eventName)
        || _pendingIntroducedEvent?.Name == eventName;

    /// Looks up the currently-active instance of a named event, so a network echo (e.g.
    /// CoopGameManager.RepairPowerBoxServerRpc, NotifyRushBoxDeliveredServerRpc) can apply a
    /// player-driven completion on THIS client's own copy of the event, not just the one
    /// running on whichever client actually triggered the action.
    public IGameEvent GetActiveEvent(string eventName) =>
        _activeInstances.TryGetValue(eventName, out var evt) ? evt : null;

    /// Called by CoopGameManager.BroadcastEventStartClientRpc on ALL clients (including server-host).
    public void ApplyEventFromNetwork(string eventName, string payload)
    {
        if (_activeInstances.ContainsKey(eventName)) return; // guard against double-apply on host

        var evt = CreateEvent(eventName, payload);
        if (evt == null) return;

        _active.Add(eventName);
        _activeInstances[eventName] = evt;
        evt.Apply(this);
    }

    /// Called by CoopGameManager.BroadcastEventEndClientRpc on non-server clients.
    public void EndEventFromNetwork(string eventName)
    {
        if (!_activeInstances.TryGetValue(eventName, out var evt)) return;

        // Mark as externally ended so if the local timer fires later, MarkEventDone skips
        // the duplicate notification/broadcast.
        _completedByNetwork.Add(eventName);
        _activeInstances.Remove(eventName);
        _active.Remove(eventName);
        evt.Remove();
    }

    /// Called by events when they finish so the slot can be re-used.
    public void MarkEventDone(string eventName)
    {
        // If EndEventFromNetwork already handled this, skip cleanup to prevent double notifications.
        if (_completedByNetwork.Contains(eventName))
        {
            _completedByNetwork.Remove(eventName);
            _active.Remove(eventName);
            _activeInstances.Remove(eventName);
            return;
        }

        _active.Remove(eventName);
        _activeInstances.Remove(eventName);

        // Server broadcasts end to non-server clients.
        if (IsMultiplayer && IsNetworkServer)
            CoopGameManager.Instance?.BroadcastEventEndClientRpc(eventName);
    }

    // ── Event factory — creates events with pre-seeded random data from server ─

    /// The gate and the head count were rolled on the server; a client rebuilds the event around
    /// them rather than rolling its own and shaking a different shutter.
    static IGameEvent MakeGateBreach(string payload)
    {
        var evt = new GateBreachEvent();
        if (!string.IsNullOrEmpty(payload)) evt.ApplyPayload(payload);
        return evt;
    }

    static IGameEvent CreateEvent(string name, string payload)
    {
        string[] p = string.IsNullOrEmpty(payload)
            ? System.Array.Empty<string>()
            : payload.Split(':');

        return name switch
        {
            "RushOrder"        => CreateRushOrder(payload),
            "WetFloor"         => new WetFloorEvent(),
            "GateBreach"       => MakeGateBreach(payload),
            "FireAlarm"        => p.Length > 0
                                      ? new FireAlarmEvent(p.Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray())
                                      : new FireAlarmEvent(),
            "BenchMalfunction" => new BenchMalfunctionEvent(),
            "PowerFailure"     => p.Length >= 4
                                      ? new PowerFailureEvent(ParseV3(p),
                                            p.Skip(3).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray())
                                      : new PowerFailureEvent(),
            "WrongOrder"       => new WrongOrderEvent(payload),
            "SupplyDelay"      => new SupplyDelayEvent(
                                      string.IsNullOrEmpty(payload)
                                          ? null
                                          : payload.Split('|')),
            "BrokenForklift"   => p.Length >= 4
                                      ? new BrokenForkliftEvent(ParseV3(p), int.Parse(p[3], CultureInfo.InvariantCulture))
                                      : new BrokenForkliftEvent(),
            "BrokenFork"       => p.Length >= 4
                                      ? new BrokenForkEvent(ParseV3(p), int.Parse(p[3], CultureInfo.InvariantCulture))
                                      : new BrokenForkEvent(),
            "ShelfCollapse"    => p.Length >= 3 ? new ShelfCollapseEvent(ParseV3(p))  : new ShelfCollapseEvent(),
            "UrgentPart"       => CreateUrgentPart(payload),
            "RadioInterference"    => p.Length >= 3 ? new RadioInterferenceEvent(ParseV3(p)) : new RadioInterferenceEvent(),
            "Thief"                => new ThiefEvent(
                                      string.IsNullOrEmpty(payload)
                                          ? null
                                          : payload.Split('|')),
            _                      => null
        };
    }

    static IGameEvent CreateRushOrder(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return new RushOrderEvent();
        int separator = payload.IndexOf(';');
        if (separator < 0) return new RushOrderEvent(payload.Split('|')); // legacy payload

        int gateIndex = int.TryParse(payload.Substring(0, separator), NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out int parsedGate)
            ? parsedGate : -1;
        string ids = payload.Substring(separator + 1);
        return new RushOrderEvent(string.IsNullOrEmpty(ids) ? null : ids.Split('|'), gateIndex);
    }

    static IGameEvent CreateUrgentPart(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return new UrgentPartEvent();
        int separator = payload.IndexOf(';');
        if (separator < 0) return new UrgentPartEvent(payload); // legacy payload

        int gateIndex = int.TryParse(payload.Substring(0, separator), NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out int parsedGate)
            ? parsedGate : -1;
        string partId = payload.Substring(separator + 1);
        return new UrgentPartEvent(string.IsNullOrEmpty(partId) ? null : partId, gateIndex);
    }

    static bool IsTimedDelivery(string eventName) =>
        eventName == "RushOrder" || eventName == "UrgentPart";

    bool OtherTimedDeliveryIsBusy(string requested)
    {
        if (!IsTimedDelivery(requested)) return false;
        string other = requested == "RushOrder" ? "UrgentPart" : "RushOrder";
        return _active.Contains(other) || _pendingIntroducedEvent?.Name == other;
    }

    static Vector3 ParseV3(string[] p) => new(
        float.Parse(p[0], CultureInfo.InvariantCulture),
        float.Parse(p[1], CultureInfo.InvariantCulture),
        float.Parse(p[2], CultureInfo.InvariantCulture));

    static string FmtV3(Vector3 v) =>
        $"{v.x.ToString(CultureInfo.InvariantCulture)}:" +
        $"{v.y.ToString(CultureInfo.InvariantCulture)}:" +
        $"{v.z.ToString(CultureInfo.InvariantCulture)}";

    // Expose FmtV3 to event classes in the same assembly.
    internal static string FormatVec3(Vector3 v) => FmtV3(v);

    // ── Role helpers ──────────────────────────────────────────────────────────

    static bool TeamHasRole(PlayerRoleType role)
    {
        if (RoleController.Instance?.CurrentRole?.RoleType == role) return true;
        return RoleTaskManager.TeamHasRole(role);
    }

    // ── Debug helpers ─────────────────────────────────────────────────────────

    /// Starts a named event by hand, the same way the scheduler would.
    ///
    /// Every one of these used to be `new SomeEvent().Apply(this)`, which is a local call and only
    /// a local call: no slot reserved, no payload rolled, no broadcast. In a session that produced
    /// three different wrong outcomes depending on who pressed it. On the host, the other machines
    /// saw nothing of the build-up — no shaking gate, no cracks, no siren — and only whatever the
    /// event happened to spawn as a NetworkObject at the very end. On a client it did nothing at
    /// all anywhere, because the events themselves check IsClientOnly and decline to put actors in
    /// a world they do not own.
    ///
    /// Routed through the real path instead, so a hand-fired event is the same event the timer
    /// fires: server rolls the payload once and every peer rebuilds from it.
    void Fire(string eventName)
    {
        if (IsMultiplayer && !IsNetworkServer)
        {
            var coop = CoopGameManager.Instance;
            if (coop == null)
            {
                Debug.LogWarning($"[Events] cannot fire {eventName} from a client — no " +
                                 "CoopGameManager in the scene to ask the server through.");
                return;
            }

            coop.RequestEventServerRpc(eventName);
            Debug.Log($"[Events] asked the server to fire {eventName}.");
            return;
        }

        if (ForceStartEvent(eventName))
            Debug.Log($"[Events] fired {eventName}.");
        else
            Debug.LogWarning($"[Events] {eventName} was refused — it is already running, or the " +
                             "scene has nothing for it to act on.");
    }

#if UNITY_EDITOR
    [ContextMenu("Debug / Fire Rush Order")]      void DbgRush()     => Fire("RushOrder");
    [ContextMenu("Debug / Fire Wet Floor")]       void DbgWet()      => Fire("WetFloor");
    [ContextMenu("Debug / Fire Shelf Collapse")]  void DbgShelf()    => Fire("ShelfCollapse");
    [ContextMenu("Debug / Fire Bench Fault")]     void DbgBench()    => Fire("BenchMalfunction");
    [ContextMenu("Debug / Fire Power Failure")]   void DbgPower()    => Fire("PowerFailure");
    [ContextMenu("Debug / Fire Fire Alarm")]      void DbgAlarm()    => Fire("FireAlarm");
    [ContextMenu("Debug / Fire Wrong Order")]     void DbgWrong()    => Fire("WrongOrder");
    [ContextMenu("Debug / Fire Broken Forklift")] void DbgFork()     => Fire("BrokenForklift");
    [ContextMenu("Debug / Fire Broken Fork")]     void DbgForkLift() => Fire("BrokenFork");
    [ContextMenu("Debug / Fire Urgent Part")]     void DbgUrgent()   => Fire("UrgentPart");
    [ContextMenu("Debug / Fire Supply Delay")]    void DbgSupply()   => Fire("SupplyDelay");
    [ContextMenu("Debug / Fire Radio Interference")] void DbgRadio() => Fire("RadioInterference");
    [ContextMenu("Debug / Fire Thief")]           void DbgThief()    => Fire("Thief");
    [ContextMenu("Debug / Fire Gate Breach")]     void DbgGateBreach() => Fire("GateBreach");

    /// A raid of nothing but box thieves, for watching the carry. Normally only one raider in
    /// three is a looter, which is a long wait to check what the arms are doing.
    ///
    /// The override is cleared straight after the raid is built, so it cannot outlive this call.
    /// It is read where the roles are rolled, which is wherever the payload is built — so from a
    /// client this gives an ordinary raid: the server rolls it, and the server has no override set.
    [ContextMenu("Debug / Fire Thief (looters only)")]
    void DbgLooters()
    {
        if (IsMultiplayer && !IsNetworkServer)
            Debug.LogWarning("[Events] looters-only is a server-side override — from a client " +
                             "this fires an ordinary raid.");

        ThiefEvent.ForceAllRole = ThiefRole.Looter;
        try     { Fire("Thief"); }
        finally { ThiefEvent.ForceAllRole = null; }
    }
#endif
}
