using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// Server-authoritative game state for co-op sessions.
/// Wraps single-player GameManager events with NetworkVariable sync.
[RequireComponent(typeof(GameManager))]
public class CoopGameManager : NetworkBehaviour
{
    public static CoopGameManager Instance { get; private set; }

    // ── Synced state ──────────────────────────────────────────────────────────
    public NetworkVariable<int> ShippingGateIndex = new(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<FixedString64Bytes> ActiveOrderName = new(
        new FixedString64Bytes(""),
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<int> CompletedOrdersNet = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<int> ScoreNet = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Bench: serialised as "partID:count"
    NetworkList<FixedString64Bytes> _benchState;

    // Loose robot limbs are parallel-local visuals, but their identity, holder and world pose are
    // canonical server state. NetworkList makes them replay to late joiners instead of relying on
    // one-shot spawn/carry RPCs that only clients present at the time could ever observe.
    NetworkList<NetworkLimbState> _limbStates;
    float _nextLimbReconcileAt;

    const float LimbInteractRange = 4.5f;
    const float LimbDropRange = 3.5f;
    const double LimbClaimLeaseSeconds = 2.5d;

    // Packed order boxes are BoxPhysicsInteractable twins that each client instantiates
    // locally, so they have no NetworkObject and no NetworkObjectId to share. They get an
    // explicit server-issued id instead, based far above NGO's NetworkObjectId range so the
    // two id spaces can never overlap — FindByNetId serves both from one lookup.
    ulong _nextDynamicBoxNetId = 1UL << 32;
    bool _benchReadyNotified;

    readonly Dictionary<ulong, double> _nextOrderAcceptAt = new();
    const double OrderAcceptCooldownSeconds = 0.5d;
    const float OrderBoardUseDistance = 5f;
    const int MaxOrderNameLength = 64;

    // ── Team bonus ─────────────────────────────────────────────────────────────
    bool TeamHasDispatcher => HasRole(PlayerRoleType.Dispatcher);

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;

        _benchState   = new NetworkList<FixedString64Bytes>(
            new List<FixedString64Bytes>(),
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        _limbStates = new NetworkList<NetworkLimbState>(
            new List<NetworkLimbState>(),
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn()
    {
        _limbStates.OnListChanged += OnLimbStateChanged;
        foreach (var state in _limbStates)
            SeveredLimbItem.ApplyNetworkState(state);

        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnLimbClientDisconnected;

        // Clients follow the server's shipping gate as state, not only through the accept RPC, so a
        // player who joins or reconnects mid-order still sees the zone at the right doorway.
        if (!IsServer)
        {
            ShippingGateIndex.OnValueChanged += OnShippingGateChanged;
            OnShippingGateChanged(-1, ShippingGateIndex.Value);
            return;
        }

        if (GameManager.Instance != null)
        {
            GameManager.Instance.OnOrderAccepted  += OnOrderAcceptedLocally;
            GameManager.Instance.OnOrderCompleted += OnOrderCompletedLocally;
            GameManager.Instance.OnScoreChanged   += OnScoreChangedLocally;
            GameManager.Instance.OnOrderExpired   += OnOrderExpiredLocally;
        }

        if (AssemblyBench.Instance != null)
            AssemblyBench.Instance.OnBenchChanged += OnBenchChangedLocally;
    }

    public override void OnNetworkDespawn()
    {
        _limbStates.OnListChanged -= OnLimbStateChanged;
        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnLimbClientDisconnected;

        if (!IsServer)
        {
            ShippingGateIndex.OnValueChanged -= OnShippingGateChanged;
            return;
        }
        if (GameManager.Instance != null)
        {
            GameManager.Instance.OnOrderAccepted  -= OnOrderAcceptedLocally;
            GameManager.Instance.OnOrderCompleted -= OnOrderCompletedLocally;
            GameManager.Instance.OnScoreChanged   -= OnScoreChangedLocally;
            GameManager.Instance.OnOrderExpired   -= OnOrderExpiredLocally;
        }
        if (AssemblyBench.Instance != null)
            AssemblyBench.Instance.OnBenchChanged -= OnBenchChangedLocally;
    }

    void Update()
    {
        if (!IsSpawned || Time.unscaledTime < _nextLimbReconcileAt) return;
        _nextLimbReconcileAt = Time.unscaledTime + 0.75f;

        if (IsServer)
        {
            // Keep a coarse last-known holder position. It is not used to animate the carried
            // limb (that is parented to the replicated hand), but it gives disconnect recovery a
            // trustworthy place to drop it even after NGO has removed the player's connection.
            for (int i = 0; i < _limbStates.Count; i++)
            {
                var state = _limbStates[i];
                if (!state.InHand) continue;
                var holder = GetNetworkPlayer(state.HolderId);
                if (holder == null ||
                    (holder.transform.position - state.Position).sqrMagnitude < 0.25f * 0.25f)
                    continue;

                state.Position = holder.transform.position;
                state.Version++;
                _limbStates[i] = state;
            }
        }

        // Network state can arrive before the corresponding player visual has spawned. Replaying
        // the tiny list later is idempotent and fills that ordering gap without another RPC.
        foreach (var state in _limbStates)
            SeveredLimbItem.ApplyNetworkState(state);
    }

    // ── Persistent loose-limb state ──────────────────────────────────────────

    public bool ServerCreateLimb(NetworkPlayer source, int encodedPart, Vector3 requestedPosition,
        int encodedLivery, ulong sender)
    {
        if (!IsServer || source == null || source.OwnerClientId != sender ||
            !TryPart(encodedPart, out BodyPart part) ||
            !source.Severed(part) ||
            !System.Enum.IsDefined(typeof(PlayerRoleType), encodedLivery) ||
            !Finite(requestedPosition) ||
            (requestedPosition - source.transform.position).sqrMagnitude > LimbDropRange * LimbDropRange)
            return false;

        int existing = LimbIndex(source.OwnerClientId, encodedPart, orphan: false);
        if (existing >= 0) return false;

        _limbStates.Add(new NetworkLimbState
        {
            OwnerId = source.OwnerClientId,
            Part = (int)part,
            Livery = encodedLivery,
            Position = requestedPosition,
            Rotation = Quaternion.identity,
            HolderId = 0UL,
            InHand = false,
            Orphan = false,
            ClaimDeadline = 0d,
            Version = 1u
        });
        return true;
    }

    public bool ServerTryClaimLimb(int encodedPart, ulong ownerId, bool orphan, ulong sender)
    {
        if (!IsServer || !TryPart(encodedPart, out BodyPart part)) return false;
        int index = LimbIndex(ownerId, encodedPart, orphan);
        if (index < 0) return false;

        var state = _limbStates[index];
        var player = GetNetworkPlayer(sender);
        if (player == null || player.IsDeadNet.Value || player.IsRagdolledNet.Value ||
            !player.Severed(part) || state.InHand ||
            player.WeaponKind.Value != (int)MeleeWeaponKind.Fists ||
            (player.transform.position - state.Position).sqrMagnitude > LimbInteractRange * LimbInteractRange)
            return false;

        double now = NetworkManager.ServerTime.Time;
        bool activeClaim = state.HolderId != 0UL && state.HolderId != sender &&
                           state.ClaimDeadline > now && GetNetworkPlayer(state.HolderId) != null;
        if (activeClaim) return false;

        state.HolderId = sender;
        state.ClaimDeadline = now + LimbClaimLeaseSeconds;
        state.Version++;
        _limbStates[index] = state;
        return true;
    }

    public bool ServerSetLimbCarry(int encodedPart, bool inHand, Vector3 requestedPosition,
        ulong ownerId, bool orphan, ulong sender)
    {
        if (!IsServer || !TryPart(encodedPart, out BodyPart part) || !Finite(requestedPosition))
            return false;

        int index = LimbIndex(ownerId, encodedPart, orphan);
        if (index < 0) return false;

        var state = _limbStates[index];
        var player = GetNetworkPlayer(sender);
        if (player == null || state.HolderId != sender || !player.Severed(part)) return false;

        int weapon = player.WeaponKind.Value;
        if (inHand && weapon != (int)MeleeWeaponKind.Fists &&
            weapon != (int)MeleeWeaponKind.RobotLimb) return false;
        if (!inHand && weapon != (int)MeleeWeaponKind.RobotLimb && state.InHand) return false;

        if (!inHand &&
            (requestedPosition - player.transform.position).sqrMagnitude > LimbDropRange * LimbDropRange)
            return false;

        state.InHand = inHand;
        state.Position = inHand ? player.transform.position : requestedPosition;
        state.Rotation = Quaternion.identity;
        state.ClaimDeadline = 0d;
        if (!inHand) state.HolderId = 0UL;
        state.Version++;
        _limbStates[index] = state;
        if (inHand)
            player.WeaponKind.Value = (int)MeleeWeaponKind.RobotLimb;
        else if (player.WeaponKind.Value == (int)MeleeWeaponKind.RobotLimb)
            player.WeaponKind.Value = (int)MeleeWeaponKind.Fists;
        return true;
    }

    public bool ServerConsumeLimb(int encodedPart, ulong ownerId, bool orphan, ulong sender)
    {
        if (!IsServer || !TryPart(encodedPart, out BodyPart part)) return false;
        int index = LimbIndex(ownerId, encodedPart, orphan);
        if (index < 0) return false;

        var state = _limbStates[index];
        var player = GetNetworkPlayer(sender);
        if (player == null || state.HolderId != sender || !player.Severed(part)) return false;

        if (player.WeaponKind.Value == (int)MeleeWeaponKind.RobotLimb)
            player.WeaponKind.Value = (int)MeleeWeaponKind.Fists;
        _limbStates.RemoveAt(index);
        return true;
    }

    public bool ServerTryGetHeldLimb(int encodedPart, ulong ownerId, bool orphan, ulong sender,
        uint expectedVersion, out NetworkLimbState state)
    {
        state = default;
        if (!IsServer || !TryPart(encodedPart, out _)) return false;
        int index = LimbIndex(ownerId, encodedPart, orphan);
        if (index < 0) return false;

        state = _limbStates[index];
        return state.InHand && state.HolderId == sender && state.Version == expectedVersion;
    }

    public bool ServerTryConsumeHeldLimb(int encodedPart, ulong ownerId, bool orphan, ulong sender,
        uint expectedVersion, out NetworkLimbState consumed)
    {
        consumed = default;
        if (!ServerTryGetHeldLimb(encodedPart, ownerId, orphan, sender, expectedVersion,
                out var state))
            return false;

        int index = LimbIndex(ownerId, encodedPart, orphan);
        if (index < 0 || _limbStates[index].Version != expectedVersion) return false;
        consumed = state;
        var player = GetNetworkPlayer(sender);
        if (player != null && player.WeaponKind.Value == (int)MeleeWeaponKind.RobotLimb)
            player.WeaponKind.Value = (int)MeleeWeaponKind.Fists;
        _limbStates.RemoveAt(index);
        return true;
    }

    void OnLimbStateChanged(NetworkListEvent<NetworkLimbState> evt)
    {
        if (evt.Type == NetworkListEvent<NetworkLimbState>.EventType.Add ||
            evt.Type == NetworkListEvent<NetworkLimbState>.EventType.Value)
            SeveredLimbItem.ApplyNetworkState(evt.Value);
        else if (evt.Type == NetworkListEvent<NetworkLimbState>.EventType.Remove ||
                 evt.Type == NetworkListEvent<NetworkLimbState>.EventType.RemoveAt)
            SeveredLimbItem.RemoveNetworkState(evt.Value.OwnerId, (BodyPart)evt.Value.Part,
                                               evt.Value.Orphan);
    }

    void OnLimbClientDisconnected(ulong clientId)
    {
        Vector3 fallback = Vector3.zero;
        var player = GetNetworkPlayer(clientId);
        if (player != null) fallback = player.transform.position;

        for (int i = 0; i < _limbStates.Count; i++)
        {
            var state = _limbStates[i];
            bool changed = false;

            if (state.HolderId == clientId)
            {
                state.HolderId = 0UL;
                state.InHand = false;
                state.ClaimDeadline = 0d;
                if (fallback != Vector3.zero) state.Position = fallback;
                changed = true;
            }

            if (state.OwnerId == clientId && !state.Orphan)
            {
                state.Orphan = true;
                changed = true;
            }

            if (!changed) continue;
            state.Version++;
            _limbStates[i] = state;
        }
    }

    int LimbIndex(ulong ownerId, int part, bool orphan)
    {
        for (int i = 0; i < _limbStates.Count; i++)
        {
            var state = _limbStates[i];
            if (state.OwnerId == ownerId && state.Part == part && state.Orphan == orphan) return i;
        }
        return -1;
    }

    static bool TryPart(int encoded, out BodyPart part)
    {
        part = (BodyPart)encoded;
        return part == BodyPart.Head || part == BodyPart.LeftArm || part == BodyPart.RightArm ||
               part == BodyPart.LeftLeg || part == BodyPart.RightLeg;
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) &&
        Mathf.Abs(value.x) < 10000f && Mathf.Abs(value.y) < 10000f && Mathf.Abs(value.z) < 10000f;

    // ── Server: mirror local GameManager state → NetworkVariables ─────────────

    void OnOrderAcceptedLocally(OrderDefinition order)
    {
        _benchReadyNotified = false;
        ActiveOrderName.Value = new FixedString64Bytes(
            order.OrderName.Length > 63 ? order.OrderName[..63] : order.OrderName);

        // Asked for, not read back: LastGateIndex is only fresh if ShippingZone's own accept handler
        // happened to run before this one, and otherwise the clients were sent the previous order's
        // gate. PlaceForNewOrder rolls once per order whichever of the two gets there first.
        int gateIdx = ShippingZone.Instance?.PlaceForNewOrder() ?? -1;
        ShippingGateIndex.Value = gateIdx;

        NotifyOrderAcceptedClientRpc(order.OrderName, order.ClientName, order.Reward, gateIdx);
    }

    void OnShippingGateChanged(int _, int gateIdx)
    {
        if (gateIdx >= 0) ShippingZone.Instance?.SetGateByIndex(gateIdx);
    }

    void OnOrderCompletedLocally(OrderDefinition order)
    {
        _benchReadyNotified = false;
        ActiveOrderName.Value = new FixedString64Bytes("");
        CompletedOrdersNet.Value++;
    }

    void OnScoreChangedLocally(int score) => ScoreNet.Value = score;

    void OnOrderExpiredLocally(OrderDefinition order)
    {
        _benchReadyNotified = false;
        ActiveOrderName.Value = new FixedString64Bytes("");
        BroadcastOrderExpiredClientRpc(order.OrderName, GameManager.Instance.LastExpiryWasInsured);
    }

    void OnBenchChangedLocally()
    {
        if (!IsServer) return;
        SyncBenchState();
        if (!_benchReadyNotified && AssemblyBench.Instance?.IsOrderComplete() == true)
        {
            _benchReadyNotified = true;
            NotifyOrderReadyClientRpc();
        }
    }

    void SyncBenchState()
    {
        _benchState.Clear();
        var order = GameManager.Instance?.ActiveOrder;
        if (order == null) return;
        var bench = AssemblyBench.Instance;
        if (bench == null) return;

        foreach (var req in order.RequiredParts)
        {
            int placed = bench.GetPlacedCount(req.Part);
            if (placed > 0)
                _benchState.Add(new FixedString64Bytes($"{req.Part.PartID}:{placed}"));
        }
    }

    // ── Packed box spawn (server-authoritative) ───────────────────────────────

    /// Called by any client when the assembler presses E on a complete bench.
    /// Server validates, measures ghost, assigns stable NetBoxId, broadcasts spawn to all clients.
    [ServerRpc(RequireOwnership = false)]
    public void RequestSpawnPackedBoxServerRpc(ServerRpcParams rpcParams = default)
    {
        var bench = AssemblyBench.Instance;
        var sender = GetNetworkPlayer(rpcParams.Receive.SenderClientId);
        if (bench == null || sender == null || sender.IsDeadNet.Value ||
            sender.IsRagdolledNet.Value ||
            Vector3.SqrMagnitude(sender.transform.position - bench.transform.position) > 25f ||
            !bench.IsOrderComplete())
            return;

        ulong netId      = _nextDynamicBoxNetId++;
        Vector3 itemSize = bench.GetGhostSize();
        Vector3 spawnPos = bench.GetPackedSpawnPosition();
        Quaternion rot   = bench.transform.rotation;
        string orderName = bench.ActiveOrder?.OrderName ?? ActiveOrderName.Value.ToString();

        // Clear server-side bench immediately to prevent a second concurrent request
        // from passing the IsOrderComplete() check above.
        bench.ClearBench();

        SpawnPackedBoxClientRpc(itemSize, spawnPos, rot, netId, orderName);
    }

    [ClientRpc]
    void SpawnPackedBoxClientRpc(Vector3 itemSize, Vector3 spawnPos, Quaternion spawnRot, ulong netId,
        string orderName)
    {
        var bench = AssemblyBench.Instance;
        if (bench == null) return;

        var boxGO = bench.CreatePackedBoxObject(itemSize, spawnPos, spawnRot);
        if (boxGO.TryGetComponent<PackedOrderBox>(out var packed))
        {
            OrderDefinition order = null;
            var orders = GameManager.Instance?.GetAllOrders();
            if (orders != null)
                foreach (var candidate in orders)
                    if (candidate != null && candidate.OrderName == orderName)
                    {
                        order = candidate;
                        break;
                    }
            packed.BindOrder(order, orderName);
        }
        boxGO.GetComponent<BoxPhysicsInteractable>()?.ForceNetId(netId);

        // Non-server clients still have an un-cleared bench; clear it now.
        if (bench.ActiveOrder != null)
            bench.ClearBench();

        ShippingZone.Instance?.FlashForPickup();
        HUDController.Instance?.AddNotification(
            "Order packed!", "Grab the box [E] and carry it to the blue beacon by the gate",
            HUDController.NotifType.Success);
        AudioManager.Instance?.PlayPlacePart();
    }

    // ── Order management ServerRpcs ───────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void AcceptOrderServerRpc(string orderName, ServerRpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        var   sender   = GetNetworkPlayer(senderId);

        if (sender == null || sender.IsDeadNet.Value || sender.IsRagdolledNet.Value ||
            string.IsNullOrWhiteSpace(orderName) || orderName.Length > MaxOrderNameLength ||
            !IsNearOrderBoard(sender))
            return;

        double now = NetworkManager.ServerTime.Time;
        if (_nextOrderAcceptAt.TryGetValue(senderId, out double nextAllowed) && now < nextAllowed)
            return;
        _nextOrderAcceptAt[senderId] = now + OrderAcceptCooldownSeconds;

        bool senderIsDispatcher = sender.RoleType == PlayerRoleType.Dispatcher;

        if (TeamHasDispatcher && !senderIsDispatcher)
        {
            SendNotifClientRpc(
                "Dispatcher only", "Only the Dispatcher can accept orders",
                (int)HUDController.NotifType.Warning, SingleClient(senderId));
            return;
        }

        // GetAllOrders(), not GetAvailableOrders() -- the board the sender clicked was built
        // from the full catalogue (HUDController.BuildOrderboardContent), while
        // GetAvailableOrders() narrows to just the first order until one is completed.
        // Looking the name up in the narrowed list silently dropped every click on any card
        // but the first, leaving nobody with an active order. Singleplayer already accepts
        // straight from the full list, so this also makes the two paths agree.
        var orders = GameManager.Instance?.GetAllOrders();

        OrderDefinition target = null;
        if (orders != null)
            foreach (var o in orders)
                if (o.OrderName == orderName) { target = o; break; }

        if (target == null)
        {
            SendNotifClientRpc(
                "Order unavailable", $"«{orderName}» is no longer on the board",
                (int)HUDController.NotifType.Warning, SingleClient(senderId));
            return;
        }

        // AcceptOrder refuses when another order is already running or the demo is finished.
        // Report it rather than returning silently -- a dead-feeling click with no explanation
        // is exactly how the bug above stayed hidden.
        if (!GameManager.Instance.AcceptOrder(target))
        {
            SendNotifClientRpc(
                "Cannot accept", GameManager.Instance.ActiveOrder != null
                    ? "The team already has an active order"
                    : "No more orders available",
                (int)HUDController.NotifType.Warning, SingleClient(senderId));
        }
    }

    /// Called by whoever installs a carried part box at the bench.
    [ServerRpc(RequireOwnership = false)]
    public void PlacePartAtBenchServerRpc(ulong boxId, uint expectedVersion,
        ServerRpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        var phys = NetworkPhysBoxManager.Instance;
        if (phys == null ||
            !phys.ServerTryGetHeldBox(boxId, senderId, expectedVersion, out var box, out _))
        {
            RejectTransaction(senderId, "Part not installed", "The box is no longer held by you");
            return;
        }

        var def = box != null ? box.GetComponent<BoxWithPart>()?.Part : null;
        var bench = AssemblyBench.Instance;
        if (box == null || def == null || bench == null || !bench.IsPartNeeded(def))
        {
            Debug.LogWarning($"[CoopGameManager] Rejected bench install for box {boxId:X}: " +
                             $"box={(box != null ? box.name : "missing")}, " +
                             $"part={(def != null ? def.PartID : "missing")}");
            SendNotifClientRpc(
                "Part not installed", "The server could not verify this box — pick it up and try again",
                (int)HUDController.NotifType.Warning, SingleClient(senderId));
            return;
        }

        var sender = GetNetworkPlayer(senderId);
        if (sender == null || Vector3.SqrMagnitude(sender.transform.position - bench.transform.position) > 25f)
        {
            RejectTransaction(senderId, "Part not installed", "Move closer to the assembly bench");
            return;
        }

        // Re-check and consume in the same server turn. A repeated or delayed request fails here
        // before it can increment the bench a second time.
        if (!phys.ServerTryConsumeHeldBox(boxId, senderId, expectedVersion, out _))
        {
            RejectTransaction(senderId, "Part not installed", "The box state changed — try again");
            return;
        }

        bench.PlacePartFromNetwork(def);
        OnBenchChangedLocally();

        // One authoritative operation: once accepted, every peer (including the sender) removes
        // precisely the box whose contents were installed.
        NetworkBoxManager.Instance?.ClearBoxAssignment(boxId);

        // Broadcast ghost update to non-server clients
        UpdateBenchGhostClientRpc(def.PartID);
    }

    [ClientRpc]
    void UpdateBenchGhostClientRpc(string partId)
    {
        if (IsServer) return;
        var def = PartRegistry.Get(partId);
        if (def == null) return;
        AssemblyBench.Instance?.PlacePartFromNetwork(def);
    }

    [ServerRpc(RequireOwnership = false)]
    public void DeliverPackedOrderServerRpc(ulong boxId, uint expectedVersion,
        ServerRpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        var game = GameManager.Instance;

        if (game?.ActiveOrder == null)
        {
            RejectTransaction(senderId, "No active order", "Take an order from the board");
            return;
        }

        var phys = NetworkPhysBoxManager.Instance;
        if (phys == null ||
            !phys.ServerTryGetHeldBox(boxId, senderId, expectedVersion, out var box, out _) ||
            box.GetComponent<PackedOrderBox>() is not { } packed ||
            !packed.MatchesOrder(game.ActiveOrder))
        {
            RejectTransaction(senderId, "Delivery rejected", "This is not the active order box");
            return;
        }

        var sender = GetNetworkPlayer(senderId);
        var zone = ShippingZone.Instance;
        if (sender == null || zone == null || !zone.ServerCanAcceptDelivery(sender.transform.position))
        {
            RejectTransaction(senderId, "Delivery rejected", "Carry the box into the active shipping zone");
            return;
        }

        if (!phys.ServerTryConsumeHeldBox(boxId, senderId, expectedVersion, out _))
        {
            RejectTransaction(senderId, "Delivery rejected", "The box state changed — try again");
            return;
        }

        CompleteShippingOnServer(game);
    }

    /// Called only by the server's ShippingZone trigger for a packed box that was physically
    /// thrown or dropped onto the plate. Client trigger overlaps are ignored.
    public bool TryDeliverDroppedPackedOrder(ulong boxId)
    {
        if (!IsServer) return false;

        var game = GameManager.Instance;
        var zone = ShippingZone.Instance;
        var phys = NetworkPhysBoxManager.Instance;
        var box = BoxPhysicsInteractable.FindByNetId(boxId);
        var packed = box != null ? box.GetComponent<PackedOrderBox>() : null;

        if (game?.ActiveOrder == null || zone == null || phys == null || box == null ||
            packed == null || !packed.MatchesOrder(game.ActiveOrder) ||
            !zone.ServerContainsDeliveryBox(box.transform.position) ||
            !phys.ServerTryConsumeUnheldBox(boxId, out _))
            return false;

        CompleteShippingOnServer(game);
        return true;
    }

    void CompleteShippingOnServer(GameManager game)
    {
        game.CompleteOrder(GetTeamMultiplier());
        BroadcastShippingCompletedClientRpc(
            game.LastBaseReward,
            game.LastSpeedBonus,
            game.LastComboBonus,
            game.LastTeamBonus,
            game.LastTotalReward,
            game.ComboCount,
            game.LastOrderGrade);
    }

    void RejectTransaction(ulong clientId, string title, string body) =>
        SendNotifClientRpc(title, body, (int)HUDController.NotifType.Warning, SingleClient(clientId));

    // ── ClientRpcs ────────────────────────────────────────────────────────────

    [ClientRpc]
    void NotifyOrderAcceptedClientRpc(string orderName, string client, int reward, int gateIdx)
    {
        if (IsServer) return;

        var orders = GameManager.Instance?.GetAllOrders();
        if (orders != null)
            foreach (var o in orders)
                if (o.OrderName == orderName) { GameManager.Instance.AcceptOrder(o); break; }
        // HUD updates (SetActiveTask, AddNotification, RefreshTaskList, timer) are handled
        // by HUDController.BindGameManager() which subscribes to GameManager.OnOrderAccepted —
        // fired by AcceptOrder() above. No explicit HUD calls needed here.

        if (gateIdx >= 0)
            ShippingZone.Instance?.SetGateByIndex(gateIdx);
    }

    [ClientRpc]
    void NotifyOrderReadyClientRpc()
    {
        HUDController.Instance?.NotifyAssemblerBenchReady();
    }

    [ClientRpc]
    void BroadcastShippingCompletedClientRpc(int baseReward, int speedBonus, int comboBonus,
        int teamBonus, int totalReward, int comboCount, string grade)
    {
        if (IsServer) return;
        GameManager.Instance?.CompleteOrderFromNetwork(
            baseReward, speedBonus, comboBonus, teamBonus, totalReward, comboCount, grade);
        // CompleteOrderFromNetwork raises OnOrderCompleted. HUDController handles that event
        // with the grade/reward toast and one concise notification, so adding another "Shipped"
        // entry here only duplicated UI on clients (the host never saw that duplicate).
    }

    [ClientRpc]
    void BroadcastOrderExpiredClientRpc(string orderName, bool insured)
    {
        if (IsServer) return;
        // Server's GameManager already called ExpireOrder(); mirror it on clients.
        GameManager.Instance?.ForceExpireOrder(insured);
        HUDController.Instance?.AddRadioMessage(
            insured ? $"Insurance covered «{orderName}» — combo protected" : $"Deadline: «{orderName}»",
            "System");
    }

    [ClientRpc]
    void SendNotifClientRpc(string title, string body, int notifTypeInt,
        ClientRpcParams rpcParams = default)
    {
        HUDController.Instance?.AddNotification(
            title, body, (HUDController.NotifType)notifTypeInt);
    }

    // ── Mini-event broadcast ──────────────────────────────────────────────────

    /// Asks the server to start a named event, for a peer that cannot start one itself.
    ///
    /// The scheduler is the server's alone — GameEventManager.Update early-returns on a client, and
    /// ForceStartEvent refuses there too, because an event rolled locally would pick its own gate,
    /// its own raider count and its own toolkit position and none of them would match anybody
    /// else's. That rule is right for the game and wrong for the debug menu, which is the one place
    /// a human deliberately starts an event and may well be sitting on a client while doing it.
    /// Without this hop, "Fire Gate Breach" from a client did nothing anywhere: the event ran
    /// locally, saw IsClientOnly, skipped the spawn, and closed itself with no raiders on any
    /// machine.
    [ServerRpc(RequireOwnership = false)]
    public void RequestEventServerRpc(string eventName, ServerRpcParams rpcParams = default)
    {
        // This is a developer command, not a gameplay request. Shipping clients must never be
        // able to start arbitrary raids/events, and even in a development build only the host may
        // invoke it. Dedicated servers can still start events through their server-side scheduler.
        if ((!Debug.isDebugBuild && !Application.isEditor) ||
            NetworkManager.Singleton == null ||
            rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId ||
            string.IsNullOrWhiteSpace(eventName) || eventName.Length > 64)
            return;

        GameEventManager.Instance?.ForceStartEvent(eventName);
    }


    /// Fires on ALL clients (including server-host) when the server starts a mini-event.
    /// GameEventManager.ApplyEventFromNetwork creates the event with the server's pre-chosen
    /// random data so all clients run the same scenario.
    [ClientRpc]
    public void BroadcastEventStartClientRpc(string eventName, string payload)
    {
        GameEventManager.Instance?.ApplyEventFromNetwork(eventName, payload);
    }

    /// Fires on non-server clients when the server's event timer expires.
    [ClientRpc]
    public void BroadcastEventEndClientRpc(string eventName)
    {
        if (IsServer) return; // server's MarkEventDone already cleaned up its instance
        GameEventManager.Instance?.EndEventFromNetwork(eventName);
    }

    /// Called by any client (Dispatcher) to fix the radio interference event.
    /// Server verifies role, then ends the event for all clients.
    [ServerRpc(RequireOwnership = false)]
    public void FixRadioServerRpc(ServerRpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        NetworkPlayer caller = FindPlayerByClientId(senderId);
        bool dispatcherRequired = RoleTaskManager.TeamHasRole(PlayerRoleType.Dispatcher);
        if (caller == null || (dispatcherRequired && caller.RoleType != PlayerRoleType.Dispatcher) ||
            RadioInterferenceEvent.Active == null ||
            RadioRepairPanel.FindNearest(caller.transform.position, RepairInteractionDistance) == null ||
            NetworkShopManager.Instance?.ServerHasRepairKit != true)
            return;

        RadioInterferenceEvent.Active?.Fix();
    }

    /// Called by whichever client just completed the hold-to-repair on a power box
    /// (PowerFailureEvent / FireAlarmEvent target). Echoed to every client so all of them
    /// call ForceRepair() on the same box and their own event instance's repaired-count
    /// advances together — see PowerBoxRepairTarget for why this can't apply locally.
    [ServerRpc(RequireOwnership = false)]
    public void RepairPowerBoxServerRpc(int boxIndex, ServerRpcParams rpcParams = default)
    {
        var target = PowerBoxRepairTarget.FindByIndex(boxIndex);
        var caller = FindPlayerByClientId(rpcParams.Receive.SenderClientId);
        if (target == null || target.IsRepaired || caller == null ||
            !WithinRepairDistance(caller.transform, target.transform) ||
            (target.RequiresToolkit && NetworkShopManager.Instance?.ServerHasRepairKit != true))
            return;

        // Apply on the server first so a second RPC observes the repaired tombstone immediately.
        target.ForceRepair();
        RepairPowerBoxClientRpc(boxIndex);
    }

    [ClientRpc]
    void RepairPowerBoxClientRpc(int boxIndex)
    {
        if (IsServer) return;
        PowerBoxRepairTarget.FindByIndex(boxIndex)?.ForceRepair();
    }

    /// Called by whichever client just put a forced gate right. Echoed so every client shuts the
    /// same shutter and clears the same cracks — without it a non-host repairing would leave every
    /// other client looking at a hole that is no longer there, and their next raid would still
    /// walk straight through it.
    [ServerRpc(RequireOwnership = false)]
    public void RepairGateServerRpc(int gateIndex, ServerRpcParams rpcParams = default)
    {
        var target = GateRepairTarget.FindByIndex(gateIndex);
        var caller = FindPlayerByClientId(rpcParams.Receive.SenderClientId);
        if (target == null || target.IsRepaired || caller == null ||
            !WithinRepairDistance(caller.transform, target.transform) ||
            NetworkShopManager.Instance?.TryConsumeRepairKitServer() != true)
            return;

        target.ForceRepair();
        RepairGateClientRpc(gateIndex);
    }

    [ClientRpc]
    void RepairGateClientRpc(int gateIndex)
    {
        if (IsServer) return;
        GateRepairTarget.FindByIndex(gateIndex)?.ForceRepair();
    }

    /// Called by whichever client just completed the hold-to-repair on a vehicle (jammed
    /// fork / broken forklift — BrokenForkEvent / BrokenForkliftEvent). Echoed to every
    /// client so all of them clear the fault on their own copy of the same vehicle and their
    /// own event instance ends together — without this, a non-host repairing left every
    /// other client's copy of the vehicle jammed/damaged forever (BrokenFork has no
    /// auto-timer at all; BrokenForklift only recovered after its own 45s local timer).
    [ServerRpc(RequireOwnership = false)]
    public void RepairVehicleFaultServerRpc(int truckIndex, ServerRpcParams rpcParams = default)
    {
        if (truckIndex < 0 || truckIndex >= ReachTruckController.All.Count) return;
        var truck = ReachTruckController.All[truckIndex];
        var caller = FindPlayerByClientId(rpcParams.Receive.SenderClientId);
        if (truck == null || !truck.HasRepairFault || caller == null ||
            !WithinRepairDistance(caller.transform, truck.transform) ||
            NetworkShopManager.Instance?.ServerHasRepairKit != true)
            return;

        // The owning fault event consumes exactly one charge while being removed on the server.
        truck.ForceRepairFaults();
        RepairVehicleFaultClientRpc(truckIndex);
    }

    [ClientRpc]
    void RepairVehicleFaultClientRpc(int truckIndex)
    {
        if (IsServer) return;
        if (truckIndex < 0 || truckIndex >= ReachTruckController.All.Count) return;
        ReachTruckController.All[truckIndex].ForceRepairFaults();
    }

    const float RepairInteractionDistance = 6f;

    static NetworkPlayer FindPlayerByClientId(ulong clientId)
    {
        foreach (var player in NetworkPlayer.All)
            if (player.OwnerClientId == clientId) return player;
        return null;
    }

    static bool WithinRepairDistance(Transform player, Transform target) =>
        player != null && target != null &&
        Vector3.SqrMagnitude(player.position - target.position) <=
        RepairInteractionDistance * RepairInteractionDistance;

    /// Called by whichever client just carried a RushOrderEvent box into its event shipping zone.
    /// Echoed to every client so the event's remaining-box count advances together — without
    /// this, only the delivering client's own copy of the event ever saw progress, so the
    /// order silently required one player to deliver every box alone.
    [ServerRpc(RequireOwnership = false)]
    public void NotifyRushBoxDeliveredServerRpc(ulong netBoxId, uint expectedVersion,
        ServerRpcParams rpcParams = default)
    {
        var active = GameEventManager.Instance?.GetActiveEvent("RushOrder") as RushOrderEvent;
        if (active == null ||
            !active.ServerTryDeliver(rpcParams.Receive.SenderClientId, netBoxId, expectedVersion))
            return;

        NotifyRushBoxDeliveredClientRpc(netBoxId);
    }

    [ClientRpc]
    void NotifyRushBoxDeliveredClientRpc(ulong netBoxId)
    {
        if (IsServer) return;
        (GameEventManager.Instance?.GetActiveEvent("RushOrder") as RushOrderEvent)?.ApplyDelivery(netBoxId);
    }

    /// Called by whichever client just carried the UrgentPartEvent's target part into the
    /// shipping zone. Echoed to every client so the event ends for everyone — without this,
    /// a non-host delivering it left the event running (and eventually timing out as
    /// "not delivered") on the host and every other client.
    [ServerRpc(RequireOwnership = false)]
    public void NotifyUrgentPartDeliveredServerRpc(ulong netBoxId, uint expectedVersion,
        ServerRpcParams rpcParams = default)
    {
        var active = GameEventManager.Instance?.GetActiveEvent("UrgentPart") as UrgentPartEvent;
        if (active == null ||
            !active.ServerTryDeliver(rpcParams.Receive.SenderClientId, netBoxId, expectedVersion))
            return;

        NotifyUrgentPartDeliveredClientRpc();
    }

    [ClientRpc]
    void NotifyUrgentPartDeliveredClientRpc()
    {
        if (IsServer) return;
        (GameEventManager.Instance?.GetActiveEvent("UrgentPart") as UrgentPartEvent)?.ForceComplete();
    }

    /// Fires on non-server clients when a gameplay system removes a bench part on the server.
    /// Keeps all clients' bench ghost in sync without touching the staging NetworkList.
    [ClientRpc]
    public void BroadcastBenchPartRemovedClientRpc(string partId)
    {
        if (IsServer) return;
        AssemblyBench.Instance?.TryRemovePartById(partId);
    }

    /// Mirror of the above: fires on non-server clients when a stolen part goes back onto the
    /// bench (ThiefController dropping its loot when the players put the thief down).
    [ClientRpc]
    public void BroadcastBenchPartRestoredClientRpc(string partId)
    {
        if (IsServer) return;
        var def = PartRegistry.Get(partId);
        if (def != null) AssemblyBench.Instance?.PlacePartFromNetwork(def);
    }

    // ── Team score bonus ──────────────────────────────────────────────────────

    public float GetTeamMultiplier()
    {
        var roles = new HashSet<PlayerRoleType>();
        foreach (var p in NetworkPlayer.All)
            if (p != null && p.RoleType != PlayerRoleType.None) roles.Add(p.RoleType);

        // Nobody loses money for playing with a small team. Diversity is an additive reward:
        // two unique roles +10%, three +20%, the full four-role crew +30%.
        return 1f + Mathf.Max(0, roles.Count - 1) * 0.10f;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static NetworkPlayer GetNetworkPlayer(ulong clientId)
    {
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client))
            return null;
        return client.PlayerObject?.GetComponent<NetworkPlayer>();
    }

    static bool IsNearOrderBoard(NetworkPlayer player)
    {
        if (player == null) return false;
        foreach (var board in Object.FindObjectsByType<OrderBoard>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (board != null && (player.transform.position - board.transform.position).sqrMagnitude <=
                OrderBoardUseDistance * OrderBoardUseDistance)
                return true;
        return false;
    }

    // ── Shelf tip-over networking ─────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void ShelfTipOverServerRpc(int shelfNetId, Vector3 hitDirection,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        NetworkPlayer player = GetNetworkPlayer(sender);
        if (player == null || player.IsDeadNet.Value || player.IsRagdolledNet.Value ||
            !FiniteShelfVector(hitDirection) ||
            !PhysicsShelf.Registry.TryGetValue(shelfNetId, out var shelf) ||
            shelf == null || shelf.IsTipped)
            return;

        // A client may report that its locally predicted truck hit something, but it never gets
        // to choose the victim or the fall direction. The server must find a spawned reach truck
        // currently occupied by that exact connection and confirm its colliders are touching the
        // requested shelf. The shelf then derives the direction from those authoritative bounds.
        Vector3 authoritativeDirection = Vector3.zero;
        bool confirmedImpact = false;
        foreach (var vehicle in Object.FindObjectsByType<NetworkVehicleSync>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (vehicle == null || !vehicle.IsSpawned || !vehicle.IsDrivenBy(sender) ||
                vehicle.GetComponent<ReachTruckController>() == null)
                continue;

            if (!shelf.ServerTryValidateVehicleContact(vehicle.transform,
                    out authoritativeDirection))
                continue;

            confirmedImpact = true;
            break;
        }

        if (!confirmedImpact) return;

        // Run the authoritative shelf on the server as well. A dedicated server is not a ClientRpc
        // recipient, and it must own the crush checks and the one-shot _tipped transition.
        shelf.TipOverLocal(authoritativeDirection, fromDomino: false);
        ShelfTipOverClientRpc(shelfNetId, authoritativeDirection);
    }

    [ClientRpc]
    void ShelfTipOverClientRpc(int shelfNetId, Vector3 hitDirection)
    {
        if (PhysicsShelf.Registry.TryGetValue(shelfNetId, out var shelf))
            shelf.TipOverLocal(hitDirection, fromDomino: false);
    }

    static bool FiniteShelfVector(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    static bool HasRole(PlayerRoleType role)
    {
        foreach (var p in NetworkPlayer.All)
            if (p.RoleType == role) return true;
        return false;
    }

    static ClientRpcParams SingleClient(ulong clientId) => new()
    {
        Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
    };
}

public struct NetworkLimbState : INetworkSerializable, System.IEquatable<NetworkLimbState>
{
    public ulong OwnerId;
    public int Part;
    public int Livery;
    public Vector3 Position;
    public Quaternion Rotation;
    public ulong HolderId;
    public bool InHand;
    public bool Orphan;
    public double ClaimDeadline;
    public uint Version;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref OwnerId);
        serializer.SerializeValue(ref Part);
        serializer.SerializeValue(ref Livery);
        serializer.SerializeValue(ref Position);
        serializer.SerializeValue(ref Rotation);
        serializer.SerializeValue(ref HolderId);
        serializer.SerializeValue(ref InHand);
        serializer.SerializeValue(ref Orphan);
        serializer.SerializeValue(ref ClaimDeadline);
        serializer.SerializeValue(ref Version);
    }

    public bool Equals(NetworkLimbState other) =>
        OwnerId == other.OwnerId && Part == other.Part && Livery == other.Livery &&
        Position == other.Position && Rotation == other.Rotation && HolderId == other.HolderId &&
        InHand == other.InHand && Orphan == other.Orphan &&
        ClaimDeadline.Equals(other.ClaimDeadline) && Version == other.Version;

    public override bool Equals(object obj) => obj is NetworkLimbState other && Equals(other);
    public override int GetHashCode() => OwnerId.GetHashCode() * 397 ^ Part * 31 ^ Version.GetHashCode();
}
