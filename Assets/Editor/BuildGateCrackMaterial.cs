using System.IO;
using UnityEditor;
using UnityEngine;

/// Creates the material GateCracks draws its damage with.
///
/// It lives in Resources rather than being made from Shader.Find at runtime: a shader that no asset
/// references is stripped out of the player, so a runtime-only material works in the editor and
/// then quietly renders magenta — or nothing — in a build.
///
/// Idempotent: re-running rewrites the settings on the existing material, so the tuning done here
/// is the single place the look is defined and nothing in the scene has to be touched.
public static class BuildGateCrackMaterial
{
    const string ShaderName = "Warehouse/GateCracks";
    const string OutputDir  = "Assets/Resources/Materials";
    const string OutputPath = OutputDir + "/GateCracks.mat";

    [MenuItem("Tools/Warehouse/Build Gate Crack Material")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var shader = Shader.Find(ShaderName);
        if (shader == null)
            return $"shader \"{ShaderName}\" not found — check it compiled " +
                   "(Assets/WarehouseDemo/Assets/Shaders/GateCracks.shader)";

        Directory.CreateDirectory(OutputDir);

        var material = AssetDatabase.LoadAssetAtPath<Material>(OutputPath);
        bool created = material == null;

        if (created)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, OutputPath);
        }
        else
        {
            material.shader = shader;
        }

        // Tuned against gate_01, which is 2.59 m across and 3.02 m tall.
        //
        // Density is in cells per metre, so a split roughly every 1.2 m — only a couple across the
        // door. Anything finer stopped reading as a sheet giving way and started reading as a tiled
        // texture, which is what the first pass at this looked like; the hairline layer at 2.6
        // times the density fills in between them, but only once the beating has gone on a while.
        //
        // Reach is about half the panel diagonal, so the first blows leave a patch around where
        // they are hitting and only a full twenty seconds carries the damage out to the frame.
        //
        // Rim is kept low on purpose. The bent lip along a split is what sells it as torn metal,
        // but at full strength the bright edge swallows the dark fissure and the whole thing turns
        // into a glowing web.
        material.SetColor("_CrackColor", new Color(0.015f, 0.014f, 0.017f, 1f));
        material.SetColor("_EdgeColor",  new Color(0.38f, 0.36f, 0.33f, 1f));
        material.SetFloat("_Progress", 0f);
        material.SetFloat("_Density",  0.85f);
        material.SetFloat("_Width",    0.030f);
        material.SetFloat("_Ragged",   0.40f);
        material.SetFloat("_Reach",    2.2f);
        material.SetFloat("_Fine",     0.45f);
        material.SetFloat("_Rim",      0.30f);
        material.SetFloat("_Bruise",   0.09f);

        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return $"{(created ? "created" : "updated")} {OutputPath} on {ShaderName}.\n" +
               "GateCracks loads this at runtime; no scene change is needed.";
    }
}
