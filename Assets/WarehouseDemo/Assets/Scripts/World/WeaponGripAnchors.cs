using UnityEngine;

/// Where the hands go on a weapon, as objects you can drag instead of numbers you have to guess.
///
/// The same two placements exist on MeleeWeaponDefinition as vectors — HandRotation/HandOffset for
/// the fist and OffHandGrip for the second hand — and they still work; this simply takes over when
/// the model carries anchors, because a grip is a thing you judge by looking at it. Nobody can read
/// `HandRotation: (-55.4, 17.0, -88.9)` and say whether the haft ends up in the palm.
///
/// Same shape as BoxHandAnchors, which does this for a carried box.
///
/// MainGrip is matched to the hand bone POSE, not just its position: the weapon is placed so that
/// this anchor lands exactly where the hand is and points the way the hand points. So aim the
/// anchor's blue axis (+Z) along the shaft the way you want the weapon to leave the fist, and put
/// it where the palm closes.
///
/// OffHandGrip is only read for a two-handed weapon (Grip = TwoHanded on the asset). It is the
/// point on the shaft the left hand is pulled to by the IK in MeleeSwingArm — its rotation is
/// ignored, only the position matters.
public class WeaponGripAnchors : MonoBehaviour
{
    [Header("Anchors (children of this prefab)")]
    [Tooltip("Placed into the right hand: both its position and its rotation are matched.")]
    [SerializeField] Transform _mainGrip;

    [Tooltip("Where the second hand is pulled on a two-handed weapon. Rotation is ignored.")]
    [SerializeField] Transform _offHandGrip;

    public Transform MainGrip   => _mainGrip;
    public Transform OffHandGrip => _offHandGrip;

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (_mainGrip != null)
        {
            Gizmos.color = new Color(0.2f, 0.5f, 1f);
            Gizmos.DrawSphere(_mainGrip.position, 0.02f);
            // The shaft direction, so it is obvious which way the weapon will leave the fist.
            Gizmos.DrawLine(_mainGrip.position, _mainGrip.position + _mainGrip.forward * 0.25f);
        }

        if (_offHandGrip != null)
        {
            Gizmos.color = new Color(1f, 0.3f, 0.2f);
            Gizmos.DrawSphere(_offHandGrip.position, 0.02f);
        }

        if (_mainGrip != null && _offHandGrip != null)
        {
            Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
            Gizmos.DrawLine(_mainGrip.position, _offHandGrip.position);
        }
    }
#endif
}
