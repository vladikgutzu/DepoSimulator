using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Server-authoritative persistent vehicle delivery. When a ShopEntry has a VehiclePartKit assigned,
/// NetworkShopManager does not spawn the vehicle immediately — it hands the purchase to this
/// manager instead. After PartKit.DeliveryDelaySeconds, a pallet with one box per required
/// part appears at a random wall_gate_GRP gate. Players carry each box to the assembly bench
/// (AssemblyBench checks for VehiclePartBox on the carried box, independent of any active
/// order); once every box has been delivered, the purchased vehicle spawns via
/// NetworkShopManager.SpawnVehicle, exactly like an instant-buy entry.
///
/// Single-player: everything below runs directly, no networking involved.
/// Multiplayer: the server owns a persistent delivery registry. Clients reconstruct presentation
/// from that registry, so timers, gates, collected slots and completion survive late join.
public class VehicleDeliveryManager : NetworkBehaviour
{
    public static VehicleDeliveryManager Instance { get; private set; }

    // Locally-instantiated delivery boxes still need one identity shared by every peer. Keep
    // them in a high, dedicated range, separate from NGO scene IDs and packed-order box IDs.
    const ulong DeliveryBoxIdPrefix = 0xD000000000000000UL;

    public static ulong DeliveryBoxNetId(int deliveryId, int slotIndex) =>
        DeliveryBoxIdPrefix | ((ulong)(uint)deliveryId << 16) | (ushort)slotIndex;

    public readonly struct DeliveryMapTarget
    {
        public int     DeliveryId { get; }
        public Vector3 Position   { get; }
        public Transform TrackedTransform { get; }
        public string  Label      { get; }
        public bool    Assembled  { get; }

        public DeliveryMapTarget(int deliveryId, Vector3 position, string label, bool assembled,
            Transform trackedTransform = null)
        {
            DeliveryId      = deliveryId;
            Position        = position;
            TrackedTransform = trackedTransform;
            Label           = label;
            Assembled       = assembled;
        }
    }

    [Header("Gate discovery")]
    [Tooltip("Name substring (case-insensitive) an ancestor must contain for a floor anchor to count as a delivery gate.")]
    [SerializeField] private string _gateNamePattern = "wall_gate_grp";
    [Tooltip("Exact name of the floor decal child under each gate that marks the correct ground-level spot — "
           + "the gate root itself can sit on a mezzanine/upper floor, so it is not a safe spawn anchor on its own.")]
    [SerializeField] private string _floorAnchorName = "decal_floor";

    [Header("Prefabs (wire via Tools > Warehouse Demo > Setup Vehicle Delivery)")]
    [SerializeField] private GameObject _palletPrefab;
    [SerializeField] private GameObject _boxPrefab;

    [Header("Vehicle delivery load")]
    [Tooltip("Vehicle parts use a larger industrial pallet. Height remains unchanged so forks enter at the authored level.")]
    [SerializeField] private Vector3 _deliveryPalletScale = new(1.65f, 1f, 1.45f);

    [Header("Box layout")]
    [SerializeField] private float _boxSpacing  = 0.55f;
    [SerializeField] private int   _boxesPerRow = 2;

    private Transform[] _gates = System.Array.Empty<Transform>();
    private NetworkList<VehicleDeliveryState> _deliveryStates;
    private NetworkList<EmptyPalletBundleState> _palletBundleStates;

    private const byte DeliveryQueued = 0;
    private const byte DeliveryArrived = 1;
    private const byte DeliveryCompleted = 2;
    private const float BenchCollectDistance = 6f;

    private struct PendingDelivery
    {
        public NetworkShopManager.VehicleType Type;
        public int          Total;
        public int          Collected;
        public HashSet<int> CollectedSlots;
    }

    private readonly Dictionary<int, PendingDelivery> _pending     = new();
    private readonly Dictionary<int, GameObject>       _localPallets = new();
    private readonly Dictionary<(int deliveryId, int slot), GameObject> _localBoxes = new();
    private readonly Dictionary<(int bundleId, int slot), GameObject> _localBundlePallets = new();
    private readonly Dictionary<int, DeliveryMapTarget> _mapTargets = new();
    private readonly HashSet<int> _activeDeliveryStatuses = new();
    private readonly Dictionary<int, uint> _appliedDeliveryVersions = new();
    private int _nextDeliveryId;
    private int _nextPalletBundleId = 1;
    private float _nextNetworkReconcile;
    private float _nextNotificationRefresh;
    private float _nextMapTargetRefresh;

    public IEnumerable<DeliveryMapTarget> ActiveMapTargets => _mapTargets.Values;

