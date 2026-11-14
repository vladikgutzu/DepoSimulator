using System.Text;
using UnityEditor;
using UnityEngine;

/// Gives every weapon prefab a Rigidbody and a collider fitted to its mesh, on the root.
///
/// The weapon models used to have neither, and the held copy had whatever it did have destroyed on
/// pickup — so a weapon could not fall out of a hand, and a drop meant building a new model on the
/// floor. With the body baked into the prefab, the one in the hand is simply switched off
/// (WeaponVisualPlacer.Hold) and switched back on when it is let go of (Release).
///
/// Covers both the models the hand holds (Catalog/Weapons/Models) and the pickups laid out in
/// the level (Catalog/Weapons/Pickups). A pickup's existing root box is refitted to its mesh, so
/// it rests on the floor on its real shape. Re-runnable: it only fits and configures, never stacks
/// a second body or collider.
public static class AddWeaponPhysics
{
    static readonly string[] Folders =
    {
        "Assets/Resources/Catalog/Weapons/Models",
        "Assets/Resources/Catalog/Weapons/Pickups",
    };

    [MenuItem("Tools/Warehouse/Add Weapon Physics")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder("[WeaponPhysics]\n");

        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", Folders))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var box = root.GetComponent<BoxCollider>();
                if (box == null)
                {
                    if (root.GetComponent<Collider>() != null)
                    {
                        report.AppendLine($"• {root.name}: has a non-box root collider, left as is");
                    }
                    else box = root.AddComponent<BoxCollider>();
                }
                if (box != null)
                {
                    box.isTrigger = false;
                    WeaponVisualPlacer.FitBox(root.transform, box);
                }

                var rb = root.GetComponent<Rigidbody>();
                if (rb == null) rb = root.AddComponent<Rigidbody>();
                WeaponVisualPlacer.ConfigureBody(rb);

                PrefabUtility.SaveAsPrefabAsset(root, path);
                report.AppendLine($"• {root.name}: box {(box != null ? box.size.ToString("F2") : "-")}, body {rb.mass} kg");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        return report.ToString();
    }
}
