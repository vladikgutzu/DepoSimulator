using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Puts draggable grip points on every weapon model, seeded from the numbers the weapon is held by
/// today.
///
/// Seeded, not zeroed, on purpose: run this and nothing changes in game — the anchors reproduce the
/// current hold exactly. From there the grip is adjusted by dragging the points in the prefab and
/// watching the hand, which is the only way anyone was ever going to judge it. Re-running leaves
/// anchors that already exist exactly where they were put.
///
/// The maths is the inverse of MeleeCombat.PlaceByGrip: that one moves the weapon so the anchor
/// lands on the hand bone, so an anchor placed like this lands the weapon where HandOffset and
/// HandRotation used to put it.
public static class AddWeaponGripAnchors
{
    const string GripName    = "GripPoint";
    const string OffHandName = "OffHandPoint";

    [MenuItem("Tools/Warehouse/Add Weapon Grip Anchors")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder();
        var defs   = Resources.LoadAll<MeleeWeaponDefinition>("Catalog/Weapons");

        if (defs.Length == 0) return "no weapon definitions under Resources/Catalog/Weapons";

        foreach (var def in defs.OrderBy(d => d.name))
        {
            if (def.Prefab == null) { report.AppendLine($"• {def.name}: no prefab — skipped"); continue; }

            string path = AssetDatabase.GetAssetPath(def.Prefab);
            if (string.IsNullOrEmpty(path)) { report.AppendLine($"• {def.name}: prefab is not an asset"); continue; }

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var anchors = root.GetComponent<WeaponGripAnchors>() ?? root.AddComponent<WeaponGripAnchors>();
                var so      = new SerializedObject(anchors);

                var grip = Ensure(root.transform, GripName, out bool gripIsNew);
                if (gripIsNew)
                {
                    // Inverse of PlaceByGrip, so the weapon ends up exactly where it sits today.
                    Quaternion hand = Quaternion.Euler(def.HandRotation);
                    grip.localRotation = Quaternion.Inverse(hand);
                    grip.localPosition = -(Quaternion.Inverse(hand) * def.HandOffset);
                }

                so.FindProperty("_mainGrip").objectReferenceValue = grip;

                // Only a two-handed weapon has a second grip; a one-hander gets no point to drag,
                // because there is no second hand on it to place.
                if (def.Grip == MeleeSwingArm.GripKind.TwoHanded)
                {
                    var off = Ensure(root.transform, OffHandName, out bool offIsNew);
                    if (offIsNew)
                    {
                        off.localPosition = def.OffHandGrip;
                        off.localRotation = Quaternion.identity;
                    }
                    so.FindProperty("_offHandGrip").objectReferenceValue = off;
                }

                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, path);

                report.AppendLine($"• {def.name}: {(gripIsNew ? "anchors added" : "anchors already there, left alone")}" +
                                  $" ({System.IO.Path.GetFileName(path)})");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        AssetDatabase.SaveAssets();

        report.AppendLine("\nDrag GripPoint / OffHandPoint in each weapon prefab: GripPoint is put "
                        + "into the fist (its +Z is the shaft direction), OffHandPoint is where the "
                        + "second hand is pulled to.");
        return report.ToString();
    }

    /// Moves an existing GripPoint back onto the numbers currently on the asset.
    ///
    /// Public because AlignWeaponGrips computes those numbers and has to be able to push them
    /// through to the anchor — the game reads the anchor now, so a tool that only wrote the asset
    /// would compute a grip nobody ever sees.
    public static bool ReseedGrip(MeleeWeaponDefinition def)
    {
        if (def == null || def.Prefab == null) return false;

        string path = AssetDatabase.GetAssetPath(def.Prefab);
        if (string.IsNullOrEmpty(path)) return false;

        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var anchors = root.GetComponent<WeaponGripAnchors>();
            var grip    = anchors != null ? anchors.MainGrip : null;
            if (grip == null) return false;

            Quaternion hand = Quaternion.Euler(def.HandRotation);
            grip.localRotation = Quaternion.Inverse(hand);
            grip.localPosition = -(Quaternion.Inverse(hand) * def.HandOffset);

            PrefabUtility.SaveAsPrefabAsset(root, path);
            return true;
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    static Transform Ensure(Transform parent, string name, out bool created)
    {
        var existing = parent.Find(name);
        created = existing == null;
        if (!created) return existing;

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }
}
