using System.Collections;
using UnityEngine;

/// Attached to CharacterVisual on the NetworkPlayer prefab.
/// Plays a physics ragdoll on a remote player's visual mesh when the owner triggers one, so
/// everyone else actually sees the fall instead of the body idling through it. Runs its own
/// local physics simulation driven by the same impulse — cosmetic only, never touches the
/// NetworkPlayer root's synced position, so it can't fight ClientNetworkTransform.
/// Requires the CharacterVisual/Skeleton bones to be rigged with Rigidbodies + CharacterJoints
/// (run "Tools → Warehouse Demo → Setup Remote Ragdoll" — mirrors the local player's own rig,
/// see SetupPlayerRagdoll.cs).
[DisallowMultipleComponent]
public class RemoteRagdollVisual : MonoBehaviour
{
    [SerializeField] float _ragdollDuration = 1.5f;
    [Tooltip("Extra time (on top of _ragdollDuration) to wait for bones to actually stop " +
             "moving before snapshotting the pose to blend from — matches RagdollController's " +
             "own settle-wait so remote viewers see the same natural get-up, not a puppet snap.")]
    [SerializeField] float _maxExtraSettleTime = 1.0f;
    [SerializeField] float _blendDuration   = 0.7f;
    [Tooltip("StandingUp (face-down get-up), matches RagdollController's own value.")]
    [SerializeField] float _standingUpDuration = 4.1f;
    [Tooltip("GettingUp (face-up get-up), matches RagdollController's own value.")]
    [SerializeField] float _gettingUpDuration = 2.43f;

    [Tooltip("Assign to Skeleton/Hips — same bone SetupPlayerRagdoll wires on the local rig.")]
    [SerializeField] Transform _hipBone;

    static readonly int _standingUpHash = Animator.StringToHash("StandingUp"); // face-down get-up
    static readonly int _gettingUpHash  = Animator.StringToHash("GettingUp");  // face-up get-up

    const float RestLinearThreshold  = 0.15f; // m/s
    const float RestAngularThreshold = 0.30f; // rad/s

    Animator    _animator;
    Rigidbody[] _ragdollBodies;

    struct BonePose
    {
        public Transform  bone;
        public Vector3    localPos;
        public Quaternion localRot;
    }

    BonePose[] _blendPoses;
    float      _blendWeight;
    bool       _blending;
    bool       _isRagdoll;

    // Ground clamp for the get-up, mirroring the one RagdollController runs for the local player.
    BodyContactPoints _contacts;
    bool    _gettingUp;
    float   _getUpGroundY;
    Vector3 _visualHome;

    void Awake()
    {
        _animator      = GetComponent<Animator>();
        _ragdollBodies = GetComponentsInChildren<Rigidbody>(true);
        _visualHome    = transform.localPosition;
        SetBonePhysics(false);
    }

    void LateUpdate()
    {
        if (_blending && _blendPoses != null)
        {
            foreach (var p in _blendPoses)
            {
                if (p.bone == null) continue;
                p.bone.localPosition = Vector3.Lerp(p.bone.localPosition, p.localPos, _blendWeight);
                p.bone.localRotation = Quaternion.Slerp(p.bone.localRotation, p.localRot, _blendWeight);
            }
        }

        if (_gettingUp) ClampGetUpToGround();
    }

    /// Keeps the watched body out of the floor for the same reason, and by the same measurement,
    /// as the local one — see RagdollController.ClampGetUpToGround.
    ///
    /// It would be tempting to rely on the owner's own clamp arriving with his synced position,
    /// and while the two run in step it does. They do not stay in step: each side waits for ITS
    /// OWN ragdoll to stop moving before starting the get-up (BodiesAtRest), and two independently
    /// simulated ragdolls settle at different moments. For the length of that disagreement the
    /// puppet plays one part of the clip while the root carries the correction for another, and
    /// the knees go back through the concrete.
    ///
    /// The correction goes on THIS transform's local Y — the CharacterVisual — never on the
    /// NetworkPlayer root, which belongs to ClientNetworkTransform. The blend above works in local
    /// space, so unlike the local player's it does not have to be carried along.
    void ClampGetUpToGround()
    {
        _contacts ??= new BodyContactPoints(transform, _ragdollBodies, _animator);

        float lowest = _contacts.LowestY();
        if (lowest == float.MaxValue) return;

        float lift = _getUpGroundY - lowest;
        if (Mathf.Abs(lift) < 0.005f) return;

        var p = transform.localPosition;
        p.y += lift;
        transform.localPosition = p;
    }

