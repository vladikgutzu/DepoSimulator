using UnityEngine;

// World-space beacon spawned by PingSystem. Auto-destroys after lifetime.
public class PingMarker : MonoBehaviour
{
    [SerializeField] private float _lifetime = 8f;
    [SerializeField] private Color _color    = new Color(1f, 0.55f, 0f);

    private static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");

    private float    _spawnTime;
    private Renderer _rend;
    private Material _mat;

    public static PingMarker Spawn(Vector3 position, Color color, float lifetime = 8f)
    {
        var marker = new GameObject("PingMarker");
        marker.transform.position = position;
        var ping = marker.AddComponent<PingMarker>();
        ping.Configure(color, lifetime);
        return ping;
    }

    private void Awake()
    {
        _spawnTime = Time.time;

        var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cyl.transform.SetParent(transform);
        cyl.transform.localPosition = new Vector3(0f, 0.5f, 0f);
        cyl.transform.localScale    = new Vector3(0.12f, 0.5f, 0.12f);
        Destroy(cyl.GetComponent<Collider>());

        _rend = cyl.GetComponent<Renderer>();
        _mat  = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _mat.color = _color;
        _mat.EnableKeyword("_EMISSION");
        _mat.SetColor(EmissionColorID, _color * 1.8f);
        _rend.material = _mat;
    }

    public void Configure(Color color, float lifetime)
    {
        _color    = color;
        _lifetime = Mathf.Max(1f, lifetime);

        if (_mat == null) return;
        _mat.color = _color;
        _mat.SetColor(EmissionColorID, _color * 1.8f);
    }

    private void Update()
    {
        float age   = Time.time - _spawnTime;
        float fade  = 1f - Mathf.Clamp01((age - _lifetime * 0.7f) / (_lifetime * 0.3f));
        _mat.color  = new Color(_color.r, _color.g, _color.b, fade);
        _mat.SetColor(EmissionColorID, _color * 1.8f * fade);

        // Pulse scale to draw attention
        float pulse = 1f + 0.15f * Mathf.Sin(Time.time * 5f);
        transform.localScale = Vector3.one * pulse;

        if (age >= _lifetime) Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (_mat != null) Destroy(_mat);
    }
}