    private bool IsMultiplayer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        _deliveryStates = new NetworkList<VehicleDeliveryState>();
        _palletBundleStates = new NetworkList<EmptyPalletBundleState>();
    }

    private void Start()
    {
        var found = new List<Transform>();
        string pattern = _gateNamePattern.ToLowerInvariant();
        foreach (var go in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            if (go.name != _floorAnchorName) continue;
            if (!HasAncestorNamed(go.transform, pattern)) continue;
            found.Add(go.transform);
        }
        // FindObjectsByType order is not stable across machines. GateIndex is synchronized, so
        // build the same hierarchy-sorted table on every peer.
        found.Sort((a, b) => string.CompareOrdinal(HierarchyPath(a), HierarchyPath(b)));
        _gates = found.ToArray();

        if (_gates.Length == 0)
            Debug.LogWarning($"[VehicleDeliveryManager] No '{_floorAnchorName}' anchors found under any "
                            + $"'{_gateNamePattern}' gate.");

        ReconcilePersistentState(false);
    }

    public override void OnNetworkSpawn()
    {
        _deliveryStates.OnListChanged += OnDeliveryStateChanged;
        _palletBundleStates.OnListChanged += OnPalletBundleStateChanged;

        if (IsServer)
        {
            foreach (var state in _deliveryStates)
                _nextDeliveryId = Mathf.Max(_nextDeliveryId, state.DeliveryId + 1);
            foreach (var state in _palletBundleStates)
                _nextPalletBundleId = Mathf.Max(_nextPalletBundleId, state.BundleId + 1);
        }

        ReconcilePersistentState(false);
    }

    public override void OnNetworkDespawn()
    {
        _deliveryStates.OnListChanged -= OnDeliveryStateChanged;
        _palletBundleStates.OnListChanged -= OnPalletBundleStateChanged;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (Time.unscaledTime >= _nextMapTargetRefresh)
        {
            _nextMapTargetRefresh = Time.unscaledTime + 0.25f;
            RefreshLiveMapTargets();
        }

        if (!IsMultiplayer || !IsSpawned) return;
        if (IsServer) AdvanceQueuedDeliveriesServer();

        if (Time.unscaledTime >= _nextNotificationRefresh)
        {
            _nextNotificationRefresh = Time.unscaledTime + 0.25f;
            RefreshDeliveryNotifications();
        }

        if (Time.unscaledTime < _nextNetworkReconcile) return;
        _nextNetworkReconcile = Time.unscaledTime + 0.5f;
        ReconcilePersistentState(false);
    }

    private static string HierarchyPath(Transform value)
    {
        if (value == null) return string.Empty;
        string path = value.GetSiblingIndex().ToString("D5") + ":" + value.name;
        for (var parent = value.parent; parent != null; parent = parent.parent)
            path = parent.GetSiblingIndex().ToString("D5") + ":" + parent.name + "/" + path;
        return path;
    }

    private static bool HasAncestorNamed(Transform t, string lowerPattern)
    {
        for (var p = t.parent; p != null; p = p.parent)
            if (p.name.ToLowerInvariant().Contains(lowerPattern)) return true;
        return false;
    }

    // ── Entry point (called by NetworkShopManager right after payment) ──────────

    public void QueueDelivery(NetworkShopManager.ShopEntry entry)
    {
        float delay = entry.PartKit != null ? Mathf.Max(0f, entry.PartKit.DeliveryDelaySeconds) : 0f;

        if (IsMultiplayer)
        {
            if (!IsServer || !IsSpawned) return;
            int total = entry.PartKit?.Parts?.Length ?? 0;
            if (total <= 0) return;
            if (total > 64)
            {
                Debug.LogError($"[VehicleDeliveryManager] {entry.DisplayName} has {total} parts; maximum is 64.");
                return;
            }

            _deliveryStates.Add(new VehicleDeliveryState
            {
                DeliveryId = _nextDeliveryId++,
                VehicleTypeIndex = (int)entry.Type,
                GateIndex = -1,
                TotalParts = total,
                CollectedMask = 0,
                DueServerTime = NetworkManager.ServerTime.Time + delay,
                Phase = DeliveryQueued,
                Version = 1,
            });
            return;
        }

        int deliveryId = _nextDeliveryId++;
        StartDeliveryStatusLocal(deliveryId, entry.DisplayName, delay);
        StartCoroutine(DeliveryTimer(deliveryId, entry, delay));
    }

    /// Delivers reusable empty pallets to a warehouse gate. The server chooses the gate and
    /// every peer creates the same physical pallets, matching the existing vehicle-parts flow.
    public void DeliverEmptyPalletBundle(int count)
    {
        count = Mathf.Clamp(count, 1, 6);
        if (_gates.Length == 0)
        {
            HUDController.Instance?.AddNotification(
                "Pallet delivery failed", "No free delivery gate was found",
                HUDController.NotifType.Warning);
            return;
        }

        int bundleId = _nextPalletBundleId++;
        int gateIndex = Random.Range(0, _gates.Length);
        if (IsMultiplayer)
        {
            if (!IsServer || !IsSpawned) return;
            _palletBundleStates.Add(new EmptyPalletBundleState
            {
                BundleId = bundleId,
                GateIndex = gateIndex,
                Count = count,
            });
        }
        else
            SpawnEmptyPalletBundleLocal(bundleId, _gates[gateIndex], count);
    }

    void SpawnEmptyPalletBundleLocal(int bundleId, Transform gate, int count, bool notify = true)
    {
        if (_palletPrefab == null) return;

        Vector3 basePos = GroundedPosition(gate.position);
        bool spawnedAny = false;
        for (int i = 0; i < count; i++)
        {
            var key = (bundleId, i);
            if (_localBundlePallets.TryGetValue(key, out var existing) && existing != null)
                continue;
            Vector3 offset = gate.right * ((i - (count - 1) * 0.5f) * 1.25f);
            var pallet = Instantiate(_palletPrefab, basePos + offset, gate.rotation);
            pallet.name = $"PurchasedEmptyPallet_{bundleId}_{i + 1}";
            pallet.GetComponent<PalletBehavior>()?.AssignRuntimeNetId(-100000 - bundleId * 10 - i);
            _localBundlePallets[key] = pallet;
            spawnedAny = true;
        }

        if (notify && spawnedAny)
            HUDController.Instance?.AddNotification(
                "Pallets delivered", $"{count} empty pallets are waiting at the gate",
                HUDController.NotifType.Success);
    }

    private void StartDeliveryStatusLocal(int deliveryId, string displayName, float delay)
    {
        _activeDeliveryStatuses.Add(deliveryId);
        StartCoroutine(DeliveryStatusTimer(deliveryId, displayName, delay));
    }

    private IEnumerator DeliveryStatusTimer(int deliveryId, string displayName, float delay)
    {
        string key = DeliveryNotificationKey(deliveryId);
        float arrivalTime = Time.time + delay;

        while (_activeDeliveryStatuses.Contains(deliveryId))
        {
            int seconds = Mathf.Max(0, Mathf.CeilToInt(arrivalTime - Time.time));
            HUDController.Instance?.SetPersistentNotification(
                key,
                $"{displayName.ToUpperInvariant()} DELIVERY · ACTIVE",
                $"Parts arrive in {seconds}s",
                HUDController.NotifType.Warning);

            if (seconds <= 0) break;
            yield return new WaitForSeconds(0.25f);
        }
    }

    private void StopDeliveryStatusLocal(int deliveryId)
    {
        _activeDeliveryStatuses.Remove(deliveryId);
        HUDController.Instance?.RemovePersistentNotification(DeliveryNotificationKey(deliveryId));
    }

    private static string DeliveryNotificationKey(int deliveryId) => $"vehicle-delivery-{deliveryId}";

    private IEnumerator DeliveryTimer(int deliveryId, NetworkShopManager.ShopEntry entry, float delay)
    {
        yield return new WaitForSeconds(delay);
        SpawnDelivery(deliveryId, entry);
    }

    // ── Persistent multiplayer delivery state ───────────────────────────────────

    private void AdvanceQueuedDeliveriesServer()
    {
        double now = NetworkManager.ServerTime.Time;
        for (int i = 0; i < _deliveryStates.Count; i++)
        {
            var state = _deliveryStates[i];
            if (state.Phase != DeliveryQueued || now < state.DueServerTime) continue;
            if (!TryGetEntry(state.VehicleTypeIndex, out var entry)) continue;

            if (_gates.Length == 0)
            {
                // Commit completion before the side effect. Re-entry, retransmission or a late
                // update can no longer spawn the purchased vehicle twice.
                state.Phase = DeliveryCompleted;
                state.Version++;
                _deliveryStates[i] = state;
                NetworkShopManager.Instance?.SpawnVehicle(entry);
                Debug.LogWarning("[VehicleDeliveryManager] No delivery gate available — spawning vehicle directly.");
                continue;
            }

            state.GateIndex = Random.Range(0, _gates.Length);
            state.Phase = DeliveryArrived;
            state.Version++;
            _deliveryStates[i] = state;
        }
    }

    private void OnDeliveryStateChanged(NetworkListEvent<VehicleDeliveryState> evt)
    {
        if (evt.Type == NetworkListEvent<VehicleDeliveryState>.EventType.Add ||
            evt.Type == NetworkListEvent<VehicleDeliveryState>.EventType.Insert ||
            evt.Type == NetworkListEvent<VehicleDeliveryState>.EventType.Value)
            ApplyPersistentDelivery(evt.Value, true);
    }

    private void OnPalletBundleStateChanged(NetworkListEvent<EmptyPalletBundleState> evt)
    {
        if (evt.Type != NetworkListEvent<EmptyPalletBundleState>.EventType.Add &&
            evt.Type != NetworkListEvent<EmptyPalletBundleState>.EventType.Insert &&
            evt.Type != NetworkListEvent<EmptyPalletBundleState>.EventType.Value) return;
        if (evt.Value.GateIndex < 0 || evt.Value.GateIndex >= _gates.Length) return;
        SpawnEmptyPalletBundleLocal(evt.Value.BundleId, _gates[evt.Value.GateIndex], evt.Value.Count);
    }

    private void ReconcilePersistentState(bool notify)
    {
        if (!IsMultiplayer || !IsSpawned || _deliveryStates == null || _gates == null) return;
        foreach (var state in _deliveryStates)
        {
            ApplyPersistentDelivery(state, notify);
            if (IsServer && state.Phase == DeliveryArrived)
                EnsureDeliveryPhysicsRegistered(state);
        }
        foreach (var bundle in _palletBundleStates)
        {
            if (bundle.GateIndex < 0 || bundle.GateIndex >= _gates.Length) continue;
            // SpawnEmptyPalletBundleLocal is guarded by deterministic object names below.
            SpawnEmptyPalletBundleLocal(bundle.BundleId, _gates[bundle.GateIndex], bundle.Count, false);
        }
    }

    /// Delivery presentation can be instantiated before NetworkPhysBoxManager finishes spawning.
    /// The first publication is therefore retried during normal reconciliation. Without this
    /// recovery path the host simulated the cartons, while every client kept the initial
    /// kinematic pallet pose forever because the null-conditional publish was silently skipped.
    private void EnsureDeliveryPhysicsRegistered(VehicleDeliveryState state)
    {
        var manager = NetworkPhysBoxManager.Instance;
        if (manager == null || !manager.IsSpawned) return;

        for (int slot = 0; slot < state.TotalParts; slot++)
        {
            if ((state.CollectedMask & (1UL << slot)) != 0) continue;
            if (!_localBoxes.TryGetValue((state.DeliveryId, slot), out var boxObject) ||
                boxObject == null ||
                !boxObject.TryGetComponent<BoxPhysicsInteractable>(out var box) ||
                manager.ServerHasBoxState(box.NetBoxId))
                continue;

            // This call is idempotent and also repairs a host body that was left kinematic by an
            // out-of-order spawn. Clients will turn their copies into kinematic motion proxies as
            // soon as the persistent Flying row below reaches them.
            box.EnablePersistentPhysics(Vector3.zero, Vector3.zero);
            manager.ServerPublishWorldMotion(
                box.NetBoxId, box.transform.position, box.transform.rotation, Vector3.zero);
        }
    }

    private void ApplyPersistentDelivery(VehicleDeliveryState state, bool notify)
    {
        bool newVersion = !_appliedDeliveryVersions.TryGetValue(state.DeliveryId, out uint applied)
                          || applied < state.Version;
        _appliedDeliveryVersions[state.DeliveryId] = System.Math.Max(applied, state.Version);
        if (!TryGetEntry(state.VehicleTypeIndex, out var entry)) return;

        if (state.Phase == DeliveryQueued)
        {
            _activeDeliveryStatuses.Add(state.DeliveryId);
            UpdatePersistentDeliveryNotification(state, entry.DisplayName);
            return;
        }

        StopDeliveryStatusLocal(state.DeliveryId);
        if (state.Phase == DeliveryCompleted)
        {
            RemoveLocalDelivery(state.DeliveryId);
            MarkVehicleAssembled(state.DeliveryId, entry);
            if (notify && newVersion)
            {
                AudioManager.Instance?.PlayPlacePart();
                HUDController.Instance?.AddNotification(
                    "Vehicle assembled", "All parts delivered", HUDController.NotifType.Success);
            }
            return;
        }

        if (state.GateIndex < 0 || state.GateIndex >= _gates.Length) return;
        if (!_localPallets.TryGetValue(state.DeliveryId, out var pallet) || pallet == null)
            SpawnDeliveryLocal(state.DeliveryId, entry, _gates[state.GateIndex], false);

        for (int slot = 0; slot < state.TotalParts; slot++)
            if ((state.CollectedMask & (1UL << slot)) != 0)
                RemoveLocalDeliveryBox(state.DeliveryId, slot);

        int collected = CountBits(state.CollectedMask);
        if (_pending.TryGetValue(state.DeliveryId, out var pending))
        {
            pending.Collected = collected;
            for (int slot = 0; slot < state.TotalParts; slot++)
                if ((state.CollectedMask & (1UL << slot)) != 0)
                    pending.CollectedSlots.Add(slot);
            _pending[state.DeliveryId] = pending;
        }

        if (notify && newVersion && collected == 0)
        {
            string gateName = FriendlyGateName(_gates[state.GateIndex]);
            HUDController.Instance?.AddNotification(
                $"{entry.DisplayName} delivery arrived",
                $"{entry.DisplayName} parts · {gateName} · collect {state.TotalParts} box(es)",
                HUDController.NotifType.Success);
        }
        else if (notify && newVersion && collected > 0)
        {
            AudioManager.Instance?.PlayPlacePart();
            HUDController.Instance?.AddNotification(
                "Part collected", $"{collected}/{state.TotalParts}",
                HUDController.NotifType.Success);
        }
    }

    private void RefreshDeliveryNotifications()
    {
        foreach (var state in _deliveryStates)
        {
            if (state.Phase != DeliveryQueued) continue;
            if (TryGetEntry(state.VehicleTypeIndex, out var entry))
                UpdatePersistentDeliveryNotification(state, entry.DisplayName);
        }
    }

    private void UpdatePersistentDeliveryNotification(VehicleDeliveryState state, string displayName)
    {
        int seconds = Mathf.Max(0, Mathf.CeilToInt(
            (float)(state.DueServerTime - NetworkManager.ServerTime.Time)));
        HUDController.Instance?.SetPersistentNotification(
            DeliveryNotificationKey(state.DeliveryId),
            $"{displayName.ToUpperInvariant()} DELIVERY · ACTIVE",
            $"Parts arrive in {seconds}s",
            HUDController.NotifType.Warning);
    }

    private bool TryGetEntry(int vehicleTypeIndex, out NetworkShopManager.ShopEntry entry)
    {
        entry = default;
        return NetworkShopManager.Instance != null && NetworkShopManager.Instance.TryGetEntry(
            (NetworkShopManager.VehicleType)vehicleTypeIndex, out entry);
    }

    private static int CountBits(ulong value)
    {
        int count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }
        return count;
    }

    private void RemoveLocalDeliveryBox(int deliveryId, int slotIndex)
    {
        if (_localBoxes.TryGetValue((deliveryId, slotIndex), out var box) && box != null)
        {
            var physicsBox = box.GetComponent<BoxPhysicsInteractable>();
            if (physicsBox != null && BoxPhysicsInteractable.GrabbedBox == physicsBox)
                physicsBox.SilentRelease();
            Destroy(box);
        }
        _localBoxes.Remove((deliveryId, slotIndex));
    }

    private void RemoveLocalDelivery(int deliveryId)
    {
        var slots = new List<int>();
        foreach (var pair in _localBoxes)
            if (pair.Key.deliveryId == deliveryId) slots.Add(pair.Key.slot);
        foreach (int slot in slots) RemoveLocalDeliveryBox(deliveryId, slot);

        if (_localPallets.TryGetValue(deliveryId, out var pallet) && pallet != null)
            Destroy(pallet);
        _localPallets.Remove(deliveryId);
        _pending.Remove(deliveryId);
    }

    private void SpawnDelivery(int deliveryId, NetworkShopManager.ShopEntry entry)
    {
        if (_gates.Length == 0)
        {
            CompleteDeliveryWithoutGateLocal(deliveryId, entry);
            Debug.LogWarning("[VehicleDeliveryManager] No delivery gate available — spawning vehicle directly.");
            NetworkShopManager.Instance?.SpawnVehicle(entry);
            return;
        }

        int gateIndex  = Random.Range(0, _gates.Length);
        SpawnDeliveryLocal(deliveryId, entry, _gates[gateIndex]);
    }

    // Instantiates presentation from an already-decided authoritative delivery record.
    private void SpawnDeliveryLocal(int deliveryId, NetworkShopManager.ShopEntry entry,
        Transform gate, bool notify = true)
    {
        var parts = entry.PartKit != null ? entry.PartKit.Parts : null;
        if (parts == null || parts.Length == 0) return;

        StopDeliveryStatusLocal(deliveryId);
        string gateName = FriendlyGateName(gate);
        _mapTargets[deliveryId] = new DeliveryMapTarget(
            deliveryId, gate.position, $"{entry.DisplayName} parts", assembled: false);

        _pending[deliveryId] = new PendingDelivery
        {
            Type           = entry.Type,
            Total          = parts.Length,
            Collected      = 0,
            CollectedSlots = new HashSet<int>(),
        };

        // `gate` is the decal_floor anchor itself — already at the correct ground-level spot in
        // front of the real gate (the gate root can sit on a mezzanine, decal_floor cannot).
        Vector3 basePos = GroundedPosition(gate.position);

        float palletTopY = basePos.y;
        PalletBehavior palletBehavior = null;
        PalletStackZone palletZone = null;
        if (_palletPrefab != null)
        {
            var palletGO = Instantiate(_palletPrefab, basePos, gate.rotation);
            palletGO.transform.localScale = Vector3.Scale(
                palletGO.transform.localScale, _deliveryPalletScale);
            _localPallets[deliveryId] = palletGO;
            _mapTargets[deliveryId] = new DeliveryMapTarget(
                deliveryId, palletGO.transform.position, $"{entry.DisplayName} parts",
                assembled: false, trackedTransform: palletGO.transform);
            // Spawned after the scene-load numbering pass, so it needs its id handed to it —
            // deliveryId is the same on every machine, which is what makes it shareable.
            palletBehavior = palletGO.GetComponent<PalletBehavior>();
            palletBehavior?.AssignRuntimeNetId(-200000 - deliveryId);
            palletZone = palletGO.GetComponent<PalletStackZone>()
                      ?? palletGO.GetComponentInChildren<PalletStackZone>();

            if (TryGetVisualBounds(palletGO, out var palletBounds))
                palletTopY = palletBounds.max.y;
        }

        const float layerGap = 0.015f;
        int currentLayer = -1;
        float layerBottomY = palletTopY + layerGap;
        float layerTopY    = palletTopY;

        for (int i = 0; i < parts.Length; i++)
        {
            // A machinery delivery is intentionally a mixed load: one real large carton forms
            // the base, up to two medium cartons form the next layer, and the rest are small.
            // This uses the same authored prefabs as packed customer orders rather than scaling
            // five copies of box_small_prefab.
            AssemblyBench.PackingBoxTier tier = DeliveryBoxTier(i, parts.Length);
            GameObject chosenPrefab = AssemblyBench.Instance?.GetPackingBoxPrefab(tier)
                                   ?? _boxPrefab;

            int layoutLayer = i == 0 ? 0 : 1 + (i - 1) / 2;
            int layerStart  = layoutLayer == 0 ? 0 : 1 + (layoutLayer - 1) * 2;
            int indexInLayer = i - layerStart;
            int itemsInLayer = layoutLayer == 0
                ? 1
                : Mathf.Min(2, parts.Length - layerStart);

            if (layoutLayer != currentLayer)
            {
                currentLayer = layoutLayer;
                layerBottomY = layerTopY + layerGap;
                layerTopY    = layerBottomY;
            }

            GameObject boxGO = chosenPrefab != null
                ? Instantiate(chosenPrefab, basePos, gate.rotation)
                : GameObject.CreatePrimitive(PrimitiveType.Cube);
            if (chosenPrefab == null)
                boxGO.transform.SetPositionAndRotation(basePos, gate.rotation);
            boxGO.name = $"VehiclePartBox_{entry.DisplayName}_{i}";

            // Place by the final prefab collider bounds. Paired layers alternate their axis like
            // crossed pallet courses, producing a stable, readable stack without intersections.
            if (TryGetSolidColliderBounds(boxGO, out var boxBounds))
            {
                float horizontalSize = Mathf.Max(boxBounds.size.x, boxBounds.size.z);
                float spacing = Mathf.Max(_boxSpacing, horizontalSize + layerGap);
                float centeredIndex = indexInLayer - (itemsInLayer - 1) * 0.5f;
                Vector3 layerAxis = (layoutLayer & 1) == 1 ? gate.right : gate.forward;
                Vector3 horizontalOffset = layerAxis * (centeredIndex * spacing);

                boxGO.transform.position += horizontalOffset
                                          + Vector3.up * (layerBottomY - boxBounds.min.y);
                layerTopY = Mathf.Max(layerTopY, layerBottomY + boxBounds.size.y);
            }
            else
            {
                float centeredIndex = indexInLayer - (itemsInLayer - 1) * 0.5f;
                Vector3 layerAxis = (layoutLayer & 1) == 1 ? gate.right : gate.forward;
                boxGO.transform.position += layerAxis * (centeredIndex * _boxSpacing)
                                          + Vector3.up * (layerBottomY - basePos.y);
                layerTopY = Mathf.Max(layerTopY, layerBottomY + _boxSpacing);
            }

            if (!boxGO.TryGetComponent<Rigidbody>(out var rb))
                rb = boxGO.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity  = false;

            // Every authored box prefab already carries its own BoxPhysicsInteractable —
            // kept enabled on purpose so players pick it up and physically carry it to the
            // bench, exactly like an order-part box. VehiclePartBox is a pure data tag riding
            // alongside it (same relationship as BoxWithPart); AssemblyBench checks for it on
            // the carried box and consumes it there.
            if (!boxGO.TryGetComponent<BoxPhysicsInteractable>(out var physicsBox))
                physicsBox = boxGO.AddComponent<BoxPhysicsInteractable>();

            // Every machine creates its own visual twin, but all twins now resolve to the same
            // server-arbitrated box. Without this ID both players could grab their local copy.
            physicsBox.ForceNetId(DeliveryBoxNetId(deliveryId, i));

            boxGO.AddComponent<VehiclePartBox>().Setup(deliveryId, i, parts[i]);
            _localBoxes[(deliveryId, i)] = boxGO;

            if (palletZone != null)
            {
                Vector3 localPos = palletZone.transform.InverseTransformPoint(boxGO.transform.position);
                Quaternion localRot = Quaternion.Inverse(palletZone.transform.rotation)
                                    * boxGO.transform.rotation;

                // Establish the same initial pose on every peer, then hand the cartons to the
                // authoritative physics instance. Clients remain network-motion proxies; they do
                // not run a second simulation that could disagree with the server.
                palletZone.ApplyRemoteBoxStack(physicsBox, localPos, localRot);
                if (!IsMultiplayer || IsServer)
                {
                    physicsBox.EnablePersistentPhysics(Vector3.zero, Vector3.zero);
                    if (IsMultiplayer)
                    {
                        NetworkPhysBoxManager.Instance?.ServerPublishWorldMotion(
                            physicsBox.NetBoxId,
                            physicsBox.transform.position,
                            physicsBox.transform.rotation,
                            Vector3.zero);
                    }
                }
            }
        }

        if (notify)
            HUDController.Instance?.AddNotification(
                $"{entry.DisplayName} delivery arrived",
                $"{entry.DisplayName} parts · {gateName} · collect {parts.Length} box(es)",
                HUDController.NotifType.Success);
    }

    private static AssemblyBench.PackingBoxTier DeliveryBoxTier(int slotIndex, int totalParts)
    {
        if (slotIndex == 0) return AssemblyBench.PackingBoxTier.Large;

        // Preserve both ends of the visual range even for two- or three-part kits.
        int mediumCount = Mathf.Min(2, Mathf.Max(0, totalParts - 2));
        return slotIndex <= mediumCount
            ? AssemblyBench.PackingBoxTier.Medium
            : AssemblyBench.PackingBoxTier.Small;
    }

    private static bool TryGetVisualBounds(GameObject root, out Bounds bounds)
    {
        bounds = default;
        bool found = false;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer.enabled) continue;
            if (!found) { bounds = renderer.bounds; found = true; }
            else        { bounds.Encapsulate(renderer.bounds); }
        }
        return found;
    }

    /// Keeps the delivery marker attached to the delivery pallet. Individual cartons may be
    /// carried anywhere in the warehouse, but the delivery POI must continue to identify the
    /// pallet that belongs to this purchase.
    private void RefreshLiveMapTargets()
    {
        foreach (var pair in _pending)
        {
            int deliveryId = pair.Key;
            PendingDelivery pending = pair.Value;
            Vector3 sum = Vector3.zero;
            int remaining = 0;

            for (int slot = 0; slot < pending.Total; slot++)
            {
                if (pending.CollectedSlots != null && pending.CollectedSlots.Contains(slot))
                    continue;
                if (!_localBoxes.TryGetValue((deliveryId, slot), out var box) || box == null)
                    continue;

                sum += box.transform.position;
                remaining++;
            }

            Vector3 position;
            Transform trackedTransform = null;
            if (_localPallets.TryGetValue(deliveryId, out var pallet) && pallet != null)
            {
                position = pallet.transform.position;
                trackedTransform = pallet.transform;
            }
            else if (remaining > 0)
                position = sum / remaining;
            else if (_mapTargets.TryGetValue(deliveryId, out var previous))
                position = previous.Position;
            else
                continue;

            string displayName = TryGetEntry((int)pending.Type, out var entry)
                ? entry.DisplayName
                : "Vehicle";
            _mapTargets[deliveryId] = new DeliveryMapTarget(
                deliveryId, position, $"{displayName} parts · {remaining}", assembled: false,
                trackedTransform: trackedTransform);
        }
    }

    private static bool TryGetSolidColliderBounds(GameObject root, out Bounds bounds)
    {
        bounds = default;
        bool found = false;
        foreach (var collider in root.GetComponentsInChildren<Collider>(true))
        {
            if (!collider.enabled || collider.isTrigger) continue;
            if (!found) { bounds = collider.bounds; found = true; }
            else        { bounds.Encapsulate(collider.bounds); }
        }
        return found;
    }

    private void CompleteDeliveryWithoutGateLocal(int deliveryId, NetworkShopManager.ShopEntry entry)
    {
        StopDeliveryStatusLocal(deliveryId);
        MarkVehicleAssembled(deliveryId, entry);
        HUDController.Instance?.AddNotification(
            $"{entry.DisplayName} delivered",
            "No delivery gate was available · vehicle moved to the equipment bay",
            HUDController.NotifType.Success);
    }

    private string FriendlyGateName(Transform floorAnchor)
    {
        string pattern = _gateNamePattern.ToLowerInvariant();
        for (var p = floorAnchor; p != null; p = p.parent)
        {
            if (!p.name.ToLowerInvariant().Contains(pattern)) continue;
            return p.name.Replace("_GRP", "").Replace("_grp", "").Replace('_', ' ');
        }
        return "delivery gate";
    }

    private static Vector3 GroundedPosition(Vector3 pos)
    {
        Vector3 origin = pos + Vector3.up * 2f;
        if (Physics.Raycast(origin, Vector3.down, out var hit, 5f, ~0, QueryTriggerInteraction.Ignore))
            return hit.point;
        return pos;
    }

    // ── Collection: single-player ────────────────────────────────────────────────

    public void CollectPartLocal(int deliveryId, int slotIndex) => ApplyCollect(deliveryId, slotIndex);

    // ── Collection: multiplayer ──────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void RequestCollectPartServerRpc(int deliveryId, int slotIndex, ServerRpcParams rpcParams = default)
    {
        int stateIndex = DeliveryStateIndexOf(deliveryId);
        if (stateIndex < 0) return;
        var state = _deliveryStates[stateIndex];
        if (state.Phase != DeliveryArrived || slotIndex < 0 || slotIndex >= state.TotalParts)
            return;

        ulong slotBit = 1UL << slotIndex;
        if ((state.CollectedMask & slotBit) != 0) return;

        ulong boxId = DeliveryBoxNetId(deliveryId, slotIndex);
        var physics = NetworkPhysBoxManager.Instance;
        ulong sender = rpcParams.Receive.SenderClientId;
        if (physics == null || !physics.ServerIsHeldBy(boxId, sender) ||
            !IsPlayerNearAssemblyBench(sender))
            return;

        // Commit the slot before clearing the grab. A concurrent/repeated request immediately
        // observes this bit, so a part and the completed vehicle can only be consumed once.
        state.CollectedMask |= slotBit;
        state.Version++;
        bool completed = CountBits(state.CollectedMask) >= state.TotalParts;
        if (completed) state.Phase = DeliveryCompleted;
        _deliveryStates[stateIndex] = state;
        physics.ServerRemoveGrabState(boxId);

        if (completed && TryGetEntry(state.VehicleTypeIndex, out var entry))
            NetworkShopManager.Instance?.SpawnVehicle(entry);
    }

    private int DeliveryStateIndexOf(int deliveryId)
    {
        for (int i = 0; i < _deliveryStates.Count; i++)
            if (_deliveryStates[i].DeliveryId == deliveryId) return i;
        return -1;
    }

    private bool IsPlayerNearAssemblyBench(ulong clientId)
    {
        Transform player = null;
        foreach (var candidate in NetworkPlayer.All)
        {
            if (candidate.OwnerClientId != clientId) continue;
            player = candidate.transform;
            break;
        }
        if (player == null) return false;

        foreach (var bench in FindObjectsByType<AssemblyBench>())
            if (Vector3.SqrMagnitude(player.position - bench.transform.position)
                <= BenchCollectDistance * BenchCollectDistance)
                return true;
        return false;
    }

    // Single-player bookkeeping. Multiplayer collection is represented by CollectedMask.
    private void ApplyCollect(int deliveryId, int slotIndex)
    {
        if (_localBoxes.TryGetValue((deliveryId, slotIndex), out var box) && box != null)
        {
            var physicsBox = box.GetComponent<BoxPhysicsInteractable>();
            if (physicsBox != null && BoxPhysicsInteractable.GrabbedBox == physicsBox)
                physicsBox.SilentRelease();
            Destroy(box);
        }
        _localBoxes.Remove((deliveryId, slotIndex));

        if (!_pending.TryGetValue(deliveryId, out var delivery)) return;
        if (!delivery.CollectedSlots.Add(slotIndex)) return; // already counted on this machine

        AudioManager.Instance?.PlayPlacePart();

        delivery.Collected++;
        _pending[deliveryId] = delivery;

        HUDController.Instance?.AddNotification(
            "Part collected", $"{delivery.Collected}/{delivery.Total}",
            HUDController.NotifType.Success);

        if (delivery.Collected < delivery.Total) return;

        _pending.Remove(deliveryId);

        if (_localPallets.TryGetValue(deliveryId, out var pallet) && pallet != null)
            Destroy(pallet);
        _localPallets.Remove(deliveryId);

        // Only one machine may spawn the actual vehicle: the server in multiplayer, or the
        // sole machine in single-player (where IsMultiplayer is false and IsServer meaningless).
        NetworkShopManager.ShopEntry assembledEntry = default;
        bool hasEntry = NetworkShopManager.Instance != null
                     && NetworkShopManager.Instance.TryGetEntry(delivery.Type, out assembledEntry);

        if (hasEntry)
            MarkVehicleAssembled(deliveryId, assembledEntry);

        if ((!IsMultiplayer || IsServer) && hasEntry)
        {
            NetworkShopManager.Instance.SpawnVehicle(assembledEntry);
        }

        HUDController.Instance?.AddNotification(
            "Vehicle assembled", "All parts delivered", HUDController.NotifType.Success);
    }

    private void MarkVehicleAssembled(int deliveryId, NetworkShopManager.ShopEntry entry)
    {
        Vector3 vehiclePosition = entry.SpawnPoint != null
            ? entry.SpawnPoint.position
            : NetworkShopManager.Instance.transform.position;
        _mapTargets[deliveryId] = new DeliveryMapTarget(
            deliveryId, vehiclePosition, entry.DisplayName, assembled: true);
    }
}

