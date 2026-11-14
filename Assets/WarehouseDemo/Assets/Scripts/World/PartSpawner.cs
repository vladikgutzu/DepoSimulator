using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;

/// Assigns parts to random scene boxes via BoxWithPart component.
/// No new geometry is spawned — existing warehouse boxes become interactable.
/// In multiplayer, only runs on the server; clients receive assignments via NetworkBoxManager.
public class PartSpawner : MonoBehaviour
{
    public static PartSpawner Instance { get; private set; }

    [Header("Box Discovery")]
    [Tooltip("Name substrings to match box root objects (case-insensitive).")]
    [SerializeField] private string[] _boxNamePatterns = { "box_med_prefab", "box_large_prefab" };
    [Tooltip("Max Y of box bounds top — filters unreachable high-shelf boxes.")]
    [SerializeField] private float _maxReachHeight = 3.5f;
    [Tooltip("Min XZ distance between two chosen boxes to avoid clustering.")]
    [SerializeField] private float _minSpacing = 1.5f;

    [Header("Spawning")]
    [SerializeField] private PartDefinition[] _allParts;

    [Header("Tutorial")]
    [Tooltip("These boxes always receive the required parts on the first (tutorial) order. "
           + "Drag easy-to-reach, well-lit boxes here — one slot per required part. "
           + "Unused slots are ignored; shortage falls back to random pool.")]
    [SerializeField] private GameObject[] _tutorialBoxes = System.Array.Empty<GameObject>();

    [Tooltip("Boxes this high or lower can be reached on foot. Above it they need the forklift — " +
             "the last order deliberately puts some parts up there; see Spawn.")]
    [SerializeField] private float _highShelfCeiling = 7.5f;

    [Tooltip("Share of the last order's parts that go on the upper shelves, 0..1.")]
    [Range(0f, 1f)]
    [SerializeField] private float _lastOrderHighShare = 0.4f;

    [Tooltip("Share of a regular multiplayer order placed on upper shelves when the team has a " +
             "Forklift operator. Keeps that role involved without blocking incomplete teams.")]
    [Range(0f, 1f)]
    [SerializeField] private float _teamOrderHighShare = 0.25f;

    // Refreshed for every order. Installed crates are destroyed, so a startup-only pool gradually
    // became a list of nulls and could silently omit a required part from the final order.
    private GameObject[]  _boxPool    = System.Array.Empty<GameObject>();

    /// The boxes the pool above throws away for being out of reach. The last order of the demo
    /// puts part of itself up here on purpose: by then the team has a forklift and a ladder, and
    /// the final job is the one that should need them.
    private GameObject[]  _highPool   = System.Array.Empty<GameObject>();
    private readonly List<BoxWithPart> _assigned = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        if (_allParts == null || _allParts.Length == 0)
        {
            _allParts = Resources.LoadAll<PartDefinition>("Catalog/Parts");
            if (_allParts.Length == 0)
                _allParts = Resources.LoadAll<PartDefinition>("MotoParts");
        }

        _boxPool = BuildBoxPool();
        Debug.Log($"[PartSpawner] Box pool: {_boxPool.Length} usable boxes, "
                + $"high-shelf pool: {_highPool.Length}, "
                + $"tutorial slots: {_tutorialBoxes?.Length ?? 0}.");

        // Give tutorial boxes Rigidbodies (same treatment as pool boxes).
        if (_tutorialBoxes != null)
            foreach (var go in _tutorialBoxes)
                if (go != null) EnsureBoxRigidbody(go);

