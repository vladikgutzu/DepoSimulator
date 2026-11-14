using System.Collections.Generic;
using UnityEngine;

/// Shipping zone that relocates to a random gate's steel_floor on each new order.
/// Builds its own visual marker (floor plate + beacon) at runtime.
public class ShippingZone : MonoBehaviour, IInteractable
{
    public static ShippingZone Instance { get; private set; }

    [Header("Collider")]
    [SerializeField] private Vector3 _colSize   = new Vector3(3.5f, 2.5f, 2.0f);
    [SerializeField] private Vector3 _colCenter = new Vector3(0f,   1.25f, 1.0f);

    [Header("Visuals")]
    // Same hue as the standard required-part highlight (GoldHDR ratio 8 : 6.4 : 0.4).
    [SerializeField] private Color _activeColor  = new Color(1.00f, 0.80f, 0.05f);
    [SerializeField] private Color _doneColor    = new Color(0.22f, 0.85f, 0.30f);
    [SerializeField] private float _beaconHeight = 3.5f;

    // ── IInteractable ──────────────────────────────────────────────────────────
    public string InteractPrompt
    {
        get
        {
            if (GameManager.Instance?.ActiveOrder == null || !_zoneActive) return "Shipping zone";
            var carried = BoxPhysicsInteractable.GrabbedBox?.GetComponent<PackedOrderBox>();
            if (carried != null) return $"Deliver order!  {InputHints.E}";
            return "Bring the packed box from the bench";
        }
    }

    // ── State ──────────────────────────────────────────────────────────────────
    Transform[] _gateFloors;
    int         _lastGateIndex = -1;
    // Whether the zone has already been placed for the order in progress. Lets whoever asks first
    // — this component's own OnOrderAccepted, or CoopGameManager broadcasting the gate — do the
    // roll, and the other just read the result, whichever order the two event handlers run in.
    bool        _placedForOrder;
    bool        _zoneActive;
    bool        _playerInZone;

    public int LastGateIndex => _lastGateIndex;

    public bool IsPlayerInZone       => _playerInZone;
    public bool IsActiveAndPlayerInZone => _zoneActive && _playerInZone;

    BoxCollider _col;
    GameObject  _markerRoot;
    GameObject  _arrowRoot;
    Renderer[]  _beaconRenderers;
    Renderer    _plateRenderer;
    GameObject  _eventMarkerRoot;
    GameObject  _eventArrowRoot;
    Renderer[]  _eventBeaconRenderers;
    Renderer    _eventPlateRenderer;
    string      _eventOwner;
    Color       _eventColor = Color.white;
    int         _eventGateIndex = -1;
    float       _bobTime;

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        // The training scene used to serialize a cyan override. Normal deliveries now share the
        // exact gold hue used by required-part boxes, so enforce it consistently in every scene.
        _activeColor = new Color(1.00f, 0.80f, 0.05f, 1f);
        SetupCollider();
        BuildVisual();
        BuildEventVisual();

        _gateFloors = FindAllSteelFloors();
        if (_gateFloors.Length == 0)
            Debug.LogWarning("[ShippingZone] No steel_floor objects found — zone will not relocate.");

        var gm = GameManager.Instance;
        if (gm != null)
        {
            gm.OnOrderAccepted  += OnOrderAccepted;
            gm.OnOrderCompleted += OnOrderDone;
            gm.OnOrderExpired   += OnOrderDone;
        }

