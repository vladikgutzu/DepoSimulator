using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Makes sure every role has a livery of its own, and says which one each is wearing.
///
/// The robot pack ships four usable liveries — blue, green, red, white — and the warehouse has five
/// looks to fill: four roles and the solo body. Two of them therefore collided, and the pair that
/// collided was the worst possible one: the Dispatcher and the Assembler were both blue, and blue is
/// also the colour the model comes in, so an undressed body and two different jobs were the same
/// robot. In a session that is exactly the "everybody looks the same" complaint.
///
/// Rather than reassign a role to a camo it has nothing to do with, the missing livery is built here,
/// in the hue PlayerRoleColors already gives the Dispatcher. That table is what the minimap dot and
/// the co-op HUD read, so the body and the dot stay the same colour by construction — which is the
/// whole point of a livery: "the violet one" has to mean one player, on the map and in the aisle.
///
/// **Which pixels are paint is taken from the pack, not guessed.** The first attempt recoloured
/// every pixel above a saturation threshold, and that repaints far more than the artist ever did:
/// the dark navy frame, the brown panels and the orange hazard stripes are all saturated, and a
/// robot whose warning stripes have gone violet reads as lit by a coloured lamp rather than as
/// painted. Comparing two liveries the artist actually made answers it exactly — the pixels that
/// differ between red and blue are the paint, and there are 23.8% of them. Everything else is left
/// byte for byte alone.
///
/// Red is the base rather than blue because it is the brighter of the two, and a violet built off
/// the blue livery's values came out as dark as the blue one and read as navy at a distance.
/// Saturation and value are damped a little so it sits with the green and the red instead of
/// glowing next to them.
///
/// Idempotent. An existing texture is left alone, so a hand-painted replacement survives a re-run.
public static class BuildRoleLiveries
{
    const string PackFolder = "Assets/Destructible_Humanoid_Robot/Materials";
    const string RedLivery  = PackFolder + "/Humanbot_Albedo_Red.png";
    const string BlueLivery = PackFolder + "/Humanbot_Albedo_Blue.png";
    const string MadeLivery = PackFolder + "/Humanbot_Albedo_Violet.png";

    const string MaterialFolder = "Assets/Resources/Catalog/PlayerSkins/Materials";

    /// How far two liveries have to disagree about a pixel for it to count as paint. Small: the
    /// pack's own repaint is decisive where it happens, so this only has to clear PNG noise.
    const float PaintDelta = 8f / 255f;

    /// Damping applied to the paint, so the new livery sits beside the ones it was built from
    /// rather than glowing next to them. Measured by eye against green and red.
    const float Saturation = 0.72f;
    const float Value      = 0.92f;

    [MenuItem("Tools/Warehouse/Build Role Liveries")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder("[Build Role Liveries]\n");

        var violet = AssetDatabase.LoadAssetAtPath<Texture2D>(MadeLivery);

        if (violet == null)
        {
            violet = Repaint(RedLivery, BlueLivery, MadeLivery,
                             PlayerRoleColors.For(PlayerRoleType.Dispatcher), report);
            if (violet == null) return report.ToString();
        }
        else report.AppendLine($"• {Path.GetFileName(MadeLivery)} already exists — left alone");

        Repoint("player_dispatcher_0", violet, report);

        report.AppendLine();
        report.AppendLine("Liveries now worn:");
        foreach (var line in Worn()) report.AppendLine("  " + line);

        return report.ToString();
    }

    // ── Making the texture ────────────────────────────────────────────────────