        var gm = GameManager.Instance;
        if (gm == null) { Debug.LogError("[PartSpawner] GameManager not found"); return; }
        gm.OnOrderAccepted  += Spawn;
        gm.OnOrderCompleted += OnOrderDone;
        gm.OnOrderExpired   += OnOrderDone;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        var gm = GameManager.Instance;
        if (gm == null) return;
        gm.OnOrderAccepted  -= Spawn;
        gm.OnOrderCompleted -= OnOrderDone;
        gm.OnOrderExpired   -= OnOrderDone;
    }

    private void OnOrderDone(OrderDefinition _) => ClearAssigned();

    // ── Pool building (runs once at Start) ───────────────────────────────────

    private GameObject[] BuildBoxPool()
    {
        // Tutorial boxes are reserved — exclude them from the random pool so they
        // can never receive a filler part that would block the tutorial assignment.
        var tutSet = new HashSet<GameObject>(
            _tutorialBoxes ?? System.Array.Empty<GameObject>());

        var candidates = new List<(GameObject go, Vector3 center)>();
        var high       = new List<(GameObject go, Vector3 center)>();

        foreach (var go in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            if (!MatchesPattern(go.name)) continue;
            if (tutSet.Contains(go)) continue;                       // reserved for tutorial
            var existingPart = go.GetComponent<BoxWithPart>();
            if (existingPart != null && existingPart.Part != null) continue; // already assigned

            if (IsInTrailerPallets(go.transform)) continue;

            var rends = go.GetComponentsInChildren<Renderer>(false);
            if (rends.Length == 0) continue;

            var bounds = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) bounds.Encapsulate(rends[i].bounds);

            if (bounds.max.y > _maxReachHeight)
            {
                // Out of reach on foot, but not out of the game: kept for the last order.
                if (bounds.max.y <= _highShelfCeiling) high.Add((go, bounds.center));
                continue;
            }

            candidates.Add((go, bounds.center));
        }

        var pool = SpacingFilter(candidates, _minSpacing)
                   .Select(t => t.go)
                   .ToArray();

        foreach (var go in pool)
            EnsureBoxRigidbody(go);

        _highPool = SpacingFilter(high, _minSpacing).Select(t => t.go).ToArray();
        foreach (var go in _highPool)
            EnsureBoxRigidbody(go);

        return pool;
    }

    private static void EnsureBoxRigidbody(GameObject go)
    {
        if (go.TryGetComponent<Rigidbody>(out _)) return;

        foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true))
        {
            string n = mc.gameObject.name.ToLower();
            bool isDecor = n.Contains("decal") || n.Contains("tape") || n.Contains("shadow");
            if (isDecor)
                mc.enabled = false;
            else
                mc.convex = true;
        }

        var rb            = go.AddComponent<Rigidbody>();
        rb.mass           = 8f;
        rb.linearDamping  = 0.4f;
        rb.angularDamping = 2f;
        rb.isKinematic    = true;
        rb.useGravity     = false;
    }

    private bool MatchesPattern(string name)
    {
        string lower = name.ToLower();
        foreach (var p in _boxNamePatterns)
            if (lower.Contains(p.ToLower())) return true;
        return false;
    }

    private static List<(GameObject go, Vector3 center)> SpacingFilter(
        List<(GameObject go, Vector3 center)> src, float minDist)
    {
        for (int i = src.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (src[i], src[j]) = (src[j], src[i]);
        }

        var kept = new List<(GameObject go, Vector3 center)>();
        float minSq = minDist * minDist;

        foreach (var item in src)
        {
            bool close = false;
            foreach (var k in kept)
            {
                float dx = item.center.x - k.center.x;
                float dz = item.center.z - k.center.z;
                if (dx * dx + dz * dz < minSq) { close = true; break; }
            }
            if (!close) kept.Add(item);
        }
        return kept;
    }

    // ── Spawning ──────────────────────────────────────────────────────────────

    private void Spawn(OrderDefinition order)
    {
        // In multiplayer, clients receive assignments from NetworkBoxManager
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
            && !NetworkManager.Singleton.IsServer)
            return;

        ClearAssigned();

        // Discover the boxes that still exist now, including ones rejected by the previous
        // spacing pass. This keeps late orders supplied after earlier crates were consumed.
        _boxPool = BuildBoxPool();

        // Ensure every pooled box with NetworkObject is network-spawned.
        // Scene-placed NetworkObjects are already spawned by NGO at session start (IsSpawned=true).
        // This guard covers any box that was somehow not yet spawned (e.g. late-added prefab).
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            foreach (var go in _boxPool)
            {
                if (go == null) continue;
                EnsureNetworkSpawned(go);
            }
            if (_tutorialBoxes != null)
                foreach (var go in _tutorialBoxes)
                    if (go != null) EnsureNetworkSpawned(go);
        }

        // Filter out boxes destroyed during previous orders (player carried & dropped them).
        var available = new List<int>();
        for (int i = 0; i < _boxPool.Length; i++)
            if (_boxPool[i] != null) available.Add(i);
        Shuffle(available);
        int used = 0;

        // Fixed placement covers the first two orders (index 0 = guided tutorial, index 1 =
        // the first "free practice" order) — reuses the same reserved tutorial slots, which
        // Spawn() re-fills from scratch each order via ClearAssigned() above. From the third
        // order onward, parts fall back to the fully randomized _boxPool.
        bool useFixedBoxes = _tutorialBoxes != null && _tutorialBoxes.Length > 0
                       && TutorialManager.Instance != null
                       && GameManager.Instance?.CompletedOrders <= 1;

        Debug.Log($"[PartSpawner] Spawn '{order.OrderName}' | parts={order.RequiredParts.Length} | pool={_boxPool.Length} | tutSlots={_tutorialBoxes?.Length ?? 0} | useFixedBoxes={useFixedBoxes} | allParts={_allParts?.Length ?? 0}");

        if (useFixedBoxes)
        {
            int ti = 0;
            foreach (var req in order.RequiredParts)
            {
                for (int i = 0; i < req.Count; i++)
                {
                    // Skip null or already-occupied tutorial slots.
                    while (ti < _tutorialBoxes.Length &&
                           (_tutorialBoxes[ti] == null ||
                            _tutorialBoxes[ti].GetComponent<BoxWithPart>() != null))
                        ti++;

                    if (ti < _tutorialBoxes.Length)
                        Assign(_tutorialBoxes[ti++], req.Part, required: true);
                    else if (used < available.Count)
                        Assign(_boxPool[available[used++]], req.Part, required: true);
                    else
                        Debug.LogWarning($"[PartSpawner] No box available for fixed-slot part {req.Part.PartName}");
                }
            }
        }
        else
        {
            if (_boxPool.Length == 0)
            {
                Debug.LogWarning("[PartSpawner] Box pool is empty.");
                return;
            }

            // The final order always reaches for the top shelves. Regular multiplayer orders do
            // the same when the team deliberately brought a Forklift operator, so that role has
            // meaningful work throughout the shift rather than only during the finale. Incomplete
            // teams keep their regular orders at floor level and can never be blocked by a role
            // they did not select.
            bool lastOrder = GameManager.Instance != null
                          && GameManager.Instance.IsFinalOrderWindow;
            bool hasForkliftOperator = !SpecializationPenalties.SoloMode
                                    && RoleTaskManager.TeamHasRole(PlayerRoleType.ForkLiftOperator);
            bool useUpperShelves = lastOrder || hasForkliftOperator;

            var highIdx = new List<int>();
            if (useUpperShelves)
            {
                for (int i = 0; i < _highPool.Length; i++) highIdx.Add(i);
                Shuffle(highIdx);
            }

            int placed = 0, highUsed = 0;
            int total  = order.RequiredParts.Sum(r => Mathf.Max(1, r.Count));
            float highShare = lastOrder ? _lastOrderHighShare : _teamOrderHighShare;
            int wantHigh = useUpperShelves && highIdx.Count > 0
                ? Mathf.Clamp(Mathf.RoundToInt(total * highShare), 1, highIdx.Count)
                : 0;

            foreach (var req in order.RequiredParts)
            {
                for (int i = 0; i < req.Count; i++, placed++)
                {
                    // Spread the high ones through the list rather than putting the first N up
                    // there: a player should not be able to clear the whole order on foot and then
                    // discover the rest is a forklift job.
                    bool goesHigh = highUsed < wantHigh
                                 && (total - placed) <= (wantHigh - highUsed) * 2;

                    if (goesHigh && highUsed < highIdx.Count)
                    {
                        Assign(_highPool[highIdx[highUsed++]], req.Part, required: true);
                        continue;
                    }

                    if (used < available.Count)
                    {
                        Assign(_boxPool[available[used++]], req.Part, required: true);
                        continue;
                    }

                    // Never silently drop a required part. If the reachable pool has been used
                    // up by earlier orders, an upper-shelf crate is still a valid (forklift)
                    // fallback. The old `break` here is what could leave Motor at 0/1 forever.
                    if (highUsed < highIdx.Count)
                    {
                        Assign(_highPool[highIdx[highUsed++]], req.Part, required: true);
                        continue;
                    }

                    Debug.LogError($"[PartSpawner] No surviving box exists for required part " +
                                   $"'{req.Part.PartName}'.");
                }
            }

            if (wantHigh > 0)
                HUDController.Instance?.AddNotification(
                    hasForkliftOperator ? "Forklift task assigned" : "Parts on the upper shelves",
                    hasForkliftOperator
                        ? $"{wantHigh} order part{(wantHigh == 1 ? " is" : "s are")} staged above floor level"
                        : $"{wantHigh} of them are out of reach — take the forklift or a ladder",
                    HUDController.NotifType.Warning);
        }

        // Sync assignments to multiplayer clients
        NetworkBoxManager.Instance?.SyncAssignments();
    }

    private void Assign(GameObject box, PartDefinition part, bool required)
    {
        if (box == null)
        {
            Debug.LogWarning($"[PartSpawner] Assign: box is null for '{part?.PartName}' — skipping.");
            return;
        }
        // ClearAssigned destroys tags at end-of-frame; when a new order is accepted in that same
        // frame, reuse the cleared component rather than fighting DisallowMultipleComponent.
        var bwp = box.GetComponent<BoxWithPart>() ?? box.AddComponent<BoxWithPart>();
        bwp.Setup(part, required);
        _assigned.Add(bwp);
        Debug.Log($"[PartSpawner] Assigned '{part.PartName}' → '{box.name}' required={required} inTrailer={IsInTrailerPallets(box.transform)}");

        // BoxWithPart is a pure data tag now — pickup/carry/hand-off/placement all go through
        // BoxPhysicsInteractable (same pattern as PackedOrderBox), so it must be enabled rather
        // than disabled here.
        var phys = box.GetComponent<BoxPhysicsInteractable>() ?? box.AddComponent<BoxPhysicsInteractable>();
        phys.enabled = true;
        phys.AdoptSceneId();   // added after the scene-load numbering pass — see AdoptSceneId
    }

    // ── Extra copies, outside the order ───────────────────────────────────────

    /// Puts one more, non-required copy of a part into the warehouse and hands back the box that
    /// got it. Returns null when there is nowhere to put it.
    ///
    /// Exists for UrgentPartEvent, which asks for a part to be fetched. Spawn() lays out only the
    /// active order's parts, so before this the event's target either did not exist in the world
    /// at all (an impossible errand that quietly timed out) or WAS the order's single copy — and
    /// handing that one in cost the player the order, with no way to get it back.
    ///
    /// Server-only in multiplayer, like the rest of this class: the assignment reaches clients
    /// through NetworkBoxManager's synced list, and letting each client pick its own box would
    /// put the part in a different crate in every game.
    public GameObject PlaceExtraPart(PartDefinition part)
    {
        if (part == null) return null;
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
                                             && !NetworkManager.Singleton.IsServer) return null;

        // Free means carrying nothing yet — a box already holding an order part is off limits,
        // or the extra copy would replace the very thing it is meant not to disturb.
        var free = new List<GameObject>();
        foreach (var go in _boxPool)
            if (go != null && go.GetComponent<BoxWithPart>() == null) free.Add(go);

        if (free.Count == 0)
        {
            Debug.LogWarning($"[PartSpawner] No free box for an extra '{part.PartName}'.");
            return null;
        }

        var box = free[Random.Range(0, free.Count)];
        EnsureNetworkSpawned(box);
        Assign(box, part, required: false);
        NetworkBoxManager.Instance?.SyncAssignments();
        return box;
    }

    /// Takes back an extra copy nobody collected. Refuses to touch a box the order needs, so a
    /// mistimed cleanup can never strip the warehouse of a required part.
    public void RemoveExtraPart(GameObject box)
    {
        if (box == null) return;

        var bwp = box.GetComponent<BoxWithPart>();
        if (bwp == null || bwp.IsRequired) return;

        // Read before the component goes: the id comes off the box's own NetworkObject, which
        // outlives it, but taking it first keeps the two lines from depending on that.
        var phys  = box.GetComponent<BoxPhysicsInteractable>();
        ulong netId = phys != null ? phys.NetBoxId : 0UL;

        _assigned.Remove(bwp);
        Destroy(bwp);

        NetworkBoxManager.Instance?.ClearBoxAssignment(netId);
        NetworkBoxManager.Instance?.SyncAssignments();
    }

    // Exposed so NetworkBoxManager can set up physics on client-side boxes
    public static void EnsureBoxPhysics(GameObject box) => EnsureBoxRigidbody(box);

    static void EnsureNetworkSpawned(GameObject box)
    {
        var netObj = box.GetComponent<NetworkObject>();
        if (netObj == null || netObj.IsSpawned) return;
        // In-scene placed NetworkObjects are spawned automatically by NGO — skip them.
        // Only runtime-instantiated NetworkObjects (IsSceneObject=false) need explicit Spawn().
        if (netObj.IsSceneObject == true) return;
        netObj.Spawn(destroyWithScene: true);
        Debug.Log($"[PartSpawner] NetworkObject.Spawn() called on '{box.name}'.");
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void ClearAssigned()
    {
        foreach (var bwp in _assigned)
        {
            if (bwp == null) continue;
            var phys = bwp.GetComponent<BoxPhysicsInteractable>();
            if (phys != null) phys.enabled = true;
            // Keep the component as an empty reusable tag. Destroy is deferred until end-of-frame
            // and could otherwise delete a new assignment placed on this box in the same frame.
            bwp.Setup(null, false);
        }
        _assigned.Clear();
    }

    /// Schedules recovery for a required crate that disappeared before being installed.
    public void RequestReplacement(PartDefinition part)
    {
        if (part == null || !isActiveAndEnabled) return;
        StartCoroutine(ReplaceMissingNextFrame(part));
    }

    System.Collections.IEnumerator ReplaceMissingNextFrame(PartDefinition part)
    {
        yield return null; // let installation/cleanup update the bench first

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
                                             && !NetworkManager.Singleton.IsServer) yield break;
        if (AssemblyBench.Instance?.IsPartNeeded(part) != true) yield break;

        // A second copy may already exist (two losses in one frame can schedule two checks).
        foreach (var existing in BoxWithPart.All)
            if (existing != null && existing.IsRequired && existing.Part == part) yield break;

        _boxPool = BuildBoxPool();
        GameObject replacement = _boxPool.FirstOrDefault(go =>
            go != null && go.GetComponent<BoxWithPart>() == null);
        if (replacement == null)
            replacement = _highPool.FirstOrDefault(go =>
                go != null && go.GetComponent<BoxWithPart>() == null);

        if (replacement == null)
        {
            Debug.LogError($"[PartSpawner] Cannot replace lost required part '{part.PartName}': " +
                           "no free box remains.");
            yield break;
        }

        Assign(replacement, part, required: true);
        NetworkBoxManager.Instance?.SyncAssignments();
        HUDController.Instance?.AddNotification(
            "Replacement part received",
            $"A new {part.PartName} crate has been placed in the warehouse",
            HUDController.NotifType.Warning);
    }

    private static bool IsInTrailerPallets(Transform t)
    {
        for (var p = t.parent; p != null; p = p.parent)
            if (p.name == "TrailerPallets") return true;
        return false;
    }

    private static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
