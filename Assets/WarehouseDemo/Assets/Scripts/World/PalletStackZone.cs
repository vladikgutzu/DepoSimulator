using UnityEngine;
using System.Collections.Generic;
using Unity.Netcode;

/// Universal physics-based pallet stacking zone.
/// Placement is fully dynamic: 4 downward raycasts per candidate XZ cell
/// find the current stack surface height, the lowest valid position wins.
/// Any box size is accepted — large boxes naturally land on top of rows
/// that are too densely packed for them.
///
/// Multiplayer: stacking and picking are broadcast via NetworkPalletManager. Boxes track
/// this pallet's transform via BoxPhysicsInteractable.BeginFollow, not Transform.SetParent
/// (boxes carry a NetworkObject; Netcode only allows the server to re-parent one), so a
/// stacked box rides along correctly if the pallet is later lifted, on every client.
/// Single-player: all network code is gated on NetworkManager.Singleton?.IsListening.
public class PalletStackZone : MonoBehaviour, IInteractable, ICanInteractCheck
{
    [Header("Grid — gizmo preview and empty-pallet footprint")]
    [SerializeField] int     _columns  = 2;
    [SerializeField] int     _rows     = 2;
    [SerializeField] int     _layers   = 3;
    [SerializeField] Vector3 _cellSize = new(0.68f, 0.68f, 0.68f);
    [SerializeField] float   _surfaceY = 0.15f;
    [SerializeField] Vector3 _gridOrigin;

    // Local-space positions of the original pre-placed boxes.
    // Used only for footprint bounds + gizmo display.
    List<Vector3> _slotPositions = new();

    // ── Runtime tracking ─────────────────────────────────────────────────────────────

    readonly List<BoxPhysicsInteractable> _onPallet = new();

    /// Read-only view of what is currently stacked here, for systems that need to target
    /// pallet contents from the outside (ThiefController picks its next box off this).
    /// Detaching a box still goes through OnBoxPickedUp so network tracking stays correct.
    public IReadOnlyList<BoxPhysicsInteractable> BoxesOnPallet => _onPallet;

    static readonly Collider[] _overlapBuf = new Collider[8];

    // ── Global registry ─────────────────────────────────────────────────────────────

    public static readonly HashSet<PalletStackZone> All = new();
    void OnEnable()  => All.Add(this);
    void OnDisable() => All.Remove(this);

    // ── Init ────────────────────────────────────────────────────────────────────────

    void Start()
    {
        var boxes = GetComponentsInChildren<BoxPhysicsInteractable>(true);
        if (boxes.Length > 0)
            InitFromBoxes(boxes);
        // else: empty pallet — footprint comes from grid config (_columns etc.)
    }

    void InitFromBoxes(BoxPhysicsInteractable[] boxes)
    {
        var sorted = new List<BoxPhysicsInteractable>(boxes);
        sorted.Sort((a, b) =>
        {
            Vector3 la = transform.InverseTransformPoint(a.transform.position);
            Vector3 lb = transform.InverseTransformPoint(b.transform.position);
            if (Mathf.Abs(la.y - lb.y) > 0.12f) return la.y.CompareTo(lb.y);
            if (Mathf.Abs(la.z - lb.z) > 0.08f) return la.z.CompareTo(lb.z);
            return la.x.CompareTo(lb.x);
        });

        _slotPositions = new List<Vector3>(sorted.Count);
        foreach (var box in sorted)
            _slotPositions.Add(transform.InverseTransformPoint(box.transform.position));

        foreach (var box in sorted)
        {
            // Authored stacks are nested under the pallet only for convenient scene editing.
            // A Rigidbody must not stay under a kinematically moved pallet at runtime: otherwise
            // it inherits the pallet Transform and is also moved by PhysX, producing overlapping
            // colliders and large depenetration impulses in the truck.
            box.DetachFromParent();
            _onPallet.Add(box);
            box.StoredOnPallet = this;
        }

        DeriveGridFromSlots();
    }

