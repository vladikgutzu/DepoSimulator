using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// Staged assembly bench with a unified ghost-mesh approach.
///
/// Parts arrive only as boxes carried in the hands (BoxWithPart) — players have no inventory.
///
/// On order accepted: spawns the matching AssemblyReferences prefab near the bench
/// with ghost shader on all renderers. Each direct child of the prefab root is named
/// after a PartID. As parts are installed, the matching child's renderers switch
/// back to their original materials, making that part "appear".
public class AssemblyBench : MonoBehaviour, IHoldInteractable, ICanInteractCheck
{
    public enum PackingBoxTier
    {
        Small,
        Medium,
        Large,
    }

    static AssemblyBench _instance;
    // The first packed order establishes the horizontal hand-span baseline for this shift.
    // Later orders may be taller or deeper, but never narrower than the box the tutorial first
    // teaches the player to carry with the authored PackedCarryHand_L/R pose.
    static float _firstPackedOrderWidth;

    public static AssemblyBench Instance
    {
        get => _instance != null ? _instance
                                 : _instance = Object.FindFirstObjectByType<AssemblyBench>();
        private set => _instance = value;
    }

    private const string GhostPrefabFolder = "AssemblyReferences";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetPackedOrderWidth() => _firstPackedOrderWidth = 0f;

    [Header("Ghost Placement")]
    [SerializeField] private Vector3 _ghostOffset = new Vector3(0f, 0f, 1.4f);

    [Header("Ghost Materials")]
    [SerializeField] private Material _ghostActiveMat;
    [SerializeField] private Material _ghostLockedMat;

    [Header("Packing — Box by size")]
    [Tooltip("Box used when assembled object max dimension < MediumThreshold.")]
    [SerializeField] private GameObject _boxSmall;
    [Tooltip("Box used when max dimension is between thresholds.")]
    [SerializeField] private GameObject _boxMedium;
    [Tooltip("Box used when max dimension >= LargeThreshold.")]
    [SerializeField] private GameObject _boxLarge;
    [SerializeField] private float _mediumThreshold = 0.55f;
    [SerializeField] private float _largeThreshold  = 1.10f;
    [Tooltip("Scene pallet that the ghost and packed box sit on.")]
    [SerializeField] private Transform _assemblyPallet;

    // ── Per-part tracking ──────────────────────────────────────────────────────
    private struct PartGroup
    {
        public PartDefinition Part;
        public Renderer[]     Renderers;
        public Material[][]   OrigMats;
        public int            Required;
        public int            Placed;
        public bool           HasVisual;
        public bool           IsComplete => Placed >= Required;
    }

    private readonly Dictionary<string, PartGroup> _partGroups = new();
    private GameObject      _ghostInstance;
    private float           _palletTopY;
    private Material        _lockedMatRuntime;
    private OrderDefinition _activeOrder;
    private bool            _shownNonAssemblerHint;

    // ── Public state ───────────────────────────────────────────────────────────
    public OrderDefinition  ActiveOrder       => _activeOrder;

    public event System.Action OnBenchChanged;
    public event System.Action OnOrderPacked;

    // The specialist installs and packs immediately. Other roles can always cover the job, but
    // take the configured hold time even when no Assembler is currently present. Solo mode is the
    // only exemption, so a player who is genuinely alone is never punished for missing roles.
    public bool RequiresHold
    {
        get
        {
            if (SpecializationPenalties.SoloMode
                || SpecializationPenalties.IsAssembler(SpecializationPenalties.GetLocalRole()))
                return false;

            var grabbed = BoxPhysicsInteractable.GrabbedBox;
            var vehicleBox = grabbed != null ? grabbed.GetComponent<VehiclePartBox>() : null;
            if (vehicleBox?.Part != null) return true;

            // Any non-Assembler may finish the order if the specialist leaves, but doing so is
            // deliberately slower. This remains true even when no Assembler is currently present.
            if (_activeOrder != null && IsOrderComplete()) return true;

            var box = grabbed != null ? grabbed.GetComponent<BoxWithPart>() : null;
            return box?.Part != null && IsPartNeeded(box.Part);
        }
    }

