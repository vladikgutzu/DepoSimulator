using UnityEngine;
using UnityEditor;

public class CreateOutlineMaterials
{
    [MenuItem("Tools/Warehouse Demo/Create Outline Materials")]
    public static void Execute()
    {
        var shader = Shader.Find("Custom/FPS_Interact_Outline");
        if (shader == null)
        {
            Debug.LogError("[CreateOutlineMaterials] Shader 'Custom/FPS_Interact_Outline' not found — check that FPS_Interact_Outline.shader compiled without errors.");
            return;
        }

        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            AssetDatabase.CreateFolder("Assets", "Resources");

        CreateOrUpdate(shader, "FPS_Outline_Can",    new Color(0.25f, 0.65f, 1.00f));
        CreateOrUpdate(shader, "FPS_Outline_Cannot", new Color(1.00f, 0.25f, 0.20f));

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[CreateOutlineMaterials] Done — FPS_Outline_Can.mat + FPS_Outline_Cannot.mat created in Assets/Resources/");
    }

    static void CreateOrUpdate(Shader shader, string name, Color color)
    {
        var path = $"Assets/Resources/{name}.mat";
        var mat  = AssetDatabase.LoadAssetAtPath<Material>(path);

        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = shader;
        }

        mat.SetColor("_OutlineColor", color);
        mat.SetFloat("_OutlineWidth", 0.005f);
        EditorUtility.SetDirty(mat);
    }
}
