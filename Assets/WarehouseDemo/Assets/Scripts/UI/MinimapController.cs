using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

/// Static overview minimap: shows the full scene, player marker moves around it.
/// All drawing is done via UI Toolkit VisualElements — no RenderTexture required.
public class MinimapController : MonoBehaviour
{
    [Header("World Bounds (XZ plane) — leave worldSize=0 to auto-detect from scene")]
    [SerializeField] Vector2 _worldCenter = Vector2.zero;
    [SerializeField] float   _worldSize   = 0f; // 0 = auto-detect from shelves/floor

    [Header("Scene Refs (auto-found if blank)")]
    [SerializeField] UIDocument _hudDocument;
    [SerializeField] Transform  _playerTransform;
    [SerializeField] Transform  _orderBoardTransform;
    [SerializeField] Transform  _benchTransform;
    [SerializeField] Transform  _shippingZoneTransform;

    // ── Runtime ──────────────────────────────────────────────────────────────
    VisualElement _view;
    VisualElement _worldLayer;    // static — holds zones and POIs
    VisualElement _playerMarker;  // moves each frame to player world position
    VisualElement _shippingPOIEl; // hidden when no active order
    VisualElement _eventShippingPOIEl;
    Vector3       _eventShippingPosition;
    string        _eventShippingOwner;
    Transform     _cameraTransform;

    // Zones: world-space fixed rectangles (floor, walls, shelves, area halos)
    readonly List<(Vector3 pos, float wM, float hM, VisualElement el)> _zones = new();
    // Named POI markers. isFixed markers (fixedPos) ignore tf and never move — used for
    // props whose mesh pivot is offset from its visual center, where tf.position would
    // place the icon in the wrong spot; tracked markers (tf) follow a live Transform and
    // hide themselves once destroyed.
    readonly List<(Transform tf, Vector3 fixedPos, bool isFixed, VisualElement el)> _staticMarkers = new();
    // Part item dots
    readonly List<(BoxWithPart box, VisualElement dot)> _partDots = new();
    // RushOrderEvent target dots — plain BoxPhysicsInteractable, not BoxWithPart, so they
    // need their own tracking list separate from the part-dot scan.
    readonly List<(BoxPhysicsInteractable box, VisualElement dot)> _rushDots = new();
    static readonly Color RushDotColor = new Color(0.75f, 0.35f, 0.95f); // magenta — distinct from part colors

    // Teammate markers — one per remote player. In a networked session CoopHUDExtension shows
    // the minimap to the Dispatcher only, so in practice these are the Dispatcher's coordination
    // tool and nobody else sees them. Empty (and free) in singleplayer, where NetworkPlayer.All is.
    readonly List<(NetworkPlayer np, VisualElement dot)> _teammateDots = new();

    // Raider markers — one per live ThiefController. Same idea as the teammate dots, but a
    // square rather than a circle so a zombie never reads as a friendly at a glance, and
    // colour-matched to the pip over its head so the map and the world agree.
    readonly List<(ThiefController thief, VisualElement dot)> _thiefDots = new();

    // Repair-quest markers — appear only while a ToolkitItem / PowerBoxRepairTarget
    // exists in the scene (spawned/removed by the events that need them).
    ToolkitItem   _trackedToolkit;
    VisualElement _toolkitPOI;

    RadioRepairPanel _trackedRadio;
    VisualElement    _radioPOI;
    // PowerFailureEvent can have up to 3 power boxes active at once — each gets its own
    // marker, removed individually as that specific box gets repaired.
    readonly List<(PowerBoxRepairTarget target, VisualElement el)> _powerBoxPOIs = new();
    // The gate a raid is currently breaking in through, while it is being shown. At most one —
    // GateBreachEvent only ever runs against a single gate.
    readonly List<(GateAnimator gate, VisualElement el)> _gatePOIs = new();
    readonly Dictionary<int, (Transform trackedTransform, Vector3 pos,
        string label, VisualElement el)>
        _vehicleDeliveryPOIs = new();

    float _partTimer;
    const float PART_REFRESH = 0.2f;

    // ── Init ─────────────────────────────────────────────────────────────────
    void Start()
    {
        if (NetworkPlayer.Local != null)
            _playerTransform = NetworkPlayer.Local.transform;
        else if (_playerTransform == null)
            _playerTransform = FindPlayerTransform();
        _cameraTransform = Camera.main?.transform;
        if (_benchTransform == null && AssemblyBench.Instance != null)
            _benchTransform = AssemblyBench.Instance.transform;
        if (_orderBoardTransform == null)
            _orderBoardTransform = FindObjectOfType<OrderBoard>()?.transform;
        if (_shippingZoneTransform == null)
            _shippingZoneTransform = FindObjectOfType<ShippingZone>()?.transform;

        // UIDocument.rootVisualElement is only available after the first render pass.
        // Try immediately (works if HUD is already active), then defer to after first render.
        AttemptBuild();
        if (_view == null)
            StartCoroutine(BuildAfterRender());
    }

    // Waits for end of frame so UI Toolkit has had a render pass and rootVisualElement is ready.
    System.Collections.IEnumerator BuildAfterRender()
    {
        yield return new WaitForEndOfFrame();
        AttemptBuild();
    }

    // Called every frame from Update() until _view is found, then once to build.
    void AttemptBuild()
    {
        if (_view != null) return; // already built

        VisualElement found = null;
        if (_hudDocument != null)
        {
            var root = _hudDocument.rootVisualElement;
            if (root != null)
                found = root.Q<VisualElement>("minimap-view");
        }
        else
        {
            foreach (var d in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (d.rootVisualElement == null) continue;
                found = d.rootVisualElement.Q<VisualElement>("minimap-view");
                if (found != null) break;
            }
        }

        if (found == null) return; // UIDocument not ready yet — Update() will retry next frame

        _view = found;

        _view.style.overflow = Overflow.Hidden;

        var floor = MakeAbsFill(new Color(0.07f, 0.085f, 0.10f));
        _view.Add(floor);

        _worldLayer = MakeAbsFill(Color.clear);
        _worldLayer.pickingMode = PickingMode.Ignore;
        _view.Add(_worldLayer);

        BuildWorldZones();
        BuildPOIMarkers();
        BuildPlayerMarker();
        BuildLegend();

        UpdatePositions();
    }

    VisualElement MakeAbsFill(Color color)
    {
        var el = new VisualElement();
        el.style.position = Position.Absolute;
        el.style.left = 0; el.style.right  = 0;
        el.style.top  = 0; el.style.bottom = 0;
        el.style.backgroundColor = new StyleColor(color);
        return el;
    }

