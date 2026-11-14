using System.Collections.Generic;
using UnityEngine;

/// Adds a highlight to tutorial-target objects.
///
/// Two modes, chosen automatically:
///   • Objects WITH a FloorHighlightTarget component → pulsing border quad on the floor.
///   • All other objects with renderers → outline material appended to sharedMaterials.
///   • Objects with no renderers (trigger-only) → pulsing gold sphere marker above them.
public class TutorialHighlighter : MonoBehaviour
{
    static readonly List<TutorialHighlighter> _active = new();
    static Material _outlineMat;
    static Material _indicatorMat;
    static Material _floorMarkerMat;

    readonly List<(Renderer r, Material[] orig)> _renderers = new();
    GameObject _indicator;
    GameObject _floorMarker;
    Vector3    _indicatorBase;

    // ── Static API ────────────────────────────────────────────────────────────

    public static TutorialHighlighter Highlight(GameObject go)
    {
        if (go == null) return null;
        var existing = go.GetComponent<TutorialHighlighter>();
        return existing != null ? existing : go.AddComponent<TutorialHighlighter>();
    }

    public static void ClearAll()
    {
        for (int i = _active.Count - 1; i >= 0; i--)
            if (_active[i] != null) _active[i].StopGlow();
        _active.Clear();
    }

    public static bool HasAnyNear(Vector3 pos, float radius)
    {
        foreach (var h in _active)
            if (h != null && Vector3.Distance(h.transform.position, pos) < radius)
                return true;
        return false;
    }

    // ── Instance ──────────────────────────────────────────────────────────────

    void Awake()
    {
        _active.Add(this);

        // FloorHighlightTarget present → use floor border quad, skip mesh outline. The assembly
        // bench is intentionally different: the tutorial points at the physical workstation, just
        // like it points at the order board, so highlight all of its child renderers instead of
        // drawing an abstract square around it.
        var fht = GetComponent<FloorHighlightTarget>();
        if (fht != null && GetComponent<AssemblyBench>() == null)
        {
            _floorMarker = CreateFloorMarker(fht);
            return;
        }

        // Default path: append outline material to every child renderer
        var mat = GetOutlineMat();
        if (mat != null)
        {
            foreach (var r in GetComponentsInChildren<Renderer>(false))
            {
                var orig = r.sharedMaterials;
                var dst  = new Material[orig.Length + 1];
                System.Array.Copy(orig, dst, orig.Length);
                dst[orig.Length] = mat;
                r.sharedMaterials = dst;
                _renderers.Add((r, orig));
            }
        }

        // Fallback for trigger-only volumes (no renderers): spawn a pulsing marker
        if (_renderers.Count == 0 && _floorMarker == null)
            _indicator = CreateIndicator();
    }

    void Update()
    {
        if (_indicator == null) return;
        float scale = 0.35f + Mathf.Sin(Time.time * 3f) * 0.07f;
        _indicator.transform.localScale    = Vector3.one * scale;
        _indicator.transform.localPosition = _indicatorBase + new Vector3(0f, Mathf.Sin(Time.time * 2f) * 0.12f, 0f);
    }

    void OnDestroy()
    {
        _active.Remove(this);
        Restore();
    }

    public void StopGlow()
    {
        Restore();
        Destroy(this);
    }

    void Restore()
    {
        foreach (var (r, orig) in _renderers)
            if (r != null) r.sharedMaterials = orig;
        _renderers.Clear();

        if (_indicator   != null) { Destroy(_indicator);   _indicator   = null; }
        if (_floorMarker != null) { Destroy(_floorMarker); _floorMarker = null; }
    }

    // ── Floor marker (for FloorHighlightTarget objects) ───────────────────────

    GameObject CreateFloorMarker(FloorHighlightTarget target)
    {
        // Collect all colliders to compute the footprint
        var cols = GetComponentsInChildren<Collider>(true);

        float floorY = transform.position.y;
        float width, depth;

        if (cols.Length > 0)
        {
            var bounds = cols[0].bounds;
            foreach (var c in cols) bounds.Encapsulate(c.bounds);
            floorY = bounds.min.y;
            width  = bounds.size.x + target.Padding * 2f;
            depth  = bounds.size.z + target.Padding * 2f;
        }
        else
        {
            // No collider: raycast downward from the object's centre
            if (Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out var hit, 10f))
                floorY = hit.point.y;
            width = 2f;
            depth = 2f;
        }

