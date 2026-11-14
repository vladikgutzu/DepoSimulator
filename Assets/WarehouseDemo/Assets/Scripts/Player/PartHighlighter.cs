using UnityEngine;
using System.Collections.Generic;

// Highlights parts needed for the active order.
// Parts live only in BoxWithPart boxes (PartSpawner assignments) — there are no loose parts.
// Uses _BaseColor tint via MaterialPropertyBlock — no shader keyword required.
public class PartHighlighter : MonoBehaviour
{
    // Warehouse pack shader emission properties (Shader Graphs/shdr_basic_01)
    static readonly int PowerID    = Shader.PropertyToID("_Power");
    static readonly int ColorMapID = Shader.PropertyToID("_Color_Map");
    static readonly int AnimateID  = Shader.PropertyToID("_animate");
    static readonly int EmissionMapID = Shader.PropertyToID("_Emmision_Map"); // sic: shader property name
    static readonly int ScaleID       = Shader.PropertyToID("_Scale");

    // Standard = any required part (unchanged yellow-orange). Urgent gives a part a distinct
    // color so players can tell at a glance why it's glowing. Rush is purple, matching the
    // rush-order marker on the minimap. Every base color has the same peak HDR channel (8), so
    // applying _hdrIntensityScale gives every gameplay highlight the same emission strength.
    static readonly Color GoldHDR   = new Color(8f, 6.4f, 0.4f, 1f);   // standard required part
    static readonly Color UrgentHDR = new Color(8f, 0.5f, 0.4f, 1f);   // UrgentPartEvent target
    static readonly Color RushHDR   = new Color(6f, 2.8f, 8f, 1f);     // RushOrderEvent target

    [SerializeField] private Color _highlightTint = new Color(1f, 0.78f, 0.05f);
    [SerializeField] private float _scanInterval   = 0.25f;

    // _Power sits in the shader's "Emission Maps & Settings" group and, going by other
    // materials' baked values (~0.25-0.55 baseline), likely feeds a non-linear (exponent-style)
    // node rather than a plain multiplier — dialing it down from 1 made the highlight nearly
    // invisible instead of just dimmer. Leave it at the known-working value and control
    // intensity via the HDR color magnitude instead, which is a straightforward linear scale.
    [SerializeField] private float _highlightPower = 1f;
    // Bloom threshold in Assets/Scenes/profiles/Day.asset is 0.83 — the raw HDR colors above
    // (max channel 8.0) land ~10x over that, which is what actually caused the blown-out white
    // blob (not _Power). 0.2 puts the brightest channel around 1.6, a couple of stops over
    // threshold — enough to read as glowing without whiting out the model behind it.
    [Tooltip("Multiplies the RGB of whichever HDR highlight color is active (Gold/Urgent). "
           + "Keep the brightest resulting channel within ~1-2x of Bloom.threshold (0.83 in the Day "
           + "profile) or it blows out into a solid white blob regardless of this scale.")]
    [Range(0.02f, 1f)]
    [SerializeField] private float _hdrIntensityScale = 0.4f;

    private float                     _nextScan;
    private readonly List<GameObject> _highlighted = new();
    private MaterialPropertyBlock     _mpb;

    /// The exact emission a required part box glows with, as last configured on the scene's
    /// highlighter — read by RepairGlow so a job on a wall (power box, gate panel) reads as the same
    /// kind of thing as a box to fetch. Defaults match the component's own defaults.
    public static Color RequiredGlowColor { get; private set; } = GoldHDR * 0.4f;
    public static Color UrgentGlowColor  { get; private set; } = UrgentHDR * 0.4f;
    public static Color RushGlowColor    { get; private set; } = RushHDR * 0.4f;
    public static float RequiredGlowPower { get; private set; } = 1f;

