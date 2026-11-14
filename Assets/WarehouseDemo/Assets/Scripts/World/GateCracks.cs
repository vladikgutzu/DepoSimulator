using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// Splits a gate open, visually, while raiders beat on it.
///
/// The gate already shakes and thumps for twenty seconds before it goes up, and until now that was
/// the only sign anything was happening to it — the shutter came up undamaged, as if it had been
/// opened rather than forced. This puts the damage on the panel: a crack network that grows out
/// from where they are hitting it, one step per blow.
///
/// Nothing is authored per gate and nothing is added to the scene. The component is created at
/// runtime by whatever raid is using the gate and takes itself apart afterwards, so a gate that is
/// never attacked carries no cost and no scene diff.
///
/// The cracks are drawn as an extra material on the panel's own renderer rather than by replacing
/// its material or duplicating the mesh. A renderer given more materials than the mesh has
/// submeshes draws the last submesh again for each spare one, which is exactly the overlay pass
/// wanted here: the gate keeps its authored look untouched underneath, and there is no second
/// object to keep in step with the animation.
[DisallowMultipleComponent]
public class GateCracks : MonoBehaviour
{
    /// Under Resources so the shader survives build stripping: a material only ever created from
    /// script references no asset, and its shader gets stripped out of the player.
    const string MaterialResource = "Materials/GateCracks";

    /// The parts of the gate that are sheet metal and can therefore tear. The rollers, the window
    /// and the floor decal are deliberately left out — a cracked roller reads as a bug.
    static readonly string[] PanelNames = { "gate_01", "gate_pane_01" };

    static readonly int ProgressId   = Shader.PropertyToID("_Progress");
    static readonly int OriginId     = Shader.PropertyToID("_Origin");
    static readonly int BoundsMinId  = Shader.PropertyToID("_BoundsMin");
    static readonly int BoundsSizeId = Shader.PropertyToID("_BoundsSize");
    static readonly int AxisUId      = Shader.PropertyToID("_AxisU");
    static readonly int AxisVId      = Shader.PropertyToID("_AxisV");

    /// One sheet of the gate, with the panel-space frame the shader needs to draw on it.
    class Panel
    {
        public Renderer Renderer;
        public Material Material;      // an instance, ours to destroy
        public Vector3  Min, Size, AxisU, AxisV;
    }

    readonly List<Panel> _panels = new();
    bool    _built;
    float   _progress;
    Vector3 _origin;
    bool    _hasOrigin;

    /// How far gone the gate is, 0 whole and 1 in pieces.
    public float Progress => _progress;

    /// Fits cracks to a gate, or hands back the ones already on it.
    ///
    /// Returns null when the gate has no sheet-metal panel or the material is missing, so a broken
    /// setup costs the raid nothing but its cracks.
    public static GateCracks Ensure(GateAnimator gate)
    {
        if (gate == null) return null;

        var cracks = gate.GetComponent<GateCracks>();
        if (cracks == null) cracks = gate.gameObject.AddComponent<GateCracks>();

        cracks.Build();

        return cracks._panels.Count > 0 ? cracks : null;
    }

    void Awake() => Build();