    void DeriveGridFromSlots()
    {
        if (_slotPositions.Count == 0) return;
        var xC = Cluster(_slotPositions.ConvertAll(p => p.x), 0.28f);
        var zC = Cluster(_slotPositions.ConvertAll(p => p.z), 0.28f);
        var yC = Cluster(_slotPositions.ConvertAll(p => p.y), 0.16f);
        _columns = Mathf.Max(1, xC.Count);
        _rows    = Mathf.Max(1, zC.Count);
        _layers  = Mathf.Max(1, yC.Count);
        float cx = xC.Count > 1 ? (xC[xC.Count-1] - xC[0]) / (_columns - 1) : 0.5f;
        float cz = zC.Count > 1 ? (zC[zC.Count-1] - zC[0]) / (_rows    - 1) : 0.5f;
        float cy = yC.Count > 1 ? (yC[yC.Count-1] - yC[0]) / (_layers  - 1) : 0.5f;
        _cellSize   = new Vector3(Mathf.Max(0.2f, cx), Mathf.Max(0.15f, cy), Mathf.Max(0.2f, cz));
        _gridOrigin.x = xC.Count > 0 ? xC[0] - _cellSize.x * 0.5f : -_columns * _cellSize.x * 0.5f;
        _gridOrigin.z = zC.Count > 0 ? zC[0] - _cellSize.z * 0.5f : -_rows    * _cellSize.z * 0.5f;
        _gridOrigin.y = 0f;
        _surfaceY = (yC.Count > 0 ? yC[0] : 0f) - _cellSize.y * 0.5f;
    }

    // ── IInteractable ───────────────────────────────────────────────────────────────

    public string InteractPrompt
    {
        get
        {
            if (BoxPhysicsInteractable.GrabbedBox == null) return null;
            return $"Place on pallet  {InputHints.E}";
        }
    }

    public bool CanInteract(GameObject interactor)
        => BoxPhysicsInteractable.GrabbedBox != null;

    public void Interact(GameObject interactor)
    {
        var box = BoxPhysicsInteractable.GrabbedBox;
        if (box == null) return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            var pallet = GetComponent<PalletBehavior>() ?? GetComponentInParent<PalletBehavior>();
            if (pallet != null)
                NetworkPalletManager.Instance?.RequestStackHeldBoxServerRpc(
                    box.NetBoxId, box.NetVersion, pallet.NetPalletId);
            return;
        }