    private void Awake()
    {
        _mpb = new MaterialPropertyBlock();
        RequiredGlowColor = new Color(GoldHDR.r * _hdrIntensityScale, GoldHDR.g * _hdrIntensityScale,
                                      GoldHDR.b * _hdrIntensityScale, GoldHDR.a);
        UrgentGlowColor = new Color(UrgentHDR.r * _hdrIntensityScale, UrgentHDR.g * _hdrIntensityScale,
                                    UrgentHDR.b * _hdrIntensityScale, UrgentHDR.a);
        RushGlowColor = new Color(RushHDR.r * _hdrIntensityScale, RushHDR.g * _hdrIntensityScale,
                                  RushHDR.b * _hdrIntensityScale, RushHDR.a);
        RequiredGlowPower = _highlightPower;
    }

    /// Forces every PartHighlighter in the scene to re-scan immediately (instead of
    /// waiting up to _scanInterval) — called when the urgent target changes.
    public static void RefreshAll()
    {
        foreach (var h in FindObjectsByType<PartHighlighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            h.ForceRefresh();
    }

    /// Priority: Urgent event target > any order-required part.
    static Color? GetHighlightColor(PartDefinition part)
    {
        if (part == null) return null;

        if (UrgentPartEvent.ActiveTargetPart == part)
            return UrgentHDR;

        var order = GameManager.Instance?.ActiveOrder;
        if (order != null)
            foreach (var req in order.RequiredParts)
                if (req.Part == part) return GoldHDR;

        return null;
    }

    private void Update()
    {
        if (Time.time < _nextScan) return;
        _nextScan = Time.time + _scanInterval;

        bool  singleplayer = SpecializationPenalties.SoloMode;
        bool  scanner      = NetworkShopManager.Instance?.ScannerActive == true;
        float range        = singleplayer || scanner
            ? 0f
            : RoleTaskManager.GetPartHighlightRange(SpecializationPenalties.GetLocalRole());

        ClearHighlights();
        if (!singleplayer && !scanner && range <= 0f) return;

        // BoxWithPart (dynamic assignment by PartSpawner)
        // UrgentPartEvent is independent of the normal order, so this scan must also run while
        // no regular order is active. GetHighlightColor filters every unrelated box back out.
        foreach (var bwp in BoxWithPart.All)
        {
            if (bwp == null) continue;
            if (!singleplayer && !scanner && Vector3.Distance(transform.position, bwp.transform.position) > range)
                continue;
            var color = GetHighlightColor(bwp.Part);
            if (color == null) continue;
            SetGoHighlight(bwp.gameObject, color.Value);
            _highlighted.Add(bwp.gameObject);
        }
    }

    private void ClearHighlights()
    {
        foreach (var go in _highlighted)
            if (go != null) SetGoHighlight(go, null);
        _highlighted.Clear();
    }

    private void SetGoHighlight(GameObject go, Color? color)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            var materials = r.sharedMaterials;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                var material = materials[materialIndex];
                if (material == null || !material.HasProperty(PowerID)) continue;

                _mpb.Clear();
                r.GetPropertyBlock(_mpb, materialIndex);
                if (color.HasValue)
                {
                    var c = color.Value;
                    _mpb.SetFloat(AnimateID, 5f);
                    _mpb.SetColor(ColorMapID, new Color(c.r * _hdrIntensityScale,
                                                       c.g * _hdrIntensityScale,
                                                       c.b * _hdrIntensityScale, c.a));
                    _mpb.SetFloat(PowerID, _highlightPower);

                    // The authored emission mask only covers selected faces on some box variants.
                    // A white override makes the entire material slot emit, so every side of the
                    // required-part box receives the same highlight.
                    if (material.HasProperty(EmissionMapID))
                        _mpb.SetTexture(EmissionMapID, Texture2D.whiteTexture);
                    if (material.HasProperty(ScaleID))
                        _mpb.SetFloat(ScaleID, 1f);
                }
                else
                {
                    _mpb.Clear();
                }
                r.SetPropertyBlock(_mpb, materialIndex);
            }
        }
    }

    public void ForceRefresh() => _nextScan = 0f;

    private void OnDisable() => ClearHighlights();
}