    public float HoldDuration => IsPackingInteraction
        ? SpecializationPenalties.Bench.PackingDelay
        : SpecializationPenalties.Bench.PlacementDelay;

    public bool IsPackingInteraction =>
        _activeOrder != null
        && IsOrderComplete()
        && BoxPhysicsInteractable.GrabbedBox?.GetComponent<VehiclePartBox>() == null;

    public string HoldProgressLabel => IsPackingInteraction
        ? "PACKING ORDER"
        : "INSTALLING PART";

    // ── ICanInteractCheck ──────────────────────────────────────────────────────
    public bool CanInteract(GameObject interactor)
    {
        if (GameEventManager.Instance?.IsBenchMalfunctioning == true) return false;
        return true;
    }

    // ── InteractPrompt ─────────────────────────────────────────────────────────
    public string InteractPrompt
    {
        get
        {
            if (GameEventManager.Instance?.IsBenchMalfunctioning == true)
                return "Bench malfunctioning  [unavailable]";

            // Vehicle delivery parts are independent of the active order — check first so a
            // carried vehicle box can be delivered even with no order accepted.
            var vehicleBox = BoxPhysicsInteractable.GrabbedBox?.GetComponent<VehiclePartBox>();
            if (vehicleBox != null && vehicleBox.Part != null)
                return RequiresHold
                    ? $"Hold {InputHints.EK} to install vehicle part: {vehicleBox.Part.PartName}"
                    : $"Deliver vehicle part: {vehicleBox.Part.PartName}  {InputHints.E}";

            if (_activeOrder == null) return "Bench";
            if (IsOrderComplete())
                return RequiresHold
                    ? $"Order ready — hold {InputHints.EK} to pack"
                    : $"Order ready — pack into a box  {InputHints.E}";

            // Carrying a box with a needed part — show install prompt directly
            var carriedBox = BoxPhysicsInteractable.GrabbedBox;
            if (carriedBox != null)
            {
                var bwp = carriedBox.GetComponent<BoxWithPart>();
                if (bwp?.Part != null && IsPartNeeded(bwp.Part))
                    return RequiresHold
                        ? $"Hold {InputHints.EK} to install: {bwp.Part.PartName}"
                        : $"Install from box: {bwp.Part.PartName}  {InputHints.E}";
            }

            var remaining = RemainingNames();
            if (remaining.Count == 0) return "All parts installed";

            if (IsTutorialMode())
                return $"Bring {remaining[0]} from a shelf";

            return $"Bring a box with: {string.Join(", ", remaining.Take(2))}";
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;
    }

    private void Start()
    {
        // Compute pallet top surface once from scene pallet bounds
        if (_assemblyPallet != null)
        {
            var rends = _assemblyPallet.GetComponentsInChildren<Renderer>(false);
            _palletTopY = _assemblyPallet.position.y;
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                _palletTopY = b.max.y;
            }
        }
        else
        {
            _palletTopY = transform.TransformPoint(_ghostOffset).y;
        }

        var gm = GameManager.Instance ?? Object.FindAnyObjectByType<GameManager>();
        if (gm == null) return;
        gm.OnOrderAccepted  += OnOrderAccepted;
        gm.OnOrderCompleted += _ => ClearBench();
        gm.OnOrderExpired   += _ => ClearBench();
    }

    private void OnDestroy()
    {
        Instance = null;
        var gm = GameManager.Instance;
        if (gm == null) return;
        gm.OnOrderAccepted -= OnOrderAccepted;
    }

    // ── Event handlers ─────────────────────────────────────────────────────────
    private void OnOrderAccepted(OrderDefinition order)
    {
        _activeOrder             = order;
        _shownNonAssemblerHint   = false;
        BuildGhost(order);
        OnBenchChanged?.Invoke();
    }