        Vector3 dropLocal = FindDynamicPlacement(box);
        box.PlaceOnPallet(this, transform, dropLocal, Quaternion.identity);
        _onPallet.Add(box);
        FreezeAllOnPallet();
    }

    // ── Auto-snap: free-flying box settled on this pallet ───────────────────────────

    public bool TryAutoSnapFreeBox(BoxPhysicsInteractable box)
    {
        if (box == null || box.StoredOnPallet != null) return false;
        if (!IsBoxOverPallet(box)) return false;

        bool multiplayer = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        if (multiplayer && !NetworkManager.Singleton.IsServer) return false;

        // Disable the box's own colliders before scanning for a surface — otherwise the
        // downward raycasts/overlap checks in FindDynamicPlacement can hit the box's own
        // (still mid-air, not-yet-settled) collider and report a false "surface" at its
        // current airborne height, leaving it floating above the real stack.
        // ApplyAutoSnap re-enables them right after.
        var ownCols = box.GetComponentsInChildren<Collider>(true);
        var wasEnabled = new bool[ownCols.Length];
        for (int i = 0; i < ownCols.Length; i++)
        {
            wasEnabled[i] = ownCols[i].enabled;
            ownCols[i].enabled = false;
        }

        Vector3 localPos;
        try
        {
            localPos = FindDynamicPlacement(box);
        }
        finally
        {
            for (int i = 0; i < ownCols.Length; i++)
                if (ownCols[i] != null) ownCols[i].enabled = wasEnabled[i];
        }

        if (multiplayer)
        {
            var pb = GetComponent<PalletBehavior>() ?? GetComponentInParent<PalletBehavior>();
            var palletManager = NetworkPalletManager.Instance;
            if (pb == null || palletManager == null ||
                !palletManager.ServerRegisterSettledCargo(
                    box.NetBoxId, pb.NetPalletId, localPos, Quaternion.identity))
            {
                return false;
            }
        }
        else
        {
            box.ApplyAutoSnap(this, transform, localPos);
            if (!_onPallet.Contains(box)) _onPallet.Add(box);
            FreezeAllOnPallet();
        }

        HUDController.Instance?.AddNotification(
            "Box on pallet!",
            "Box caught and placed",
            HUDController.NotifType.Success);
        return true;
    }

    void FreezeAllOnPallet() => StartCoroutine(TemporaryFreeze());

    System.Collections.IEnumerator TemporaryFreeze()
    {
        // Snapshot boxes that are currently dynamic
        var frozen = new System.Collections.Generic.List<Rigidbody>();
        foreach (var b in _onPallet)
        {
            if (b == null) continue;
            var rb = b.GetComponent<Rigidbody>();
            if (rb == null || rb.isKinematic) continue;
            rb.linearVelocity  = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic     = true;
            frozen.Add(rb);
        }

        yield return new UnityEngine.WaitForSeconds(0.2f);

        // Restore to dynamic
        foreach (var rb in frozen)
        {
            if (rb == null) continue;
            rb.isKinematic = false;
            rb.useGravity  = true;
        }
    }

    bool IsBoxOverPallet(BoxPhysicsInteractable box)
    {
        Vector3 local = transform.InverseTransformPoint(box.transform.position);
        GetPalletBoundsLocal(out float minX, out float maxX, out float minZ, out float maxZ);

        const float margin = 0.35f;
        if (local.x < minX - margin || local.x > maxX + margin) return false;
        if (local.z < minZ - margin || local.z > maxZ + margin) return false;

        float maxHeight = _surfaceY + _cellSize.y * (_layers + 2);
        return local.y >= _surfaceY - 0.3f && local.y <= maxHeight;
    }

    // ── Called by BoxPhysicsInteractable.Grab() ─────────────────────────────────────

    public void OnBoxPickedUp(BoxPhysicsInteractable box)
    {
        // Before the list loses it, and before the box has moved: whatever was stacked on top of
        // it has just lost the thing holding it up. This machine's own physics would work that out
        // by itself for boxes that are still dynamic, but not for the settled ones it has frozen
        // kinematic — and never for the copies on everybody else's machine, which is the half that
        // was actually visible. See Unsupport.
        WakeAbove(box, broadcastRest: true);

        _onPallet.Remove(box);

    }

    public void RequestTakeBox(BoxPhysicsInteractable box)
    {
        if (box == null) return;
        var pallet = GetComponent<PalletBehavior>() ?? GetComponentInParent<PalletBehavior>();
        var manager = NetworkPalletManager.Instance;
        if (pallet == null || manager == null ||
            !manager.TryGetCargoVersion(box.NetBoxId, out uint version))
            return;

        manager.RequestTakeBoxFromPalletServerRpc(
            box.NetBoxId, version, pallet.NetPalletId);
    }

    // ── Multiplayer: remote stacking ─────────────────────────────────────────────────

    /// Apply the server's persistent pallet cargo manifest on every peer.
    public void ApplyRemoteBoxStack(BoxPhysicsInteractable box, Vector3 localPos, Quaternion localRot)
    {
        box.ApplyRemoteStack(this, transform, localPos, localRot);
        if (!_onPallet.Contains(box))
            _onPallet.Add(box);
    }

    public Vector3 FindAuthoritativePlacement(BoxPhysicsInteractable box) =>
        FindDynamicPlacement(box);

    /// Remove a box after the authoritative cargo manifest row disappears.
    public void ApplyRemoteBoxRemove(BoxPhysicsInteractable box)
    {
        WakeAbove(box, broadcastRest: false);

        _onPallet.Remove(box);
        if (box.StoredOnPallet == this)
            box.StoredOnPallet = null;
        box.EndFollow();
    }

    /// True while the box still occupies the physical volume of this pallet. Slot displacement
    /// and rotation are checked by NetworkPalletManager against the authoritative manifest.
    public bool IsStillPalletCargo(BoxPhysicsInteractable box) =>
        box != null && box.StoredOnPallet == this && IsBoxOverPallet(box);

    /// Server-side transition from pallet cargo to world physics. This deliberately does not
    /// wake neighbours here; PhysX on the server decides which of them actually loses support.
    public void DetachAuthoritativeCargo(BoxPhysicsInteractable box)
    {
        if (box == null) return;
        _onPallet.Remove(box);
        if (box.StoredOnPallet == this) box.StoredOnPallet = null;
        box.EndFollow();
    }

    /// Client-side mirror of an authoritative manifest removal. The matching BoxNetState supplies
    /// the world pose and velocity; local stack simulation must not invent a different collapse.
    public void ApplyRemoteCargoDeparture(BoxPhysicsInteractable box)
    {
        if (box == null) return;
        _onPallet.Remove(box);
        if (box.StoredOnPallet == this) box.StoredOnPallet = null;
        box.EndFollow();
    }

    /// The boxes resting on the one that has just been taken off this pallet.
    ///
    /// Chosen by position rather than by bounds, deliberately. Message ordering can move a box
    /// toward its carrier before the pallet-removal message arrives, while its original origin
    /// still identifies the vacated column reliably. The pallet's own cell size says how wide
    /// that column is, so nothing here depends on which message landed first.
    ///
    /// Erring wide costs nothing: a box in the next column over is still supported, so waking it
    /// makes it settle back where it stands rather than fall.
    void WakeAbove(BoxPhysicsInteractable removed, bool broadcastRest)
    {
        var above = new List<BoxPhysicsInteractable>();
        CollectBoxesAbove(removed, above);

        foreach (var b in above) b.Unsupport(broadcastRest);
    }

    /// Returns every box that loses its last physical support with this carton. Collider bounds
    /// are used instead of transform centres so differently-sized delivery boxes are handled
    /// correctly and neighbouring stacks are not woken accidentally.
    public void CollectBoxesAbove(BoxPhysicsInteractable removed,
        List<BoxPhysicsInteractable> destination)
    {
        if (removed == null || destination == null) return;

        if (!TryGetWorldBounds(removed, out Bounds removedBounds)) return;

        var boxes = new List<BoxPhysicsInteractable>(_onPallet.Count);
        var boundsByBox = new Dictionary<BoxPhysicsInteractable, Bounds>(_onPallet.Count + 1)
        {
            [removed] = removedBounds
        };

        foreach (var other in _onPallet)
        {
            if (other == null || other == removed || boundsByBox.ContainsKey(other)) continue;
            if (!TryGetWorldBounds(other, out Bounds otherBounds)) continue;

            boxes.Add(other);
            boundsByBox.Add(other, otherBounds);
        }

        // Grow the unsupported set layer by layer. A candidate is released only when it
        // depended on a lost support and no box outside this set still supports it.
        var unsupported = new HashSet<BoxPhysicsInteractable> { removed };
        bool foundAnother;
        do
        {
            foundAnother = false;
            foreach (BoxPhysicsInteractable candidate in boxes)
            {
                if (unsupported.Contains(candidate)) continue;

                Bounds candidateBounds = boundsByBox[candidate];
                bool dependedOnLostSupport = false;
                foreach (BoxPhysicsInteractable lostSupport in unsupported)
                {
                    if (!BoundsProvideSupport(boundsByBox[lostSupport], candidateBounds)) continue;
                    dependedOnLostSupport = true;
                    break;
                }

                if (!dependedOnLostSupport) continue;

                bool hasRemainingSupport = false;
                foreach (BoxPhysicsInteractable possibleSupport in boxes)
                {
                    if (possibleSupport == candidate || unsupported.Contains(possibleSupport)) continue;
                    if (!BoundsProvideSupport(boundsByBox[possibleSupport], candidateBounds)) continue;
                    hasRemainingSupport = true;
                    break;
                }

                if (hasRemainingSupport) continue;

                unsupported.Add(candidate);
                foundAnother = true;
            }
        }
        while (foundAnother);

        foreach (BoxPhysicsInteractable box in boxes)
        {
            if (unsupported.Contains(box) && !destination.Contains(box)) destination.Add(box);
        }
    }

    static bool BoundsProvideSupport(Bounds lower, Bounds upper)
    {
        const float maxVerticalGap = 0.12f;
        const float maxVerticalPenetration = 0.08f;
        const float maxMinimumOverlap = 0.025f;

        float verticalGap = upper.min.y - lower.max.y;
        if (verticalGap < -maxVerticalPenetration || verticalGap > maxVerticalGap) return false;

        float overlapX = Mathf.Min(lower.max.x, upper.max.x) - Mathf.Max(lower.min.x, upper.min.x);
        float overlapZ = Mathf.Min(lower.max.z, upper.max.z) - Mathf.Max(lower.min.z, upper.min.z);
        float requiredX = Mathf.Min(maxMinimumOverlap, Mathf.Min(lower.size.x, upper.size.x) * 0.1f);
        float requiredZ = Mathf.Min(maxMinimumOverlap, Mathf.Min(lower.size.z, upper.size.z) * 0.1f);
        return overlapX >= requiredX && overlapZ >= requiredZ;
    }

    static bool TryGetWorldBounds(BoxPhysicsInteractable box, out Bounds bounds)
    {
        bounds = default;
        bool found = false;

        Collider[] colliders = box.GetComponentsInChildren<Collider>(true);
        foreach (Collider collider in colliders)
        {
            if (collider == null || !collider.enabled || collider.isTrigger ||
                !collider.gameObject.activeInHierarchy) continue;

            if (!found)
            {
                bounds = collider.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(collider.bounds);
            }
        }

        if (found) return true;

        Renderer[] renderers = box.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null || !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy) continue;

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return found;
    }

    // ── Dynamic placement ───────────────────────────────────────────────────────────
    //
    // Scans the pallet XZ area in a grid whose cell size matches the box footprint.
    // At each candidate (lx, lz) in local space, 4 corner rays are cast straight
    // down from above in world space to find the current stack surface.
    // The candidate with the LOWEST resulting placement Y is chosen — this fills
    // from the ground up and naturally handles mixed box sizes: a large box whose
    // footprint overlaps multiple occupied small-box slots will see a higher surface
    // and lose to an empty spot (or get StackAboveTop if nothing fits below).

    Vector3 FindDynamicPlacement(BoxPhysicsInteractable box)
    {
        // World-space half-extents of the box being placed.
        var rends = box.GetComponentsInChildren<Renderer>(false);
        if (rends.Length == 0) return StackAboveTop();

        var wb = rends[0].bounds;
        foreach (var r in rends) wb.Encapsulate(r.bounds);

        float bx = wb.extents.x;   // half-width  (world)
        float bz = wb.extents.z;   // half-depth  (world)
        float by = wb.extents.y;   // half-height (world)

        // Pallet search area in local space.
        GetPalletBoundsLocal(out float minLX, out float maxLX, out float minLZ, out float maxLZ);

        // Step = one box footprint in local space (correct for pallet scale).
        float sx    = Mathf.Max(Mathf.Abs(transform.lossyScale.x), 0.001f);
        float sz    = Mathf.Max(Mathf.Abs(transform.lossyScale.z), 0.001f);
        float stepX = Mathf.Max(bx * 2f / sx, 0.10f);
        float stepZ = Mathf.Max(bz * 2f / sz, 0.10f);

        float rayStartY   = transform.position.y + 6f;
        float minSurface  = transform.TransformPoint(new Vector3(0f, _surfaceY, 0f)).y;
        float maxRayDist  = rayStartY - minSurface + 2f;

        // Corner offsets in local space (80% of half-step).
        float ocx = stepX * 0.40f;
        float ocz = stepZ * 0.40f;

        // Validation probe: slightly smaller than actual box so touching neighbours
        // is OK but overlapping them is not.
        var checkHalf = new Vector3(bx * 0.85f, by * 0.85f, bz * 0.85f);

        float bestLocalY = float.MaxValue;
        Vector3 bestLocal = Vector3.zero;
        bool    found     = false;

        for (float lx = minLX + stepX * 0.5f; lx < maxLX + 0.001f; lx += stepX)
        {
            for (float lz = minLZ + stepZ * 0.5f; lz < maxLZ + 0.001f; lz += stepZ)
            {
                // ── Step 1: find highest surface under this footprint via 4 corner rays ──
                float surfaceWorldY = minSurface;
                bool  anyHit        = false;

                for (int ci = 0; ci < 4; ci++)
                {
                    float ox = (ci & 1) == 0 ? -ocx :  ocx;
                    float oz = (ci & 2) == 0 ? -ocz :  ocz;
                    Vector3 wc  = transform.TransformPoint(new Vector3(lx + ox, 0f, lz + oz));
                    var     ray = new Ray(new Vector3(wc.x, rayStartY, wc.z), Vector3.down);
                    if (Physics.Raycast(ray, out var hit, maxRayDist,
                                        Physics.DefaultRaycastLayers,
                                        QueryTriggerInteraction.Ignore))
                    {
                        if (hit.point.y > surfaceWorldY) surfaceWorldY = hit.point.y;
                        anyHit = true;
                    }
                }

                if (!anyHit) continue;   // off pallet edge

                float placeWorldY = surfaceWorldY + by;

                // ── Step 2: validate — nothing occupies this exact volume ──────────────
                // Rays can fall through gaps between boxes and hit the floor,
                // giving a falsely low Y. OverlapBox at the computed position catches
                // any existing box that would be physically overlapped.
                // Carried boxes have disabled colliders → invisible to OverlapBox → fine.
                Vector3 wp = transform.TransformPoint(new Vector3(lx, 0f, lz));
                wp.y = placeWorldY;
                int n = Physics.OverlapBoxNonAlloc(wp, checkHalf, _overlapBuf,
                                                   transform.rotation,
                                                   Physics.DefaultRaycastLayers,
                                                   QueryTriggerInteraction.Ignore);
                bool blocked = false;
                for (int i = 0; i < n; i++)
                    if (_overlapBuf[i].GetComponentInParent<BoxPhysicsInteractable>() != null)
                    { blocked = true; break; }
                if (blocked) continue;

                // Valid placement — prefer lowest Y (fill from ground up).
                float localY = transform.InverseTransformPoint(wp).y;
                if (localY < bestLocalY)
                {
                    bestLocalY = localY;
                    bestLocal  = new Vector3(lx, localY, lz);
                    found = true;
                }
            }
        }

        return found ? bestLocal : StackAboveTop();
    }

    /// The spot on the edge of the stack nearest `from`, at floor height.
    ///
    /// Anyone walking up to a pallet has to aim at its edge rather than at its origin. The origin
    /// sits in the middle of a prop that is well over a metre across, so a raider told to get
    /// within arm's reach of it stops that much short of the boxes and swings at nothing — which
    /// is exactly what he was doing.
    public Vector3 ClosestEdgePoint(Vector3 from)
    {
        GetPalletBoundsLocal(out float minX, out float maxX, out float minZ, out float maxZ);

        Vector3 local = transform.InverseTransformPoint(from);
        local.x = Mathf.Clamp(local.x, minX, maxX);
        local.z = Mathf.Clamp(local.z, minZ, maxZ);
        local.y = _surfaceY;

        return transform.TransformPoint(local);
    }

    // Pallet footprint in local space — derived from pre-placed box positions,
    // or from the grid config when the pallet was empty at start.
    void GetPalletBoundsLocal(out float minX, out float maxX,
                               out float minZ, out float maxZ)
    {
        if (_slotPositions.Count > 0)
        {
            minX = float.MaxValue; maxX = float.MinValue;
            minZ = float.MaxValue; maxZ = float.MinValue;
            foreach (var s in _slotPositions)
            {
                if (s.x < minX) minX = s.x;  if (s.x > maxX) maxX = s.x;
                if (s.z < minZ) minZ = s.z;  if (s.z > maxZ) maxZ = s.z;
            }
            minX -= _cellSize.x * 0.5f;  maxX += _cellSize.x * 0.5f;
            minZ -= _cellSize.z * 0.5f;  maxZ += _cellSize.z * 0.5f;
        }
        else
        {
            minX = _gridOrigin.x;                        maxX = minX + _columns * _cellSize.x;
            minZ = _gridOrigin.z;                        maxZ = minZ + _rows    * _cellSize.z;
        }
    }

    Vector3 StackAboveTop()
    {
        float topWorldY = transform.TransformPoint(new Vector3(0f, _surfaceY, 0f)).y;
        foreach (var b in _onPallet)
            if (b != null)
                topWorldY = Mathf.Max(topWorldY, b.transform.position.y + _cellSize.y * 0.5f);
        float localY = transform.InverseTransformPoint(
                           new Vector3(transform.position.x, topWorldY, transform.position.z)).y
                       + _cellSize.y * 0.5f;
        return new Vector3(0f, localY, 0f);
    }

    // ── Grid helpers ─────────────────────────────────────────────────────────────────

    static List<float> Cluster(List<float> values, float gap)
    {
        values.Sort();
        var result = new List<float>();
        if (values.Count == 0) return result;
        float sum = values[0]; int n = 1; float prev = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] - prev > gap) { result.Add(sum / n); sum = values[i]; n = 1; }
            else                         { sum += values[i]; n++; }
            prev = values[i];
        }
        result.Add(sum / n);
        return result;
    }

    // ── Editor presets ───────────────────────────────────────────────────────────────

    [ContextMenu("Preset — Standard  (2×2)")]
    void SetupStandard()
    { _columns=2; _rows=2; _layers=3; _cellSize=new(0.68f,0.68f,0.68f); _surfaceY=0.15f; CenterGrid(); MarkDirty(); }

    [ContextMenu("Preset — Wide  (3×2)")]
    void SetupWide()
    { _columns=3; _rows=2; _layers=3; _cellSize=new(0.68f,0.68f,0.68f); _surfaceY=0.15f; CenterGrid(); MarkDirty(); }

    [ContextMenu("Preset — Large Pack  (3×3)")]
    void SetupLarge()
    { _columns=3; _rows=3; _layers=4; _cellSize=new(0.68f,0.68f,0.68f); _surfaceY=0.15f; CenterGrid(); MarkDirty(); }

    [ContextMenu("Recenter grid origin")]
    void CenterGrid()
    { _gridOrigin=new(-_columns*_cellSize.x*0.5f,0f,-_rows*_cellSize.z*0.5f); MarkDirty(); }

    void MarkDirty()
    {
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    // ── Gizmos ───────────────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        if (Application.isPlaying && _slotPositions.Count > 0)
        {
            Gizmos.color = new Color(0.1f, 0.9f, 0.3f, 0.5f);
            foreach (var s in _slotPositions)
                Gizmos.DrawWireCube(s, _cellSize * 0.5f);
        }
        else
        {
            int drawLayers = Mathf.Min(_layers, 4);
            for (int layer = 0; layer < drawLayers; layer++)
            for (int row   = 0; row   < _rows;      row++)
            for (int col   = 0; col   < _columns;   col++)
            {
                Gizmos.color = new Color(0.9f, 0.9f, 0.1f, (1f - layer * 0.2f) * 0.25f);
                var c = _gridOrigin + new Vector3(
                    (col+0.5f)*_cellSize.x, _surfaceY+(layer+0.5f)*_cellSize.y, (row+0.5f)*_cellSize.z);
                Gizmos.DrawWireCube(c, _cellSize * 0.92f);
            }
        }
    }
}
