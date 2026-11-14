using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Builds the player's wardrobe: a copy of the body materials wearing each role's livery, plus one
/// for single player.
///
/// **A livery is a texture and nothing else.** This used to have a second mechanism — a role with no
/// texture of its own got the source material under a `_BaseColor` tint from PlayerRoleColors — and
/// that mechanism was a trap rather than a fallback. A tint multiplies whatever the base map already
/// is, so it darkens the metal and the hazard stripes along with the panels, and the two mechanisms
/// then stack: the Dispatcher ended up with a violet texture *and* a violet tint on top, which is
/// twice the colour anybody asked for and reads as a different material entirely. Roles now get a
/// base map or they get left in the model's own skin and told so.
///
/// Idempotent — an existing material or skin asset is left alone, so anything art-directed by hand
/// survives a re-run.
public static class CreatePlayerSkins
{
    const string SkinFolder     = "Assets/Resources/Catalog/PlayerSkins";
    const string MaterialFolder = "Assets/Resources/Catalog/PlayerSkins/Materials";

    /// Read off the player body: whatever it is currently wearing is what gets recoloured.
    static Material[] SourceMaterials()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) return System.Array.Empty<Material>();

        // Active renderers only. A swapped-out body is left in the hierarchy deactivated so the
        // change can be undone, and including inactive ones picks up the retired character - the
        // first run of this tinted the old model's three materials instead of the new one's two.
        var body = player.GetComponentsInChildren<SkinnedMeshRenderer>(false).FirstOrDefault();

        return body != null ? body.sharedMaterials.Where(m => m != null).ToArray()
                            : System.Array.Empty<Material>();
    }

    [MenuItem("Tools/Warehouse/Create Player Skins")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute() => Execute(null);

    /// albedos maps a skin key ("solo", "picker", "forkliftoperator", …) to the base map that
    /// role's body wears. A role left out of it keeps the model's own skin — which means two roles
    /// looking alike, so it is reported rather than papered over. Tools/Warehouse/Build Role
    /// Liveries exists to fill exactly that gap.
    ///
    /// The textures answer to PlayerRoleColors: a role's livery is expected to already BE that
    /// colour, because the minimap dot and the co-op HUD will keep calling it green whatever the
    /// body wears.
    public static string Execute(IReadOnlyDictionary<string, Texture> albedos)
    {
        var report = new StringBuilder();

        var source = SourceMaterials();
        if (source.Length == 0)
            return "no player body found in the open scene - its materials are what get copied";

        Directory.CreateDirectory(MaterialFolder);

        report.AppendLine($"source: {string.Join(", ", source.Select(m => m.name))}");

        Build(report, source, "solo", solo: true, role: PlayerRoleType.None,
              albedo: Albedo(albedos, "solo"));

        foreach (PlayerRoleType role in System.Enum.GetValues(typeof(PlayerRoleType)))
        {
            if (role == PlayerRoleType.None) continue;   // covered by the solo set
            string key = role.ToString().ToLowerInvariant();
            Build(report, source, key, solo: false, role: role, albedo: Albedo(albedos, key));
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return report.ToString();
    }

    static Texture Albedo(IReadOnlyDictionary<string, Texture> albedos, string key) =>
        albedos != null && albedos.TryGetValue(key, out var t) ? t : null;

    static void Build(StringBuilder report, Material[] source, string key,
                      bool solo, PlayerRoleType role, Texture albedo)
    {
        var mats = new Material[source.Length];

        for (int i = 0; i < source.Length; i++)
        {
            string path = $"{MaterialFolder}/player_{key}_{i}.mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) { mats[i] = existing; continue; }

            var m = new Material(source[i]) { name = $"player_{key}_{i}" };

            if (albedo != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", albedo);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", albedo);
                report.AppendLine($"  {key}: base map -> {albedo.name}");
            }
            else
            {
                report.AppendLine($"  {key}: NO LIVERY - keeps the model's own skin and will look " +
                                  "like every other role without one. Run " +
                                  "Tools/Warehouse/Build Role Liveries.");
            }

            // Whatever the source material was tinted with, cleared. Not a colour being applied —
            // a colour being got out of the way: _BaseColor multiplies the base map, so a livery
            // seen through a leftover tint is neither the texture nor the tint.
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Color"))     m.SetColor("_Color", Color.white);

            AssetDatabase.CreateAsset(m, path);
            mats[i] = m;
            report.AppendLine($"  created {path}");
        }

        // ── The set that ties them to a role ─────────────────────────────────
        string setPath = $"{SkinFolder}/skin_{key}.asset";
        if (AssetDatabase.LoadAssetAtPath<PlayerSkinSet>(setPath) != null)
        {
            report.AppendLine($"• {setPath} already exists - left alone");
            return;
        }

        var set = ScriptableObject.CreateInstance<PlayerSkinSet>();
        set.Solo      = solo;
        set.Role      = role;
        set.Materials = mats;

        AssetDatabase.CreateAsset(set, setPath);
        report.AppendLine($"• created {setPath}");
    }
}