    // ── IInteractable ──────────────────────────────────────────────────────────
    public void Interact(GameObject interactor)
    {
        if (GameEventManager.Instance?.IsBenchMalfunctioning == true)
        {
            HUDController.Instance?.AddNotification(
                "Bench malfunctioning", "Wait for it to be repaired...",
                HUDController.NotifType.Danger);
            return;
        }

        // Vehicle delivery parts are independent of the active order — handle before any
        // order-related early-outs so a vehicle box can be delivered with no order accepted.
        var vehicleGrabbed = BoxPhysicsInteractable.GrabbedBox;
        var vehicleBox      = vehicleGrabbed != null ? vehicleGrabbed.GetComponent<VehiclePartBox>() : null;
        if (vehicleBox != null)
        {
            bool isMpVehicle = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;
            if (isMpVehicle)
            {
                // Keep the box in hand until the server validates that this client owns it. The
                // accepted collection RPC releases and destroys the local copy transactionally.
                VehicleDeliveryManager.Instance?.RequestCollectPartServerRpc(vehicleBox.DeliveryId, vehicleBox.SlotIndex);
            }
            else
            {
                vehicleGrabbed.SilentRelease();
                VehicleDeliveryManager.Instance?.CollectPartLocal(vehicleBox.DeliveryId, vehicleBox.SlotIndex);
            }

            return;
        }

        if (_activeOrder == null)
        {
            HUDController.Instance?.AddNotification(
                "Bench", "No active order — take one from the board",
                HUDController.NotifType.Warning);
            return;
        }

        if (IsOrderComplete())
        {
            bool isMP = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;
            if (isMP)
                CoopGameManager.Instance?.RequestSpawnPackedBoxServerRpc();
            else
                SpawnPackedBox();
            return;
        }

        // Non-assembler hint — only in multiplayer, shown once per order so it doesn't spam.
        if (!SpecializationPenalties.SoloMode &&
            !_shownNonAssemblerHint &&
            !SpecializationPenalties.IsAssembler(SpecializationPenalties.GetLocalRole()))
        {
            _shownNonAssemblerHint = true;
            HUDController.Instance?.AddNotification(
                "Assembler specialization",
                "Your teammate installs instantly; hold the interaction key to cover this station",
                HUDController.NotifType.Warning);
        }

        bool isMultiplayer = Unity.Netcode.NetworkManager.Singleton != null
                          && Unity.Netcode.NetworkManager.Singleton.IsListening;

        // If player is physically carrying a box that contains a needed part — install it directly
        var grabbed = BoxPhysicsInteractable.GrabbedBox;
        if (grabbed != null)
        {
            var bwp = grabbed.GetComponent<BoxWithPart>();
            if (bwp?.Part != null && IsPartNeeded(bwp.Part))
            {
                var boxedPart = bwp.Part;
                ulong netBoxId = grabbed.NetBoxId;
                if (isMultiplayer)
                {
                    // Keep the box in hand until the server atomically validates holder, version,
                    // distance and required part. The consumed state then releases/destroys it on
                    // every peer; a rejected request leaves the local carry untouched.
                    CoopGameManager.Instance?.PlacePartAtBenchServerRpc(netBoxId, grabbed.NetVersion);
                }
                else
                {
                    grabbed.SilentRelease();
                    Object.Destroy(grabbed.gameObject);
                    PlacePart(boxedPart);
                }
                return;
            }
        }

        var rem = RemainingNames();
        HUDController.Instance?.AddNotification("Bench",
            rem.Count > 0 ? $"Bring a box with: {string.Join(", ", rem.Take(2))}" : "No parts needed",
            HUDController.NotifType.Warning);
    }

