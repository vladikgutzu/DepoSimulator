using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// Builds working prefabs for the torn-off chunks, because the ones the pack ships are empty.
///
/// Measured, not guessed: instantiate Prefabs/Damage/Damage_1 and its renderer has
/// `sharedMesh = NULL` and `materials = [NULL]`, with bounds of exactly zero. They are legacy
/// Unity 5 prefabs — `m_PrefabInternal`, serializedVersion 5 — and their references into
/// Damage.fbx did not survive whatever migration brought them here. Nothing was ever drawn from
/// them, which is why hanging one off a broken limb produced no visible change at all.
///
/// The meshes themselves are fine: Damage.fbx holds Damage_L_Geom (134 verts), _M_ (125), _S_ (77)
/// and _XS_ (62), plus the lambert1 material. So this rebuilds each chunk from the model directly.
///
/// Output goes to Resources so BotDamageVisual can load it in a build. AssetDatabase does not
/// exist in a player, and a prefab nothing references would not be in the build to find anyway.
public static class BuildDamageChunks
{
    const string ModelPath = "Assets/Destructible_Humanoid_Robot/Models/Damage.fbx";
    const string OutputDir = "Assets/Resources/Damage";

    /// The chunk names inside the model, smallest first.
    static readonly string[] Sizes = { "XS", "S", "M", "L" };

    /// How long the biggest chunk should be, in metres.
    ///
    /// The model comes in at nothing like the robot's scale — measured, its largest piece imports
    /// as 8.2 x 16.6 x 7.5, which on a body 1.8 m tall is a nine-storey shard of metal. Rather than
    /// guess an import scale, every chunk is divided by the same factor, chosen so the largest one
    /// lands here. One factor for all four keeps the pack's own relative sizing intact.
    const float LargestChunk = 0.30f;

    const string MaterialPath = OutputDir + "/TornMetal.mat";

    [MenuItem("Tools/Warehouse/Build Damage Chunks")]
    public static void Run() => Debug.Log(Execute());

    /// A material of its own for the chunks.
    ///
    /// They arrive sharing lambert1 with the body, which sounds right and renders wrong: the body
    /// reads as painted blue plating and a chunk beside it comes out flat white. The meshes are
    /// from a different model and their UVs do not land anywhere meaningful on the body's atlas, so
    /// the shared material samples nothing useful. Torn-open internals want to be dark anyway.
    static Material TornMetal(System.Collections.Generic.List<string> report)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (existing != null) return existing;

        var body = AssetDatabase.LoadAssetAtPath<Material>(
            "Assets/Destructible_Humanoid_Robot/Materials/lambert1.mat");

        var shader = body != null && body.shader != null
            ? body.shader
            : Shader.Find("Universal Render Pipeline/Lit");

        if (shader == null) { report.Add("• no URP Lit shader — chunks keep the model's material"); return null; }

        var material = new Material(shader) { name = "TornMetal" };

        // Dark, dull and slightly metallic: the inside of a machine, not its paintwork.
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(0.16f, 0.16f, 0.17f));
        if (material.HasProperty("_Color"))     material.SetColor("_Color",     new Color(0.16f, 0.16f, 0.17f));
        if (material.HasProperty("_Metallic"))  material.SetFloat("_Metallic",  0.75f);
        if (material.HasProperty("_Smoothness"))material.SetFloat("_Smoothness",0.35f);
        if (material.HasProperty("_Glossiness"))material.SetFloat("_Glossiness",0.35f);

        AssetDatabase.CreateAsset(material, MaterialPath);
        report.Add($"• created {MaterialPath}");

        return material;
    }

    /// One divisor for every chunk, from whichever is biggest.
    static float ScaleFactor(GameObject model)
    {
        float longest = 0f;

        foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
            if (filter.sharedMesh != null)
            {
                var size = Vector3.Scale(filter.sharedMesh.bounds.size, filter.transform.lossyScale);
                longest = Mathf.Max(longest, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
            }

        return longest < 1e-4f ? 1f : LargestChunk / longest;
    }

    public static string Execute()
    {
        var model = AssetDatabase.LoadMainAssetAtPath(ModelPath) as GameObject;
        if (model == null) return $"no model at {ModelPath}";

        Directory.CreateDirectory(OutputDir);

        var report = new System.Collections.Generic.List<string>();

        // Measured off the model rather than assumed, so a re-export at a different scale still
        // produces chunks the right size.
        float factor = ScaleFactor(model);
        report.Add($"scale factor {factor:F4} (largest chunk -> {LargestChunk:F2} m)");

        var material = TornMetal(report);

        // One instantiation of the whole model, then a child lifted out of it per chunk. Loading
        // the child asset directly would give the sub-object without the import scale the model
        // root applies, and the chunks would come out at the FBX's own units — metres where they
        // should be centimetres.
        var root = (GameObject)PrefabUtility.InstantiatePrefab(model);

        foreach (string size in Sizes)
        {
            string wanted = $"Damage_{size}";

            var source = root.GetComponentsInChildren<Transform>(true)
                             .FirstOrDefault(t => t.name == wanted);

            if (source == null) { report.Add($"• {wanted}: not in the model"); continue; }

            var copy = Object.Instantiate(source.gameObject);
            copy.name = wanted;

            // Detached from the model's own hierarchy, so the saved prefab carries the world scale
            // it was actually being drawn at rather than inheriting one from a parent that is not
            // coming with it.
            copy.transform.SetParent(null, worldPositionStays: false);
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            copy.transform.localScale    = source.lossyScale * factor;

            var renderer = copy.GetComponentInChildren<MeshRenderer>(true);
            var filter   = copy.GetComponentInChildren<MeshFilter>(true);

            if (renderer != null && material != null)
                renderer.sharedMaterials = renderer.sharedMaterials.Select(_ => material).ToArray();

            string state = filter == null || filter.sharedMesh == null
                ? "NO MESH"
                : $"{filter.sharedMesh.vertexCount} verts, " +
                  $"{(renderer != null ? renderer.bounds.size.ToString("F3") : "?")} m, " +
                  $"uv={(filter.sharedMesh.uv != null && filter.sharedMesh.uv.Length > 0 ? "yes" : "NONE")}";

            string path = $"{OutputDir}/{wanted}.prefab";
            PrefabUtility.SaveAsPrefabAsset(copy, path);
            Object.DestroyImmediate(copy);

            report.Add($"• {path} — {state}");
        }

        Object.DestroyImmediate(root);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return string.Join("\n", report) +
               "\n\nBotDamageVisual loads these from Resources; the pack's own Damage_1..4 prefabs " +
               "are empty and are no longer used.";
    }
}