    // ── World zones ───────────────────────────────────────────────────────────
    void BuildWorldZones()
    {
        // Step 1: auto-detect world bounds (sets _worldCenter/_worldSize)
        DetectWorldBounds();

        // Step 2: floor background — added first so it renders behind everything
        RegisterZone(new Vector3(_worldCenter.x, 0f, _worldCenter.y),
                     _worldSize * 0.92f, _worldSize * 0.92f,
                     new Color(0.09f, 0.10f, 0.13f, 1f));

        // Step 3: wall objects
        BuildWallZones();

        // Step 4: shelf footprints (on top of walls)
        BuildShelfZones();

        // Step 5: area halos around key stations (ShippingZone excluded — it moves dynamically)
        if (_orderBoardTransform != null) RegisterZone(_orderBoardTransform.position, 5f, 4f, new Color(0.96f, 0.65f, 0.14f, 0.08f));
        if (_benchTransform      != null) RegisterZone(_benchTransform.position,      5f, 4f, new Color(0.30f, 0.60f, 0.95f, 0.08f));
    }

    // Auto-detects scene bounds from shelf groups and floor objects.
    // Sets _worldCenter and _worldSize so the whole scene fits the minimap.
    void DetectWorldBounds()
    {
        if (_worldSize > 0.5f) return; // already set in inspector

        var union = new Bounds();
        bool hasAny = false;

        foreach (var go in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            string lower = go.name.ToLower();

            bool isShelf = false;
            foreach (var p in ShelfPatterns) if (lower.Contains(p)) { isShelf = true; break; }

            bool isFloor = lower.Contains("floor") || lower.Contains("concrete")
                        || lower.Contains("ground_plane") || lower.Contains("tile");

            if (!isShelf && !isFloor) continue;

            var rends = go.GetComponentsInChildren<Renderer>(false);
            if (rends.Length == 0) continue;

            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

            if (isFloor && b.center.y > 1.5f) continue; // floor must be near ground

            if (!hasAny) { union = b; hasAny = true; }
            else union.Encapsulate(b);
        }

        if (hasAny)
        {
            _worldCenter = new Vector2(union.center.x, union.center.z);
            // Add padding so shelves don't touch the minimap edges
            _worldSize = Mathf.Max(union.size.x, union.size.z) + 16f;
        }
        else
        {
            _worldCenter = Vector2.zero;
            _worldSize = 80f;
        }
    }

    // Draws wall objects as dark rectangles.
    void BuildWallZones()
    {
        foreach (var go in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            if (!go.name.ToLower().Contains("wall")) continue;
            var rends = go.GetComponentsInChildren<Renderer>(false);
            if (rends.Length == 0) continue;
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            if (b.center.y > 7f) continue; // skip very high geometry
            if (b.size.x * b.size.z < 0.25f) continue; // skip tiny objects
            var center = new Vector3(b.center.x, 0f, b.center.z);
            RegisterZone(center, b.size.x + 0.3f, b.size.z + 0.3f,
                         new Color(0.20f, 0.22f, 0.26f, 0.85f));
        }
    }

    // Name substrings that identify shelf-row container objects.
    static readonly string[] ShelfPatterns = {
        "shelflarge_grp_row", "shelflarge_grp_0", "shelflarge_grp_right",
        "shelflarge_grp_03",  "shelf_forklift_grp"
    };

    void BuildShelfZones()
    {
        // ShelfRegistry is the single source of truth for shelf numbering, shared
        // with ShelfStickerNumberer (in-world sticker decals) so both always agree.
        foreach (var entry in ShelfRegistry.GetOrderedShelves())
        {
            var b = entry.Bounds;
            // Use XZ footprint only; add padding so shelves look slightly wider on minimap
            var center = new Vector3(b.center.x, 0f, b.center.z);
            RegisterZone(center, b.size.x + 0.6f, b.size.z + 0.6f,
                         new Color(0.24f, 0.26f, 0.30f, 0.85f), outline: true, label: entry.Number.ToString());
        }
    }

    void RegisterZone(Vector3 worldPos, float wM, float hM, Color color, bool outline = false, string label = null)
    {
        var el = new VisualElement();
        el.style.position = Position.Absolute;
        // Width/height are set in pixels each frame by UpdatePositions so the
        // layout uses _view.resolvedStyle.width — safer than percentages inside
        // a stretched parent whose explicit size may be 0 at build time.
        el.style.backgroundColor             = new StyleColor(color);
        el.style.borderTopLeftRadius         = 2f;
        el.style.borderTopRightRadius        = 2f;
        el.style.borderBottomLeftRadius      = 2f;
        el.style.borderBottomRightRadius     = 2f;

        if (outline)
        {
            el.style.borderTopWidth    = 1f; el.style.borderBottomWidth = 1f;
            el.style.borderLeftWidth   = 1f; el.style.borderRightWidth  = 1f;
            var outlineColor = new Color(0.38f, 0.42f, 0.48f, 0.70f);
            el.style.borderTopColor    = new StyleColor(outlineColor);
            el.style.borderBottomColor = new StyleColor(outlineColor);
            el.style.borderLeftColor   = new StyleColor(outlineColor);
            el.style.borderRightColor  = new StyleColor(outlineColor);
        }

        if (!string.IsNullOrEmpty(label))
        {
            var lbl = new Label(label);
            // Absolute + fill so the number is always dead-centered on the zone's
            // own box regardless of how small the box is, instead of depending on
            // flex centering (which overflows unpredictably when the zone is
            // smaller than the label's line height).
            lbl.style.position                = Position.Absolute;
            lbl.style.left = 0; lbl.style.right  = 0;
            lbl.style.top  = 0; lbl.style.bottom = 0;
            lbl.style.fontSize                = 10;
            lbl.style.color                   = new StyleColor(new Color(0.85f, 0.88f, 0.92f, 0.9f));
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.unityTextAlign          = TextAnchor.MiddleCenter;
            lbl.pickingMode                   = PickingMode.Ignore;
            el.Add(lbl);
        }

        _worldLayer.Add(el);
        _zones.Add((worldPos, wM, hM, el));
    }

