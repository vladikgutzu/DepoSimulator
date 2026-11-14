using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

/// Attach to the cart root alongside CartController.
/// Boxes track _stackBase via BoxPhysicsInteractable.BeginFollow (not Transform.SetParent —
/// these boxes carry a NetworkObject, and Netcode only lets the server re-parent one; a
/// client doing it gets silently reverted with an "Only the server can re-parent
/// NetworkObjects" error) and are stacked by real bounds height.
/// Player looks at the cart body to PLACE a held box.
/// Player looks at a specific box on the cart to TAKE that box (any position).
///
/// Multiplayer: placement and removal are broadcast via NetworkPhysBoxManager. Because
/// follow is re-derived every LateUpdate from the cart's live transform (itself kept in
/// sync by NetworkVehicleSync), stored boxes track a moving cart correctly for every client.
[RequireComponent(typeof(CartController))]
public class CartStorageInteractable : MonoBehaviour, IInteractable, ICanInteractCheck
{
    [SerializeField] Transform _stackBase;  // empty child on the cart platform surface
    [SerializeField] int _maxBoxes = 3;
    [SerializeField, Tooltip("Additional local offset for PackedOrderBox. Positive Z moves the finished order away from the handle.")]
    Vector3 _packedOrderOffset = new(0f, 0f, 0.45f);

    readonly List<BoxPhysicsInteractable> _stored = new();
    Collider[] _cartColliders;

    // Exposed for NetworkPhysBoxManager so it can parent boxes on non-placing clients.
    public Transform StackBase => _stackBase;
    public int Capacity => _maxBoxes;

    public bool IsFull  => _stored.Count >= _maxBoxes;
    public bool IsEmpty => _stored.Count == 0;

    void Awake()
    {
        // Cache before the player is parented to the cart so only permanent cart colliders are
        // included. Stored cargo remains selectable, but cannot physically fight its carrier.
        _cartColliders = GetComponentsInChildren<Collider>(true);
    }

    // ── IInteractable ─────────────────────────────────────────────────────────────

    public string InteractPrompt
    {
        get
        {
            if (BoxPhysicsInteractable.GrabbedBox == null) return string.Empty;
            return IsFull
                ? $"Cart full ({_stored.Count}/{_maxBoxes})"
                : $"Load onto cart ({_stored.Count}/{_maxBoxes})  {InputHints.E}";
        }
    }

    public bool CanInteract(GameObject interactor)
        => BoxPhysicsInteractable.GrabbedBox != null && !IsFull;

    public void Interact(GameObject interactor)
    {
        var held = BoxPhysicsInteractable.GrabbedBox;
        if (held == null || IsFull) return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            var netObj = GetComponent<Unity.Netcode.NetworkObject>();
            if (netObj != null && netObj.IsSpawned)
                NetworkPhysBoxManager.Instance?.RequestPlaceBoxOnCartServerRpc(
                    held.NetBoxId, held.NetVersion, netObj.NetworkObjectId);
            return;
        }

