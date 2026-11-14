using UnityEngine;

/// Legacy compatibility component. Fragile cargo was removed from the demo; keeping the type
/// prevents old prefabs/scenes that still serialize it from producing a missing-script warning.
[RequireComponent(typeof(BoxPhysicsInteractable))]
public class FragileBox : MonoBehaviour
{
    [SerializeField] float _breakImpulseThreshold = 12f;
    [SerializeField] int   _scorePenalty          = 50;

    public bool IsBroken { get; private set; }

    BoxPhysicsInteractable _box;
    Renderer[]             _renderers;
    Material[]             _materials;     // instantiated per-renderer, reused
    Color[]                _originalColors;

    static readonly Color BrokenTint = new(0.55f, 0.25f, 0.15f);

    void Awake()
    {
        _box = GetComponent<BoxPhysicsInteractable>();
    }

    void Start()
    {
        CacheRenderers();
    }

    void CacheRenderers()
    {
        _renderers      = GetComponentsInChildren<Renderer>(true);
        _materials      = new Material[_renderers.Length];
        _originalColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
        {
            _materials[i]      = _renderers[i].material;  // instantiate once, reuse
            _originalColors[i] = _materials[i].HasProperty("_Color_Map")
                ? _materials[i].GetColor("_Color_Map")
                : Color.white;
        }
    }

    void OnCollisionEnter(Collision col) { }

    /// Called directly in solo or via ClientRpc in multiplayer.
    public void BreakLocal()
    {
        // Intentionally inert. See class comment.
    }

    static readonly string[] ColorProps = { "_Color_Map", "_BaseColor", "_Color" };
    static readonly string[] DirtProps  = { "_Dirt_Color_01", "_Dirt_Color_02" };

    void ApplyBrokenVisual()
    {
        if (_materials == null) CacheRenderers();
        for (int i = 0; i < _materials.Length; i++)
        {
            var mat = _materials[i];
            if (mat == null) continue;
            foreach (var prop in ColorProps)
                if (mat.HasProperty(prop)) { mat.SetColor(prop, BrokenTint); break; }
            foreach (var prop in DirtProps)
                if (mat.HasProperty(prop)) mat.SetColor(prop, new Color(0.3f, 0.1f, 0.05f));
        }
    }

    // Returns the first cached material (for testing).
    public Material GetFirstMaterial() => _materials != null && _materials.Length > 0 ? _materials[0] : null;
}
