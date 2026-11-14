using UnityEditor;
using UnityEngine;

/// Sets the wordmark logo up as a UI texture.
///
/// The defaults a PNG lands with are aimed at 3D: mipmaps on, and no promise that the alpha means
/// transparency. Neither suits a flat image drawn at a fixed size in a UI panel — mipmaps blur it
/// and cost memory for levels nothing samples.
public static class SetupLogoImport
{
    const string Path = "Assets/Resources/UI/Logo.png";

    [MenuItem("Tools/Warehouse/Setup Logo Import")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var importer = AssetImporter.GetAtPath(Path) as TextureImporter;
        if (importer == null) return $"no texture importer at {Path} — is the file there?";

        importer.textureType         = TextureImporterType.Default;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled       = false;
        importer.wrapMode            = TextureWrapMode.Clamp;
        importer.filterMode          = FilterMode.Bilinear;
        // Drawn at 72px on the loading screen and 52px in the menu; 512 is already generous, and
        // leaves room if either grows.
        importer.maxTextureSize      = 512;

        importer.SaveAndReimport();

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(Path);
        return tex == null
            ? $"{Path} reimported but no Texture2D came back"
            : $"{Path}: {tex.width}x{tex.height}, mipmaps off, alpha as transparency";
    }
}
