using UnityEngine;

/// Makes a broken leg look broken while walking.
///
/// Without this a player with a leg hanging open walks exactly as before, only slower — the speed
/// penalty is the whole of it, and the locomotion clip has no idea anything happened. Next to a
/// missing panel and a chunk of torn plating that reads as a bug rather than as an injury.
///
/// There is no limp clip in the project and no reason to author one: a limp is not really a
/// different walk, it is the same walk with the body refusing to put weight on one side. That is a
/// thing you can do to the hips, over whatever clip happens to be playing, and it keeps working if
/// the locomotion is ever changed.
///
/// The cue is the one physiotherapists name: the hips drop and roll *away* from the bad leg while
/// it is carrying weight, because the body is unloading it. Everything above the hips comes along
/// for free, since the whole skeleton hangs off them.
///
/// In LateUpdate, and after everything else's, because the animator writes the hips every frame:
/// this reads what the animation produced and adds to it, so nothing accumulates.
[DefaultExecutionOrder(200)]
[DisallowMultipleComponent]
public class PlayerLimp : MonoBehaviour
{
    [Tooltip("How far the hips drop when weight goes onto the bad leg, in metres.")]
    [SerializeField] float _dip = 0.055f;

    [Tooltip("How far the hips roll away from the bad leg, in degrees.")]
    [SerializeField] float _roll = 7f;

    [Tooltip("How quickly the lurch comes and goes. Lower is heavier and more laboured.")]
    [SerializeField] float _response = 9f;

    [Tooltip("Below this speed he is standing rather than walking, and a limp on the spot is a " +
             "twitch. Metres per second.")]
    [SerializeField] float _minSpeed = 0.35f;

    Animator  _animator;
    Transform _hips, _leftFoot, _rightFoot;
    Vector3   _lastPosition;
    float     _weight;      // 0 walking normally, 1 fully on the bad leg

    void Awake()
    {
        Rebind();
        _lastPosition = transform.position;
    }

    /// Re-finds the bones. The body can be swapped at runtime, and every cached bone on the old
    /// skeleton is a reference to something that no longer exists — the same trap HitReaction and
    /// CarryIK both have to handle.
    public void Rebind()
    {
        _animator = GetComponentInChildren<Animator>();

        if (_animator == null || !_animator.isHuman)
        {
            _hips = _leftFoot = _rightFoot = null;
            return;
        }

        _hips      = _animator.GetBoneTransform(HumanBodyBones.Hips);
        _leftFoot  = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        _rightFoot = _animator.GetBoneTransform(HumanBodyBones.RightFoot);
    }

    InjuredLocomotion _hobble;

    void LateUpdate()
    {
        if (_hips == null || _leftFoot == null || _rightFoot == null) return;

        // Stand down when the hobble clip is playing. That clip already carries the whole limp;
        // adding a hip dip on top of it would be two limps stacked on one body.
        if (_hobble == null) _hobble = GetComponent<InjuredLocomotion>();
        if (_hobble != null && _hobble.Hobbling) { _weight = 0f; return; }

        var bad = BadLeg();

        float speed = (transform.position - _lastPosition).magnitude / Mathf.Max(Time.deltaTime, 1e-4f);
        _lastPosition = transform.position;

        // Weight is on the bad leg when its foot is the lower of the two — the same test used to
        // measure stride off an in-place clip, and it works whatever the animation is doing.
        bool loaded = bad != BodyPart.None
                   && speed > _minSpeed
                   && (bad == BodyPart.LeftLeg
                        ? _leftFoot.position.y < _rightFoot.position.y
                        : _rightFoot.position.y < _leftFoot.position.y);

        _weight = Mathf.MoveTowards(_weight, loaded ? 1f : 0f, _response * Time.deltaTime);

        if (_weight <= 0.001f) return;

        // Added to what the animator just wrote, never accumulated onto the previous frame's
        // result: the animator overwrites the hips every frame, so this stays a fixed offset.
        float lean = bad == BodyPart.LeftLeg ? 1f : -1f;

        _hips.localPosition += Vector3.down * (_dip * _weight);
        _hips.localRotation  = Quaternion.AngleAxis(_roll * _weight * lean, Vector3.forward)
                             * _hips.localRotation;
    }

    BodyPart BadLeg()
    {
        var injuries = InjurySystem.Instance;
        if (injuries == null) return BodyPart.None;

        foreach (var (_, part) in injuries.Active)
            if (part == BodyPart.LeftLeg || part == BodyPart.RightLeg) return part;

        return BodyPart.None;
    }
}
