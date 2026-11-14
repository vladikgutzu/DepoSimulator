using Unity.Cinemachine;
using UnityEngine;

/// Third-person spring-arm camera with wall collision, shoulder offset,
/// ground clamp, and distance-based player character fade.
/// Attach to the Player root alongside FirstPersonController.
/// CameraModeSwitcher enables/disables this when entering/leaving a vehicle.
[RequireComponent(typeof(FirstPersonController))]
public class TPSCameraController : MonoBehaviour
{
    [Header("Distance")]
    [Tooltip("Ideal camera distance from shoulder pivot.")]
    [SerializeField] float _followDistance = 3.5f;
    [Tooltip("Minimum distance when a wall is detected.")]
    [SerializeField] float _minDistance    = 0.6f;

    [Header("Pivot")]
    [Tooltip("Height above player root where the camera orbits.")]
    [SerializeField] float _pivotHeight = 1.55f;

    [Header("Shoulder Offset (player-local)")]
    [Tooltip("X > 0 = right shoulder. Y = up. Z = forward lean.")]
    [SerializeField] Vector3 _shoulderOffset = new Vector3(1.2f, 0.12f, 0f);

    [Header("Collision")]
    [Tooltip("Sphere radius for wall detection.")]
    [SerializeField] float _probeRadius = 0.12f;
    [Tooltip("Layers that block the camera. Exclude Player / Trigger layers.")]
    [SerializeField] LayerMask _collisionMask = ~0;

    [Header("Smoothing")]
    [Tooltip("How fast the camera springs back after a wall obstruction clears.")]
    [SerializeField] float _distanceLerpSpeed = 14f;

    [Header("Ground Clamp")]
    [Tooltip("Minimum gap between the camera and the detected floor surface.")]
    [SerializeField] float _minCameraHeight = 0.15f;

    [Header("Player Fade")]
    [Tooltip("Camera distance below which the player character begins to fade out.")]
    [SerializeField] float _playerFadeStartDist = 1.8f;
    [Tooltip("Camera distance at which the player character is fully transparent.")]
    [SerializeField] float _playerFadeEndDist   = 0.5f;
    [Tooltip("Fade transition speed (alpha units per second).")]
    [SerializeField] float _playerFadeSpeed     = 8f;

    // ── Internals ─────────────────────────────────────────────────────────────

    FirstPersonController _fpc;
    Camera                _mainCam;
    CinemachineBrain      _brain;
    float                 _currentDist;

    Transform _vehicleTarget;
    float     _vehicleDistance;

    // Player fade
    PlayerSkin   _skin;
    Renderer[]   _playerRenderers;
    Material[][] _playerOrigMats;
    Material[][] _playerFadeMats;
    float        _playerAlpha = 1f;

    /// True while this owns the body's materials. PlayerSkin reads it so it does not repaint over
    /// the fade instances and cancel the fade.
    public bool FadingPlayer => _playerRenderers != null;

