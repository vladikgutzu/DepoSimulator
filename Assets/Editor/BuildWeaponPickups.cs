using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// Builds one drag-and-drop pickup prefab per weapon, so weapons can be placed in the warehouse by
/// hand instead of being scattered by code at the start of the shift.
///
/// Everything a pickup needs is baked in: the model, a solid collider PlayerInteraction can raycast
/// against, and the MeleeWeaponItem with its kind already set. Drop one in the scene, put it where
/// it belongs, and it works — the id every peer needs is assigned by the scene numbering pass on
/// load, not here.
///
/// Idempotent: re-running rebuilds each prefab in place, so the instances already placed in the
/// scene keep their positions and their overrides.
public static class BuildWeaponPickups
{
    const string OutputDir = "Assets/Resources/Catalog/Weapons/Pickups";

    /// The collider is what PlayerInteraction.Scan hits, and it must not be a trigger: the scan
    /// raycasts with QueryTriggerInteraction.Ignore, so a trigger is invisible to it.
    static readonly Vector3 HitBoxSize = new(0.35f, 0.35f, 0.7f);

    [MenuItem("Tools/Warehouse/Build Weapon Pickups")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        Directory.CreateDirectory(OutputDir);

        var report = new List<string>();

        foreach (MeleeWeaponKind kind in System.Enum.GetValues(typeof(MeleeWeaponKind)))
        {
            if (kind == MeleeWeaponKind.Fists) continue;   // nothing to lay on the floor

            string path = $"{OutputDir}/weapon_pickup_{kind.ToString().ToLowerInvariant()}.prefab";

            var root = MeleeWeaponItem.BuildVisual(kind);
            if (root == null) { report.Add($"• {kind}: no visual — skipped"); continue; }

            root.name = $"WeaponPickup_{kind}";

            // Lying on its side, the way one would be left on a shelf or a bench. The level author
            // can turn it however they like afterwards; this is only a sane starting pose.
            root.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            var hitBox = root.AddComponent<BoxCollider>();
            hitBox.size   = HitBoxSize;
            hitBox.center = Vector3.zero;

            var item = root.AddComponent<MeleeWeaponItem>();

            // _kind is private and serialized, which is the right shape for a value the level
            // should not be editing by accident — so it is written through SerializedObject.
            var so = new SerializedObject(item);
            so.FindProperty("_kind").enumValueIndex =
                System.Array.IndexOf(System.Enum.GetValues(typeof(MeleeWeaponKind)), kind);
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);

            report.Add($"• {path}");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return string.Join("\n", report) +
               $"\n\nDrag these into the warehouse wherever weapons should live. While the scene " +
               $"holds any of them, GameEventManager stops scattering its own at shift start.";
    }
}