    // ── POI markers ───────────────────────────────────────────────────────────
    void BuildPOIMarkers()
    {
        AddPOI(_orderBoardTransform,   new Color(0.96f, 0.65f, 0.14f), "O", "Orders");
        AddPOI(_benchTransform,        new Color(0.30f, 0.60f, 0.95f), "B", "Bench");
        _shippingPOIEl = AddPOI(_shippingZoneTransform, new Color(0.18f, 0.72f, 1.00f), "S", "Shipping");

        var chargingColor = new Color(0.18f, 0.86f, 0.62f);
        foreach (var station in FindObjectsByType<ChargingStation>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (station != null)
                AddPOI(station.transform, chargingColor, "⚡", "Charge");

        var lift = FindObjectOfType<ScissorLift>();
        if (lift != null)
            AddPOI(lift.transform, new Color(0.90f, 0.60f, 0.20f), "L", "Lift");
    }

    VisualElement AddPOI(Transform tf, Color color, string icon, string label)
    {
        if (tf == null || _worldLayer == null) return null;
        var wrapper = BuildPOIVisual(color, icon, label);
        _worldLayer.Add(wrapper);
        _staticMarkers.Add((tf, Vector3.zero, false, wrapper));
        return wrapper;
    }

    // Fixed-position variant — for props whose mesh pivot doesn't match its visual
    // center (e.g. powerBox_07), where tracking transform.position would misplace the icon.
    VisualElement AddPOI(Vector3 fixedWorldPos, Color color, string icon, string label)
    {
        if (_worldLayer == null) return null;
        var wrapper = BuildPOIVisual(color, icon, label);
        _worldLayer.Add(wrapper);
        _staticMarkers.Add((null, fixedWorldPos, true, wrapper));
        return wrapper;
    }

    VisualElement BuildPOIVisual(Color color, string icon, string label)
    {
        var wrapper = new VisualElement();
        wrapper.style.position      = Position.Absolute;
        wrapper.style.flexDirection = FlexDirection.Column;
        wrapper.style.alignItems    = Align.Center;

        var circle = new VisualElement();
        circle.style.width  = 16f;
        circle.style.height = 16f;
        circle.style.backgroundColor         = new StyleColor(new Color(color.r, color.g, color.b, 0.90f));
        circle.style.borderTopLeftRadius     = 8f;
        circle.style.borderTopRightRadius    = 8f;
        circle.style.borderBottomLeftRadius  = 8f;
        circle.style.borderBottomRightRadius = 8f;
        circle.style.borderTopWidth    = 1f;
        circle.style.borderBottomWidth = 1f;
        circle.style.borderLeftWidth   = 1f;
        circle.style.borderRightWidth  = 1f;
        circle.style.borderTopColor    = new StyleColor(new Color(1f, 1f, 1f, 0.25f));
        circle.style.borderBottomColor = new StyleColor(new Color(1f, 1f, 1f, 0.25f));
        circle.style.borderLeftColor   = new StyleColor(new Color(1f, 1f, 1f, 0.25f));
        circle.style.borderRightColor  = new StyleColor(new Color(1f, 1f, 1f, 0.25f));
        circle.style.alignItems        = Align.Center;
        circle.style.justifyContent    = Justify.Center;

        var iconLbl = new Label(icon);
        iconLbl.style.fontSize                = 12;
        iconLbl.style.color                   = new StyleColor(Color.white);
        iconLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
        iconLbl.style.unityTextAlign          = TextAnchor.MiddleCenter;
        circle.Add(iconLbl);

        var txt = new Label(label);
        txt.style.fontSize       = 7.2f;
        txt.style.color          = new StyleColor(new Color(color.r, color.g, color.b, 0.90f));
        txt.style.unityTextAlign = TextAnchor.MiddleCenter;
        txt.style.marginTop      = 1f;

        wrapper.Add(circle);
        wrapper.Add(txt);
        return wrapper;
    }

    // ── Player marker (fixed at center) ──────────────────────────────────────
    void BuildPlayerMarker()
    {
        if (_view == null) return;

        _playerMarker = new VisualElement();
        _playerMarker.style.position    = Position.Absolute;
        _playerMarker.style.width       = 20f;
        _playerMarker.style.height      = 20f;
        // Position is set each frame by UpdatePositions based on world coords

        Color accent = new Color(0.96f, 0.65f, 0.14f);

        var ring = new VisualElement();
        ring.style.position = Position.Absolute;
        ring.style.left = 0; ring.style.top = 0;
        ring.style.width  = 20f;
        ring.style.height = 20f;
        ring.style.borderTopLeftRadius     = 10f;
        ring.style.borderTopRightRadius    = 10f;
        ring.style.borderBottomLeftRadius  = 10f;
        ring.style.borderBottomRightRadius = 10f;
        ring.style.borderTopWidth    = 2f;
        ring.style.borderBottomWidth = 2f;
        ring.style.borderLeftWidth   = 2f;
        ring.style.borderRightWidth  = 2f;
        ring.style.borderTopColor    = new StyleColor(new Color(accent.r, accent.g, accent.b, 0.65f));
        ring.style.borderBottomColor = new StyleColor(new Color(accent.r, accent.g, accent.b, 0.65f));
        ring.style.borderLeftColor   = new StyleColor(new Color(accent.r, accent.g, accent.b, 0.65f));
        ring.style.borderRightColor  = new StyleColor(new Color(accent.r, accent.g, accent.b, 0.65f));
        ring.style.backgroundColor   = new StyleColor(new Color(accent.r, accent.g, accent.b, 0.12f));

        var dot = new VisualElement();
        dot.style.position = Position.Absolute;
        dot.style.width    = 7f;
        dot.style.height   = 7f;
        dot.style.left     = new StyleLength(6.5f);
        dot.style.top      = new StyleLength(6.5f);
        dot.style.backgroundColor         = new StyleColor(accent);
        dot.style.borderTopLeftRadius     = 3.5f;
        dot.style.borderTopRightRadius    = 3.5f;
        dot.style.borderBottomLeftRadius  = 3.5f;
        dot.style.borderBottomRightRadius = 3.5f;

        // Direction arm — points up by default; rotated by player yaw each frame
        var arm = new VisualElement();
        arm.style.position = Position.Absolute;
        arm.style.width    = 2f;
        arm.style.height   = 7f;
        arm.style.left     = new StyleLength(9f);
        arm.style.top      = new StyleLength(1f);
        arm.style.backgroundColor         = new StyleColor(accent);
        arm.style.borderTopLeftRadius     = 1f;
        arm.style.borderTopRightRadius    = 1f;
        arm.style.borderBottomLeftRadius  = 0f;
        arm.style.borderBottomRightRadius = 0f;

        _playerMarker.Add(ring);
        _playerMarker.Add(dot);
        _playerMarker.Add(arm);
        _view.Add(_playerMarker); // added to _view, NOT _worldLayer — stays fixed at center
    }

    // ── Legend ────────────────────────────────────────────────────────────────
    void BuildLegend()
    {
        if (_view == null) return;

        var legend = new VisualElement();
        legend.style.position      = Position.Absolute;
        legend.style.bottom        = 4f;
        legend.style.left          = 4f;
        legend.style.right         = 4f;
        legend.style.flexDirection = FlexDirection.Row;
        legend.style.flexWrap      = Wrap.Wrap;
        legend.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.72f));
        legend.style.paddingLeft   = 4f;
        legend.style.paddingRight  = 4f;
        legend.style.paddingTop    = 2f;
        legend.style.paddingBottom = 2f;
        legend.style.borderTopLeftRadius     = 2f;
        legend.style.borderTopRightRadius    = 2f;
        legend.style.borderBottomLeftRadius  = 2f;
        legend.style.borderBottomRightRadius = 2f;

