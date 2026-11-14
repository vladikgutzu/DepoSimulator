using UnityEngine;

/// Creates a top-down orthographic camera that follows the player and
/// renders to a RenderTexture displayed on the HUD mini map.
/// Attach to any GO in the scene; it self-constructs the camera in Awake.
public class MiniMapCamera : MonoBehaviour
{
    [SerializeField] private Transform _target;
    [SerializeField] private float _height      = 28f;
    [SerializeField] private float _orthoSize   = 20f;
    [SerializeField] private int   _rtResolution = 256;

    private Camera        _cam;
    private RenderTexture _rt;

    private void Awake()
    {
        // MiniMapCamera is disabled — MinimapController now draws a 2D schematic
        // minimap via UI Toolkit VisualElements and does not need a RenderTexture.
        // Keeping this component for future reference only.
        enabled = false;
    }

    private void AwakeOriginal()
    {
        _rt = new RenderTexture(_rtResolution, _rtResolution, 16, RenderTextureFormat.ARGB32);
        _rt.filterMode = FilterMode.Bilinear;
        _rt.Create();

        var camGO = new GameObject("MiniMapCamGO");
        camGO.transform.SetParent(null);
        _cam = camGO.AddComponent<Camera>();
        _cam.orthographic     = true;
        _cam.orthographicSize = _orthoSize;
        _cam.clearFlags       = CameraClearFlags.SolidColor;
        _cam.backgroundColor  = new Color(0.04f, 0.06f, 0.10f, 1f);
        _cam.depth            = -2;
        _cam.targetTexture    = _rt;
        _cam.cullingMask = ~(1 << LayerMask.NameToLayer("UI"));
        camGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    }

    private void Start()
    {
        if (_target == null)
        {
            var fp = FindObjectOfType<FirstPersonController>();
            if (fp != null) _target = fp.transform;
        }

        // HUD will receive minimap texture when HUDController is available
    }

    private void LateUpdate()
    {
        if (_target == null || _cam == null) return;
        _cam.transform.position = new Vector3(_target.position.x, _height, _target.position.z);
    }

    private void OnDestroy()
    {
        if (_cam != null) Destroy(_cam.gameObject);
        if (_rt  != null) _rt.Release();
    }
}
