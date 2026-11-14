using UnityEngine;

public class DebugHighlight
{
    static readonly int PowerID    = Shader.PropertyToID("_Power");
    static readonly int ColorMapID = Shader.PropertyToID("_Color_Map");
    static readonly int AnimateID  = Shader.PropertyToID("_animate");

    // Call this in Edit mode (no game running) to clear leftover debug MPBs
    public static string Execute()
    {
        int cleared = 0;
        string[] boxNames = { "box_med_box", "box_large_box", "box_med_tape", "box_large_tape" };

        var allRenderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        foreach (var r in allRenderers)
        {
            bool isBox = false;
            foreach (var n in boxNames)
                if (r.gameObject.name == n) { isBox = true; break; }
            if (!isBox) continue;

            if (r.sharedMaterial == null || !r.sharedMaterial.HasProperty(PowerID)) continue;

            // Restore original shared material values
            var mpb = new MaterialPropertyBlock();
            mpb.SetFloat(PowerID,    r.sharedMaterial.GetFloat(PowerID));
            mpb.SetColor(ColorMapID, r.sharedMaterial.GetColor(ColorMapID));
            mpb.SetFloat(AnimateID,  r.sharedMaterial.GetFloat(AnimateID));
            r.SetPropertyBlock(mpb);
            cleared++;
        }
        return $"Cleared {cleared} box renderers.";
    }
}