    // ── Ghost construction ─────────────────────────────────────────────────────
    private void BuildGhost(OrderDefinition order)
    {
        ClearGhost();

        var palletBasePos = _assemblyPallet != null
            ? _assemblyPallet.position
            : transform.TransformPoint(_ghostOffset);
        var ghostPos = new Vector3(palletBasePos.x, _palletTopY, palletBasePos.z);

        // Priority 1: explicit SharedGhostPrefab (multi-part from same mesh + GhostChildPaths)
        GameObject prefab     = order.SharedGhostPrefab;
        bool       useChildPaths = prefab != null;

        // Priority 2: legacy prefab in Resources/AssemblyReferences/
        if (prefab == null)
            prefab = Resources.Load<GameObject>($"{GhostPrefabFolder}/{order.name}");

        if (prefab != null)
        {
            // Use non-generic Instantiate to avoid InvalidCastException with certain prefab asset types.
            var spawned = Object.Instantiate((Object)prefab, ghostPos, transform.rotation);
            _ghostInstance = spawned as GameObject;
        }

        if (_ghostInstance == null)
        {
            // Priority 3: auto-build ghost container from each part's VisualPrefab.
            // Each part's prefab becomes a child named by PartID — works with existing Find(partID) logic.
            bool anyVisual = System.Array.Exists(order.RequiredParts, r => r.Part?.VisualPrefab != null);
            if (anyVisual)
            {
                _ghostInstance = new GameObject($"Ghost_{order.name}");
                _ghostInstance.transform.SetPositionAndRotation(ghostPos, transform.rotation);

                var spawned = new HashSet<string>();
                float offsetX = 0f;
                foreach (var req in order.RequiredParts)
                {
                    if (req.Part?.VisualPrefab == null || !spawned.Add(req.Part.PartID)) continue;
                    var childGO = Object.Instantiate(req.Part.VisualPrefab as Object,
                                      _ghostInstance.transform) as GameObject;
                    if (childGO == null)
                    {
                        Debug.LogWarning($"[AssemblyBench] VisualPrefab '{req.Part.PartID}' is not a GameObject — skipping ghost.");
                        continue;
                    }
                    childGO.name = req.Part.PartID;
                    childGO.transform.localPosition = new Vector3(offsetX, 0f, 0f);
                    offsetX += 0.7f;
                }
            }
        }

        if (_ghostInstance != null)
        {
            // Product prefabs may be authored as physical world objects. A ghost must never
            // inherit that motion or it can fall off the bench before the first part is placed.
            foreach (var body in _ghostInstance.GetComponentsInChildren<Rigidbody>(true))
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.useGravity = false;
                body.isKinematic = true;
                body.detectCollisions = false;
            }

            foreach (var col in _ghostInstance.GetComponentsInChildren<Collider>(true))
                Destroy(col);
        }

        _partGroups.Clear();
        foreach (var req in order.RequiredParts)
        {
            string partID = req.Part.PartID;
            if (_partGroups.ContainsKey(partID))
            {
                var ex = _partGroups[partID];
                ex.Required += Mathf.Max(1, req.Count);
                _partGroups[partID] = ex;
                continue;
            }

            Renderer[]   renderers = System.Array.Empty<Renderer>();
            Material[][] origMats  = System.Array.Empty<Material[]>();
            bool         hasVisual = false;

            if (_ghostInstance != null)
            {
                if (useChildPaths)
                {
                    if (req.Part.GhostChildPaths is { Length: > 0 })
                    {
                        // Multi-part: collect renderers from specified child paths only
                        var rendList = new List<Renderer>();
                        foreach (var path in req.Part.GhostChildPaths)
                        {
                            // Logical multi-part orders can use generated component visuals even
                            // when the final product is a single, indivisible imported mesh.
                            // "$none" keeps an intermediate component tracked without claiming
                            // final-product renderers; "$all" reveals the complete product when
                            // the finishing component is installed.
                            if (path == "$none") continue;
                            if (path == "$all")
                            {
                                rendList.AddRange(_ghostInstance.GetComponentsInChildren<Renderer>(true));
                                continue;
                            }

                            var child = _ghostInstance.transform.Find(path);
                            if (child != null)
                                rendList.AddRange(child.GetComponentsInChildren<Renderer>(true));
                            else
                                Debug.LogWarning($"[AssemblyBench] GhostChildPath not found: '{path}' in '{prefab.name}'");
                        }
                        renderers = rendList.ToArray();
                    }
                    else
                    {
                        // Single-part or whole-object: entire ghost belongs to this part
                        renderers = _ghostInstance.GetComponentsInChildren<Renderer>(true);
                    }
                    origMats  = renderers.Select(r => r.sharedMaterials).ToArray();
                    hasVisual = renderers.Length > 0;
                }
                else
                {
                    var groupRoot = _ghostInstance.transform.Find(partID);
                    if (groupRoot != null)
                    {
                        renderers = groupRoot.GetComponentsInChildren<Renderer>(true);
                        origMats  = renderers.Select(r => r.sharedMaterials).ToArray();
                        hasVisual = renderers.Length > 0;
                    }
                    else
                    {
                        Debug.Log($"[AssemblyBench] No child '{partID}' in ghost — tracked without visual.");
                    }
                }
            }

            _partGroups[partID] = new PartGroup
            {
                Part      = req.Part,
                Renderers = renderers,
                OrigMats  = origMats,
                Required  = Mathf.Max(1, req.Count),
                Placed    = 0,
                HasVisual = hasVisual,
            };
        }

