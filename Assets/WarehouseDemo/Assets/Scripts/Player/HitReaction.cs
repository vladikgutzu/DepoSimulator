using UnityEngine;

/// A body's flinch when something connects with it.
///
/// It exists because a blow that lands on someone else was invisible. The player being hit felt
/// it — camera, injury, sometimes a ragdoll — and everyone watching saw a man standing still
/// while another man swung at him. Health bars are not the answer to that; the body reacting is.
///
/// It bends bones rather than moving the transform, on purpose. Position is owned by the
/// CharacterController on a player and the NavMeshAgent on a raider, and anything that writes to
/// it fights them and desyncs the network. A rotation layered on the skeleton in LateUpdate is
/// nobody else's business: it reads clearly, it composes with whatever clip is playing, and it
/// cannot move anyone through a wall.
///
/// Not an animation clip either: a clip would have to interrupt the run, the swing or the carry
/// to play, and the flinch has to be able to happen during any of them.
[DefaultExecutionOrder(200)]   // after the animator, after any IK that also writes bones
public class HitReaction : MonoBehaviour
{
    [Tooltip("Peak deflection of the spine, in degrees, at full strength.")]
    [SerializeField] float _maxAngle = 26f;

    [Tooltip("How fast it springs back. Higher snaps quicker.")]
    [SerializeField] float _stiffness = 140f;

    [Tooltip("How hard the spring is braked. Around 2*sqrt(stiffness) stops it wobbling.")]
    [SerializeField] float _damping = 22f;

    // The bend is spread down the chain rather than put all on one bone: bending only the chest
    // snaps a body in half, while carrying part of it through the spine and paying it off in the
    // head reads as a whole person absorbing a hit.
    static readonly (HumanBodyBones bone, float share)[] Chain =
    {
        (HumanBodyBones.Spine,  0.30f),
        (HumanBodyBones.Chest,  0.40f),
        (HumanBodyBones.Head,   0.30f),
    };

    Animator  _animator;
    Transform[] _bones;

    float   _angle;      // current deflection, degrees
    float   _velocity;   // spring velocity, degrees per second
    Vector3 _axis;       // world axis the bend happens about

    /// Finds or adds the reaction on a body. Added at runtime rather than authored on the
    /// prefabs so that bodies built before this existed, and bodies swapped at runtime by
    /// Tools/Warehouse/Player Body, are all covered without a setup step to remember.
    public static HitReaction Ensure(GameObject body)
    {
        if (body == null) return null;

        var hit = body.GetComponent<HitReaction>();
        return hit != null ? hit : body.AddComponent<HitReaction>();
    }

    void Awake() => Rebind();

    /// Picks the skeleton up again after the body has been swapped underneath us.
    public void Rebind()
    {
        _animator = GetComponentInChildren<Animator>();
        _bones    = new Transform[Chain.Length];

        if (_animator == null || !_animator.isHuman) return;

        for (int i = 0; i < Chain.Length; i++)
            _bones[i] = _animator.GetBoneTransform(Chain[i].bone);
    }

    /// Take one. `direction` is the way the blow travelled, in world space; `strength` is 0..1.
    public void Play(Vector3 direction, float strength = 1f)
    {
        if (strength <= 0f) return;

        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) direction = -transform.forward;

        // Bending about the horizontal axis across the blow is what makes the body fold away
        // from it rather than spin around it.
        _axis = Vector3.Cross(Vector3.up, direction.normalized);
        if (_axis.sqrMagnitude < 0.0001f) _axis = transform.right;
        _axis.Normalize();

        // Kicked as velocity rather than set as a position, so a second hit during the first
        // one adds to it instead of restarting it — being hit twice should look worse than once.
        _velocity += _maxAngle * Mathf.Clamp01(strength) * 12f;
    }

    /// True while the flinch is still visibly running.
    public bool IsPlaying => Mathf.Abs(_angle) > 0.05f || Mathf.Abs(_velocity) > 0.05f;

    void LateUpdate()
    {
        if (!IsPlaying) return;
        if (_bones == null || _animator == null) return;

        // A ragdoll owns the skeleton outright — its own physics writes every bone, and a flinch
        // layered on top would fight the joints. A knockdown is its own, larger feedback anyway.
        if (!_animator.enabled) { _angle = 0f; _velocity = 0f; return; }

        // Critically damped spring back to rest. Unscaled would be wrong here — this is part of
        // the world, and if the world is slowed the flinch should slow with it.
        float dt = Time.deltaTime;
        _velocity += (-_stiffness * _angle - _damping * _velocity) * dt;
        _angle    += _velocity * dt;

        float clamped = Mathf.Clamp(_angle, -_maxAngle, _maxAngle);

        for (int i = 0; i < _bones.Length; i++)
        {
            var bone = _bones[i];
            if (bone == null) continue;

            // Applied on top of the pose the animator just wrote, not instead of it.
            bone.rotation = Quaternion.AngleAxis(clamped * Chain[i].share, _axis) * bone.rotation;
        }
    }
}