        PlaceBox(held);
    }

    // ── Called by BoxPhysicsInteractable when player looks at a stored box ────

    public void TakeBox(BoxPhysicsInteractable box, GameObject taker)
    {
        int idx = _stored.IndexOf(box);
        if (idx < 0) return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            var netObj = GetComponent<Unity.Netcode.NetworkObject>();
            var manager = NetworkPhysBoxManager.Instance;
            if (netObj != null && netObj.IsSpawned && manager != null &&
                manager.TryGetCartCargoVersion(box.NetBoxId, out uint version))
                manager.RequestTakeBoxFromCartServerRpc(
                    box.NetBoxId, version, netObj.NetworkObjectId);
            return;
        }

        _stored.RemoveAt(idx);
        box.StoredInCart = null;
        box.EndFollow();
        SetCartBoxCollisionIgnored(box, false);

        // Restack boxes that were above the one removed
        RestackFrom(idx);

        // Hand directly to player in solo mode.
        box.Interact(taker);
    }

    // ── Internal ──────────────────────────────────────────────────────────────────

    void PlaceBox(BoxPhysicsInteractable box)
    {
        // Transfer from player hands — colliders stay OFF until box is positioned
        // to avoid the box briefly overlapping the player at carry position and
        // pushing the CharacterController upward.
        box.PlaceOnCart(this);

        _stored.Add(box);
        box.StoredInCart = this;

        PositionBox(box, _stored.Count - 1);

        SetCartBoxCollisionIgnored(box, true);

        // Enable colliders now that the box is at its final cart position.
        box.EnableCollidersOnCart();

        // Lock physics; box follows the cart transform via BeginFollow (set by PositionBox).
        var rb = box.GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }

        AudioManager.Instance?.PlayReturnPart();

    }

    void PositionBox(BoxPhysicsInteractable box, int index)
    {
        Vector3 localPosition = CalculateLocalPosition(box, index);
        box.BeginFollow(_stackBase, localPosition, Quaternion.identity);
    }

    Vector3 CalculateLocalPosition(BoxPhysicsInteractable box, int index)
    {
        float y = 0f;
        for (int i = 0; i < index; i++)
            y += GetBoxHeight(_stored[i]);

        // bottomOffset = distance from box's transform origin to its lowest rendered point
        // (world Y, measured at current position — constant regardless of where the box is).
        // Subtracting it aligns the bottom face with StackBase + accumulated stack height,
        // regardless of where the mesh pivot sits (bottom, center, or anywhere else).
        float bottomOffset = GetBottomOffset(box);
        Vector3 localPosition = new(0f, y - bottomOffset, 0f);
        if (box.GetComponent<PackedOrderBox>() != null)
            localPosition += _packedOrderOffset;
        return localPosition;
    }

    /// Server computes the only accepted slot and pose. Clients never submit arbitrary offsets.
    public bool TryGetAuthoritativePlacement(BoxPhysicsInteractable box, out int slotIndex,
        out Vector3 localPos, out Quaternion localRot)
    {
        slotIndex = _stored.Count;
        localPos = default;
        localRot = Quaternion.identity;
        if (box == null || _stackBase == null || IsFull || _stored.Contains(box)) return false;
        localPos = CalculateLocalPosition(box, slotIndex);
        return true;
    }

    void RestackFrom(int startIndex)
    {
        for (int i = startIndex; i < _stored.Count; i++)
            PositionBox(_stored[i], i);
    }

    // Returns the signed offset from the box pivot to the bottom of its visual bounds
    // in world Y (negative = bottom is below pivot, zero = pivot IS at the bottom).
    static float GetBottomOffset(BoxPhysicsInteractable box)
    {
        var rends = box.GetComponentsInChildren<Renderer>(false);
        if (rends.Length == 0) return -0.25f;   // assume centered pivot, 0.5m fallback height
        var b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        return b.min.y - box.transform.position.y;
    }

    static float GetBoxHeight(BoxPhysicsInteractable box)
    {
        var rends = box.GetComponentsInChildren<Renderer>(false);
        if (rends.Length == 0) return 0.5f;
        var b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        return b.size.y;
    }

    // ── Multiplayer remote sync ────────────────────────────────────────────────────

    /// Called on non-placing clients to parent a box to this cart at the given local pose.
    public void SyncBoxPlaced(BoxPhysicsInteractable box, int slotIndex,
                              Vector3 localPos, Quaternion localRot)
    {
        if (box.IsLocallyGrabbed) box.SilentRelease();
        if (box.IsRemotelyGrabbed) box.SetRemoteGrabbed(false, box.RemoteGrabberId);
        if (box.StoredOnPallet != null)
            box.StoredOnPallet.ApplyRemoteCargoDeparture(box);

        int current = _stored.IndexOf(box);
        if (current >= 0) _stored.RemoveAt(current);
        int slot = Mathf.Clamp(slotIndex, 0, _stored.Count);
        _stored.Insert(slot, box);
        box.StoredInCart = this;
        box.BeginFollow(_stackBase, localPos, localRot);
        SetCartBoxCollisionIgnored(box, true);
        var rb = box.GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }
        // Re-enable colliders (they were disabled during grab)
        foreach (var col in box.GetComponentsInChildren<Collider>())
            { col.enabled = true; col.isTrigger = false; }
    }

    public int GetStoredIndex(BoxPhysicsInteractable box) => _stored.IndexOf(box);

    /// Called on non-grabbing clients when a box is taken from this cart by another player.
    public void SyncBoxRemoved(BoxPhysicsInteractable box)
    {
        int idx = _stored.IndexOf(box);
        if (idx < 0) return;
        _stored.RemoveAt(idx);
        box.StoredInCart = null;
        box.EndFollow();
        SetCartBoxCollisionIgnored(box, false);
        RestackFrom(idx);
    }

    void SetCartBoxCollisionIgnored(BoxPhysicsInteractable box, bool ignored)
    {
        if (box == null || _cartColliders == null) return;
        var boxColliders = box.GetComponentsInChildren<Collider>(true);
        foreach (var cartCollider in _cartColliders)
        {
            if (cartCollider == null) continue;
            foreach (var boxCollider in boxColliders)
                if (boxCollider != null && boxCollider != cartCollider)
                    Physics.IgnoreCollision(cartCollider, boxCollider, ignored);
        }
    }

    void OnDestroy()
    {
        foreach (var box in _stored)
            SetCartBoxCollisionIgnored(box, false);
    }

    // ── Editor helper ─────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    [ContextMenu("Create Stack Base")]
    void CreateStackBase()
    {
        var go = new GameObject("StackBase");
        go.transform.SetParent(transform);
        go.transform.localRotation = Quaternion.identity;
        // Place on top of the cart platform — adjust Y in Inspector as needed
        go.transform.localPosition = new Vector3(0f, 0.55f, 0f);
        _stackBase = go.transform;
        UnityEditor.EditorUtility.SetDirty(this);
    }
#endif
}
