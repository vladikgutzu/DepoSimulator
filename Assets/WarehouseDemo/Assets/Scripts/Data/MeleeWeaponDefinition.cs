using UnityEngine;

/// Everything about one melee weapon: how it hits, how it is held, and what it looks like.
///
/// Kind stays an enum rather than becoming a string id like PartDefinition uses, because the
/// weapon a raider carries is replicated as an int over the wire (NetworkThief.PublishWeapon)
/// and read back by ThiefEvent's payload. Keeping the enum keeps that working; the asset only
/// takes over the numbers hanging off it.
[CreateAssetMenu(menuName = "Warehouse/Melee Weapon", fileName = "New Melee Weapon")]
public class MeleeWeaponDefinition : ScriptableObject
{
    [Tooltip("Which weapon this describes. One asset per kind — duplicates are ignored.")]
    public MeleeWeaponKind Kind = MeleeWeaponKind.Bat;

    [Header("Presentation")]
    public string DisplayName = "Bat";
    [TextArea]
    [Tooltip("Shown on pickup. Name the trade it makes — that is the part the player cannot " +
             "see by looking at it.")]
    public string PickupHint = "Left click to swing";

    [Header("Combat")]
    [Tooltip("A raider has 3 health, so this is really how many connects put him down.")]
    public int   Damage    = 2;
    [Tooltip("Metres from the player that still connect.")]
    public float Reach     = 2.4f;
    [Tooltip("Degrees either side of the aim direction that still connect.")]
    public float HalfAngle = 65f;
    [Tooltip("Seconds between swings. The whole swing animation is fitted into this.")]
    public float Cooldown  = 0.8f;
    [Tooltip("Seconds from pressing attack to the blow actually landing — the weight of the " +
             "weapon, felt as commitment. Heavier hafts want more. Must be less than Cooldown; " +
             "0 falls back to 30% of it, which is what every weapon did before this existed.")]
    public float Windup    = 0f;
    [Tooltip("Camera shake on a connect.")]
    public float Trauma    = 0.25f;

    [Tooltip("How many raiders one swing can catch. 1 is a duelling weapon that only ever hits " +
             "the nearest man in the arc; more than that sweeps through everyone standing in it, " +
             "which is what makes HalfAngle worth anything and what a crowd weapon is for.")]
    public int   MaxTargets = 1;

    [Header("Handling")]
    [Tooltip("Two-handed pins the off hand to the shaft and alternates forehand/backhand off " +
             "that grip; one-handed keeps it in a single fist.")]
    public MeleeSwingArm.GripKind Grip = MeleeSwingArm.GripKind.TwoHanded;

    [Tooltip("Where the off hand takes hold, in the weapon's own space. The models put the " +
             "grip at the origin with the shaft running down +Z, so Z is how far along it the " +
             "second hand sits. Ignored unless Grip is TwoHanded.")]
    public Vector3 OffHandGrip = new Vector3(0f, 0f, 0.16f);

    [Header("In the hand")]
    // How the weapon sits in the fist that holds it. This is the pair of numbers to nudge when a
    // model floats beside the hand or points the wrong way: every model is a different shape and
    // pivots slightly differently, so there is no single placement that suits all of them.
    //
    // The defaults are what every weapon used to get from hard-coded values in MeleeCombat, so
    // nothing moves until these are actually changed.
    [Tooltip("Position relative to the hand bone.")]
    public Vector3 HandOffset   = new Vector3(0.03f, 0.06f, 0.02f);

    [Tooltip("Rotation relative to the hand bone, in degrees. Start here if the weapon points " +
             "out of the wrong side of the fist.")]
    public Vector3 HandRotation = Vector3.zero;

    [Header("Carry")]
    // How the weapon hangs on the player's carry socket while it is simply being carried. The
    // socket is the one place on the body every weapon hangs from; these two are what make a
    // sledgehammer hang differently from a rolling pin once it is there.
    [Tooltip("Position on the carry socket.")]
    public Vector3 CarryOffset;

    [Tooltip("Rotation on the carry socket, in degrees.")]
    public Vector3 CarryRotation;

    [Header("Art")]
    [Tooltip("Leave empty and MeleeWeaponItem builds a primitive stand-in, so a weapon is " +
             "playable before any art exists for it.")]
    public GameObject Prefab;
}