        // Manual overrides take priority over auto-computed values
        if (target.WidthOverride > 0f) width = target.WidthOverride;
        if (target.DepthOverride > 0f) depth = target.DepthOverride;

        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "TutorialFloorMarker";
        Destroy(go.GetComponent<Collider>());

        // Flat on the floor: Quad faces +Z by default, rotate so it faces up
        go.transform.position   = new Vector3(transform.position.x, floorY + target.YOffset, transform.position.z);
        go.transform.rotation   = Quaternion.Euler(90f, 0f, 0f);
        go.transform.localScale = new Vector3(width, depth, 1f);

        var r = go.GetComponent<Renderer>();
        if (r != null) r.sharedMaterial = GetFloorMarkerMat();

        return go;
    }

    // ── Indicator (fallback for trigger-only objects) ─────────────────────────

    GameObject CreateIndicator()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "TutorialHighlight_Indicator";
        go.transform.SetParent(transform, false);
        _indicatorBase             = IndicatorLocalPos();
        go.transform.localPosition = _indicatorBase;
        go.transform.localScale    = Vector3.one * 0.35f;
        Destroy(go.GetComponent<Collider>());

        var r = go.GetComponent<Renderer>();
        if (r != null) r.sharedMaterial = GetIndicatorMat();

        return go;
    }

    Vector3 IndicatorLocalPos()
    {
        var col = GetComponent<Collider>();
        if (col != null)
        {
            var localCenter = transform.InverseTransformPoint(col.bounds.center);
            return new Vector3(localCenter.x, localCenter.y + col.bounds.extents.y + 0.6f, localCenter.z);
        }
        return new Vector3(0f, 1.2f, 0f);
    }

    // ── Material helpers ──────────────────────────────────────────────────────

    static Material GetFloorMarkerMat()
    {
        if (_floorMarkerMat != null) return _floorMarkerMat;

        _floorMarkerMat = Resources.Load<Material>("FloorMarker_Tutorial");
        if (_floorMarkerMat != null) return _floorMarkerMat;

        var shader = Shader.Find("Custom/FloorMarkerBorder");
        if (shader == null)
        {
            Debug.LogWarning("[TutorialHighlighter] FloorMarkerBorder shader not found.");
            return null;
        }
        _floorMarkerMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        // Gold, matching PartHighlighter's always-on required-part glow — blue is reserved for
        // the "currently aimed at" look-at outline (PlayerInteraction's FPS_Outline_Can).
        _floorMarkerMat.SetColor("_Color",       new Color(0.961f, 0.651f, 0.137f, 1f));
        _floorMarkerMat.SetFloat("_BorderWidth", 0.06f);
        _floorMarkerMat.SetFloat("_PulseSpeed",  2.5f);
        _floorMarkerMat.SetFloat("_MinAlpha",    0.25f);
        _floorMarkerMat.SetFloat("_MaxAlpha",    1.00f);
        return _floorMarkerMat;
    }

    static Material GetIndicatorMat()
    {
        if (_indicatorMat != null) return _indicatorMat;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Color");

        if (shader == null)
        {
            Debug.LogWarning("[TutorialHighlighter] No unlit shader found for fallback indicator.");
            return null;
        }

        _indicatorMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        _indicatorMat.SetColor("_BaseColor", new Color(0.961f, 0.651f, 0.137f, 1f));
        return _indicatorMat;
    }

    static Material GetOutlineMat()
    {
        if (_outlineMat != null) return _outlineMat;

        _outlineMat = Resources.Load<Material>("FPS_Outline_Tutorial");
        if (_outlineMat != null) return _outlineMat;

        var shader = Shader.Find("Custom/FPS_Tutorial_Outline")
                  ?? Shader.Find("Custom/FPS_Interact_Outline");
        if (shader == null)
        {
            Debug.LogWarning("[TutorialHighlighter] FPS_Tutorial_Outline shader not found.");
            return null;
        }
        _outlineMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        // Gold, matching PartHighlighter's always-on required-part glow — blue is reserved for
        // the "currently aimed at" look-at outline (PlayerInteraction's FPS_Outline_Can).
        // Alpha 0.45 matches FPS_Tutorial_Outline.shader's own documented default — that shader
        // is a semi-transparent front-face fill (not a thin rim like FPS_Interact_Outline), so a
        // fully opaque alpha here paints the whole silhouette as a flat matte blob and hides the
        // object's real material/texture underneath instead of just tinting it.
        _outlineMat.SetColor("_OutlineColor", new Color(0.961f, 0.651f, 0.137f, 0.45f));
        _outlineMat.SetFloat("_OutlineWidth", 0.015f);
        return _outlineMat;
    }
}