/// Persistent authoritative record for one purchased vehicle delivery.
/// CollectedMask supports up to 64 part boxes and makes collection atomic/idempotent.
public struct VehicleDeliveryState : INetworkSerializable, System.IEquatable<VehicleDeliveryState>
{
    public int DeliveryId;
    public int VehicleTypeIndex;
    public int GateIndex;
    public int TotalParts;
    public ulong CollectedMask;
    public double DueServerTime;
    public byte Phase;
    public uint Version;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref DeliveryId);
        serializer.SerializeValue(ref VehicleTypeIndex);
        serializer.SerializeValue(ref GateIndex);
        serializer.SerializeValue(ref TotalParts);
        serializer.SerializeValue(ref CollectedMask);
        serializer.SerializeValue(ref DueServerTime);
        serializer.SerializeValue(ref Phase);
        serializer.SerializeValue(ref Version);
    }

    public bool Equals(VehicleDeliveryState other) =>
        DeliveryId == other.DeliveryId && VehicleTypeIndex == other.VehicleTypeIndex &&
        GateIndex == other.GateIndex && TotalParts == other.TotalParts &&
        CollectedMask == other.CollectedMask && DueServerTime.Equals(other.DueServerTime) &&
        Phase == other.Phase && Version == other.Version;

    public override bool Equals(object obj) => obj is VehicleDeliveryState other && Equals(other);
    public override int GetHashCode() => DeliveryId;
}

/// Persistent record for a purchased empty-pallet bundle, including late join.
public struct EmptyPalletBundleState : INetworkSerializable, System.IEquatable<EmptyPalletBundleState>
{
    public int BundleId;
    public int GateIndex;
    public int Count;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref BundleId);
        serializer.SerializeValue(ref GateIndex);
        serializer.SerializeValue(ref Count);
    }

    public bool Equals(EmptyPalletBundleState other) =>
        BundleId == other.BundleId && GateIndex == other.GateIndex && Count == other.Count;

    public override bool Equals(object obj) => obj is EmptyPalletBundleState other && Equals(other);
    public override int GetHashCode() => BundleId;
}