    static readonly int _pidBaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int _pidSurface   = Shader.PropertyToID("_Surface");
    static readonly int _pidSrcBlend  = Shader.PropertyToID("_SrcBlend");
    static readonly int _pidDstBlend  = Shader.PropertyToID("_DstBlend");
    static readonly int _pidZWrite    = Shader.PropertyToID("_ZWrite");

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        _fpc  = GetComponent<FirstPersonController>();
        _skin = GetComponent<PlayerSkin>();
    }

    void Start()
    {
        _mainCam = Camera.main;
        if (_mainCam == null) { enabled = false; return; }

        _brain = _mainCam.GetComponent<CinemachineBrain>();
        _currentDist = _followDistance;

        // NOTE: do NOT auto-exclude gameObject.layer here. This project has no dedicated
        // "Player" layer — the player root (and most level geometry, including the floor)
        // sits on layer 0 "Default". Clearing that bit blinded both the wall SphereCast and
        // the ground-clamp Raycast to nearly the entire level, letting the camera sink through
        // the floor. The spring-arm cast origin (shoulder-offset, raised to pivot height) is
        // already well clear of the player's own capsule, so self-hits aren't a real risk.

        if (_brain != null) _brain.enabled = false;

        foreach (var innerCam in GetComponentsInChildren<Camera>(true))
            if (innerCam.gameObject != _mainCam.gameObject)
                innerCam.enabled = false;
    }

    // ── Vehicle target override ───────────────────────────────────────────────

    public void SetVehicleTarget(Transform target, float distance = 0f)
    {
        _vehicleTarget   = target;
        _vehicleDistance = distance > 0f ? distance : _followDistance;
        if (target != null) _currentDist = _vehicleDistance;
    }

    void OnEnable()
    {
        if (_brain != null) _brain.enabled = false;
    }

    void OnDisable()
    {
        if (_brain != null) _brain.enabled = true;

        // CameraModeSwitcher turns this off when the player gets into a forklift. Whatever the
        // fade had on the body at that moment would otherwise stay on it — half-transparent, and
        // never updated again, because nothing else drives this fade.
        RestorePlayerMats();
    }

    // ── Camera update ─────────────────────────────────────────────────────────

    void LateUpdate()
    {
        if (_mainCam == null || _fpc == null) return;

        bool vehicleMode = _vehicleTarget != null;

        float   yaw   = vehicleMode ? _vehicleTarget.eulerAngles.y : transform.eulerAngles.y;
        Vector3 pivot = vehicleMode ? _vehicleTarget.position
                                    : transform.position + Vector3.up * _pivotHeight;
        Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;

        Vector3 shoulderOffset = vehicleMode ? Vector3.zero : _shoulderOffset;
        Vector3 shoulderPivot  = pivot
            + right            * shoulderOffset.x
            + Vector3.up       * shoulderOffset.y
            + transform.forward * shoulderOffset.z;

        var     camRot = Quaternion.Euler(_fpc.Pitch, yaw, 0f);
        Vector3 camDir = camRot * Vector3.back;

        if (vehicleMode)
        {
            // ── Vehicle mode: no collision detection ──────────────────────────
            // The SphereCast would hit the vehicle body and snap the camera inside
            // the cab. CameraAnchor is pre-positioned outside the vehicle geometry.
            _currentDist = Mathf.Lerp(_currentDist, _vehicleDistance, Time.deltaTime * _distanceLerpSpeed);

            _mainCam.transform.position = shoulderPivot + camDir * _currentDist;
            _mainCam.transform.LookAt(pivot + Vector3.up * 0.5f);
            return;
        }

        // ── Spring-arm wall detection ─────────────────────────────────────────
        const float k_startOffset = 0.25f;
        Vector3 castOrigin = shoulderPivot + camDir * k_startOffset;
        float   castLength = Mathf.Max(0f, _followDistance - k_startOffset) + _probeRadius;

        float targetDist = _followDistance;
        if (Physics.SphereCast(castOrigin, _probeRadius, camDir, out RaycastHit hit,
                                castLength, _collisionMask, QueryTriggerInteraction.Ignore))
        {
            float wallDist = k_startOffset + hit.distance - _probeRadius;
            targetDist = Mathf.Max(_minDistance, wallDist);
        }

        // ── Geometric floor clamp on arm length ───────────────────────────────
        // When the camera arm points downward (e.g. player looks up at high shelves)
        // the floor may be excluded from _collisionMask (same layer as player).
        // We cap arm length so the camera stays above the player's feet plane —
        // applied both to targetDist and to _currentDist after the Lerp so the
        // Lerp can't overshoot during fast pitch changes.
        float maxDistFloor = float.MaxValue;
        if (camDir.y < -0.001f)
        {
            float headroom = shoulderPivot.y - (transform.position.y + _minCameraHeight);
            maxDistFloor = Mathf.Max(_minDistance, headroom / (-camDir.y));
        }
        targetDist = Mathf.Min(targetDist, maxDistFloor);

        // Smooth pull-in / spring-back
        _currentDist = Mathf.Lerp(_currentDist, targetDist, Time.deltaTime * _distanceLerpSpeed);
        _currentDist = Mathf.Min(_currentDist, maxDistFloor); // hard cap after lerp

        // ── Position camera ───────────────────────────────────────────────────
        Vector3 camPos = shoulderPivot + camDir * _currentDist;

        // ── Ground clamp: raycast-based (catches geometry on included layers) ─
        const float k_groundScanUp = 4f;
        if (Physics.Raycast(camPos + Vector3.up * k_groundScanUp, Vector3.down,
                            out RaycastHit groundHit, k_groundScanUp + 1f,
                            _collisionMask, QueryTriggerInteraction.Ignore))
        {
            float minY = groundHit.point.y + _minCameraHeight;
            if (camPos.y < minY) camPos.y = minY;
        }

        // Final safety: camera never below player feet regardless of layer config.
        float playerFloorY = transform.position.y + _minCameraHeight;
        if (camPos.y < playerFloorY)
            camPos.y = playerFloorY;

        _mainCam.transform.position = camPos;
        _mainCam.transform.LookAt(pivot + Vector3.up * 0.15f);

        // ── Player fade based on camera distance ──────────────────────────────
        UpdatePlayerFade();
    }

    // ── Player fade ───────────────────────────────────────────────────────────

    void UpdatePlayerFade()
    {
        float targetAlpha = Mathf.InverseLerp(_playerFadeEndDist, _playerFadeStartDist, _currentDist);
        _playerAlpha = Mathf.MoveTowards(_playerAlpha, targetAlpha, Time.deltaTime * _playerFadeSpeed);

        if (_playerAlpha >= 0.999f) { RestorePlayerMats(); return; }

        if (_playerRenderers == null) BeginPlayerFade();

        for (int i = 0; i < _playerRenderers.Length; i++)
        {
            var rend = _playerRenderers[i];
            if (rend == null) continue;

            // Create transparent material instances on demand.
            if (_playerFadeMats[i] == null)
            {
                var orig = _playerOrigMats[i];
                _playerFadeMats[i] = new Material[orig.Length];
                for (int j = 0; j < orig.Length; j++)
                {
                    _playerFadeMats[i][j] = new Material(orig[j]);
                    SetTransparentMode(_playerFadeMats[i][j]);
                }
                // Assign the array reference exactly once, when the fade materials are created —
                // reassigning `.materials` every frame (even to the same array) rebinds the
                // renderer and can break SRP batching. The colour updates below mutate those
                // already-assigned instances in place, which the renderer picks up on its own.
                rend.materials = _playerFadeMats[i];
            }

            // Apply current alpha to each sub-material.
            for (int j = 0; j < _playerFadeMats[i].Length; j++)
            {
                var orig = _playerOrigMats[i][j];
                Color c = orig.HasProperty(_pidBaseColor)
                    ? orig.GetColor(_pidBaseColor)
                    : Color.white;
                c.a = _playerAlpha;
                _playerFadeMats[i][j].SetColor(_pidBaseColor, c);
            }
        }
    }

    /// Takes the body as it looks right now, at the moment it starts to fade.
    ///
    /// Never in Start, and never kept between fades. PlayerSkin dresses the body from Update once
    /// the role has arrived over the network, which is several frames after Start — a capture
    /// taken there holds the model's stock albedo, and restoring it puts that stock albedo back
    /// over the role livery for good. See PlayerProximityFader.BeginFade for the same fix and the
    /// full account of what it looked like in co-op.
    void BeginPlayerFade()
    {
        var smrs = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        _playerRenderers = new Renderer[smrs.Length];
        _playerOrigMats  = new Material[smrs.Length][];
        _playerFadeMats  = new Material[smrs.Length][];
        for (int i = 0; i < smrs.Length; i++)
        {
            _playerRenderers[i] = smrs[i];
            _playerOrigMats[i]  = smrs[i].sharedMaterials;
        }
    }

    /// Puts the body back and drops the capture, so the next fade takes a fresh one.
    void RestorePlayerMats()
    {
        if (_playerRenderers == null) return;

        for (int i = 0; i < _playerRenderers.Length; i++)
        {
            if (_playerRenderers[i] != null && _playerOrigMats[i] != null)
            {
                // The livery as of now rather than as of when the fade started: a role picked, or
                // a body swapped, while the camera was inside the player must survive the way out.
                Material[] live = null;
                if (_skin != null) live = _skin.WornMaterials(_playerRenderers[i]);

                _playerRenderers[i].sharedMaterials = live != null ? live : _playerOrigMats[i];
            }

            DestroyFadeMats(i);
        }

        _playerRenderers = null;
        _playerOrigMats  = null;
        _playerFadeMats  = null;
    }

    static void SetTransparentMode(Material m)
    {
        m.SetFloat(_pidSurface, 1f);
        m.SetInt(_pidSrcBlend,  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt(_pidDstBlend,  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt(_pidZWrite,    0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = 3000;
    }

    void DestroyFadeMats(int i)
    {
        if (_playerFadeMats[i] == null) return;
        foreach (var m in _playerFadeMats[i])
            if (m != null) Destroy(m);
        _playerFadeMats[i] = null;
    }

    void OnDestroy() => RestorePlayerMats();
}
