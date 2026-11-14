using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// Fills the raider's body table, so a raid is not three copies of one man in different shirts.
///
/// Only bodies that are actually usable go in, and "usable" is narrower than "in the pack".
///
/// Zombie Guy 2 is the one exclusion. He is humanoid and does play the clips, but he is the heavy
/// body while every clip is retargeted from Guy 1's proportions, so his run comes out as a waddle —
/// which is how the whole problem got noticed. He is left out rather than deleted: add him back to
/// Families if the look is wanted.
///
/// The girl bodies were unusable for a while — the importer kept handing back a Generic avatar no
/// matter what bone map it was given — and their rigs have since been fixed at the source. They are
/// checked here rather than assumed: a body that is not humanoid is skipped and named, because a
/// raider who cannot play a single clip is worse than one fewer face.
///
/// Five bodies, five skins each: twenty-five raiders who do not look like each other.
public static class AssignThiefBodies
{
    const string ThiefPrefabPath = "Assets/Prefabs/Thief.prefab";
    const string PrefabFolder    = "Assets/Zombie Slaughter/Prefabs";
    const string MeshFolder      = "Assets/Zombie Slaughter/Meshes";
    const string MaterialFolder  = "Assets/Zombie Slaughter/Materials";

    /// Bodies to offer, in order. Guy 2 is deliberately absent — see the note above. Interleaved
    /// rather than guys-then-girls, because the body is stepped through in order and a raid should
    /// not turn up as three men followed by three women.
    static readonly string[] Families =
    {
        "Zombie Guy 1", "Zombie Girl 1",
        "Zombie Guy 3", "Zombie Girl 2",
                        "Zombie Girl 3",
    };

    [MenuItem("Tools/Warehouse Demo/Assign Thief Bodies")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var root = PrefabUtility.LoadPrefabContents(ThiefPrefabPath);
        var report = new List<string>();

        try
        {
            var thief = root.GetComponent<ThiefController>();
            if (thief == null) return "Thief prefab has no ThiefController";

            var so  = new SerializedObject(thief);
            var arr = so.FindProperty("_bodies");

            var usable = new List<(string family, GameObject prefab, Avatar avatar, List<Material> skins)>();

            foreach (string family in Families)
            {
                string prefabPath = $"{PrefabFolder}/{family}A/{family}A.prefab";
                string meshPath   = $"{MeshFolder}/{family} Mesh.FBX";

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var avatar = AssetDatabase.LoadAllAssetsAtPath(meshPath).OfType<Avatar>().FirstOrDefault();

                if (prefab == null) { report.Add($"• {family}: no prefab at {prefabPath}"); continue; }
                if (avatar == null || !avatar.isHuman)
                {
                    report.Add($"• {family}: not humanoid — skipped, it could not play the clips");
                    continue;
                }

                var skins = new List<Material>();
                foreach (char v in "ABCDE")
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/{family}{v}.mat");
                    if (mat != null) skins.Add(mat);
                }

                usable.Add((family, prefab, avatar, skins));
            }

            if (usable.Count == 0) return "no usable bodies found — nothing written";

            arr.arraySize = usable.Count;
            for (int i = 0; i < usable.Count; i++)
            {
                var (family, prefab, avatar, skins) = usable[i];
                var element = arr.GetArrayElementAtIndex(i);

                element.FindPropertyRelative("Prefab").objectReferenceValue = prefab;
                element.FindPropertyRelative("Avatar").objectReferenceValue = avatar;

                var skinArr = element.FindPropertyRelative("Skins");
                skinArr.arraySize = skins.Count;
                for (int s = 0; s < skins.Count; s++)
                    skinArr.GetArrayElementAtIndex(s).objectReferenceValue = skins[s];

                report.Add($"• {family}: {skins.Count} skin(s)");
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, ThiefPrefabPath);

            report.Add($"\n{usable.Count} bodies x skins = " +
                       $"{usable.Sum(u => Mathf.Max(u.skins.Count, 1))} distinct raiders.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        return string.Join("\n", report);
    }
}
