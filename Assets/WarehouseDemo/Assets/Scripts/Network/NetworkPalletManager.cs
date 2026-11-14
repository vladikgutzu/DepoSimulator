using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Server-authoritative manager for pallet lift sync and box stacking on pallets.
/// Pattern mirrors NetworkPhysBoxManager — stable integer IDs, first-come-first-served.
///
/// Implements: multiplayer pallet networking design.
/// Single-player: all paths are gated on NetworkManager.Singleton?.IsListening.
public class NetworkPalletManager : NetworkBehaviour
{
    public static NetworkPalletManager Instance { get; private set; }

    NetworkList<PalletLiftState> _liftList;
    NetworkList<PalletCargoState> _cargoList;
    readonly Dictionary<int, LiftAuthority> _liftAuthorities = new();
    readonly Dictionary<int, uint> _appliedRestVersions = new();
    readonly Dictionary<int, PalletStackZone> _zonesByPalletId = new();
    const int CargoChecksPerFrame = 12;
    float _nextLiftTick;
    float _nextCargoReconcile;
    int _cargoReconcileCursor = -1;
    bool _cargoReconcileActive;
    bool _sceneCargoRegistered;

    struct LiftAuthority
    {
        public ulong VehicleNetworkObjectId;
        public Vector3 ForkLocalPosition;
        public Quaternion ForkLocalRotation;
    }

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;