        LegendItem(legend, new Color(0.96f, 0.65f, 0.14f), "You");
        LegendItem(legend, new Color(0.96f, 0.50f, 0.15f), "Part");
        LegendItem(legend, new Color(0.90f, 0.20f, 0.20f), "Urgent");
        LegendItem(legend, RushDotColor,                   "Rush");
        LegendItem(legend, new Color(0.45f, 0.48f, 0.55f), "Extra");
        LegendItem(legend, new Color(0.18f, 0.86f, 0.62f), "Charge");
        // One entry for all four raider roles. The role colours differ on the map, but the
        // square is what tells a raider apart from a part or a teammate — four near-identical
        // swatches would only crowd a legend that already wraps.
        LegendItem(legend, new Color(0.90f, 0.15f, 0.12f), "Raider", square: true);

        _view.Add(legend);
    }

    // `square` mirrors the raider marker's shape into the legend — see MakeThiefDot for why
    // raiders get a square in the first place.
    void LegendItem(VisualElement parent, Color color, string text, bool square = false)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems    = Align.Center;
        row.style.marginRight   = 8f;

        var dot = new VisualElement();
        dot.style.width  = 9f;
        dot.style.height = 9f;
        dot.style.backgroundColor         = new StyleColor(color);
        float radius = square ? 0f : 4.5f;
        dot.style.borderTopLeftRadius     = radius;
        dot.style.borderTopRightRadius    = radius;
        dot.style.borderBottomLeftRadius  = radius;
        dot.style.borderBottomRightRadius = radius;
        dot.style.marginRight = 3f;

        var lbl = new Label(text);
        lbl.style.fontSize = 11f;
        lbl.style.color    = new StyleColor(new Color(0.75f, 0.78f, 0.82f));

        row.Add(dot);
        row.Add(lbl);
        parent.Add(row);
    }

    // ── Update ────────────────────────────────────────────────────────────────
    static Transform FindPlayerTransform()
    {
        if (NetworkPlayer.Local != null)
            return NetworkPlayer.Local.transform;

        var fpc = FindObjectOfType<FirstPersonController>();
        if (fpc != null) return fpc.transform;
        var tpc = FindObjectOfType<StarterAssets.ThirdPersonController>();
        if (tpc != null) return tpc.transform;
        var tpsCam = FindObjectOfType<TPSCameraController>();
        if (tpsCam != null) return tpsCam.transform;
        var go = GameObject.FindWithTag("Player");
        return go != null ? go.transform : null;
    }

    void Update()
    {
        if (_view == null)
        {
            AttemptBuild(); // retry every frame until UIDocument is ready
            return;
        }

        // Scene references can point at the host/placeholder player. As soon as NGO exposes the
        // local NetworkPlayer, it is the only valid center for this client's scrolling map.
        if (NetworkPlayer.Local != null && _playerTransform != NetworkPlayer.Local.transform)
            _playerTransform = NetworkPlayer.Local.transform;
        else if (_playerTransform == null)
            _playerTransform = FindPlayerTransform();
        if (_cameraTransform == null)
            _cameraTransform = Camera.main?.transform;

        // Event tutorials pause Time.timeScale, so this cannot live behind the timed refresh
        // below: the red/purple destination must appear as soon as its first popup is shown.
        RefreshEventShippingPOI();
        UpdatePositions(); // handles null playerTransform gracefully

        _partTimer += Time.deltaTime;
        if (_partTimer >= PART_REFRESH)
        {
            _partTimer = 0f;
            RefreshPartDots();
            RefreshRushDots();
            RefreshTeammateDots();
            RefreshThiefDots();
            RefreshToolkitPOI();
            RefreshPowerBoxPOI();
            RefreshRadioPOI();
            RefreshBreachGatePOI();
            RefreshVehicleDeliveryPOIs();
        }
    }

    // ── Repair-quest markers ──────────────────────────────────────────────────

    void RefreshToolkitPOI()
    {
        var toolkit = FindFirstObjectByType<ToolkitItem>();
        // ReferenceEquals, not == — Unity's == overload treats "real null" and "reference
        // to an already-destroyed object" as equal, which would make this look unchanged
        // forever once the tracked toolkit is destroyed on pickup, leaving its POI stuck.
        if (ReferenceEquals(toolkit, _trackedToolkit)) return;

        RemovePOI(ref _toolkitPOI);
        if (toolkit == null) { _trackedToolkit = null; return; }

        var el = AddPOI(toolkit.transform, new Color(0.98f, 0.72f, 0.20f), "T", "Toolkit");
        if (el == null) return; // _worldLayer not built yet — leave _trackedToolkit unset, retry next tick
        _trackedToolkit = toolkit;
        _toolkitPOI     = el;
    }

    /// The charger the radio has to be fixed at, for as long as it is broken.
    ///
    /// Without it the job is "find the one piece of radio equipment in a warehouse this size",
    /// which is not a puzzle, just a walk. In a session the minimap belongs to the Dispatcher —
    /// and so does the radio — so the marker lands in front of the player whose job it is.
    void RefreshRadioPOI()
    {
        var panel = RadioRepairPanel.Current;

        // ReferenceEquals, not ==, for the same reason RefreshToolkitPOI uses it: a destroyed
        // component compares equal to null under Unity's operator, and the marker would stick.
        if (ReferenceEquals(panel, _trackedRadio)) return;

        RemovePOI(ref _radioPOI);
        if (panel == null) { _trackedRadio = null; return; }

        // The charger's own mesh, not its pivot — the group's origin sits on the floor.
        var rend = panel.GetComponentInChildren<Renderer>();
        Vector3 pos = rend != null ? rend.bounds.center : panel.transform.position;

        // Radio blue, away from the power box's yellow and the parts' orange.
        var el = AddPOI(pos, new Color(0.30f, 0.76f, 1.00f), "R", "Radio");
        if (el == null) return;   // world layer not built yet — retried next tick

        _trackedRadio = panel;
        _radioPOI     = el;
    }

    void RefreshPowerBoxPOI()
    {
        // Drop markers for boxes that got repaired (component destroyed) since last tick.
        for (int i = _powerBoxPOIs.Count - 1; i >= 0; i--)
        {
            if (_powerBoxPOIs[i].target != null) continue;
            var el = _powerBoxPOIs[i].el;
            _staticMarkers.RemoveAll(m => m.el == el);
            el.RemoveFromHierarchy();
            _powerBoxPOIs.RemoveAt(i);
        }

        // Add markers for any new targets (PowerFailureEvent can spawn up to 3 at once).
        var current = FindObjectsByType<PowerBoxRepairTarget>(FindObjectsSortMode.None);
        var tracked = new HashSet<PowerBoxRepairTarget>(_powerBoxPOIs.Select(e => e.target));
        foreach (var box in current)
        {
            if (tracked.Contains(box)) continue;

            // Use the renderer's world bounds center, not transform.position — powerBox_06's
            // mesh pivot is offset from its visual center, which would misplace the icon.
            var rend = box.GetComponent<Renderer>();
            Vector3 pos = rend != null ? rend.bounds.center : box.transform.position;
            // Electric yellow — deliberately far from the orange used by part dots
            // (0.96, 0.50, 0.15) so a power box never reads as just another part at a glance.
            var el = AddPOI(pos, new Color(1.00f, 0.92f, 0.15f), "P", "Power Box");
            if (el == null) continue; // _worldLayer not built yet — retry this box next tick
            _powerBoxPOIs.Add((box, el));
        }
    }

    // The gate being broken into, for as long as the raid says to show it.
    //
    // Scanned rather than pushed, the same way power boxes are: the event sets a flag on the gate
    // and this picks it up. Nothing here decides *when* — a Dispatcher sees it from the first blow
    // and a solo player only in the last few seconds, and that timing belongs to GateBreachEvent,
    // which is the thing that knows how far into the beating it is.
    //
    // Whether it is drawn at all is settled elsewhere again: in a session the whole minimap is
    // hidden from everyone but the Dispatcher (CoopHUDExtension), so this needs no role check.
    void RefreshBreachGatePOI()
    {
        for (int i = _gatePOIs.Count - 1; i >= 0; i--)
        {
            var (gate, el) = _gatePOIs[i];
            if (gate != null && (gate.ShowOnMap || gate.IsBreached)) continue;

            _staticMarkers.RemoveAll(m => m.el == el);
            el.RemoveFromHierarchy();
            _gatePOIs.RemoveAt(i);
        }

        var tracked = new HashSet<GateAnimator>(_gatePOIs.Select(e => e.gate));

        foreach (var gate in GateAnimator.All)
        {
            // Shown while a raid is coming through it, and afterwards for as long as the hole is
            // still there: a gate nobody repaired is a standing job, and the marker is how it gets
            // found again once the noise has stopped.
            if (gate == null || !(gate.ShowOnMap || gate.IsBreached) || tracked.Contains(gate))
                continue;

            // The doorway on the floor, not the gate's own transform — that sits up at the roller
            // about four and a half metres in the air, and on a top-down map an icon placed there
            // lands off the wall it belongs to.
            Vector3 pos = gate.FloorAnchor != null ? gate.FloorAnchor.position : gate.transform.position;

            // Red, and nothing else on this map is red. It is the only marker that means a threat.
            var el = AddPOI(pos, new Color(0.95f, 0.20f, 0.20f), "!", "Breach");
            if (el == null) continue;   // _worldLayer not built yet — retry next tick

            _gatePOIs.Add((gate, el));
        }
    }

    void RefreshVehicleDeliveryPOIs()
    {
        var manager = VehicleDeliveryManager.Instance;
        // The map guides the team to an ARRIVED parts pallet. Once the delivery is assembled,
        // its job is complete: remove the pallet marker and do not replace it with a vehicle POI.
        var targets = manager != null
            ? manager.ActiveMapTargets.Where(target => !target.Assembled).ToArray()
            : System.Array.Empty<VehicleDeliveryManager.DeliveryMapTarget>();
        var activeIds = new HashSet<int>(targets.Select(t => t.DeliveryId));

        foreach (int deliveryId in _vehicleDeliveryPOIs.Keys.ToArray())
        {
            if (activeIds.Contains(deliveryId)) continue;
            RemoveVehicleDeliveryPOI(deliveryId);
        }

        foreach (var target in targets)
        {
            if (_vehicleDeliveryPOIs.TryGetValue(target.DeliveryId, out var current)
                && current.label == target.Label
                && current.trackedTransform == target.TrackedTransform
                && (target.TrackedTransform != null
                    || Vector3.SqrMagnitude(current.pos - target.Position) < 0.01f))
                continue;

            RemoveVehicleDeliveryPOI(target.DeliveryId);

            Color color = new Color(1.00f, 0.58f, 0.12f);
            const string icon = "D";
            string label = $"{target.Label} delivery";
            var el = target.TrackedTransform != null
                ? AddPOI(target.TrackedTransform, color, icon, label)
                : AddPOI(target.Position, color, icon, label);
            if (el == null) continue;

            _vehicleDeliveryPOIs[target.DeliveryId] =
                (target.TrackedTransform, target.Position, target.Label, el);
        }
    }

    void RemoveVehicleDeliveryPOI(int deliveryId)
    {
        if (!_vehicleDeliveryPOIs.Remove(deliveryId, out var tracked)) return;
        _staticMarkers.RemoveAll(m => m.el == tracked.el);
        tracked.el?.RemoveFromHierarchy();
    }

    // Detaches a dynamically-added POI both visually and from the position-update list —
    // needed because the tracked object (toolkit picked up, box repaired) gets destroyed,
    // and a stale Transform reference would otherwise just freeze in place forever.
    void RemovePOI(ref VisualElement el)
    {
        if (el == null) return;
        var target = el; // ref params can't be captured by the RemoveAll lambda directly
        _staticMarkers.RemoveAll(m => m.el == target);
        el.RemoveFromHierarchy();
        el = null;
    }

    void UpdatePositions()
    {
        float vpW = _view.resolvedStyle.width;
        float vpH = _view.resolvedStyle.height;
        if (vpW < 1f || vpH < 1f) return; // layout not yet resolved

        // Scroll the world layer so the player is always at the minimap centre.
        // Everything in _worldLayer uses StaticU/V * vpW/H coordinates, so
        // translating by (0.5 - playerU)*vpW keeps the player dot on-centre.
        if (_playerTransform != null && _worldLayer != null)
        {
            float pu = StaticU(_playerTransform.position.x);
            float pv = StaticV(_playerTransform.position.z);
            _worldLayer.style.translate = new StyleTranslate(
                new Translate(new Length((0.5f - pu) * vpW), new Length((0.5f - pv) * vpH)));
        }

        // Zone rectangles (floor, walls, shelves, halos) — positions inside _worldLayer
        foreach (var (worldPos, wM, hM, el) in _zones)
        {
            float u   = StaticU(worldPos.x);
            float v   = StaticV(worldPos.z);
            float wPx = wM / _worldSize * vpW;
            float hPx = hM / _worldSize * vpH;
            el.style.left   = new StyleLength(u * vpW - wPx * 0.5f);
            el.style.top    = new StyleLength(v * vpH - hPx * 0.5f);
            el.style.width  = new StyleLength(wPx);
            el.style.height = new StyleLength(hPx);
        }

        // POI markers — may move (ShippingZone relocates on order accept)
        foreach (var (tf, fixedPos, isFixed, el) in _staticMarkers)
        {
            if (isFixed) PlacePOIPx(el, fixedPos, vpW, vpH);
            else if (tf != null) PlacePOIPx(el, tf.position, vpW, vpH);
        }

        // Part dots
        foreach (var (box, dot) in _partDots)
            if (box != null) PlaceStaticPx(dot, box.transform.position, vpW, vpH, -4f, -8f);

        // Rush order target dots
        foreach (var (box, dot) in _rushDots)
            if (box != null) PlaceStaticPx(dot, box.transform.position, vpW, vpH, -4f, -8f);

        // Teammate dots — NetworkPlayer objects carry ClientNetworkTransform, so their
        // positions are already replicated here every tick; nothing extra to sync.
        foreach (var (np, dot) in _teammateDots)
            if (np != null) PlaceStaticPx(dot, np.transform.position, vpW, vpH, -5f, -5f);

        // Raider dots — server-spawned NetworkObjects, so their transforms are already
        // replicated on every client; same as the teammates, nothing extra to sync.
        foreach (var (thief, dot) in _thiefDots)
            if (thief != null) PlaceStaticPx(dot, thief.transform.position, vpW, vpH, -5f, -5f);

        // The destination is intentionally revealed only after the team has assembled and packed
        // the order. Showing it as soon as an order was accepted made the map point players at the
        // final step while they were still collecting parts.
        if (_shippingPOIEl != null)
        {
            bool packedOrderReady = Object.FindFirstObjectByType<PackedOrderBox>() != null;
            _shippingPOIEl.style.display = packedOrderReady ? DisplayStyle.Flex : DisplayStyle.None;
        }

        if (_eventShippingPOIEl != null
            && _eventShippingPOIEl.style.display != DisplayStyle.None)
            PlacePOIPx(_eventShippingPOIEl, _eventShippingPosition, vpW, vpH);

        // Player marker fixed at the centre of the minimap view
        if (_playerMarker != null)
        {
            _playerMarker.style.left       = new StyleLength(vpW * 0.5f - 10f);
            _playerMarker.style.top        = new StyleLength(vpH * 0.5f - 10f);
            _playerMarker.style.marginLeft = StyleKeyword.None;
            _playerMarker.style.marginTop  = StyleKeyword.None;
        }

        // Rotate player marker arrow to show facing direction on the north-up map
        Transform yawSource = _cameraTransform != null ? _cameraTransform : _playerTransform;
        float yaw = yawSource != null ? yawSource.eulerAngles.y : 0f;
        if (_playerMarker != null)
            _playerMarker.style.rotate = new StyleRotate(new Rotate(new Angle(yaw, AngleUnit.Degree)));
    }

    // Maps a world XZ coordinate to a [0,1] UV on the static minimap.
    // 0.5 = scene center (_worldCenter). Screen Y increases downward, so Z is inverted.
    float StaticU(float worldX) => 0.5f + (worldX - _worldCenter.x) / _worldSize;
    float StaticV(float worldZ) => 0.5f - (worldZ - _worldCenter.y) / _worldSize;

    void RefreshEventShippingPOI()
    {
        if (_worldLayer == null) return;

        var zone = ShippingZone.Instance;
        if (zone == null
            || !zone.TryGetActiveEventTarget(out Vector3 position, out Color color,
                                              out string owner))
        {
            if (_eventShippingPOIEl != null)
                _eventShippingPOIEl.style.display = DisplayStyle.None;
            _eventShippingOwner = null;
            return;
        }

        _eventShippingPosition = position;
        if (_eventShippingPOIEl == null || _eventShippingOwner != owner)
        {
            _eventShippingPOIEl?.RemoveFromHierarchy();
            string label = owner == "RushOrder" ? "Rush Zone" : "Urgent Zone";
            _eventShippingPOIEl = BuildPOIVisual(color, "S", label);
            _worldLayer.Add(_eventShippingPOIEl);
            _eventShippingOwner = owner;
        }

        _eventShippingPOIEl.style.display = DisplayStyle.Flex;
        _eventShippingPOIEl.BringToFront();
    }

    void PlaceStaticPx(VisualElement el, Vector3 worldPos, float vpW, float vpH,
                       float offsetX, float offsetY)
    {
        if (el == null) return;
        el.style.left       = new StyleLength(StaticU(worldPos.x) * vpW + offsetX);
        el.style.top        = new StyleLength(StaticV(worldPos.z) * vpH + offsetY);
        el.style.marginLeft = StyleKeyword.None;
        el.style.marginTop  = StyleKeyword.None;
    }

    void PlacePOIPx(VisualElement el, Vector3 worldPos, float vpW, float vpH)
    {
        if (el == null) return;

        // A POI wrapper is as wide as its label. Using the old constant -8 px assumed the
        // wrapper was only the 16 px circle, so long delivery labels pushed the actual icon to
        // the right of its world position. Centre by the resolved wrapper width instead.
        float width = el.resolvedStyle.width;
        float offsetX = width > 0f && !float.IsNaN(width) && !float.IsInfinity(width)
            ? -width * 0.5f
            : -8f;
        PlaceStaticPx(el, worldPos, vpW, vpH, offsetX, -9f);
    }

    // ── Part dots ─────────────────────────────────────────────────────────────

    // Priority matches PartHighlighter: Urgent event target > any order-required part >
    // not needed ("Extra").
    static Color GetDotColor(BoxWithPart box)
    {
        var part = box.Part;
        if (part != null && UrgentPartEvent.ActiveTargetPart == part)
            return new Color(0.90f, 0.20f, 0.20f); // Urgent — red

        return box.IsRequired ? new Color(0.96f, 0.50f, 0.15f)   // Standard — yellow-orange
                               : new Color(0.45f, 0.48f, 0.55f); // Extra — grey
    }

    void RefreshPartDots()
    {
        if (_worldLayer == null) return;

        // Remove dots for boxes that lost their BoxWithPart (part taken or order cleared)
        for (int i = _partDots.Count - 1; i >= 0; i--)
        {
            if (_partDots[i].box == null || _partDots[i].box.Part == null)
            {
                _partDots[i].dot.RemoveFromHierarchy();
                _partDots.RemoveAt(i);
            }
        }

        var current = FindObjectsByType<BoxWithPart>(FindObjectsSortMode.None);
        var tracked = new HashSet<BoxWithPart>(_partDots.Select(e => e.box));

        foreach (var box in current)
        {
            if (box.Part == null || tracked.Contains(box)) continue;
            var dot = MakePartDot();
            _worldLayer.Add(dot);
            _partDots.Add((box, dot));
        }

        // Colour + height badge update
        foreach (var (box, dot) in _partDots)
        {
            if (box == null) continue;

            Color c = GetDotColor(box);

            // The circle is the first child; the level badge is the second
            var circle = dot.ElementAt(0);
            var badge  = dot.ElementAt(1) as Label;

            if (circle != null) circle.style.backgroundColor = new StyleColor(c);

            // Derive shelf level from Y position — always shown
            float y = box.transform.position.y;
            string levelText;
            Color  levelColor;
            if (y < 0.6f)      { levelText = "G"; levelColor = new Color(0.75f, 0.80f, 0.85f, 0.9f); }
            else if (y < 2.0f) { levelText = "1"; levelColor = new Color(1f,    1f,    1f,    0.9f); }
            else               { levelText = "2"; levelColor = new Color(1f,    0.85f, 0.3f,  0.9f); }

            if (badge != null)
            {
                badge.text            = levelText;
                badge.style.color     = new StyleColor(levelColor);
                badge.style.display   = DisplayStyle.Flex;
            }
        }
    }

    // Tracks RushOrderEvent.ActiveTargets — dots removed as boxes get delivered/destroyed
    // or the event ends (RushOrderEvent.Remove() clears the set either way).
    void RefreshRushDots()
    {
        if (_worldLayer == null) return;

        for (int i = _rushDots.Count - 1; i >= 0; i--)
        {
            if (_rushDots[i].box != null && RushOrderEvent.ActiveTargets.Contains(_rushDots[i].box))
                continue;
            _rushDots[i].dot.RemoveFromHierarchy();
            _rushDots.RemoveAt(i);
        }

        var tracked = new HashSet<BoxPhysicsInteractable>(_rushDots.Select(e => e.box));
        foreach (var box in RushOrderEvent.ActiveTargets)
        {
            if (box == null || tracked.Contains(box)) continue;
            var dot = MakePartDot();
            _worldLayer.Add(dot);
            _rushDots.Add((box, dot));
        }

        foreach (var (box, dot) in _rushDots)
        {
            if (box == null) continue;

            var circle = dot.ElementAt(0);
            var badge  = dot.ElementAt(1) as Label;
            if (circle != null) circle.style.backgroundColor = new StyleColor(RushDotColor);

            float y = box.transform.position.y;
            string levelText;
            Color  levelColor;
            if (y < 0.6f)      { levelText = "G"; levelColor = new Color(0.75f, 0.80f, 0.85f, 0.9f); }
            else if (y < 2.0f) { levelText = "1"; levelColor = new Color(1f,    1f,    1f,    0.9f); }
            else               { levelText = "2"; levelColor = new Color(1f,    0.85f, 0.3f,  0.9f); }

            if (badge != null)
            {
                badge.text          = levelText;
                badge.style.color   = new StyleColor(levelColor);
                badge.style.display = DisplayStyle.Flex;
            }
        }
    }

    // Tracks NetworkPlayer.All, skipping the local player — they are already drawn by
    // _playerMarker, pinned to the centre of the view while the world scrolls underneath.
    // Polled on the same timer as the part dots rather than driven by
    // NetworkPlayer.OnRosterChanged: a handful of players is nothing to walk every 0.2s,
    // and it keeps this free of subscribe/unsubscribe lifecycle to get wrong.
    void RefreshTeammateDots()
    {
        if (_worldLayer == null) return;

        // Drop dots for players who left (OnNetworkDespawn removes them from All).
        for (int i = _teammateDots.Count - 1; i >= 0; i--)
        {
            var tracked = _teammateDots[i].np;
            if (tracked != null && NetworkPlayer.All.Contains(tracked)) continue;
            _teammateDots[i].dot.RemoveFromHierarchy();
            _teammateDots.RemoveAt(i);
        }

        var known = new HashSet<NetworkPlayer>(_teammateDots.Select(e => e.np));
        foreach (var np in NetworkPlayer.All)
        {
            if (np == null || np.IsOwner || known.Contains(np)) continue;
            var dot = MakeTeammateDot();
            _worldLayer.Add(dot);
            _teammateDots.Add((np, dot));
        }

        // Colour + name refreshed every pass — a player can change role mid-session.
        foreach (var (np, dot) in _teammateDots)
        {
            if (np == null) continue;
            var circle = dot.ElementAt(0);
            var name   = dot.ElementAt(1) as Label;
            if (circle != null) circle.style.backgroundColor = new StyleColor(RoleDotColor(np.RoleType));
            if (name   != null) name.text = np.PlayerName;
        }
    }

    // Same colour the player is actually wearing, so the dot and the body agree.
    static Color RoleDotColor(PlayerRoleType role) => PlayerRoleColors.For(role);

    // Tracks ThiefController.All. Polled on the part-dot timer like the teammates: a raid is
    // five raiders at most, and polling keeps this free of OnResolved
    // subscribe/unsubscribe lifecycle that would strand a marker if a raid ended the wrong way.
    void RefreshThiefDots()
    {
        if (_worldLayer == null) return;

        // Drop dots for raiders that despawned — OnDisable takes them out of All.
        for (int i = _thiefDots.Count - 1; i >= 0; i--)
        {
            var tracked = _thiefDots[i].thief;
            if (tracked != null && ThiefController.All.Contains(tracked)) continue;
            _thiefDots[i].dot.RemoveFromHierarchy();
            _thiefDots.RemoveAt(i);
        }

        var known = new HashSet<ThiefController>(_thiefDots.Select(e => e.thief));
        foreach (var thief in ThiefController.All)
        {
            if (thief == null || known.Contains(thief)) continue;
            var dot = MakeThiefDot();
            _worldLayer.Add(dot);
            _thiefDots.Add((thief, dot));
        }

        // Colour follows the role pip, refreshed every pass. A downed raider hides entirely,
        // matching the head marker that goes dark once the body drops — the corpse lingers a
        // few seconds before despawn and should not read as a live threat on the map.
        foreach (var (thief, dot) in _thiefDots)
        {
            if (thief == null) continue;

            dot.style.display = thief.Current == ThiefController.State.Down
                ? DisplayStyle.None
                : DisplayStyle.Flex;

            var square = dot.ElementAt(0);
            if (square != null)
                square.style.backgroundColor =
                    new StyleColor(ThiefController.MarkerColorFor(thief.Role));
        }
    }

    // A square, not a circle: the minimap already spends circles on parts and teammates, and
    // a raider must never be mistaken for either. Ringed in white for the same reason the
    // teammate dot is — it keeps the marker readable over the dark shelf rectangles.
    VisualElement MakeThiefDot()
    {
        var wrapper = new VisualElement();
        wrapper.style.position = Position.Absolute;
        wrapper.style.width    = 10f;
        wrapper.style.height   = 10f;

        var square = new VisualElement();
        square.style.position = Position.Absolute;
        square.style.left     = 0f;  square.style.top    = 0f;
        square.style.width    = 10f; square.style.height = 10f;
        square.style.borderTopWidth  = 2f; square.style.borderBottomWidth = 2f;
        square.style.borderLeftWidth = 2f; square.style.borderRightWidth  = 2f;
        var borderC = new StyleColor(new Color(1f, 1f, 1f, 0.9f));
        square.style.borderTopColor  = borderC; square.style.borderBottomColor = borderC;
        square.style.borderLeftColor = borderC; square.style.borderRightColor  = borderC;

        wrapper.Add(square);
        return wrapper;
    }

    // Returns a container: role-coloured dot + the player's name underneath. Deliberately
    // larger and white-ringed so a teammate never reads as a part dot at a glance.
    VisualElement MakeTeammateDot()
    {
        var wrapper = new VisualElement();
        wrapper.style.position = Position.Absolute;
        wrapper.style.width    = 10f;
        wrapper.style.height   = 10f;

        var circle = new VisualElement();
        circle.style.position = Position.Absolute;
        circle.style.left     = 0f;  circle.style.top    = 0f;
        circle.style.width    = 10f; circle.style.height = 10f;
        circle.style.borderTopLeftRadius     = 5f;
        circle.style.borderTopRightRadius    = 5f;
        circle.style.borderBottomLeftRadius  = 5f;
        circle.style.borderBottomRightRadius = 5f;
        circle.style.borderTopWidth    = 2f; circle.style.borderBottomWidth = 2f;
        circle.style.borderLeftWidth   = 2f; circle.style.borderRightWidth  = 2f;
        var borderC = new StyleColor(new Color(1f, 1f, 1f, 0.9f));
        circle.style.borderTopColor    = borderC; circle.style.borderBottomColor = borderC;
        circle.style.borderLeftColor   = borderC; circle.style.borderRightColor  = borderC;

        // Name below the dot. Fixed width + negative left offset centres it on the marker,
        // since the wrapper itself is only 10px wide.
        var name = new Label();
        name.style.position       = Position.Absolute;
        name.style.top            = 12f;
        name.style.left           = -25f;
        name.style.width          = 60f;
        name.style.fontSize       = 9;
        name.style.unityTextAlign = TextAnchor.MiddleCenter;
        name.style.unityFontStyleAndWeight = FontStyle.Bold;
        name.style.color          = new StyleColor(new Color(1f, 1f, 1f, 0.85f));

        wrapper.Add(circle);
        wrapper.Add(name);
        return wrapper;
    }

    // Returns a container: circle (coloured dot) + level badge overlaid top-right.
    VisualElement MakePartDot()
    {
        // Wrapper — positions the whole indicator via PlaceFollow
        var wrapper = new VisualElement();
        wrapper.style.position = Position.Absolute;
        wrapper.style.width    = 12f;
        wrapper.style.height   = 12f;

        // Circle
        var circle = new VisualElement();
        circle.style.position = Position.Absolute;
        circle.style.left     = 0f; circle.style.top    = 4f;
        circle.style.width    = 8f; circle.style.height = 8f;
        circle.style.backgroundColor         = new StyleColor(new Color(0.45f, 0.48f, 0.55f));
        circle.style.borderTopLeftRadius     = 4f;
        circle.style.borderTopRightRadius    = 4f;
        circle.style.borderBottomLeftRadius  = 4f;
        circle.style.borderBottomRightRadius = 4f;
        circle.style.borderTopWidth    = 1f; circle.style.borderBottomWidth = 1f;
        circle.style.borderLeftWidth   = 1f; circle.style.borderRightWidth  = 1f;
        var borderC = new StyleColor(new Color(0.65f, 0.68f, 0.72f, 0.4f));
        circle.style.borderTopColor    = borderC; circle.style.borderBottomColor = borderC;
        circle.style.borderLeftColor   = borderC; circle.style.borderRightColor  = borderC;

        // Height level badge (top-right corner of wrapper)
        var badge = new Label();
        badge.style.position  = Position.Absolute;
        badge.style.right     = 0f;
        badge.style.top       = 0f;
        badge.style.fontSize  = 11;
        badge.style.unityFontStyleAndWeight = FontStyle.Bold;
        badge.style.color     = new StyleColor(Color.white);

        wrapper.Add(circle);
        wrapper.Add(badge);
        return wrapper;
    }
}