        ApplyGhostMaterials();
        GhostUntrackedRenderers();
    }

    // ── Material management ────────────────────────────────────────────────────
    private void ApplyGhostMaterials()
    {
        foreach (var kvp in _partGroups)
        {
            var data = kvp.Value;
            if (!data.HasVisual || data.IsComplete) continue;
            SetRendererMat(data.Renderers, _ghostActiveMat);
        }
    }

    private static void SetRendererMat(Renderer[] renderers, Material mat)
    {
        foreach (var r in renderers)
        {
            if (r == null) continue;
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = mat;
            r.sharedMaterials = mats;
        }
    }

    // Applies ghost material to any renderer NOT owned by a tracked PartGroup.
    // Handles decorative/structural parts in the prefab that have no PartID match.
    private void GhostUntrackedRenderers()
    {
        if (_ghostInstance == null || _ghostActiveMat == null) return;

        var tracked = new HashSet<Renderer>();
        foreach (var g in _partGroups.Values)
            foreach (var r in g.Renderers)
                if (r != null) tracked.Add(r);

        foreach (var r in _ghostInstance.GetComponentsInChildren<Renderer>(true))
        {
            if (tracked.Contains(r)) continue;
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = _ghostActiveMat;
            r.sharedMaterials = mats;
        }
    }

    private Material GetLockedMat()
    {
        if (_ghostLockedMat != null) return _ghostLockedMat;
        if (_lockedMatRuntime == null && _ghostActiveMat != null)
        {
            _lockedMatRuntime = new Material(_ghostActiveMat) { hideFlags = HideFlags.HideAndDontSave };
            _lockedMatRuntime.SetColor("_BaseColor", new Color(0.12f, 0.12f, 0.18f, 0.10f));
        }
        return _lockedMatRuntime;
    }

    // ── Placement ──────────────────────────────────────────────────────────────
    private void PlacePart(PartDefinition part)
    {
        string partID = part.PartID;
        bool tutorial = IsTutorialMode();

        if (!_partGroups.TryGetValue(partID, out var data))
        {
            if (!tutorial) return;
            // Map to first incomplete slot — any part fills it in tutorial
            foreach (var key in new List<string>(_partGroups.Keys))
            {
                var pg = _partGroups[key];
                if (!pg.IsComplete) { data = pg; partID = key; break; }
            }
            if (string.IsNullOrEmpty(partID)) return;
        }

        if (data.IsComplete) return;

        data.Placed++;
        _partGroups[partID] = data;

        if (data.IsComplete && data.HasVisual)
        {
            for (int i = 0; i < data.Renderers.Length; i++)
                if (data.Renderers[i] != null)
                    data.Renderers[i].sharedMaterials = data.OrigMats[i];
        }

        AudioManager.Instance?.PlayPlacePart();
        OnBenchChanged?.Invoke();

        bool multiplayer = Unity.Netcode.NetworkManager.Singleton?.IsListening == true;
        if (IsOrderComplete() && !multiplayer)
            HUDController.Instance?.AddNotification(
                "Order assembled!", "Return to the bench and press [E] to pack it",
                HUDController.NotifType.Success);
    }

    // ── Packing ────────────────────────────────────────────────────────────────
    private Vector3 GhostSize()
    {
        if (_ghostInstance == null) return Vector3.zero;
        var renderers = _ghostInstance.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return Vector3.zero;
        var bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        return bounds.size;
    }

    private float GhostMaxDimension()
    {
        var size = GhostSize();
        return Mathf.Max(size.x, size.y, size.z);
    }

    private GameObject PickBoxPrefab(float maxDim)
    {
        if (maxDim >= _largeThreshold  && _boxLarge  != null) return _boxLarge;
        if (maxDim >= _mediumThreshold && _boxMedium != null) return _boxMedium;
        return _boxSmall ?? _boxMedium ?? _boxLarge;
    }

    /// <summary>
    /// Reuses the packing station's authored carton family for other delivery systems. Keeping
    /// one source of prefab references prevents a vehicle delivery from approximating a large
    /// crate by stretching the small carton mesh.
    /// </summary>
    public GameObject GetPackingBoxPrefab(PackingBoxTier tier)
    {
        return tier switch
        {
            PackingBoxTier.Large  => _boxLarge  ?? _boxMedium ?? _boxSmall,
            PackingBoxTier.Medium => _boxMedium ?? _boxLarge  ?? _boxSmall,
            _                     => _boxSmall  ?? _boxMedium ?? _boxLarge,
        };
    }

    /// Exposed so CoopGameManager can measure ghost size on server before clearing bench.
    public float GetGhostMaxDimension() => GhostMaxDimension();

    /// Exposed so CoopGameManager can measure the full per-axis ghost size on server before
    /// clearing bench — needed for ScaleBoxToFit's non-uniform stretch.
    public Vector3 GetGhostSize() => GhostSize();

    /// Returns the world-space position where the packed box should be spawned.
    public Vector3 GetPackedSpawnPosition()
    {
        var basePos = _assemblyPallet != null
            ? _assemblyPallet.position
            : transform.TransformPoint(new Vector3(_ghostOffset.x, 0f, _ghostOffset.z));
        return new Vector3(basePos.x, _palletTopY + 0.20f, basePos.z);
    }

    // Extra headroom applied on top of the item's own bounds so it doesn't clip through the
    // box walls once scaled — a box exactly item-sized still looks like the item pokes out.
    private const float PackedBoxPadding = 1.15f;

    /// Grows a just-instantiated box PER-AXIS so each of its own local dimensions comfortably
    /// exceeds the matching dimension of itemSize. Never shrinks an axis — small/medium items
    /// already fit their tier's box as-is. Non-uniform (vs. a single uniform factor) is what
    /// makes a tall narrow item (e.g. a 2.3m fridge) end up in a tall narrow box instead of a
    /// giant cube — a uniform scale-to-fit stretches every axis by the same amount, which
    /// balloons the two axes that were already big enough.
    ///
    /// Must be called while boxGO's rotation is identity — bounds are read/written in world
    /// space, so a rotated box would mix up which world axis maps to which local scale axis.
    /// CreatePackedBoxObject instantiates at identity and rotates afterward for this reason.
    private static void ScaleBoxToFit(GameObject boxGO, Vector3 itemSize)
    {
        var rends = boxGO.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return;

        var bounds = rends[0].bounds;
        foreach (var r in rends) bounds.Encapsulate(r.bounds);
        if (bounds.size.x <= 0f || bounds.size.y <= 0f || bounds.size.z <= 0f) return;

        Vector3 required = itemSize * PackedBoxPadding;
        Vector3 scale    = boxGO.transform.localScale;
        scale.x *= Mathf.Max(1f, required.x / bounds.size.x);
        scale.y *= Mathf.Max(1f, required.y / bounds.size.y);
        scale.z *= Mathf.Max(1f, required.z / bounds.size.z);
        boxGO.transform.localScale = scale;
    }

    /// Keeps every finished-order carton at least as wide as the first carton packed this shift.
    /// CreatePackedBoxObject runs for both solo and every multiplayer peer, so using the first
    /// actual result avoids a duplicated hard-coded measurement and stays deterministic across
    /// the network. This method is called while the root rotation is identity; bounds X therefore
    /// maps directly to the box's local width axis. Only X scale is changed.
    private static void EnforceFirstPackedOrderWidth(GameObject boxGO)
    {
        var renderers = boxGO.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            if (renderers[i] != null) bounds.Encapsulate(renderers[i].bounds);

        float width = bounds.size.x;
        if (width <= 0.0001f) return;

        if (_firstPackedOrderWidth <= 0.0001f)
        {
            _firstPackedOrderWidth = width;
            return;
        }

        if (width >= _firstPackedOrderWidth) return;

        Vector3 scale = boxGO.transform.localScale;
        scale.x *= _firstPackedOrderWidth / width;
        boxGO.transform.localScale = scale;
    }

    /// Creates the packed-order box GO (Rigidbody + BoxPhysicsInteractable + PackedOrderBox).
    /// Does NOT clear the bench or show notifications — caller handles those.
    public GameObject CreatePackedBoxObject(Vector3 itemSize, Vector3 spawnPos, Quaternion spawnRot)
    {
        float tierDim = Mathf.Max(itemSize.x, itemSize.y, itemSize.z);
        var chosenBox = PickBoxPrefab(tierDim);
        GameObject boxGO;
        if (chosenBox != null)
        {
            // Instantiate at identity so ScaleBoxToFit's world-space bounds line up with local
            // scale axes, then rotate into place — see ScaleBoxToFit's doc comment.
            boxGO = Instantiate(chosenBox, spawnPos, Quaternion.identity);
            ScaleBoxToFit(boxGO, itemSize);
            EnforceFirstPackedOrderWidth(boxGO);
            boxGO.transform.rotation = spawnRot;
        }
        else
        {
            boxGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
            boxGO.name = "PackedOrder";
            boxGO.transform.SetPositionAndRotation(spawnPos, Quaternion.identity);
            boxGO.transform.localScale = new Vector3(0.55f, 0.40f, 0.50f);
            EnforceFirstPackedOrderWidth(boxGO);
            boxGO.transform.rotation = spawnRot;
        }

        if (!boxGO.TryGetComponent<Rigidbody>(out var rb))
            rb = boxGO.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity  = false;
        rb.mass        = 12f;

        if (!boxGO.TryGetComponent<BoxPhysicsInteractable>(out _))
            boxGO.AddComponent<BoxPhysicsInteractable>();

        boxGO.AddComponent<PackedOrderBox>();
        StartCoroutine(AllowOverlappingPlayersToExit(boxGO));
        return boxGO;
    }

    /// A large packed product can grow into the narrow gap where the assembler is standing.
    /// Temporarily ignore only the collision pairs that overlap at spawn, then restore each pair
    /// as soon as that character has walked clear. The box remains solid for everybody else.
    IEnumerator AllowOverlappingPlayersToExit(GameObject boxGO)
    {
        yield return new WaitForFixedUpdate();
        if (boxGO == null) yield break;

        var boxColliders = boxGO.GetComponentsInChildren<Collider>(true);
        if (boxColliders.Length == 0) yield break;

        Bounds boxBounds = boxColliders[0].bounds;
        for (int i = 1; i < boxColliders.Length; i++)
            if (boxColliders[i] != null && boxColliders[i].enabled)
                boxBounds.Encapsulate(boxColliders[i].bounds);

        foreach (var controller in Object.FindObjectsByType<CharacterController>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (controller == null || !controller.enabled || !boxBounds.Intersects(controller.bounds))
                continue;

            foreach (var boxCollider in boxColliders)
                if (boxCollider != null) Physics.IgnoreCollision(boxCollider, controller, true);

            StartCoroutine(RestorePlayerCollisionWhenClear(boxGO, boxColliders, controller));
        }
    }

    static IEnumerator RestorePlayerCollisionWhenClear(GameObject boxGO, Collider[] boxColliders,
        CharacterController controller)
    {
        while (boxGO != null && controller != null)
        {
            Bounds bounds = default;
            bool hasBounds = false;
            foreach (var col in boxColliders)
            {
                if (col == null || !col.enabled) continue;
                if (!hasBounds) { bounds = col.bounds; hasBounds = true; }
                else bounds.Encapsulate(col.bounds);
            }

            if (!hasBounds || !bounds.Intersects(controller.bounds)) break;
            yield return new WaitForFixedUpdate();
        }

        if (controller == null) yield break;
        foreach (var boxCollider in boxColliders)
            if (boxCollider != null) Physics.IgnoreCollision(boxCollider, controller, false);
    }

    private void SpawnPackedBox()
    {
        var itemSize  = GhostSize();
        var spawnPos  = GetPackedSpawnPosition();
        var boxGO     = CreatePackedBoxObject(itemSize, spawnPos, transform.rotation);

        if (boxGO.TryGetComponent<PackedOrderBox>(out var packed))
            packed.BindOrder(_activeOrder, _activeOrder?.OrderName);

        ClearBench();
        OnOrderPacked?.Invoke();

        AudioManager.Instance?.PlayPlacePart();
        HUDController.Instance?.AddNotification(
            "Order packed!",
            "Grab the box [E] and carry it to the glowing beacon by the gate",
            HUDController.NotifType.Success);
        ShippingZone.Instance?.FlashForPickup();
    }

    // ── Queries ────────────────────────────────────────────────────────────────
    private static bool IsTutorialMode()
        => TutorialManager.Instance != null && (GameManager.Instance?.CompletedOrders ?? 0) == 0;

    private List<string> RemainingNames()
    {
        var names = new List<string>();
        foreach (var kvp in _partGroups)
            if (!kvp.Value.IsComplete)
                names.Add(kvp.Value.Part.PartName);
        return names;
    }

    // ── Public API ─────────────────────────────────────────────────────────────
    public bool IsOrderComplete()
    {
        if (_activeOrder == null || _partGroups.Count == 0) return false;
        foreach (var kvp in _partGroups)
            if (!kvp.Value.IsComplete) return false;
        return true;
    }

    /// Returns true if this part is required by the active order and not yet fully placed.
    public bool IsPartNeeded(PartDefinition part)
    {
        if (_activeOrder == null) return false;
        return _partGroups.TryGetValue(part.PartID, out var data) && !data.IsComplete;
    }

    public int GetPlacedCount(PartDefinition part)
        => _partGroups.TryGetValue(part.PartID, out var d) ? d.Placed : 0;

    /// Called by CoopGameManager.PlacePartAtBenchServerRpc and its ghost echo on clients.
    public void PlacePartFromNetwork(PartDefinition part) => PlacePart(part);

    // ── Event helpers ──────────────────────────────────────────────────────────

    /// Returns a random unfinished part from the active order (for UrgentPartEvent).
    public PartDefinition GetRandomUnplacedPart()
    {
        var candidates = new System.Collections.Generic.List<PartDefinition>();
        foreach (var kv in _partGroups)
            if (!kv.Value.IsComplete) candidates.Add(kv.Value.Part);
        return candidates.Count > 0 ? candidates[Random.Range(0, candidates.Count)] : null;
    }

    /// Returns a random part that has at least one placed instance.
    public PartDefinition GetRandomPlacedPart()
    {
        var candidates = new System.Collections.Generic.List<PartDefinition>();
        foreach (var kv in _partGroups)
            if (kv.Value.Placed > 0) candidates.Add(kv.Value.Part);
        return candidates.Count > 0 ? candidates[Random.Range(0, candidates.Count)] : null;
    }

    /// Removes one placed instance of the specified part by PartID.
    /// Returns true if a part was removed, false if it wasn't placed.
    public bool TryRemovePartById(string partId)
    {
        if (!_partGroups.TryGetValue(partId, out var data) || data.Placed <= 0) return false;
        data.Placed--;
        _partGroups[partId] = data;
        if (data.HasVisual && _ghostActiveMat != null)
            SetRendererMat(data.Renderers, _ghostActiveMat);
        OnBenchChanged?.Invoke();
        return true;
    }

    /// Removes one placed instance of a random part and restores its ghost visual.
    /// Returns the removed part, or null if nothing was placed yet.
    public PartDefinition TryRemovePlacedPart()
    {
        foreach (var key in new System.Collections.Generic.List<string>(_partGroups.Keys))
        {
            var data = _partGroups[key];
            if (data.Placed <= 0) continue;

            data.Placed--;
            _partGroups[key] = data;
    
            // Restore ghost material so the slot looks empty again.
            if (data.HasVisual && _ghostActiveMat != null)
                SetRendererMat(data.Renderers, _ghostActiveMat);

            OnBenchChanged?.Invoke();
            return data.Part;
        }
        return null;
    }

    // ── Cleanup ────────────────────────────────────────────────────────────────
    public void ClearBench()
    {
        ClearGhost();
        _activeOrder = null;
        OnBenchChanged?.Invoke();
    }

    private void ClearGhost()
    {
        if (_ghostInstance != null) Destroy(_ghostInstance);
        _ghostInstance = null;
        _partGroups.Clear();
    }
}