        SetVisible(false);
    }

    private void OnDestroy()
    {
        if (_eventMarkerRoot != null) Destroy(_eventMarkerRoot);
        if (Instance == this) Instance = null;
        var gm = GameManager.Instance;
        if (gm == null) return;
        gm.OnOrderAccepted  -= OnOrderAccepted;
        gm.OnOrderCompleted -= OnOrderDone;
        gm.OnOrderExpired   -= OnOrderDone;
    }

    // ── Trigger proximity ──────────────────────────────────────────────────────
    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            _playerInZone = true;
            return;
        }

        if (!_zoneActive) return;

        // Packed order box dropped/thrown into the zone — auto-submit.
        // Colliders are disabled while the box is held, so this only fires
        // when the box is physically in the zone and not carried by the player.
        var packedBox = other.GetComponentInParent<PackedOrderBox>();
        if (packedBox == null) return;

        var phys = packedBox.GetComponent<BoxPhysicsInteractable>();
        if (phys == null || BoxPhysicsInteractable.GrabbedBox == phys) return;

        bool isMP = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;
        if (isMP)
        {
            // Trigger physics is authoritative only on the server. Clients do not destroy or
            // complete anything from their independently simulated overlap.
            if (Unity.Netcode.NetworkManager.Singleton.IsServer)
                CoopGameManager.Instance?.TryDeliverDroppedPackedOrder(phys.NetBoxId);
            return;
        }

        SetMarkerColor(_doneColor);
        _playerInZone = false;
        Object.Destroy(phys.gameObject);
        GameManager.Instance?.CompleteOrder();
    }

    void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        _playerInZone = false;
    }

    // Called by PlayerInteraction when E is pressed while in zone.
    public void TrySubmitOrder()
    {
        if (!_zoneActive) return;

        var grabbedBox = BoxPhysicsInteractable.GrabbedBox;
        if (grabbedBox == null || grabbedBox.GetComponent<PackedOrderBox>() == null)
        {
            HUDController.Instance?.AddNotification(
                "Delivery zone",
                "Bring the packed box from the bench",
                HUDController.NotifType.Warning);
            return;
        }

        grabbedBox.SilentRelease();
        SetMarkerColor(_doneColor);
        Object.Destroy(grabbedBox.gameObject);
        GameManager.Instance?.CompleteOrder();
    }

    // ── IInteractable ──────────────────────────────────────────────────────────
    public void Interact(GameObject interactor)
    {
        if (Unity.Netcode.NetworkManager.Singleton != null
            && Unity.Netcode.NetworkManager.Singleton.IsListening)
        {
            // Multiplayer: physical packed box required, same as solo.
            var grabbedBox = BoxPhysicsInteractable.GrabbedBox;
            if (grabbedBox == null || grabbedBox.GetComponent<PackedOrderBox>() == null)
            {
                HUDController.Instance?.AddNotification(
                    "Delivery zone",
                    "Bring the packed box from the bench",
                    HUDController.NotifType.Warning);
                return;
            }
            ulong netBoxId = grabbedBox.NetBoxId;
            CoopGameManager.Instance?.DeliverPackedOrderServerRpc(netBoxId, grabbedBox.NetVersion);
            return;
        }
        TrySubmitOrder();
    }

    /// Server-side spatial validation for a carried delivery. Bounds.SqrDistance is zero while
    /// inside and still permits a small margin for the player's root standing at the plate edge.
    public bool ServerCanAcceptDelivery(Vector3 playerPosition) =>
        _zoneActive && _col != null && _col.bounds.SqrDistance(playerPosition) <= 4f;

    public bool ServerContainsDeliveryBox(Vector3 boxPosition) =>
        _zoneActive && _col != null && _col.bounds.SqrDistance(boxPosition) <= 1f;

    /// Positions the zone at a specific gate index (for client sync from server).
    public void SetGateByIndex(int idx)
    {
        if (_gateFloors == null || idx < 0 || idx >= _gateFloors.Length) return;
        _lastGateIndex  = idx;
        _placedForOrder = true;
        var floor = _gateFloors[idx];
        transform.SetPositionAndRotation(floor.position, floor.rotation);
    }

    // ── Gate discovery ─────────────────────────────────────────────────────────
    // Only gates where gate_01_GRP.localPosition.y ≈ -1.604 are floor-level
    // shipping gates. Gates with y ≈ -4.57 are raised/recessed and unusable.
    // Hierarchy: wall_gate_GRP_prefab → wall_gate_GRP (outer) → wall_gate_GRP (inner)
    //                                                                ├─ decal_floor  ← anchor
    //                                                                └─ main_wall → gate_01_GRP
    const float GateFloorLocalY = -1.604f;

    /// Sorted, because the index into this array is what the server sends. FindObjectsByType has
    /// no defined order, and two machines loading the same scene do not agree on it — the server's
    /// "gate 3" was a different doorway on a client, so the shipping zone stood at one gate for the
    /// host and at another for everyone else.
    static Transform[] FindAllSteelFloors()
    {
        var result = new List<Transform>();
        foreach (var t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (t.name != "gate_01_GRP") continue;
            if (Mathf.Abs(t.localPosition.y - GateFloorLocalY) > 0.01f) continue;

            // gate_01_GRP → main_wall → wall_gate_GRP (inner) — decal_floor is sibling of main_wall
            var innerGRP = t.parent?.parent;
            if (innerGRP == null || innerGRP.name != "wall_gate_GRP") continue;

            var decal = innerGRP.Find("decal_floor");
            if (decal != null) result.Add(decal);
        }
        SceneNetIds.SortDeterministically(result);
        return result.ToArray();
    }

    // ── Zone placement ─────────────────────────────────────────────────────────
    void OnOrderAccepted(OrderDefinition _)
    {
        _playerInZone = false;

        // In a session a client never rolls its own gate: the server's choice arrives through
        // CoopGameManager (RPC and ShippingGateIndex) and is applied with SetGateByIndex.
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm != null && nm.IsListening && !nm.IsServer) return;

        PlaceForNewOrder();
        // Zone is pre-positioned but stays hidden until order is packed
    }

    /// Picks the gate for the order just accepted and moves the zone there, once per order — a
    /// second call returns the gate already chosen. Returns -1 when there are no gates.
    public int PlaceForNewOrder()
    {
        if (_placedForOrder) return _lastGateIndex;
        if (_gateFloors == null || _gateFloors.Length == 0) return -1;

        // An event destination is a separate live zone even though it uses the same gate pool.
        // Never place a newly accepted regular order on top of it. Prefer a gate different from
        // the previous regular-order gate as well, but allow reuse of the previous gate when it
        // is the only free one.
        var candidates = new List<int>(_gateFloors.Length);
        for (int i = 0; i < _gateFloors.Length; i++)
            if (i != _eventGateIndex && i != _lastGateIndex) candidates.Add(i);

        if (candidates.Count == 0)
            for (int i = 0; i < _gateFloors.Length; i++)
                if (i != _eventGateIndex) candidates.Add(i);

        if (candidates.Count == 0) return -1;
        int idx = candidates[Random.Range(0, candidates.Count)];

        SetGateByIndex(idx);
        return idx;
    }

    void OnOrderDone(OrderDefinition _)
    {
        _placedForOrder = false;
        _playerInZone = false;
        _zoneActive = false;
        RefreshMarkerVisual();
    }

    void Update()
    {
        if ((!_zoneActive || _arrowRoot == null)
            && (_eventMarkerRoot == null || !_eventMarkerRoot.activeSelf)) return;
        _bobTime += Time.deltaTime;
        float bob = Mathf.Sin(_bobTime * 2.5f) * 0.15f;
        if (_zoneActive && _arrowRoot != null)
        {
            _arrowRoot.transform.localRotation = Quaternion.Euler(0f, _bobTime * 90f, 0f);
            _arrowRoot.transform.localPosition = new Vector3(0f, bob, 0f);
        }
        if (_eventMarkerRoot != null && _eventMarkerRoot.activeSelf && _eventArrowRoot != null)
        {
            _eventArrowRoot.transform.localRotation = Quaternion.Euler(0f, _bobTime * 90f, 0f);
            _eventArrowRoot.transform.localPosition = new Vector3(0f, bob, 0f);
        }
    }

    public void FlashForPickup()
    {
        _zoneActive = true;
        RefreshMarkerVisual();
        HUDController.Instance?.AddNotification(
            "Shipping zone",
            "Carry the box to the glowing beacon by the gate!",
            HUDController.NotifType.Warning);
    }

    /// Chooses an event gate from the same usable warehouse gates as normal orders, but never the
    /// gate occupied by an active regular order. The server includes this index in the event
    /// payload so all peers display the destination at the same gate.
    public int PickEventGateIndex()
    {
        if (_gateFloors == null || _gateFloors.Length == 0) return -1;
        var candidates = new List<int>(_gateFloors.Length);
        bool regularOrderActive = GameManager.Instance?.ActiveOrder != null;
        for (int i = 0; i < _gateFloors.Length; i++)
            if (!regularOrderActive || i != _lastGateIndex) candidates.Add(i);
        if (candidates.Count == 0) return -1;
        return candidates[Random.Range(0, candidates.Count)];
    }

    /// Shows an independent event destination. It does not move, recolour, or enable interaction
    /// on the regular packed-order zone.
    public int ShowEventTarget(string eventName, Color color, int gateIndex = -1)
    {
        if (string.IsNullOrEmpty(eventName) || _eventMarkerRoot == null) return -1;
        if (!string.IsNullOrEmpty(_eventOwner) && _eventOwner != eventName) return -1;

        if (gateIndex < 0) gateIndex = PickEventGateIndex();
        if (_gateFloors == null || gateIndex < 0 || gateIndex >= _gateFloors.Length) return -1;

        _eventOwner = eventName;
        _eventColor = color;
        _eventGateIndex = gateIndex;
        var floor = _gateFloors[gateIndex];
        _eventMarkerRoot.transform.SetPositionAndRotation(floor.position, floor.rotation);
        SetEventMarkerColor(color);
        _eventMarkerRoot.SetActive(true);
        return gateIndex;
    }

    public void HideEventTarget(string eventName)
    {
        if (string.IsNullOrEmpty(eventName) || _eventOwner != eventName) return;
        _eventOwner = null;
        _eventGateIndex = -1;
        if (_eventMarkerRoot != null) _eventMarkerRoot.SetActive(false);
    }

    public bool TryGetEventDeliveryPosition(string eventName, out Vector3 position)
    {
        bool valid = _eventOwner == eventName && _eventMarkerRoot != null
                  && _eventMarkerRoot.activeSelf && _eventGateIndex >= 0;
        position = valid ? _eventMarkerRoot.transform.position : Vector3.zero;
        return valid;
    }

    /// Current independent event destination for HUD systems such as the minimap. Unlike
    /// TryGetEventDeliveryPosition this intentionally does not require the caller to already know
    /// which event owns the marker; the owner and its colour are returned with the position.
    public bool TryGetActiveEventTarget(out Vector3 position, out Color color, out string owner)
    {
        bool valid = !string.IsNullOrEmpty(_eventOwner) && _eventMarkerRoot != null
                  && _eventMarkerRoot.activeSelf && _eventGateIndex >= 0;
        position = valid ? _eventMarkerRoot.transform.position : Vector3.zero;
        color    = valid ? _eventColor : Color.clear;
        owner    = valid ? _eventOwner : null;
        return valid;
    }

    public int EventGateIndex => _eventGateIndex;

    void RefreshMarkerVisual()
    {
        if (_zoneActive)
        {
            SetMarkerColor(_activeColor);
            SetVisible(true);
        }
        else
        {
            SetVisible(false);
        }
    }

    // ── Collider setup ─────────────────────────────────────────────────────────
    void SetupCollider()
    {
        _col = GetComponent<BoxCollider>();
        if (_col == null) _col = gameObject.AddComponent<BoxCollider>();
        _col.isTrigger = true;
        _col.size      = _colSize;
        _col.center    = _colCenter;
    }

    // ── Visual construction ────────────────────────────────────────────────────
    void BuildVisual()
    {
        _markerRoot = new GameObject("ShippingZoneVisual");
        _markerRoot.transform.SetParent(transform, false);

        // Floor plate — flat quad covering the gate bay
        var plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
        plate.name = "zone_plate";
        plate.transform.SetParent(_markerRoot.transform, false);
        plate.transform.localPosition = new Vector3(1.0f, 0.01f, 0f);
        plate.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        plate.transform.localScale    = new Vector3(3.2f, 2.8f, 1f);
        Object.Destroy(plate.GetComponent<Collider>());
        _plateRenderer = plate.GetComponent<Renderer>();
        ApplyZoneMaterial(_plateRenderer, _activeColor, alpha: 0.45f);

        // Beacon post — thin vertical stripe, visible from distance
        var beaconRenderers = new List<Renderer>();

        var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        post.name = "zone_beacon_post";
        post.transform.SetParent(_markerRoot.transform, false);
        post.transform.localPosition = new Vector3(0f, _beaconHeight * 0.5f, 0f);
        post.transform.localScale    = new Vector3(0.08f, _beaconHeight * 0.5f, 0.08f);
        Object.Destroy(post.GetComponent<Collider>());
        var postR = post.GetComponent<Renderer>();
        ApplyZoneMaterial(postR, _activeColor, alpha: 0.85f);
        beaconRenderers.Add(postR);

        // Arrow chevron — three flat boxes stacked, in a rotating root
        _arrowRoot = new GameObject("zone_arrow_root");
        _arrowRoot.transform.SetParent(_markerRoot.transform, false);
        _arrowRoot.transform.localPosition = new Vector3(0f, _beaconHeight + 1.5f, 0f);

        for (int i = 0; i < 3; i++)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = $"zone_arrow_{i}";
            bar.transform.SetParent(_arrowRoot.transform, false);
            float w = 1.1f - i * 0.3f;
            bar.transform.localPosition = new Vector3(0f, -i * 0.32f, 0f);
            bar.transform.localScale    = new Vector3(w, 0.20f, 0.20f);
            bar.transform.localRotation = Quaternion.identity;
            Object.Destroy(bar.GetComponent<Collider>());
            var barR = bar.GetComponent<Renderer>();
            ApplyZoneMaterial(barR, _activeColor, alpha: 0.95f);
            beaconRenderers.Add(barR);
        }

        _beaconRenderers = beaconRenderers.ToArray();
    }

    void BuildEventVisual()
    {
        _eventMarkerRoot = Instantiate(_markerRoot);
        _eventMarkerRoot.name = "EventShippingZoneVisual";
        _eventMarkerRoot.transform.SetParent(null, true);
        var plate = _eventMarkerRoot.transform.Find("zone_plate");
        _eventPlateRenderer = plate != null ? plate.GetComponent<Renderer>() : null;
        var arrow = _eventMarkerRoot.transform.Find("zone_arrow_root");
        _eventArrowRoot = arrow != null ? arrow.gameObject : null;

        var renderers = new List<Renderer>();
        foreach (var renderer in _eventMarkerRoot.GetComponentsInChildren<Renderer>(true))
            if (renderer != _eventPlateRenderer) renderers.Add(renderer);
        _eventBeaconRenderers = renderers.ToArray();
        _eventMarkerRoot.SetActive(false);
    }

    static void ApplyZoneMaterial(Renderer r, Color color, float alpha)
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"))
            { hideFlags = HideFlags.HideAndDontSave };

        // URP transparent surface
        mat.SetFloat("_Surface",  1f); // 1 = Transparent
        mat.SetFloat("_Blend",    0f); // Alpha
        mat.SetFloat("_AlphaClip", 0f);
        mat.renderQueue = 3000;
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

        var c = new Color(color.r, color.g, color.b, alpha);
        mat.SetColor("_BaseColor", c);

        // Emission for glow
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", color * 0.6f);

        r.sharedMaterial = mat;
    }

    void SetVisible(bool visible)
    {
        if (_markerRoot != null) _markerRoot.SetActive(visible);
        if (_col != null)        _col.enabled = visible;
    }

    void SetMarkerColor(Color color)
    {
        if (_plateRenderer  != null) ApplyZoneMaterial(_plateRenderer, color, 0.45f);
        if (_beaconRenderers != null)
        {
            foreach (var r in _beaconRenderers)
                if (r != null) ApplyZoneMaterial(r, color, 0.90f);
        }
    }

    void SetEventMarkerColor(Color color)
    {
        if (_eventPlateRenderer != null) ApplyZoneMaterial(_eventPlateRenderer, color, 0.45f);
        if (_eventBeaconRenderers == null) return;
        foreach (var renderer in _eventBeaconRenderers)
            if (renderer != null) ApplyZoneMaterial(renderer, color, 0.90f);
    }
}