        _liftList = new NetworkList<PalletLiftState>(
            new List<PalletLiftState>(),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        _cargoList = new NetworkList<PalletCargoState>(
            new List<PalletCargoState>(),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn()
    {
        // Claimed here, not only in Awake. Awake runs once per scene load, this is cleared in
        // OnNetworkDespawn, and the co-op flow deliberately reuses the scene between rooms — so
        // from the second room onwards the static stayed null on the machine that hosted the first
        // one, and every `Instance?.` call through it did nothing at all. That reads in-game as
        // "nobody can pick anything up", because a grab is exactly such a call.
        if (Instance == null) Instance = this;

        _cargoReconcileActive = false;
        _cargoReconcileCursor = -1;
        _nextCargoReconcile = 0f;
        _zonesByPalletId.Clear();

        _liftList.OnListChanged += OnLiftListChanged;
        _cargoList.OnListChanged += OnCargoListChanged;

        // Late-join clients: apply any lifts already in progress.
        if (!IsServer)
        {
            foreach (var state in _liftList)
                ApplyLiftLocally(state, playSound: false);
            foreach (var state in _cargoList)
                ApplyCargoLocally(state);
        }

        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            StartCoroutine(RegisterSceneCargoSoon());
        }

        if (!IsServer) StartCoroutine(ReportSceneIdsSoon());
    }

    public override void OnNetworkDespawn()
    {
        _liftList.OnListChanged -= OnLiftListChanged;
        _cargoList.OnListChanged -= OnCargoListChanged;
        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (!IsSpawned || _cargoList == null) return;

        if (IsServer && Time.time >= _nextLiftTick)
        {
            // Lift poses are reliable NetworkList deltas. Twenty updates per second remain smooth
            // with PalletBehavior interpolation and avoid building a reliable backlog behind box
            // state when the host is busy.
            _nextLiftTick = Time.time + 1f / 20f;
            ServerTickLiftedPallets();
        }

        if (!_cargoReconcileActive)
        {
            if (Time.time < _nextCargoReconcile) return;

            _cargoReconcileActive = true;
            _cargoReconcileCursor = _cargoList.Count - 1;

            // Lift rows are few and cheap. Refresh them once at the beginning of the client's
            // recovery pass, then spread the potentially large cargo manifest over frames.
            if (!IsServer)
                foreach (var state in _liftList)
                    ApplyLiftLocally(state, playSound: false);
        }

        // Clients consume the manifest. The server owns PhysX and watches the same manifest for
        // boxes that have actually been knocked out of their saved pallet slot.
        if (IsServer)
        {
            if (ReconcileServerCargoPhysicsBatch())
                FinishCargoReconcile(0.1f);
            return;
        }

        // A late join can receive the list before its scene boxes/pallet registry is ready.
        // Re-applying the manifest makes that ordering harmless and repairs missed placement.
        // Work in bounded batches: hundreds of authored cartons used to land in one 9 ms spike.
        if (ApplyClientCargoBatch())
            FinishCargoReconcile(1f);
    }

    void FinishCargoReconcile(float delay)
    {
        _cargoReconcileActive = false;
        _cargoReconcileCursor = -1;
        _nextCargoReconcile = Time.time + delay;
    }

    bool ApplyClientCargoBatch()
    {
        int remaining = CargoChecksPerFrame;
        while (remaining-- > 0 && _cargoReconcileCursor >= 0)
        {
            if (_cargoReconcileCursor >= _cargoList.Count)
                _cargoReconcileCursor = _cargoList.Count - 1;
            if (_cargoReconcileCursor < 0) break;

            ApplyCargoLocally(_cargoList[_cargoReconcileCursor--]);
        }
        return _cargoReconcileCursor < 0;
    }

    System.Collections.IEnumerator RegisterSceneCargoSoon()
    {
        // PalletStackZone builds its authored-box list in Start. Waiting one frame guarantees the
        // list and stable scene IDs exist before the server publishes the initial manifest.
        yield return null;
        if (!IsSpawned || !IsServer || _sceneCargoRegistered) yield break;

        _sceneCargoRegistered = true;
        foreach (var zone in PalletStackZone.All)
        {
            if (zone == null) continue;
            var pallet = zone.GetComponent<PalletBehavior>() ??
                         zone.GetComponentInParent<PalletBehavior>();
            if (pallet == null) continue;

            foreach (var box in zone.BoxesOnPallet)
            {
                if (box == null || CargoIndexOf(box.NetBoxId) >= 0) continue;
                _cargoList.Add(new PalletCargoState
                {
                    BoxId    = box.NetBoxId,
                    PalletId = pallet.NetPalletId,
                    LocalPos = zone.transform.InverseTransformPoint(box.transform.position),
                    LocalRot = Quaternion.Inverse(zone.transform.rotation) * box.transform.rotation,
                    Version  = box.NetVersion + 1,
                });
            }
        }
    }

    bool ReconcileServerCargoPhysicsBatch()
    {
        int remaining = CargoChecksPerFrame;
        while (remaining-- > 0 && _cargoReconcileCursor >= 0)
        {
            if (_cargoReconcileCursor >= _cargoList.Count)
                _cargoReconcileCursor = _cargoList.Count - 1;
            if (_cargoReconcileCursor < 0) break;

            int i = _cargoReconcileCursor--;
            PalletCargoState state = _cargoList[i];
            var box = BoxPhysicsInteractable.FindByNetId(state.BoxId);
            var pallet = PalletBehavior.FindByNetId(state.PalletId);
            if (box == null || pallet == null || box.IsLocallyGrabbed || box.IsRemotelyGrabbed)
                continue;

            var zone = ResolveZone(pallet);
            if (zone == null) continue;

            Vector3 localPos = zone.transform.InverseTransformPoint(box.transform.position);
            Quaternion localRot = Quaternion.Inverse(zone.transform.rotation) * box.transform.rotation;

            // A small amount of contact settling is harmless. Crossing these limits means PhysX
            // has genuinely toppled or pushed the carton out of its authoritative pallet slot.
            bool leftPallet = !zone.IsStillPalletCargo(box);
            bool displaced  = Vector3.Distance(localPos, state.LocalPos) > 0.16f;
            bool toppled    = Quaternion.Angle(localRot, state.LocalRot) > 12f;
            // During transport a carton is allowed to slide or rotate while it remains physically
            // over the deck. Once parked, retain the tighter saved-slot checks used to repair
            // authored stacks.
            if (!leftPallet && (pallet.IsLifted || (!displaced && !toppled))) continue;

            var rb = box.GetComponent<Rigidbody>();
            Vector3 velocity = rb != null ? rb.linearVelocity : Vector3.zero;
            Vector3 angular  = rb != null ? rb.angularVelocity : Vector3.zero;

            zone.DetachAuthoritativeCargo(box);
            _cargoList.RemoveAt(i);
            box.EnablePersistentPhysics(velocity, angular);
            NetworkPhysBoxManager.Instance?.ServerPublishWorldMotion(
                state.BoxId, box.transform.position, box.transform.rotation, velocity,
                state.Version + 1);
        }
        return _cargoReconcileCursor < 0;
    }

    PalletStackZone ResolveZone(PalletBehavior pallet)
    {
        if (pallet == null) return null;
        int id = pallet.NetPalletId;
        if (_zonesByPalletId.TryGetValue(id, out var cached) && cached != null)
            return cached;

        var zone = pallet.GetComponent<PalletStackZone>() ??
                   pallet.GetComponentInChildren<PalletStackZone>();
        if (zone != null) _zonesByPalletId[id] = zone;
        return zone;
    }

    System.Collections.IEnumerator ReportSceneIdsSoon()
    {
        yield return new WaitForSeconds(1f);
        ReportSceneIds();
    }

    public static void ReportSceneIds()
    {
        var inst = Instance;
        var nm = NetworkManager.Singleton;
        if (inst == null || !inst.IsSpawned || nm == null || !nm.IsListening || nm.IsServer) return;

        inst.VerifySceneIdsServerRpc(PalletBehavior.SceneIdCount, PalletBehavior.SceneIdHash);
    }

    [ServerRpc(RequireOwnership = false)]
    void VerifySceneIdsServerRpc(int count, ulong hash, ServerRpcParams rpcParams = default)
    {
        if (count == PalletBehavior.SceneIdCount && hash == PalletBehavior.SceneIdHash) return;

        string message = $"[NetworkPalletManager] Pallet identities differ for client " +
                         $"{rpcParams.Receive.SenderClientId}: server {PalletBehavior.SceneIdCount}/" +
                         $"{PalletBehavior.SceneIdHash:X16}, client {count}/{hash:X16}. " +
                         "Pallet updates may resolve to the wrong object. Ensure both peers use the same build and scene.";
        Debug.LogWarning(message);
        SceneIdMismatchClientRpc(message, SingleClient(rpcParams.Receive.SenderClientId));
    }

    [ClientRpc]
    void SceneIdMismatchClientRpc(string message, ClientRpcParams _ = default) =>
        Debug.LogWarning(message);

    // ── List changed ──────────────────────────────────────────────────────────────

    void OnLiftListChanged(NetworkListEvent<PalletLiftState> evt)
    {
        if (evt.Type == NetworkListEvent<PalletLiftState>.EventType.Add
            || evt.Type == NetworkListEvent<PalletLiftState>.EventType.Value
            || evt.Type == NetworkListEvent<PalletLiftState>.EventType.Insert)
        {
            ApplyLiftLocally(evt.Value, playSound: true);
        }
        else if (evt.Type == NetworkListEvent<PalletLiftState>.EventType.Remove
              || evt.Type == NetworkListEvent<PalletLiftState>.EventType.RemoveAt)
        {
            var pallet = PalletBehavior.FindByNetId(evt.Value.PalletId);
            pallet?.ApplyRemoteRelease(evt.Value.Position, evt.Value.Rotation);
        }
    }

    void ApplyLiftLocally(PalletLiftState state, bool playSound)
    {
        var pallet = PalletBehavior.FindByNetId(state.PalletId);
        if (pallet == null) return;

        if (!state.IsLifted)
        {
            if (_appliedRestVersions.TryGetValue(state.PalletId, out uint applied) &&
                applied >= state.Version)
                return;
            _appliedRestVersions[state.PalletId] = state.Version;
            pallet.ApplyRemoteRelease(state.Position, state.Rotation);
            return;
        }

        _appliedRestVersions.Remove(state.PalletId);
        // The local lifter predicts directly from its fork. Everybody else, including the
        // listen server for a remote driver, consumes the server-computed pallet pose.
        if (NetworkManager.Singleton == null ||
            state.LiftedByClientId != NetworkManager.Singleton.LocalClientId)
        {
            Transform remoteFork = null;
            if (TryResolveLiftVehicle(state.VehicleNetworkObjectId,
                    state.LiftedByClientId, out _, out var truck) && truck.ForkMount != null)
                remoteFork = truck.ForkMount;

            pallet.SetCargoStabilization(state.CargoStabilization);
            pallet.SetRemoteLifted(true, remoteFork, state.ForkLocalPosition,
                state.ForkLocalRotation, playSound);
            pallet.ApplyRemotePosition(state.Position, state.Rotation);
        }
    }

    // ── Lift ServerRpc ────────────────────────────────────────────────────────────

    /// Client truck requests exclusive lift of a pallet. First caller wins.
    [ServerRpc(RequireOwnership = false)]
    public void RequestLiftServerRpc(int palletId, ulong vehicleNetworkObjectId,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var pallet = PalletBehavior.FindByNetId(palletId);
        ReachTruckController truck = null;
        bool approved = !IsPalletLifted(palletId) && pallet != null &&
                        TryResolveLiftVehicle(vehicleNetworkObjectId, sender,
                            out _, out truck) && truck.ForkMount != null &&
                        Vector3.SqrMagnitude(
                            pallet.transform.position - truck.ForkMount.position) <= 4f;
        if (!approved)
        {
            Vector3 deniedPos = pallet != null ? pallet.transform.position : Vector3.zero;
            Quaternion deniedRot = pallet != null ? pallet.transform.rotation : Quaternion.identity;
            LiftResultClientRpc(palletId, false, deniedPos, deniedRot, 0,
                SingleClient(sender));
            return;
        }

        Transform fork = truck.ForkMount;
        int at = LiftIndexOf(palletId);
        uint version = at >= 0 ? _liftList[at].Version + 1 : 1;
        Vector3 forkLocalPosition = fork.InverseTransformPoint(pallet.transform.position);
        Quaternion forkLocalRotation = Quaternion.Inverse(fork.rotation) * pallet.transform.rotation;
        var state = new PalletLiftState
        {
            PalletId = palletId,
            IsLifted = true,
            LiftedByClientId = sender,
            VehicleNetworkObjectId = vehicleNetworkObjectId,
            CargoStabilization = CargoStabilizationFor(sender),
            ForkLocalPosition = forkLocalPosition,
            ForkLocalRotation = forkLocalRotation,
            Position = pallet.transform.position,
            Rotation = pallet.transform.rotation,
            Version = version,
        };
        WriteLiftState(at, state);
        _liftAuthorities[palletId] = new LiftAuthority
        {
            VehicleNetworkObjectId = vehicleNetworkObjectId,
            ForkLocalPosition = forkLocalPosition,
            ForkLocalRotation = forkLocalRotation,
        };
        pallet.BeginAuthoritativeCargoTransport();
        LiftResultClientRpc(palletId, true, state.Position, state.Rotation, state.Version,
            SingleClient(sender));
    }

    [ClientRpc]
    void LiftResultClientRpc(int palletId, bool approved, Vector3 authoritativePos,
        Quaternion authoritativeRot, uint version, ClientRpcParams _ = default) =>
        PalletBehavior.FindByNetId(palletId)?.ApplyLiftResult(
            approved, authoritativePos, authoritativeRot, version);

    // ── Release ServerRpc ─────────────────────────────────────────────────────────

    /// Driver lowers and releases the pallet. The server computes and persists the final
    /// kinematic pallet pose; only the released cargo returns to dynamic physics.
    [ServerRpc(RequireOwnership = false)]
    public void ReleasePalletServerRpc(int palletId, uint expectedVersion,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        int at = LiftIndexOf(palletId);
        if (at < 0 || !_liftList[at].IsLifted ||
            _liftList[at].LiftedByClientId != sender ||
            _liftList[at].Version != expectedVersion)
            return;
        ServerReleaseLiftAt(at);
    }

    void ServerTickLiftedPallets()
    {
        for (int i = _liftList.Count - 1; i >= 0; i--)
        {
            var state = _liftList[i];
            if (!state.IsLifted) continue;

            if (!_liftAuthorities.TryGetValue(state.PalletId, out var authority) ||
                authority.VehicleNetworkObjectId != state.VehicleNetworkObjectId ||
                !TryResolveLiftVehicle(state.VehicleNetworkObjectId,
                    state.LiftedByClientId, out _, out var truck) || truck.ForkMount == null)
            {
                ServerReleaseLiftAt(i);
                continue;
            }

            Transform fork = truck.ForkMount;
            Vector3 position = fork.TransformPoint(authority.ForkLocalPosition);
            Quaternion rotation = fork.rotation * authority.ForkLocalRotation;
            if (Vector3.SqrMagnitude(position - state.Position) < 0.000001f &&
                Quaternion.Angle(rotation, state.Rotation) < 0.01f)
                continue;

            state.Position = position;
            state.Rotation = rotation;
            _liftList[i] = state;
        }
    }

    void ServerReleaseLiftAt(int index)
    {
        if (!IsServer || index < 0 || index >= _liftList.Count) return;
        var state = _liftList[index];
        if (!state.IsLifted) return;

        if (_liftAuthorities.TryGetValue(state.PalletId, out var authority) &&
            TryResolveLiftVehicle(authority.VehicleNetworkObjectId,
                state.LiftedByClientId, out _, out var truck) && truck.ForkMount != null)
        {
            state.Position = truck.ForkMount.TransformPoint(authority.ForkLocalPosition);
            state.Rotation = truck.ForkMount.rotation * authority.ForkLocalRotation;
        }

        var pallet = PalletBehavior.FindByNetId(state.PalletId);
        pallet?.ApplyAuthoritativeLiftPose(state.Position, state.Rotation);
        if (pallet != null)
        {
            // ApplyAuthoritativeLiftPose may lift the pallet out of the floor. Persist that exact
            // server result so every peer and every late joiner receives the same resting pose.
            state.Position = pallet.transform.position;
            state.Rotation = pallet.transform.rotation;
        }

        ServerFinalizeTransportCargo(state.PalletId, pallet);

        _liftAuthorities.Remove(state.PalletId);
        state.IsLifted = false;
        state.LiftedByClientId = ulong.MaxValue;
        state.VehicleNetworkObjectId = 0;
        state.Version++;
        _liftList[index] = state;
    }

    void ServerFinalizeTransportCargo(int palletId, PalletBehavior pallet)
    {
        if (!IsServer || pallet == null) return;
        var zone = ResolveZone(pallet);
        if (zone == null) return;

        for (int i = _cargoList.Count - 1; i >= 0; i--)
        {
            PalletCargoState cargo = _cargoList[i];
            if (cargo.PalletId != palletId) continue;

            var box = BoxPhysicsInteractable.FindByNetId(cargo.BoxId);
            if (box == null)
            {
                _cargoList.RemoveAt(i);
                continue;
            }

            if (!zone.IsStillPalletCargo(box))
            {
                var body = box.GetComponent<Rigidbody>();
                Vector3 velocity = body != null ? body.linearVelocity : Vector3.zero;
                Vector3 angularVelocity = body != null ? body.angularVelocity : Vector3.zero;
                zone.DetachAuthoritativeCargo(box);
                _cargoList.RemoveAt(i);
                box.EnablePersistentPhysics(velocity, angularVelocity);
                NetworkPhysBoxManager.Instance?.ServerPublishWorldMotion(
                    cargo.BoxId, box.transform.position, box.transform.rotation, velocity,
                    cargo.Version + 1);
                continue;
            }

            // Preserve the physical result of the trip. Updating the manifest reparks the box at
            // its actual pose instead of snapping it back to the slot it occupied before pickup.
            cargo.LocalPos = zone.transform.InverseTransformPoint(box.transform.position);
            cargo.LocalRot = Quaternion.Inverse(zone.transform.rotation) * box.transform.rotation;
            cargo.Version++;
            _cargoList[i] = cargo;
        }
    }

    public void ServerReleasePalletsForVehicle(ulong vehicleNetworkObjectId, ulong driverId)
    {
        if (!IsServer) return;
        for (int i = _liftList.Count - 1; i >= 0; i--)
        {
            var state = _liftList[i];
            if (!state.IsLifted || state.VehicleNetworkObjectId != vehicleNetworkObjectId ||
                state.LiftedByClientId != driverId)
                continue;
            ServerReleaseLiftAt(i);
        }
    }

    bool TryResolveLiftVehicle(ulong vehicleNetworkObjectId, ulong driverId,
        out NetworkVehicleSync sync, out ReachTruckController truck)
    {
        sync = null;
        truck = null;
        var manager = NetworkManager.Singleton;
        if (manager == null || !manager.SpawnManager.SpawnedObjects.TryGetValue(
                vehicleNetworkObjectId, out var vehicle))
            return false;

        sync = vehicle.GetComponent<NetworkVehicleSync>();
        truck = vehicle.GetComponent<ReachTruckController>();
        // The server must validate the driver. A client is only resolving a transform from an
        // already server-authored lift row; accepting it immediately avoids a one-second fallback
        // when the vehicle occupant NetworkVariable arrives just after the pallet list delta.
        return sync != null && truck != null && (!IsServer || sync.IsDrivenBy(driverId));
    }

    int LiftIndexOf(int palletId)
    {
        for (int i = 0; i < _liftList.Count; i++)
            if (_liftList[i].PalletId == palletId) return i;
        return -1;
    }

    void WriteLiftState(int index, PalletLiftState state)
    {
        if (index >= 0) _liftList[index] = state;
        else _liftList.Add(state);
    }

    // ── Atomic pallet cargo transactions ─────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void RequestStackHeldBoxServerRpc(ulong boxId, uint expectedBoxVersion, int palletId,
        ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var phys = NetworkPhysBoxManager.Instance;
        var pallet = PalletBehavior.FindByNetId(palletId);
        var zone = pallet != null
            ? pallet.GetComponent<PalletStackZone>() ?? pallet.GetComponentInChildren<PalletStackZone>()
            : null;

        if (phys == null || pallet == null || zone == null || pallet.IsLifted ||
            !phys.ServerTryGetHeldBox(boxId, sender, expectedBoxVersion, out var box,
                out var held) ||
            !TryGetClientTransform(sender, out var player) ||
            Vector3.SqrMagnitude(player.position - pallet.transform.position) > 25f ||
            CargoIndexOf(boxId) >= 0)
        {
            PalletCommandRejectedClientRpc("Could not place that box on the pallet",
                SingleClient(sender));
            return;
        }

        Vector3 localPos = zone.FindAuthoritativePlacement(box);
        phys.ServerRemoveGrabState(boxId);
        WriteCargo(new PalletCargoState
        {
            BoxId = boxId,
            PalletId = palletId,
            LocalPos = localPos,
            LocalRot = Quaternion.identity,
            Version = held.Version + 1,
        });
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestTakeBoxFromPalletServerRpc(ulong boxId, uint expectedCargoVersion,
        int palletId, ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        int at = CargoIndexOf(boxId);
        var pallet = PalletBehavior.FindByNetId(palletId);
        var phys = NetworkPhysBoxManager.Instance;
        var zone = pallet != null
            ? pallet.GetComponent<PalletStackZone>() ?? pallet.GetComponentInChildren<PalletStackZone>()
            : null;

        if (at < 0 || _cargoList[at].PalletId != palletId ||
            _cargoList[at].Version != expectedCargoVersion || pallet == null || pallet.IsLifted ||
            zone == null || phys == null || !phys.ServerCanGrantGrabFromContainer(boxId, sender) ||
            !TryGetClientTransform(sender, out var player) ||
            Vector3.SqrMagnitude(player.position - pallet.transform.position) > 25f)
        {
            PalletCommandRejectedClientRpc("That pallet load changed before you could take it",
                SingleClient(sender));
            return;
        }

        // Capture support dependencies before removing the requested box from the local zone.
        // These boxes have their own persistent cargo rows; merely waking their local Rigidbody
        // would let the next reconciliation pass pin them back into the now-empty slot.
        var unsupported = new List<BoxPhysicsInteractable>();
        zone.CollectBoxesAbove(BoxPhysicsInteractable.FindByNetId(boxId), unsupported);

        PalletCargoState cargo = _cargoList[at];
        _cargoList.RemoveAt(at);

        // Losing support is an authoritative container-to-world transition. Remove every affected
        // manifest row first, then publish one Flying row so all peers enable gravity from the same
        // pose. The server later publishes the final Resting pose through SettleAndFreeze.
        foreach (var upper in unsupported)
        {
            if (upper == null) continue;

            int upperAt = CargoIndexOf(upper.NetBoxId);
            if (upperAt < 0 || _cargoList[upperAt].PalletId != palletId) continue;

            PalletCargoState upperCargo = _cargoList[upperAt];
            zone.DetachAuthoritativeCargo(upper);
            _cargoList.RemoveAt(upperAt);

            upper.Unsupport(broadcastRest: true);
            var upperBody = upper.GetComponent<Rigidbody>();
            Vector3 velocity = upperBody != null ? upperBody.linearVelocity : Vector3.zero;
            phys.ServerPublishWorldMotion(
                upperCargo.BoxId, upper.transform.position, upper.transform.rotation, velocity,
                upperCargo.Version + 1);
        }

        phys.ServerGrantGrabFromContainer(boxId, sender, cargo.Version + 1);
    }

    /// Server's physical auto-snap path. No client trigger can publish pallet membership.
    public bool ServerRegisterSettledCargo(ulong boxId, int palletId, Vector3 localPos,
        Quaternion localRot)
    {
        if (!IsServer || CargoIndexOf(boxId) >= 0) return false;
        var pallet = PalletBehavior.FindByNetId(palletId);
        var phys = NetworkPhysBoxManager.Instance;
        if (pallet == null || pallet.IsLifted || phys == null ||
            !phys.ServerMoveWorldBoxIntoContainer(boxId, out uint version))
            return false;

        WriteCargo(new PalletCargoState
        {
            BoxId = boxId,
            PalletId = palletId,
            LocalPos = localPos,
            LocalRot = localRot,
            Version = version,
        });
        return true;
    }

    void WriteCargo(PalletCargoState state)
    {
        int at = CargoIndexOf(state.BoxId);
        if (at >= 0) _cargoList[at] = state;
        else _cargoList.Add(state);
    }

    public bool TryGetCargoVersion(ulong boxId, out uint version)
    {
        int at = CargoIndexOf(boxId);
        version = at >= 0 ? _cargoList[at].Version : 0;
        return at >= 0;
    }

    [ClientRpc]
    void PalletCommandRejectedClientRpc(string body, ClientRpcParams _ = default)
    {
        HUDController.Instance?.AddNotification(
            "Pallet", body, HUDController.NotifType.Warning);
    }

    void OnCargoListChanged(NetworkListEvent<PalletCargoState> evt)
    {
        switch (evt.Type)
        {
            case NetworkListEvent<PalletCargoState>.EventType.Add:
            case NetworkListEvent<PalletCargoState>.EventType.Value:
            case NetworkListEvent<PalletCargoState>.EventType.Insert:
                ApplyCargoLocally(evt.Value);
                break;
            case NetworkListEvent<PalletCargoState>.EventType.Remove:
            case NetworkListEvent<PalletCargoState>.EventType.RemoveAt:
                RemoveCargoLocally(evt.Value);
                break;
        }
    }

    void ApplyCargoLocally(PalletCargoState state)
    {
        var box = BoxPhysicsInteractable.FindByNetId(state.BoxId);
        var pallet = PalletBehavior.FindByNetId(state.PalletId);
        if (box == null || pallet == null) return;

        var zone = pallet.GetComponent<PalletStackZone>();
        if (zone == null) zone = pallet.GetComponentInChildren<PalletStackZone>();
        if (zone == null) return;

        // A lifted pallet keeps manifest membership for interaction/persistence, but its boxes are
        // server-simulated world bodies. The recovery sweep must not pin an active physics proxy
        // back to the saved slot once per second.
        if (pallet.IsLifted && box.StoredOnPallet == zone)
            return;

        if (box.StoredInCart != null)
            box.StoredInCart.SyncBoxRemoved(box);
        if (box.StoredOnPallet != null && box.StoredOnPallet != zone)
            box.StoredOnPallet.ApplyRemoteBoxRemove(box);

        // The per-change callback already applies fresh rows. The one-second sweep below is only
        // a recovery path for state that arrived before its scene objects or was otherwise missed;
        // reapplying an unchanged manifest rebuilt follow state for every authored pallet carton
        // and produced two Rigidbody warnings (with stack traces) per box, per second on clients.
        if (box.StoredOnPallet == zone
            && box.NetVersion == state.Version
            && box.IsFollowing(zone.transform, state.LocalPos, state.LocalRot))
            return;

        box.NoteNetVersion(state.Version);
        zone.ApplyRemoteBoxStack(box, state.LocalPos, state.LocalRot);
    }

    void RemoveCargoLocally(PalletCargoState state)
    {
        var box = BoxPhysicsInteractable.FindByNetId(state.BoxId);
        if (box == null || box.StoredOnPallet == null) return;

        var pallet = PalletBehavior.FindByNetId(state.PalletId);
        if (pallet == null) return;
        var zone = pallet.GetComponent<PalletStackZone>();
        if (zone == null) zone = pallet.GetComponentInChildren<PalletStackZone>();
        if (zone == null || box.StoredOnPallet != zone) return;

        // A manifest removal is authoritative. Do not independently wake neighbouring boxes on
        // clients: the server will publish each box that really loses support, in order.
        zone.ApplyRemoteCargoDeparture(box);
    }

    int CargoIndexOf(ulong boxId)
    {
        for (int i = 0; i < _cargoList.Count; i++)
            if (_cargoList[i].BoxId == boxId) return i;
        return -1;
    }

    /// Server-only destruction path used by pallet-drop events. Cargo membership disappears first,
    /// then each box enters the same host-authoritative Flying state as a throw or shelf collapse.
    public void ServerScatterCargo(int palletId, Vector3 center, float force, float radius)
    {
        if (!IsServer) return;

        var pallet = PalletBehavior.FindByNetId(palletId);
        var zone = pallet != null
            ? pallet.GetComponent<PalletStackZone>() ?? pallet.GetComponentInChildren<PalletStackZone>()
            : null;

        float safeRadius = Mathf.Max(0.1f, radius);
        for (int i = _cargoList.Count - 1; i >= 0; i--)
        {
            PalletCargoState state = _cargoList[i];
            if (state.PalletId != palletId) continue;

            var box = BoxPhysicsInteractable.FindByNetId(state.BoxId);
            if (box != null) zone?.DetachAuthoritativeCargo(box);
            _cargoList.RemoveAt(i);
            if (box == null) continue;

            Vector3 away = box.transform.position - center;
            float distance = away.magnitude;
            if (away.sqrMagnitude < 0.0001f)
            {
                float angle = (state.BoxId % 360UL) * Mathf.Deg2Rad;
                away = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            }

            float falloff = Mathf.Clamp01(1f - distance / safeRadius);
            Vector3 direction = (away.normalized + Vector3.up * 0.65f).normalized;
            Vector3 impulse = direction * force * Mathf.Lerp(0.35f, 1f, falloff);
            box.KnockOff(impulse, state.Version + 1);
        }
    }

    void ReleasePersistentCargo(int palletId)
    {
        if (!IsServer) return;

        var pallet = PalletBehavior.FindByNetId(palletId);
        var palletRb = pallet != null ? pallet.GetComponent<Rigidbody>() : null;
        Vector3 velocity = palletRb != null ? palletRb.linearVelocity : Vector3.zero;
        Vector3 angularVelocity = palletRb != null ? palletRb.angularVelocity : Vector3.zero;

        for (int i = _cargoList.Count - 1; i >= 0; i--)
        {
            var state = _cargoList[i];
            if (state.PalletId != palletId) continue;

            var box = BoxPhysicsInteractable.FindByNetId(state.BoxId);
            if (box != null)
            {
                box.EnablePersistentPhysics(velocity, angularVelocity);
                NetworkPhysBoxManager.Instance?.ServerPublishWorldMotion(
                    state.BoxId, box.transform.position, box.transform.rotation, velocity,
                    state.Version + 1);
            }
            _cargoList.RemoveAt(i);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    bool IsPalletLifted(int palletId)
    {
        foreach (var state in _liftList)
            if (state.PalletId == palletId && state.IsLifted) return true;
        return false;
    }

    void OnClientDisconnected(ulong clientId)
    {
        for (int i = _liftList.Count - 1; i >= 0; i--)
        {
            var state = _liftList[i];
            if (!state.IsLifted || state.LiftedByClientId != clientId) continue;
            ServerReleaseLiftAt(i);
        }
    }

    static ClientRpcParams SingleClient(ulong clientId) => new()
    {
        Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
    };

    static bool TryGetClientTransform(ulong clientId, out Transform player)
    {
        player = null;
        var manager = NetworkManager.Singleton;
        if (manager == null ||
            !manager.ConnectedClients.TryGetValue(clientId, out var client) ||
            client.PlayerObject == null)
            return false;
        player = client.PlayerObject.transform;
        return true;
    }

    static float CargoStabilizationFor(ulong clientId)
    {
        foreach (var player in NetworkPlayer.All)
            if (player != null && player.OwnerClientId == clientId)
                return SpecializationPenalties.IsOperator(player.RoleType) ? 2f : 1f;
        return 1f;
    }
}

/// One authoritative row for one box placed on a pallet. The server also publishes authored
/// starting cargo so late joiners and clients with different initialization timing converge.
public struct PalletCargoState : INetworkSerializable, System.IEquatable<PalletCargoState>
{
    public ulong      BoxId;
    public int        PalletId;
    public Vector3    LocalPos;
    public Quaternion LocalRot;
    public uint       Version;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref BoxId);
        serializer.SerializeValue(ref PalletId);
        serializer.SerializeValue(ref LocalPos);
        serializer.SerializeValue(ref LocalRot);
        serializer.SerializeValue(ref Version);
    }

    public bool Equals(PalletCargoState other) =>
        BoxId == other.BoxId && PalletId == other.PalletId &&
        LocalPos == other.LocalPos && LocalRot == other.LocalRot && Version == other.Version;

    public override bool Equals(object obj) => obj is PalletCargoState other && Equals(other);
    public override int GetHashCode() => BoxId.GetHashCode() * 397 ^ PalletId;
}

/// Persistent authoritative state for a pallet that has been moved during the session.
/// Resting rows remain in the list so a late joiner receives the final pose too.
public struct PalletLiftState : INetworkSerializable, System.IEquatable<PalletLiftState>
{
    public int   PalletId;
    public bool  IsLifted;
    public ulong LiftedByClientId;
    public ulong VehicleNetworkObjectId;
    public float CargoStabilization;
    public Vector3 ForkLocalPosition;
    public Quaternion ForkLocalRotation;
    public Vector3 Position;
    public Quaternion Rotation;
    public uint Version;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref PalletId);
        serializer.SerializeValue(ref IsLifted);
        serializer.SerializeValue(ref LiftedByClientId);
        serializer.SerializeValue(ref VehicleNetworkObjectId);
        serializer.SerializeValue(ref CargoStabilization);
        serializer.SerializeValue(ref ForkLocalPosition);
        serializer.SerializeValue(ref ForkLocalRotation);
        serializer.SerializeValue(ref Position);
        serializer.SerializeValue(ref Rotation);
        serializer.SerializeValue(ref Version);
    }

    public bool Equals(PalletLiftState other) =>
        PalletId == other.PalletId
        && IsLifted == other.IsLifted
        && LiftedByClientId == other.LiftedByClientId
        && VehicleNetworkObjectId == other.VehicleNetworkObjectId
        && Mathf.Approximately(CargoStabilization, other.CargoStabilization)
        && ForkLocalPosition == other.ForkLocalPosition
        && ForkLocalRotation == other.ForkLocalRotation
        && Position == other.Position && Rotation == other.Rotation
        && Version == other.Version;

    public override bool Equals(object obj) =>
        obj is PalletLiftState other && Equals(other);

    public override int GetHashCode() => PalletId.GetHashCode() * 397 ^ (int)Version;
}
