using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Reports what PlayerSkin has to dress, on the scene rig and on the co-op prefab.
///
/// Written because the old skin dressed one renderer and the body has 35, and the failure was
/// invisible in the inspector: the material on the piece it did dress was correct, and nothing
/// anywhere said the other 34 had been left behind.
public static class DiagPlayerSkin
{
    const string PrefabPath = "Assets/WarehouseDemo/Assets/Prefabs/NetworkPlayer.prefab";

    [MenuItem("Tools/Warehouse/Diag Player Skin")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder("[Diag Player Skin]\n");

        var player = GameObject.FindGameObjectWithTag("Player");
        Describe(report, "scene rig", player);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        Describe(report, "NetworkPlayer prefab", prefab);

        report.AppendLine();
        report.AppendLine("Skins in the catalog:");
        foreach (var set in Resources.LoadAll<PlayerSkinSet>("Catalog/PlayerSkins"))
        {
            string who = set.Solo ? "solo" : set.Role.ToString();
            string mats = set.Materials == null
                ? "(none)"
                : string.Join(", ", set.Materials.Select(m => m == null ? "null" : m.name));

            report.AppendLine($"  {who,-18} {set.Materials?.Length ?? 0} material(s): {mats}");
        }

        return report.ToString();
    }

    static void Describe(StringBuilder report, string label, GameObject root)
    {
        if (root == null) { report.AppendLine($"• {label}: not found"); return; }

        var skin = root.GetComponentInChildren<PlayerSkin>(true);
        report.AppendLine($"• {label} ({root.name}): PlayerSkin " +
                          (skin == null ? "MISSING" : $"on '{skin.gameObject.name}'"));

        if (skin == null) return;

        // The same two filters Bind uses: active only, and the material the most renderers share.
        var all = skin.GetComponentsInChildren<Renderer>(false);

        var tally = new Dictionary<Material, int>();
        foreach (var r in all)
        {
            var m = r != null ? r.sharedMaterial : null;
            if (m == null) continue;
            tally.TryGetValue(m, out int n);
            tally[m] = n + 1;
        }

        report.AppendLine($"    active renderers: {all.Length}");

        foreach (var kv in tally.OrderByDescending(kv => kv.Value))
        {
            var tex = kv.Key.HasProperty("_BaseMap") ? kv.Key.GetTexture("_BaseMap") : null;
            report.AppendLine($"      {kv.Value,3} x {kv.Key.name}" +
                              (tex != null ? $"  (base map: {tex.name})" : ""));
        }

        int counts = all.Count(r => r != null && r.sharedMaterials.Length != 1);
        if (counts > 0)
            report.AppendLine($"    {counts} renderer(s) do not have exactly one material — " +
                              "a one-material skin will skip those");
    }
}
