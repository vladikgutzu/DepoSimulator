using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Wraps the raw HittingTools models into weapon prefabs the game can actually hold.
///
/// Everything downstream assumes one thing about a weapon model: the grip is at the origin and
/// the shaft runs down +Z. MeleeCombat parents it to the hand at identity, and MeleeSwingArm
/// places the off hand at an offset along that +Z. None of the source models are authored that
/// way — they run along +Y, some are pivoted through the middle, and three of them are in the
/// wrong units entirely (a rolling pin 6.4 units long, a plank 22).
///
/// Rather than teach the code about each model's quirks, the fixup lives where it belongs: in a
/// prefab. The wrapper holds the model rotated, scaled and shifted so the contract holds, and
/// the weapon definition just points at the wrapper.
public static class BuildWeaponModels
{
    const string SourceFolder = "Assets/Fbx/HittingTools";
    const string OutputFolder = "Assets/Resources/Catalog/Weapons/Models";

    /// A weapon that reads as the wrong size is worse than a primitive, so each is given the
    /// length it should be in the player's hand. 0 keeps whatever the model came with.
    static readonly (string file, string prefab, float length)[] Models =
    {
        ("Baseball Bat",             "weapon_model_bat",         0.80f),
        ("Crowbar",                  "weapon_model_crowbar",     0.60f),
        ("PipeWrenchUnreal",         "weapon_model_pipewrench",  0.45f),
        ("RollingPin_low",           "weapon_model_rollingpin",  0.45f),
        ("NoJankPlank",              "weapon_model_plank",       1.00f),
    };

    [MenuItem("Tools/Warehouse/Build Weapon Models")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        Directory.CreateDirectory(OutputFolder);
        var report = new StringBuilder();

        foreach (var (file, prefabName, length) in Models)
        {
            string path = FindModel(file);
            if (path == null) { report.AppendLine($"• {file}: not found"); continue; }

            report.AppendLine(Build(path, prefabName, length));
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return report.ToString();
    }

    static string FindModel(string fileName) =>
        AssetDatabase.FindAssets("t:Model", new[] { SourceFolder })
                     .Select(AssetDatabase.GUIDToAssetPath)
                     .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == fileName);

    static string Build(string modelPath, string prefabName, float targetLength)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (model == null) return $"• {prefabName}: could not load {modelPath}";

        var root = new GameObject(prefabName);

        try
        {
            var inner = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(inner, PrefabUnpackMode.Completely,
                                               InteractionMode.AutomatedAction);
            inner.transform.SetParent(root.transform, false);

            var b = Bounds(inner);
            if (b.size == Vector3.zero) return $"• {prefabName}: model has no renderers";

            // Longest axis is the shaft, whichever way the model was authored.
            int axis = b.size.x >= b.size.y && b.size.x >= b.size.z ? 0
                     : b.size.y >= b.size.z                          ? 1
                     : 2;

            // Turn that axis to face +Z.
            var rot = axis switch
            {
                0 => Quaternion.Euler(0f, -90f, 0f),   // X -> Z
                1 => Quaternion.Euler(90f, 0f, 0f),    // Y -> Z
                _ => Quaternion.identity,
            };

            float rawLength = b.size[axis];
            float scale = targetLength > 0f && rawLength > 0.001f ? targetLength / rawLength : 1f;

            // Where the model ends up is worked out from that single measurement rather than by
            // moving it and reading the renderer again. Renderer.bounds does not reliably refresh
            // inside one edit-mode call - a transform write that happens to be a no-op (a rotation
            // already at identity, a scale already 1) left the bat unrotated and collapsed the
            // rolling pin's bounds to zero, silently, in the first version of this.
            var placed = Transformed(b, rot, scale);

            inner.transform.localRotation = rot;
            inner.transform.localScale    = Vector3.one * scale;
            inner.transform.localPosition = -new Vector3(placed.center.x, placed.center.y, placed.min.z);

            string outPath = $"{OutputFolder}/{prefabName}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, outPath);

            return $"• {prefabName}: {rawLength:F2} m on axis {"XYZ"[axis]} -> " +
                   $"{placed.size.z:F2} m along Z (x{scale:F3}), grip at the origin";
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    /// The axis-aligned box a set of bounds occupies after being rotated and scaled about the
    /// origin. All eight corners are carried through, because rotating a box and taking its new
    /// extents is not the same as rotating its extents.
    static Bounds Transformed(Bounds b, Quaternion rot, float scale)
    {
        var e = b.extents;
        var c = b.center;

        var result = new Bounds(rot * (c * scale), Vector3.zero);

        for (int i = 0; i < 8; i++)
        {
            var corner = c + new Vector3(
                (i & 1) == 0 ? -e.x : e.x,
                (i & 2) == 0 ? -e.y : e.y,
                (i & 4) == 0 ? -e.z : e.z);

            result.Encapsulate(rot * (corner * scale));
        }

        return result;
    }

    static Bounds Bounds(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);

        var b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        return b;
    }
}
