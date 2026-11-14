using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Re-points weapon prefabs at their materials after those materials have been extracted out of
/// the FBX files.
///
/// A prefab built on an imported model refers to the material INSIDE that model. Extracting the
/// material replaces it with a file on disk and rebinds the model — but not the prefab, which is
/// still holding a reference to a sub-asset that no longer exists, and so renders untextured.
///
/// The imported model knows what it uses now, so the fix is to copy from it: for each renderer in
/// the prefab, find the matching one in the model and take its materials.
public static class RepairWeaponPrefabMaterials
{
    [MenuItem("Tools/Warehouse/Repair Weapon Prefab Materials")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder();
        int fixedCount = 0;

        foreach (var def in Resources.LoadAll<MeleeWeaponDefinition>("Catalog/Weapons")
                                     .Where(d => d != null && d.Prefab != null)
                                     .OrderBy(d => d.name))
        {
            string prefabPath = AssetDatabase.GetAssetPath(def.Prefab);
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            bool touched = false;

            try
            {
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer.sharedMaterials.All(m => m != null)) continue;

                    var source = SourceRenderer(renderer);
                    if (source == null)
                    {
                        report.AppendLine($"• {def.Kind}: {renderer.name} — no source model to copy from");
                        continue;
                    }

                    renderer.sharedMaterials = source.sharedMaterials;
                    touched = true;
                    fixedCount++;

                    report.AppendLine($"• {def.Kind}: {renderer.name} ← " +
                                      string.Join(", ", source.sharedMaterials.Select(m => m != null ? m.name : "NONE")));
                }

                if (touched) PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        AssetDatabase.SaveAssets();
        report.AppendLine($"\n{fixedCount} renderers re-pointed.");
        return report.ToString();
    }

    /// The same renderer, but on the imported model the prefab was built from — found through the
    /// mesh, which still knows which file it came out of.
    static Renderer SourceRenderer(Renderer renderer)
    {
        var filter = renderer.GetComponent<MeshFilter>();
        var mesh   = filter != null ? filter.sharedMesh
                   : renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : null;
        if (mesh == null) return null;

        string modelPath = AssetDatabase.GetAssetPath(mesh);
        if (string.IsNullOrEmpty(modelPath)) return null;

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (model == null) return null;

        // By name first, then by mesh: a model with one renderer needs no matching at all, and a
        // renamed copy in the prefab would otherwise find nothing.
        return model.GetComponentsInChildren<Renderer>(true)
                    .FirstOrDefault(r => r.name == renderer.name)
            ?? model.GetComponentsInChildren<Renderer>(true)
                    .FirstOrDefault(r => r.GetComponent<MeshFilter>() != null
                                      && r.GetComponent<MeshFilter>().sharedMesh == mesh);
    }
}