    /// Repaints `basePath` in a new hue, on exactly the pixels where it disagrees with `otherPath`.
    static Texture2D Repaint(string basePath, string otherPath, string targetPath,
                             Color target, StringBuilder report)
    {
        var source = Decode(basePath, report);
        var other  = Decode(otherPath, report);

        if (source == null || other == null)
        {
            if (source != null) Object.DestroyImmediate(source);
            if (other  != null) Object.DestroyImmediate(other);
            return null;
        }

        try
        {
            if (source.width != other.width || source.height != other.height)
            {
                report.AppendLine("• the two liveries are different sizes — cannot compare them");
                return null;
            }

            Color.RGBToHSV(target, out float hue, out _, out _);

            var pixels = source.GetPixels();
            var against = other.GetPixels();
            int painted = 0;

            for (int i = 0; i < pixels.Length; i++)
            {
                if (!Differs(pixels[i], against[i])) continue;   // structure, not paint

                Color.RGBToHSV(pixels[i], out _, out float sat, out float val);

                var repainted = Color.HSVToRGB(Mathf.Clamp01(hue),
                                               Mathf.Clamp01(sat * Saturation),
                                               Mathf.Clamp01(val * Value));
                repainted.a = pixels[i].a;
                pixels[i]   = repainted;
                painted++;
            }

            source.SetPixels(pixels);
            source.Apply();

            File.WriteAllBytes(targetPath, source.EncodeToPNG());
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);

            report.AppendLine($"• built {Path.GetFileName(targetPath)} from " +
                              $"{Path.GetFileName(basePath)} — {100f * painted / pixels.Length:0.0}% " +
                              "of it is paint, the rest is untouched");

            return AssetDatabase.LoadAssetAtPath<Texture2D>(targetPath);
        }
        finally
        {
            Object.DestroyImmediate(source);
            Object.DestroyImmediate(other);
        }
    }

    static bool Differs(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) > PaintDelta ||
        Mathf.Abs(a.g - b.g) > PaintDelta ||
        Mathf.Abs(a.b - b.b) > PaintDelta;

    /// Decoded straight off disk rather than read through the imported asset.
    ///
    /// The pack ships its textures unreadable, which is right for a shipped texture and useless for
    /// reading pixels out of one — and the obvious fix, flipping isReadable and calling
    /// SaveAndReimport around the read, stalls the editor for minutes on a 4096² texture and did not
    /// return at all when this tool was first run. A PNG on disk is already the authored pixels;
    /// LoadImage gives them up with the importer uninvolved, and leaves the project as it was found.
    static Texture2D Decode(string path, StringBuilder report)
    {
        if (!File.Exists(path))
        {
            report.AppendLine($"• no texture at {path}");
            return null;
        }

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);

        if (ImageConversion.LoadImage(tex, File.ReadAllBytes(path))) return tex;

        Object.DestroyImmediate(tex);
        report.AppendLine($"• {path} could not be decoded");
        return null;
    }

    // ── Wearing it ────────────────────────────────────────────────────────────

    static void Repoint(string materialName, Texture2D albedo, StringBuilder report)
    {
        string path = $"{MaterialFolder}/{materialName}.mat";
        var    mat  = AssetDatabase.LoadAssetAtPath<Material>(path);

        if (mat == null) { report.AppendLine($"• no material at {path}"); return; }

        bool worn = mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") == albedo;

        // Both names, because URP's Lit reads _BaseMap and the built-in path still reads _MainTex —
        // and CreatePlayerSkins wrote both when it made these, so leaving one behind would give the
        // material two different answers about what it is wearing.
        if (!worn)
        {
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", albedo);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", albedo);
        }

        // Any tint on the material is cleared — not a colour being applied, a colour being got out
        // of the way. _BaseColor multiplies the base map, and the Dispatcher's material still
        // carried the violet tint an older CreatePlayerSkins gave it back when it had no texture of
        // its own: violet texture times violet tint is neither of the two, and it is why a livery
        // has to be the only thing carrying the colour.
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color", Color.white);

        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();

        report.AppendLine(worn
            ? $"• {materialName} already wears {albedo.name}"
            : $"• {materialName} now wears {albedo.name}");
    }

    /// What every role is actually wearing, so a collision shows up in this log rather than in a
    /// session.
    static List<string> Worn()
    {
        var lines = new List<string>();
        var seen  = new Dictionary<string, string>();

        foreach (var set in Resources.LoadAll<PlayerSkinSet>("Catalog/PlayerSkins"))
        {
            if (set == null || set.Materials == null || set.Materials.Length == 0) continue;

            var mat = set.Materials[0];
            var tex = mat != null && mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;

            string who  = set.Solo ? "solo" : set.Role.ToString();
            string what = tex != null ? tex.name : "(no base map)";

            if (seen.TryGetValue(what, out string other))
                lines.Add($"{who,-18} {what}   ← SAME AS {other}");
            else
            {
                seen[what] = who;
                lines.Add($"{who,-18} {what}");
            }
        }

        return lines;
    }
}
