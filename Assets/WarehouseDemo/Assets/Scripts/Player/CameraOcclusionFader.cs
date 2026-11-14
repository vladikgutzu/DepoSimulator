using UnityEngine;
using System.Collections.Generic;

// Attach to Player.
// Each frame raycasts from Camera → Player head. Any URP Lit renderer in the way
// gets a transparent material instance and fades to _fadeAlpha.
// When the line of sight clears, the object fades back to opaque and the
// original shared materials are restored (no permanent scene changes).
[RequireComponent(typeof(CharacterController))]
public class CameraOcclusionFader : MonoBehaviour
{
    [Tooltip("Alpha when the object is occluding the player (0 = invisible, 0.2 = ghost)")]
    [Range(0f, 0.5f)]
    [SerializeField] float _fadeAlpha = 0.15f;

    [Tooltip("How fast the fade transition is (units/sec)")]
    [SerializeField] float _fadeSpeed = 10f;

    [Tooltip("Layers to check for occlusion. Set to shelf/wall layers only.")]
    [SerializeField] LayerMask _occlusionMask = ~0; // everything by default

    [Tooltip("Height offset to aim the raycast at (player eye/head level)")]
    [SerializeField] float _headHeight = 1.4f;

    // ── Internal ─────────────────────────────────────────────────────────────

    Camera _cam;

    sealed class FadeState
    {
        public Renderer   renderer;
        public Material[] origSharedMats;
        public Material[] fadeMats;       // lazy-created, kept for reuse
        public float      alpha   = 1f;
        public bool       occluding;
    }

    // Key = renderer.GetInstanceID() to avoid Destroyed-object hash issues.
    readonly Dictionary<int, FadeState> _tracked  = new();
    readonly List<int>                  _toRemove = new();

    // Boxes share the same physics layer as walls/shelves (both "Default"), so the occlusion
    // mask alone can't exclude them from RaycastAll — this dictionary caches each hit collider's
    // renderer list so a box/shelf sitting in the camera ray for many consecutive frames (the
    // common case) doesn't re-allocate a Renderer[] via GetComponentsInChildren every frame.
    readonly Dictionary<Collider, Renderer[]> _rendererCache = new();

    // Cached shader property IDs (Shader.PropertyToID is O(1) after first call).
    static readonly int _idSurface   = Shader.PropertyToID("_Surface");
    static readonly int _idBlend     = Shader.PropertyToID("_Blend");
    static readonly int _idSrcBlend  = Shader.PropertyToID("_SrcBlend");
    static readonly int _idDstBlend  = Shader.PropertyToID("_DstBlend");
    static readonly int _idZWrite    = Shader.PropertyToID("_ZWrite");
    static readonly int _idBaseColor = Shader.PropertyToID("_BaseColor");

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Start()
    {
        _cam = Camera.main;
    }

    void LateUpdate()
    {
        if (_cam == null) { _cam = Camera.main; return; }

        // ── Reset occlusion flags ─────────────────────────────────────────
        foreach (var s in _tracked.Values) s.occluding = false;

        // ── Raycast camera → player head ──────────────────────────────────
        var camPos  = _cam.transform.position;
        var headPos = transform.position + Vector3.up * _headHeight;
        var dir     = headPos - camPos;
        float dist  = dir.magnitude;

        if (dist < 0.01f) return;

        var hits = Physics.RaycastAll(camPos, dir / dist, dist - 0.15f,
                                      _occlusionMask, QueryTriggerInteraction.Ignore);

        foreach (var hit in hits)
        {
            // Don't fade the player or any trigger volumes
            if (hit.collider.CompareTag("Player")) continue;

            // Boxes are meant to stay solid (only shelves/walls should ghost) and are
            // numerous/dynamic enough that tracking them here is pure waste — skip.
            if (hit.collider.GetComponentInParent<BoxPhysicsInteractable>() != null) continue;

            if (!_rendererCache.TryGetValue(hit.collider, out var renderers))
            {
                renderers = hit.collider.GetComponentsInChildren<Renderer>(false);
                _rendererCache[hit.collider] = renderers;
            }
            foreach (var rend in renderers)
            {
                int id = rend.GetInstanceID();
                if (!_tracked.TryGetValue(id, out var state))
                {
                    state = new FadeState
                    {
                        renderer      = rend,
                        origSharedMats = rend.sharedMaterials,
                    };
                    _tracked[id] = state;
                }
                state.occluding = true;
            }
        }

        // ── Update alpha and apply / restore materials ────────────────────
        _toRemove.Clear();
        foreach (var kvp in _tracked)
        {
            var state = kvp.Value;

            // Renderer was destroyed (e.g. stacked box picked up)
            if (state.renderer == null) { _toRemove.Add(kvp.Key); continue; }

            float target = state.occluding ? _fadeAlpha : 1f;
            state.alpha  = Mathf.MoveTowards(state.alpha, target,
                                              Time.deltaTime * _fadeSpeed);

            if (state.alpha >= 0.999f && !state.occluding)
            {
                // Fully restored — give back shared materials, stop tracking
                state.renderer.sharedMaterials = state.origSharedMats;
                DestroyFadeMats(state);
                _toRemove.Add(kvp.Key);
            }
            else
            {
                ApplyFade(state);
            }
        }

        foreach (var id in _toRemove) _tracked.Remove(id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void ApplyFade(FadeState state)
    {
        // Create transparent material instances once per renderer
        if (state.fadeMats == null)
        {
            var orig = state.origSharedMats;
            state.fadeMats = new Material[orig.Length];
            for (int i = 0; i < orig.Length; i++)
            {
                state.fadeMats[i] = new Material(orig[i]);
                SetTransparentMode(state.fadeMats[i]);
            }
            // Assign the array reference exactly once — reassigning `.materials` every frame
            // (even to the same array) rebinds the renderer and can break SRP batching. The
            // colour updates below mutate these already-assigned instances in place.
            state.renderer.materials = state.fadeMats;
        }

        // Update alpha on each fade material
        for (int i = 0; i < state.fadeMats.Length; i++)
        {
            var src = state.origSharedMats[i];
            Color c = src.HasProperty(_idBaseColor)
                ? src.GetColor(_idBaseColor)
                : Color.white;
            c.a = state.alpha;
            state.fadeMats[i].SetColor(_idBaseColor, c);
        }
    }

    // Switches a URP Lit material instance to Alpha-blend transparent mode.
    static void SetTransparentMode(Material m)
    {
        m.SetFloat(_idSurface,  1f);  // Transparent
        m.SetFloat(_idBlend,    0f);  // Alpha blend
        m.SetInt(_idSrcBlend,   (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt(_idDstBlend,   (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt(_idZWrite,     0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = 3000; // Transparent queue
    }

    static void DestroyFadeMats(FadeState state)
    {
        if (state.fadeMats == null) return;
        foreach (var m in state.fadeMats)
            if (m != null) Destroy(m);
        state.fadeMats = null;
    }

    void OnDestroy()
    {
        // Restore all renderers so no scene objects stay transparent after play stops
        foreach (var state in _tracked.Values)
        {
            if (state.renderer != null)
                state.renderer.sharedMaterials = state.origSharedMats;
            DestroyFadeMats(state);
        }
        _tracked.Clear();
    }
}
