using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;

/// Works out how a weapon should sit in the fist, instead of anyone guessing at Euler angles.
///
/// A weapon model leaves BuildWeaponModels with its grip on the origin and its shaft down +Z.
/// The hand bone's own axes are whatever the rig happened to be authored with - on this one +Z
/// points sideways, which is why every weapon stuck out of the fist like a lance. So the shaft
/// is aimed along the fingers, measured from the hand bone to the middle finger, and the wrist
/// rotation that achieves it is written into each weapon asset.
///
/// Run in play mode: the bones have to be posed for the measurement to mean anything.
public static class AlignWeaponGrips
{
    const string WeaponFolder = "Assets/Resources/Catalog/Weapons";

    /// A weapon held dead along the fingers looks like a pointer. Tipping it up out of the fist
    /// is what makes it read as carried rather than aimed.
    const float TipUpDegrees = 35f;

    public static string Execute()
    {
        if (!Application.isPlaying) return "play mode only - the rig has to be posed to measure it";

        // Found rather than taken from MeleeCombat.Instance: compiling a script during play
        // reloads the domain and wipes statics, so the singleton can be null while the component
        // it points at is alive and well.
        var combat = Object.FindAnyObjectByType<MeleeCombat>();
        if (combat == null) return "no MeleeCombat in the running scene";

        var anim = combat.GetComponentInChildren<Animator>();
        if (anim == null || !anim.isHuman) return "no humanoid animator";

        var hand   = anim.GetBoneTransform(HumanBodyBones.RightHand);
        var finger = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal)
                  ?? anim.GetBoneTransform(HumanBodyBones.RightIndexProximal);
        var thumb  = anim.GetBoneTransform(HumanBodyBones.RightThumbProximal);

        if (hand == null || finger == null)
            return "the avatar does not map the right hand and its fingers";

        // Along the fingers, in the hand's own space: this is where a held shaft should run.
        Vector3 alongFingers = hand.InverseTransformPoint(finger.position).normalized;

        // The palm's normal, so the tip-up happens over the back of the hand rather than
        // sideways through it. Derived from the thumb when there is one, since that is what
        // actually tells the two sides of a hand apart.
        Vector3 towardThumb = thumb != null
            ? hand.InverseTransformPoint(thumb.position).normalized
            : Vector3.up;

        Vector3 palmAxis = Vector3.Cross(alongFingers, towardThumb).normalized;
        if (palmAxis.sqrMagnitude < 0.01f) palmAxis = Vector3.up;

        Vector3 aim = Quaternion.AngleAxis(TipUpDegrees, palmAxis) * alongFingers;

        // The rotation that takes the model's +Z onto that aim, keeping the weapon's own up
        // roughly aligned with the back of the hand so it does not roll in the grip.
        Quaternion grip = Quaternion.LookRotation(aim, palmAxis);

        var euler = grip.eulerAngles;
        euler = new Vector3(Wrap(euler.x), Wrap(euler.y), Wrap(euler.z));

        var report = new StringBuilder();
        report.AppendLine($"fingers  (hand-local) = {alongFingers}");
        report.AppendLine($"palm axis            = {palmAxis}");
        report.AppendLine($"grip rotation        = {euler}");

        int written  = 0;
        int reseeded = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:MeleeWeaponDefinition",
                                                          new[] { WeaponFolder }))
        {
            var def = AssetDatabase.LoadAssetAtPath<MeleeWeaponDefinition>(
                          AssetDatabase.GUIDToAssetPath(guid));
            if (def == null) continue;

            def.HandRotation = euler;
            EditorUtility.SetDirty(def);
            written++;

            // The grip the game actually reads is the anchor on the model (WeaponGripAnchors),
            // with the asset's numbers as the fallback. Push the measurement through to it, or
            // this tool would keep computing a hold nobody sees.
            if (AddWeaponGripAnchors.ReseedGrip(def)) reseeded++;
        }

        AssetDatabase.SaveAssets();
        report.AppendLine($"written to {written} weapon assets, {reseeded} grip anchors moved with them");

        // Put it on what is already in hand so the change is visible without re-equipping.
        combat.ApplyHandPlacement();

        return report.ToString();
    }

    static float Wrap(float deg) => deg > 180f ? deg - 360f : deg;
}
