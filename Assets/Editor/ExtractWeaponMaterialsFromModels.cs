using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Pulls the materials that live inside weapon FBX files out into real assets.
///
/// A material embedded in a model is read-only: you cannot retint it, swap its texture or move it
/// to another shader, because it is part of the imported file rather than a thing in the project.
/// Three of the weapons came that way, so three of them could not be art-directed at all while the
/// other three could.
///
/// Extracting writes each one next to its model as a .mat and remaps the importer to use it, so
/// the weapon looks exactly the same afterwards — it is just editable now.
public static class ExtractWeaponMaterialsFromModels
{
    [MenuItem("Tools/Warehouse/Extract Weapon Materials")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder();

        // Every model any weapon prefab actually renders, found rather than listed: a hand-typed
        // list goes stale the first time a weapon is replaced.
        var models = Resources.LoadAll<MeleeWeaponDefinition>("Catalog/Weapons")
            .Where(d => d != null && d.Prefab != null)
            .SelectMany(d => d.Prefab.GetComponentsInChildren<Renderer>(true))
            .SelectMany(r => r.sharedMaterials)
            .Where(m => m != null)
            .Select(AssetDatabase.GetAssetPath)
            .Where(p => !string.IsNullOrEmpty(p) && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();

        if (models.Length == 0) return "no weapon materials are embedded in a model — nothing to extract";

        int extracted = 0;

        foreach (string modelPath in models)
        {
            string dir = Path.Combine(Path.GetDirectoryName(modelPath), "Materials");
            Directory.CreateDirectory(dir);

            report.AppendLine($"{Path.GetFileName(modelPath)}:");

            foreach (var mat in AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Material>())
            {
                string dest = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(dir, mat.name + ".mat"));
                string error = AssetDatabase.ExtractAsset(mat, dest);

                if (string.IsNullOrEmpty(error))
                {
                    report.AppendLine($"   {mat.name} → {dest}");
                    extracted++;
                }
                else report.AppendLine($"   {mat.name}: {error}");
            }

            // The importer now holds a remap to the files on disk; without the reimport the model
            // keeps rendering with the copies still inside it.
            AssetDatabase.WriteImportSettingsIfDirty(modelPath);
            AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceUpdate);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        report.AppendLine($"\n{extracted} materials extracted from {models.Length} models — they are "
                        + "ordinary assets now and can be edited, retextured or replaced.");
        return report.ToString();
    }
}
