using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Points each weapon definition at its model prefab, and makes sure the model's materials will
/// actually render.
///
/// Only weapons with a model that genuinely is that weapon are listed. A definition left out
/// keeps its primitive stand-in, which is honest: a stand-in reads as "art pending", whereas a
/// pipe wrench standing in for a sledgehammer reads as a bug.
public static class AssignWeaponModels
{
    const string ModelFolder  = "Assets/Resources/Catalog/Weapons/Models";
    const string WeaponFolder = "Assets/Resources/Catalog/Weapons";

    static readonly (MeleeWeaponKind kind, string model)[] Mapping =
    {
        (MeleeWeaponKind.Bat,              "weapon_model_bat"),
        (MeleeWeaponKind.Crowbar,          "weapon_model_crowbar"),
        (MeleeWeaponKind.PipeWrench,       "weapon_model_pipewrench"),
        (MeleeWeaponKind.Plank,            "weapon_model_plank"),
        (MeleeWeaponKind.RollingPin,       "weapon_model_rollingpin"),
    };

    [MenuItem("Tools/Warehouse/Assign Weapon Models")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder();

        foreach (var (kind, model) in Mapping)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelFolder}/{model}.prefab");
            if (prefab == null) { report.AppendLine($"• {kind}: {model}.prefab missing"); continue; }

            var def = AssetDatabase
                .FindAssets("t:MeleeWeaponDefinition", new[] { WeaponFolder })
                .Select(g => AssetDatabase.LoadAssetAtPath<MeleeWeaponDefinition>(
                                 AssetDatabase.GUIDToAssetPath(g)))
                .FirstOrDefault(d => d != null && d.Kind == kind);

            if (def == null) { report.AppendLine($"• {kind}: no definition asset"); continue; }

            def.Prefab = prefab;
            EditorUtility.SetDirty(def);
            report.AppendLine($"• {kind} -> {model}");
        }

        report.AppendLine(FixMaterials());

        AssetDatabase.SaveAssets();
        return report.ToString();
    }

    /// The pack's materials arrive on whatever shader they were authored against. Anything that
    /// is not a URP shader renders magenta in this project - the same trap the zombie pack had.
    static string FixMaterials()
    {
        var urp = Shader.Find("Universal Render Pipeline/Lit");
        if (urp == null) return "• URP/Lit not found - materials left alone";

        var mats = AssetDatabase.FindAssets("t:Material", new[] { "Assets/Fbx/HittingTools" })
                                .Select(g => AssetDatabase.LoadAssetAtPath<Material>(
                                                 AssetDatabase.GUIDToAssetPath(g)))
                                .Where(m => m != null)
                                .ToList();

        int converted = 0;
        var stillWrong = new StringBuilder();

        foreach (var m in mats)
        {
            if (m.shader == urp) continue;

            // Embedded FBX materials are read-only; naming them is more use than failing silently.
            if (AssetDatabase.GetAssetPath(m).EndsWith(".fbx"))
            {
                stillWrong.Append($" {m.name}");
                continue;
            }

            m.shader = urp;
            EditorUtility.SetDirty(m);
            converted++;
        }

        return $"• materials: {converted} of {mats.Count} switched to URP/Lit" +
               (stillWrong.Length > 0
                   ? $"; embedded in their FBX and left as-is:{stillWrong} " +
                     "(set Material Location to External on the model if they render magenta)"
                   : "");
    }
}
