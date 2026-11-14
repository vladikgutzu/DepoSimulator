using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Hooks each weapon material up to the textures that shipped beside it.
///
/// The models come out of import on URP/Lit but with nothing in their texture slots, so every
/// weapon renders as flat white plastic. Switching a material to a URP shader does not carry
/// bindings across either - _MainTex is not _BaseMap - so the crowbar lost its maps the moment
/// it was converted.
///
/// The packs name their files by what they are (BaseColor, Normal, Roughness), so the maps are
/// matched by that rather than by a table that would need extending for every new weapon.
public static class WireWeaponTextures
{
    const string SourceFolder = "Assets/Fbx/HittingTools";

    [MenuItem("Tools/Warehouse/Wire Weapon Textures")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder();

        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { SourceFolder }))
        {
            string matPath = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) continue;

            // Textures live either beside the model or in a sibling "textures" folder, so the
            // search starts at the package root for this weapon rather than the material's own
            // directory.
            string root = PackageRoot(matPath);
            var textures = AssetDatabase.FindAssets("t:Texture2D", new[] { root })
                                        .Select(AssetDatabase.GUIDToAssetPath)
                                        .Distinct()
                                        .ToList();

            if (textures.Count == 0) { report.AppendLine($"• {mat.name}: no textures under {root}"); continue; }

            var applied = new StringBuilder();

            Assign(mat, "_BaseMap",   Pick(textures, "basecolor", "base_color", "albedo", "diffuse"), applied, "base");
            Assign(mat, "_BumpMap",   Pick(textures, "normal_opengl", "normal"),                      applied, "normal");
            Assign(mat, "_MetallicGlossMap", Pick(textures, "metallic"),                              applied, "metallic");
            Assign(mat, "_OcclusionMap",     Pick(textures, "ambientocclusion", "occlusion", "_ao"),  applied, "ao");

            if (applied.Length == 0) { report.AppendLine($"• {mat.name}: nothing matched"); continue; }

            // A base map that is never sampled leaves the weapon white anyway.
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.GetTexture("_BumpMap") != null) mat.EnableKeyword("_NORMALMAP");
            if (mat.GetTexture("_MetallicGlossMap") != null) mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            if (mat.GetTexture("_OcclusionMap") != null) mat.EnableKeyword("_OCCLUSIONMAP");

            EditorUtility.SetDirty(mat);
            report.AppendLine($"• {mat.name}:{applied}");
        }

        AssetDatabase.SaveAssets();
        return report.Length > 0 ? report.ToString() : "no weapon materials found";
    }

    /// Walks up out of source/ or Materials/ to the folder that holds the whole weapon.
    static string PackageRoot(string assetPath)
    {
        string dir = Path.GetDirectoryName(assetPath).Replace('\\', '/');

        for (int i = 0; i < 3; i++)
        {
            string name = Path.GetFileName(dir).ToLowerInvariant();
            if (name != "materials" && name != "source" && !name.EndsWith(".fbm")) break;

            dir = Path.GetDirectoryName(dir).Replace('\\', '/');
        }

        return dir;
    }

    static string Pick(System.Collections.Generic.List<string> textures, params string[] keys) =>
        keys.Select(k => textures.FirstOrDefault(
                        t => Path.GetFileNameWithoutExtension(t).ToLowerInvariant().Contains(k)))
            .FirstOrDefault(t => t != null);

    static void Assign(Material mat, string slot, string texturePath,
                       StringBuilder applied, string label)
    {
        if (texturePath == null || !mat.HasProperty(slot)) return;

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (tex == null) return;

        // Import type has to match what the slot expects, and getting it wrong is not subtle:
        // the crowbar's albedo came in flagged as a normal map, so it was unpacked as vectors
        // rather than colour and rendered as the exact inverse of itself - a red crowbar showing
        // up cyan. Normals need the normal type; everything else needs plain colour, and only
        // the base map is sRGB (metallic and occlusion are data, not colour).
        var want = slot == "_BumpMap" ? TextureImporterType.NormalMap : TextureImporterType.Default;
        bool srgb = slot == "_BaseMap";

        var texImp = AssetImporter.GetAtPath(texturePath) as TextureImporter;
        if (texImp != null && (texImp.textureType != want || texImp.sRGBTexture != srgb))
        {
            texImp.textureType  = want;
            texImp.sRGBTexture  = srgb;
            texImp.SaveAndReimport();
        }

        mat.SetTexture(slot, tex);
        applied.Append($" {label}");
    }
}