    /// Safe to call more than once — Ensure leans on that, and so does Awake on a component that
    /// was already set up by the raid that added it.
    void Build()
    {
        if (_built) return;
        _built = true;

        var material = Resources.Load<Material>(MaterialResource);
        if (material == null)
        {
            Debug.LogWarning($"[GateCracks] no material at Resources/{MaterialResource} — " +
                             "run Tools/Warehouse/Build Gate Crack Material. Gate will not crack.");
            return;
        }

        foreach (string panelName in PanelNames)
        {
            var target = GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.name == panelName);

            var renderer = target != null ? target.GetComponent<MeshRenderer>() : null;
            var filter   = target != null ? target.GetComponent<MeshFilter>()   : null;

            if (renderer == null || filter == null || filter.sharedMesh == null) continue;

            var bounds = filter.sharedMesh.bounds;

            // Which way the sheet faces. The gates in this warehouse are not all modelled on the
            // same axis, so the flat plane is found rather than assumed: of the three local axes,
            // the one the panel is thinnest along is its normal, and the other two span the face.
            int normal = 0;
            if (bounds.size.y < bounds.size[normal]) normal = 1;
            if (bounds.size.z < bounds.size[normal]) normal = 2;

            var spanning = new[] { 0, 1, 2 }.Where(i => i != normal).ToArray();

            var panel = new Panel
            {
                Renderer = renderer,
                Material = new Material(material) { name = $"{material.name} ({panelName})" },
                Min      = bounds.min,
                Size     = bounds.size,
                AxisU    = Axis(spanning[0]),
                AxisV    = Axis(spanning[1]),
            };

            // Appended, not substituted: slot 0 stays the gate's own painted metal.
            //
            // Any crack material already on the renderer is stripped first. A stale one is left
            // behind whenever this component is destroyed in a way that skips OnDestroy — a domain
            // reload in the editor does exactly that — and without this they stack up a slot at a
            // time until the panel is drawn five times over.
            var slots = renderer.sharedMaterials.Where(m => !IsOurs(m)).ToList();
            slots.Add(panel.Material);
            renderer.sharedMaterials = slots.ToArray();

            panel.Material.SetVector(BoundsMinId,  panel.Min);
            panel.Material.SetVector(BoundsSizeId, panel.Size);
            panel.Material.SetVector(AxisUId,      panel.AxisU);
            panel.Material.SetVector(AxisVId,      panel.AxisV);
            panel.Material.SetFloat (ProgressId,   0f);

            _panels.Add(panel);
        }
    }

    /// True for a material this component put on a renderer, whether or not it was this instance
    /// that put it there. Matched on the shader rather than the material, because every panel gets
    /// its own copy and a previous life's copies are unreachable from here.
    static bool IsOurs(Material material)
    {
        var reference = Resources.Load<Material>(MaterialResource);
        return material != null && reference != null && material.shader == reference.shader;
    }

    static Vector3 Axis(int index) =>
        index == 0 ? Vector3.right : index == 1 ? Vector3.up : Vector3.forward;

    /// Where they are hitting it, in world space. Everything spreads from here, so it is worth
    /// setting to the actual doorway rather than letting each panel crack around its own middle.
    public void SetOrigin(Vector3 worldPoint)
    {
        _origin    = worldPoint;
        _hasOrigin = true;
        PushOrigin();
    }

    /// 0 is an undamaged gate, 1 is one about to give way.
    ///
    /// Meant to be stepped on each blow rather than ramped every frame: a crack that creeps along
    /// on its own looks like rot, and one that jumps when the door is struck looks like force.
    public void SetProgress(float amount)
    {
        _progress = Mathf.Clamp01(amount);

        foreach (var panel in _panels)
            if (panel.Material != null)
                panel.Material.SetFloat(ProgressId, _progress);
    }

    /// Back to an undamaged gate — called when the gate shuts behind the raid, so the next one
    /// does not start against a door that is already in pieces.
    public void Clear() => SetProgress(0f);

    void PushOrigin()
    {
        if (!_hasOrigin) return;

        foreach (var panel in _panels)
        {
            if (panel.Material == null || panel.Renderer == null) continue;

            // The one impact point, expressed in each panel's own frame, so the cracks on the
            // small pane belong to the same blow as the ones on the big sheet.
            Vector3 local = panel.Renderer.transform.InverseTransformPoint(_origin) - panel.Min;

            float spanU = Mathf.Max(Vector3.Dot(panel.Size, panel.AxisU), 1e-4f);
            float spanV = Mathf.Max(Vector3.Dot(panel.Size, panel.AxisV), 1e-4f);

            panel.Material.SetVector(OriginId, new Vector4(
                Vector3.Dot(local, panel.AxisU) / spanU,
                Vector3.Dot(local, panel.AxisV) / spanV, 0f, 0f));
        }
    }

    void OnDestroy()
    {
        foreach (var panel in _panels)
        {
            // Put the renderer back the way it was found before the material goes, or it is left
            // holding a slot pointing at a destroyed object. Matched by identity rather than by
            // the index it went in at, which does not survive anything else changing the array.
            if (panel.Renderer != null)
            {
                var slots = panel.Renderer.sharedMaterials.Where(m => m != panel.Material).ToArray();
                if (slots.Length != panel.Renderer.sharedMaterials.Length)
                    panel.Renderer.sharedMaterials = slots;
            }

            if (panel.Material == null) continue;

            if (Application.isPlaying) Destroy(panel.Material);
            else                       DestroyImmediate(panel.Material);
        }

        _panels.Clear();
    }

    // ── Debug ─────────────────────────────────────────────────────────────────

    [ContextMenu("Debug / Crack half")] void DbgHalf() => SetProgress(0.5f);
    [ContextMenu("Debug / Crack full")] void DbgFull() => SetProgress(1f);
    [ContextMenu("Debug / Clear")]      void DbgNone() => Clear();
}
