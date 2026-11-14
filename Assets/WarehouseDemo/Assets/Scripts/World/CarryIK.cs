using UnityEngine;

/// Pins a body's hands to two points — what makes a carried box look held rather than followed.
///
/// Separate from PlayerHandIK on purpose: that one is a singleton, refuses to live under a
/// NetworkPlayer and carries the local rig's foot IK with it. A raider needs none of that and
/// there can be four of them on screen at once, so this is the small half of it.
///
/// Needs IK Pass enabled on the animator layer whose mask covers the arms — without it Unity
/// never calls OnAnimatorIK and the goals below are simply never read.
public class CarryIK : MonoBehaviour
{
    [Range(0f, 8f)] public float BlendSpeed = 6f;

    Animator  _anim;
    Transform _rightTarget;
    Transform _leftTarget;
    float     _weight;
    float     _targetWeight;

    /// Attaches to whichever object owns the Animator, since OnAnimatorIK is only called there.
    public static CarryIK Ensure(Animator animator)
    {
        if (animator == null) return null;

        var ik = animator.GetComponent<CarryIK>();
        if (ik == null) ik = animator.gameObject.AddComponent<CarryIK>();

        ik._anim = animator;
        return ik;
    }

    public void SetTargets(Transform right, Transform left, float weight = 1f)
    {
        _rightTarget  = right;
        _leftTarget   = left;
        _targetWeight = weight;
    }

    /// Eased out rather than dropped: hands letting go of a box should leave it, not blink off it.
    public void Clear() => _targetWeight = 0f;

    void Awake() { if (_anim == null) _anim = GetComponent<Animator>(); }

    void Update()
    {
        _weight = Mathf.MoveTowards(_weight, _targetWeight, Time.deltaTime * BlendSpeed);
        if (_weight <= 0f && _targetWeight <= 0f) { _rightTarget = null; _leftTarget = null; }
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (_anim == null) return;

        bool right = _rightTarget != null && _weight > 0f;
        bool left  = _leftTarget  != null && _weight > 0f;

        // Rotation is pulled less hard than position. A wrist forced all the way onto a target's
        // orientation reads as a mannequin; letting the animation keep some of it keeps the arm
        // looking like it belongs to the body it is attached to.
        _anim.SetIKPositionWeight(AvatarIKGoal.RightHand, right ? _weight        : 0f);
        _anim.SetIKRotationWeight(AvatarIKGoal.RightHand, right ? _weight * 0.6f : 0f);
        if (right)
        {
            _anim.SetIKPosition(AvatarIKGoal.RightHand, _rightTarget.position);
            _anim.SetIKRotation(AvatarIKGoal.RightHand, _rightTarget.rotation);
        }

        _anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, left ? _weight        : 0f);
        _anim.SetIKRotationWeight(AvatarIKGoal.LeftHand, left ? _weight * 0.6f : 0f);
        if (left)
        {
            _anim.SetIKPosition(AvatarIKGoal.LeftHand, _leftTarget.position);
            _anim.SetIKRotation(AvatarIKGoal.LeftHand, _leftTarget.rotation);
        }
    }
}
