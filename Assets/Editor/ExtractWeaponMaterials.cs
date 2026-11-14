using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Pulls weapon materials out of their FBX so they can be put on a URP shader.
///
/// A material embedded in a model is read-only, so a non-URP one cannot be fixed in place - it
/// just renders magenta forever. Extracting turns it into an ordinary asset that can be
/// retargeted and, after that, art-directed.
public static class ExtractWeaponMaterials
{
    const string SourceFolder = "Assets/Fbx/HittingTools";

    [MenuItem("Tools/Warehouse/Extract Weapon Materials")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var urp = Shader.Find("Universal Render Pipeline/Lit");
        if (urp == null) return "URP/Lit shader not found";

        var report = new StringBuilder();

        foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { SourceFolder }))
        {
            string modelPath = AssetDatabase.GUIDToAssetPath(guid);

            var embedded = AssetDatabase.LoadAllAssetsAtPath(modelPath)
                                        .OfType<Material>()
                                        .Where(m => m.shader != urp)
                                        .ToList();
            if (embedded.Count == 0) continue;

            string folder = Path.GetDirectoryName(modelPath).Replace('\\', '/') + "/Materials";
            Directory.CreateDirectory(folder);

            var imp = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            if (imp == null) continue;

            // Without this the next reimport re-embeds them and undoes the extraction.
            imp.materialLocation = ModelImporterMaterialLocation.External;

            foreach (var m in embedded)
            {
                string target = $"{folder}/{m.name}.mat";
                string err = AssetDatabase.ExtractAsset(m, target);

                report.AppendLine(string.IsNullOrEmpty(err)
                    ? $"• extracted {target}"
                    : $"• {m.name}: {err}");
            }

            AssetDatabase.WriteImportSettingsIfDirty(modelPath);
        }

        AssetDatabase.Refresh();

        // Whatever is now on disk gets retargeted.
        int converted = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { SourceFolder }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (m == null || m.shader == urp) continue;

            m.shader = urp;
            EditorUtility.SetDirty(m);
            converted++;
        }

        AssetDatabase.SaveAssets();
        report.AppendLine($"• {converted} switched to URP/Lit");

        return report.Length > 0 ? report.ToString() : "nothing to do";
    }
}
