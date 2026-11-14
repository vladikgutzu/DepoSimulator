using UnityEngine;

/// Fades the player's own renderers to transparent when the camera gets too close.
/// Attach to the Player root alongside TPSCameraController.
[RequireComponent(typeof(CharacterController))]
public class PlayerProximityFader : MonoBehaviour
{
    [Tooltip("Distance at which the player starts fading out.")]
    [SerializeField] float _fadeStartDistance = 1.5f;
    [Tooltip("Distance at which the player is fully at minimum alpha.")]
    [SerializeField] float _fadeEndDistance = 0.5f;
    [Tooltip("Minimum alpha when fully close (0 = fully invisible).")]
    [Range(0f, 1f)]
    [SerializeField] float _minAlpha = 0f;
    [Tooltip("Fade transition speed (alpha units/sec).")]
    [SerializeField] float _fadeSpeed = 12f;

    Camera     _cam;
    PlayerSkin _skin;
    Renderer[] _renderers;
    Material[][] _origSharedMats;
    Material[][] _fadeMats;
    float _currentAlpha  = 1f;
    bool  _fadeMatsActive = false;

    /// True while this owns the body's materials. PlayerSkin reads it so it does not repaint
    /// over the fade instances and cancel the fade.
    public bool Fading => _fadeMatsActive;

    static readonly int _idSurface   = Shader.PropertyToID("_Surface");
    static readonly int _idBlend     = Shader.PropertyToID("_Blend");
    static readonly int _idSrcBlend  = Shader.PropertyToID("_SrcBlend");
    static readonly int _idDstBlend  = Shader.PropertyToID("_DstBlend");
    static readonly int _idZWrite    = Shader.PropertyToID("_ZWrite");
    static readonly int _idBaseColor = Shader.PropertyToID("_BaseColor");

    void Start()
    {
        _cam  = Camera.main;
        _skin = GetComponent<PlayerSkin>();
    }

    /// Takes the body as it looks right now, at the moment it starts to fade.
    ///
    /// This used to happen once, in Start, and was the reason co-op players saw themselves in a
    /// colour nobody else saw. PlayerSkin dresses the body from Update, after the role arrives
    /// over the network — several frames after Start — so what got captured here was the model's
    /// stock albedo, and the restore below put that stock albedo back over the role livery the
    /// first time the camera came close. Permanent, because PlayerSkin had no reason to repaint:
    /// the role had not changed. Remote puppets carry no fader, so everyone else went on seeing
    /// the right colour, which is why it read as a sync bug rather than a local one.
    ///
    /// Re-walking the hierarchy is part of it: a body swapped mid-shift leaves the Start-time
    /// renderer list pointing at the retired robot.
    void BeginFade()
    {
        _renderers      = GetComponentsInChildren<Renderer>(false);
        _origSharedMats = new Material[_renderers.Length][];
        _fadeMats       = new Material[_renderers.Length][];
        for (int i = 0; i < _renderers.Length; i++)
            _origSharedMats[i] = _renderers[i].sharedMaterials;
    }

    void LateUpdate()
    {
        if (_cam == null) { _cam = Camera.main; return; }

        float dist        = Vector3.Distance(_cam.transform.position, transform.position);
        float targetAlpha = Mathf.Lerp(_minAlpha, 1f,
                                Mathf.InverseLerp(_fadeEndDistance, _fadeStartDistance, dist));

        _currentAlpha = Mathf.MoveTowards(_currentAlpha, targetAlpha, Time.deltaTime * _fadeSpeed);

        if (_currentAlpha >= 0.999f)
        {
            if (_fadeMatsActive)
            {
                RestoreAndCleanup();
                _fadeMatsActive = false;
            }
        }
        else
        {
            if (!_fadeMatsActive) BeginFade();
            ApplyFade(_currentAlpha);
            _fadeMatsActive = true;
        }
    }

    void ApplyFade(float alpha)
    {
        if (_renderers == null) return;

        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null) continue;

            if (_fadeMats[i] == null)
            {
                var orig = _origSharedMats[i];
                _fadeMats[i] = new Material[orig.Length];
                for (int j = 0; j < orig.Length; j++)
                {
                    _fadeMats[i][j] = new Material(orig[j]);
                    SetTransparentMode(_fadeMats[i][j]);
                }
                // Assign the array reference exactly once, when the fade materials are created —
                // reassigning `.materials` every frame (even to the same array) rebinds the
                // renderer and can break SRP batching. The colour updates below mutate those
                // already-assigned instances in place, which the renderer picks up on its own.
                _renderers[i].materials = _fadeMats[i];
            }

            for (int j = 0; j < _fadeMats[i].Length; j++)
            {
                var src = _origSharedMats[i][j];
                Color c = src.HasProperty(_idBaseColor) ? src.GetColor(_idBaseColor) : Color.white;
                c.a = alpha;
                _fadeMats[i][j].SetColor(_idBaseColor, c);
            }
        }
    }

    void RestoreAndCleanup()
    {
        // BeginFade builds these three together, so a null here means no fade ever started.
        // OnDestroy fires anyway, and used to walk straight into it.
        if (_renderers == null || _origSharedMats == null || _fadeMats == null) return;

        for (int i = 0; i < _renderers.Length; i++)
        {
            // A destroyed renderer needs no restoring, and a null original array would put a null
            // into sharedMaterials rather than the materials it started with.
            if (_renderers[i] != null && _origSharedMats[i] != null)
            {
                // The livery as of now, not as of when the fade started: a role picked, or a body
                // swapped, while the camera was inside the player must not be undone on the way
                // out. Only the capture knows what a piece the livery does not cover — a carried
                // box, a weapon in the fist — was wearing, so that is the fallback.
                Material[] live = null;
                if (_skin != null) live = _skin.WornMaterials(_renderers[i]);

                _renderers[i].sharedMaterials = live != null ? live : _origSharedMats[i];
            }

            if (_fadeMats[i] != null)
            {
                foreach (var m in _fadeMats[i])
                    if (m != null) Destroy(m);
                _fadeMats[i] = null;
            }
        }

        // Dropped rather than kept: a capture is only good for the fade it was taken for, and
        // holding a stale one is precisely the bug this file used to have.
        _renderers      = null;
        _origSharedMats = null;
        _fadeMats       = null;
    }

    static void SetTransparentMode(Material m)
    {
        m.SetFloat(_idSurface, 1f);
        m.SetFloat(_idBlend,   0f);
        m.SetInt(_idSrcBlend,  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt(_idDstBlend,  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt(_idZWrite,    0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = 3000;
    }

    void OnDestroy()
    {
        RestoreAndCleanup();
    }
}