    /// The surface under the body, found the way RagdollController.GroundRootY finds it: ignoring
    /// the character's own colliders, which are what a ray fired from inside him hits first.
    float GroundUnderBody()
    {
        var from = (_hipBone != null ? _hipBone.position : transform.position) + Vector3.up * 0.5f;

        var hits = Physics.RaycastAll(from, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var hit in hits)
        {
            if (hit.collider.transform.IsChildOf(transform.root)) continue;
            return hit.point.y;
        }
        return transform.position.y;
    }

    /// True for the whole fall-and-get-up sequence. Anything else that writes these bones --
    /// MeleeSwingArm's springs, for one -- has to stand down while physics owns them.
    public bool IsRagdoll => _isRagdoll;

    public void Trigger(Vector3 impulse, Vector3 hitPoint)
    {
        if (_isRagdoll || _ragdollBodies.Length == 0) return;
        StartCoroutine(RagdollSequence(impulse, hitPoint));
    }

    IEnumerator RagdollSequence(Vector3 impulse, Vector3 hitPoint)
    {
        _isRagdoll = true;

        if (_animator) _animator.enabled = false;
        SetBonePhysics(true);

        var hitBone = GetNearestBone(hitPoint);
        hitBone?.AddForceAtPosition(impulse, hitPoint, ForceMode.Impulse);

        yield return new WaitForSeconds(_ragdollDuration);

        float settleElapsed = 0f;
        while (settleElapsed < _maxExtraSettleTime && !BodiesAtRest())
        {
            settleElapsed += Time.deltaTime;
            yield return null;
        }

        _blendPoses  = SnapshotPoses();
        _blendWeight = 1f;

        bool faceDown = _hipBone != null && Vector3.Dot(_hipBone.forward, Vector3.down) > 0f;
        int   getUpHash     = faceDown ? _standingUpHash    : _gettingUpHash;
        float getUpDuration = faceDown ? _standingUpDuration : _gettingUpDuration;

        SetBonePhysics(false);
        if (_animator)
        {
            _animator.enabled = true;
            _animator.Play(getUpHash, 0, 0f);
        }

        _getUpGroundY = GroundUnderBody();
        _gettingUp    = true;

        _blending = true;
        float elapsed = 0f;
        while (elapsed < _blendDuration)
        {
            _blendWeight = 1f - elapsed / _blendDuration;
            elapsed += Time.deltaTime;
            yield return null;
        }
        _blendWeight = 0f;
        _blending    = false;
        _blendPoses  = null;

        float remaining = getUpDuration - elapsed;
        if (remaining > 0f) yield return new WaitForSeconds(remaining);

        // Back to where the body hangs off the root when nothing is correcting it, or the offset
        // taken to get him out of the floor would ride along for the rest of the shift.
        _gettingUp = false;
        transform.localPosition = _visualHome;

        _isRagdoll = false;
    }

    bool BodiesAtRest()
    {
        foreach (var rb in _ragdollBodies)
        {
            if (rb.linearVelocity.sqrMagnitude  > RestLinearThreshold  * RestLinearThreshold)  return false;
            if (rb.angularVelocity.sqrMagnitude > RestAngularThreshold * RestAngularThreshold) return false;
        }
        return true;
    }

    // Matches RagdollController's own fix — bones are set up with ~0 damping, so the impact
    // impulse keeps them spinning on residual angular momentum well past when they should have
    // settled. Damping here makes the fall read as landing, not as a slowly-decelerating spin.
    const float BoneLinearDamping  = 1.5f;
    const float BoneAngularDamping = 5f;

    void SetBonePhysics(bool active)
    {
        foreach (var rb in _ragdollBodies)
        {
            rb.isKinematic      = !active;
            rb.detectCollisions = active;
            if (active)
            {
                rb.linearDamping  = BoneLinearDamping;
                rb.angularDamping = BoneAngularDamping;
                rb.WakeUp();
            }
        }
    }

    BonePose[] SnapshotPoses()
    {
        var poses = new BonePose[_ragdollBodies.Length];
        for (int i = 0; i < _ragdollBodies.Length; i++)
        {
            var t = _ragdollBodies[i].transform;
            poses[i] = new BonePose { bone = t, localPos = t.localPosition, localRot = t.localRotation };
        }
        return poses;
    }

    Rigidbody GetNearestBone(Vector3 worldPoint)
    {
        Rigidbody nearest = null;
        float     minSqr  = float.MaxValue;
        foreach (var rb in _ragdollBodies)
        {
            float sqr = (rb.position - worldPoint).sqrMagnitude;
            if (sqr < minSqr) { minSqr = sqr; nearest = rb; }
        }
        return nearest;
    }
}
