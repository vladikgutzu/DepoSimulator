using System.Collections.Generic;
using UnityEngine;

/// Makes a repair target glow the way a required part box does: the warehouse pack shader's own
/// animated emission (Shader Graphs/shdr_basic_01 — _animate, _Color_Map, _Power), in the same
/// gold PartHighlighter uses.
///
/// Material copies rather than a MaterialPropertyBlock like PartHighlighter's. Every power box on
/// the walls is Batching Static, and a statically batched renderer is exactly where layering
/// something over its shared state already failed once — the appended outline material never drew
/// in a build. A material of its own is the one thing such a renderer is certain to draw with.
public sealed class RepairGlow
{
    static readonly int PowerID    = Shader.PropertyToID("_Power");
    static readonly int ColorMapID = Shader.PropertyToID("_Color_Map");
    static readonly int AnimateID  = Shader.PropertyToID("_animate");
    static readonly int EmissionMapID = Shader.PropertyToID("_Emmision_Map");   // sic, the shader's own spelling
    static readonly int ScaleID       = Shader.PropertyToID("_Scale");

    /// How much brighter than a part box a repair target glows. A box sits in a lit aisle and is
    /// fetched from arm's length; a power box is a dark metal panel on a wall, often during a
    /// blackout, and has to read as "live — go here" from across the floor. At the part-box value
    /// it looked like an ordinary box that happened to catch the light.
    const float Intensity = 3f;

    readonly List<(Renderer renderer, Material[] original, Material[] copies)> _swapped = new();

    RepairGlow() { }

    /// Lights up every renderer on the object and its children whose material has the pack
    /// shader's emission inputs. Renderers without them are left alone.
    public static RepairGlow On(GameObject go)
    {
        var glow = new RepairGlow();
        if (go == null) return glow;

        foreach (var r in go.GetComponentsInChildren<Renderer>(false))
        {
            var original = r.sharedMaterials;
            Material[] copies = null;

            for (int i = 0; i < original.Length; i++)
            {
                var m = original[i];
                if (m == null || !m.HasProperty(PowerID) || !m.HasProperty(ColorMapID)) continue;

                copies ??= (Material[])original.Clone();
                var copy = new Material(m) { name = m.name + " (repair glow)" };
                copy.SetFloat(AnimateID, 5f);
                var c = PartHighlighter.RequiredGlowColor;
                copy.SetColor(ColorMapID, new Color(c.r * Intensity, c.g * Intensity, c.b * Intensity, c.a));
                copy.SetFloat(PowerID, PartHighlighter.RequiredGlowPower);

                // The emission is the colour times a mask texture. A box's mask covers the whole
                // box; a wall or pole prop shares its group's AO atlas as the mask, so the same
                // colour came out patchy and faint across a power box. White makes the whole
                // surface glow, and _Scale matches a box's so the animation reads the same.
                if (m.HasProperty(EmissionMapID)) copy.SetTexture(EmissionMapID, Texture2D.whiteTexture);
                if (m.HasProperty(ScaleID))       copy.SetFloat(ScaleID, 1f);
                copies[i] = copy;
            }

            if (copies == null) continue;
            r.sharedMaterials = copies;
            glow._swapped.Add((r, original, copies));
        }

        return glow;
    }

    /// Puts the original materials back and frees the copies. Safe to call more than once.
    public void Off()
    {
        foreach (var (renderer, original, copies) in _swapped)
        {
            if (renderer != null) renderer.sharedMaterials = original;
            for (int i = 0; i < copies.Length; i++)
                if (copies[i] != null && copies[i] != original[i]) Object.Destroy(copies[i]);
        }
        _swapped.Clear();
    }
}
