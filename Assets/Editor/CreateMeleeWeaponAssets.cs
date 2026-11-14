using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Writes the MeleeWeaponDefinition assets the registry loads.
///
/// Idempotent: an asset that already exists is left exactly as it is, so re-running after
/// adding a weapon fills in the gap without stamping over numbers somebody has since balanced.
public static class CreateMeleeWeaponAssets
{
    const string Folder = "Assets/Resources/Catalog/Weapons";

    [MenuItem("Tools/Warehouse/Create Melee Weapon Assets")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        Directory.CreateDirectory(Folder);

        var report = new StringBuilder();

        // The numbers that used to live in MeleeCombat.StatsFor, moved verbatim so the refactor
        // changes where balance lives without changing what it is.
        // A raider has 3 health, so damage is really "how many connects put him down". The set
        // trades along one line: the harder it hits, the longer you are committed to the swing.
        Make(report, MeleeWeaponKind.Bat, "Bat",
             damage: 2, reach: 2.4f, halfAngle: 65f, cooldown: 0.80f, windup: 0.34f, trauma: 0.25f,
             grip: MeleeSwingArm.GripKind.TwoHanded, offHand: new Vector3(0f, 0f, 0.16f),
             hint: "Left click to swing \u2014 long reach, two hits to put a raider down");

        Make(report, MeleeWeaponKind.Crowbar, "Crowbar",
             damage: 2, reach: 2.0f, halfAngle: 55f, cooldown: 0.65f, windup: 0.26f, trauma: 0.22f,
             grip: MeleeSwingArm.GripKind.OneHanded, offHand: Vector3.zero,
             hint: "Left click to swing \u2014 two hits, quicker than the bat");

        // Short but dense: the same two connects as the crowbar, slower, and it keeps a hand free.
        Make(report, MeleeWeaponKind.PipeWrench, "Pipe Wrench",
             damage: 2, reach: 1.9f, halfAngle: 50f, cooldown: 0.75f, windup: 0.32f, trauma: 0.28f,
             grip: MeleeSwingArm.GripKind.OneHanded, offHand: Vector3.zero,
             hint: "Left click to swing \u2014 heavy in one hand, two hits");

        // The reach weapon, and the only sweeping one. Barely hurts \u2014 the same 1 as bare hands,
        // swung slower than they are \u2014 so against a single raider it is a downgrade, and is meant
        // to be. What it buys is the whole arc at once, which is what a gate's worth of them
        // walking in is for.
        //
        // This is also what its hint always claimed and never did: swings only ever hit the
        // nearest man in the arc, so "the widest arc" bought nothing at all and the plank was
        // simply worse than fists, 2.10 s to a kill against 1.65 s.
        Make(report, MeleeWeaponKind.Plank, "Plank",
             damage: 1, reach: 2.8f, halfAngle: 70f, cooldown: 0.70f, windup: 0.30f, trauma: 0.18f,
             grip: MeleeSwingArm.GripKind.TwoHanded, offHand: new Vector3(0f, 0f, 0.35f),
             hint: "Left click to sweep \u2014 the longest reach, and it catches everyone in front of you",
             maxTargets: 6);

        // Fastest thing in the game and the weakest. For staying mobile in a crowd.
        Make(report, MeleeWeaponKind.RollingPin, "Rolling Pin",
             damage: 1, reach: 1.6f, halfAngle: 55f, cooldown: 0.38f, windup: 0.12f, trauma: 0.12f,
             grip: MeleeSwingArm.GripKind.OneHanded, offHand: Vector3.zero,
             hint: "Left click to swing \u2014 quick and one-handed, but three hits");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return report.ToString();
    }

    static void Make(StringBuilder report, MeleeWeaponKind kind, string displayName,
                     int damage, float reach, float halfAngle, float cooldown, float windup,
                     float trauma, MeleeSwingArm.GripKind grip, Vector3 offHand, string hint,
                     int maxTargets = 1)
    {
        string path = $"{Folder}/weapon_{kind.ToString().ToLowerInvariant()}.asset";

        var existing = AssetDatabase.LoadAssetAtPath<MeleeWeaponDefinition>(path);
        if (existing != null)
        {
            // Fields added after an asset was written come back at the C# default. Filling in
            // only those leaves anything already balanced untouched, which a blanket overwrite
            // would not.
            var filled = new System.Collections.Generic.List<string>();

            if (existing.Windup <= 0f)
            {
                existing.Windup = windup;
                filled.Add($"Windup = {windup}s");
            }

            if (existing.MaxTargets <= 1 && maxTargets > 1)
            {
                existing.MaxTargets = maxTargets;
                filled.Add($"MaxTargets = {maxTargets}");
            }

            // Player-facing copy, not balance: a hint that promises something the weapon no longer
            // does — or, as here, now finally does — is worth correcting even on an asset that is
            // otherwise left alone.
            if (existing.PickupHint != hint)
            {
                existing.PickupHint = hint;
                filled.Add("PickupHint");
            }

            if (filled.Count > 0)
            {
                EditorUtility.SetDirty(existing);
                report.AppendLine($"• {path}: filled in {string.Join(", ", filled)}.");
            }
            else
            {
                report.AppendLine($"• {path} already exists — left alone.");
            }
            return;
        }

        var def = ScriptableObject.CreateInstance<MeleeWeaponDefinition>();
        def.Kind        = kind;
        def.DisplayName = displayName;
        def.PickupHint  = hint;
        def.Damage      = damage;
        def.Reach       = reach;
        def.HalfAngle   = halfAngle;
        def.Cooldown    = cooldown;
        def.Windup      = windup;
        def.Trauma      = trauma;
        def.Grip        = grip;
        def.OffHandGrip = offHand;
        def.MaxTargets  = maxTargets;

        AssetDatabase.CreateAsset(def, path);
        report.AppendLine($"• created {path}");
    }
}
