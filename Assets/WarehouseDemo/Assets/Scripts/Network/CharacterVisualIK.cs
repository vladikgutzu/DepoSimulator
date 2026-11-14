using UnityEngine;

/// Applies hand and foot IK to a remote player's CharacterVisual Animator.
/// Added at runtime by NetworkVehicleSync.ApplyOccupantIK when any player
/// enters a vehicle. Requires IK Pass = true on the Animator's Base Layer.
[RequireComponent(typeof(Animator))]
public class CharacterVisualIK : MonoBehaviour
{
    Animator  _anim;
    Transform _spine, _chest;
    Transform _rightHand, _leftHand;
    Transform _packedRightHand, _packedLeftHand;
    Transform _rightFoot, _leftFoot;
    float     _handW, _handTgt;
    float     _footW, _footTgt;
    float     _carryLean, _carryLeanTgt;
    bool      _packedCarryActive;

    // Matches PlayerHandIK.BlendSpeed so a remote-observed player's IK settles at the same pace
    // as what the local player sees of themselves.
    [Range(0f, 5f)] public float BlendSpeed = 4f;

    void Awake()
    {
        _anim = GetComponent<Animator>();
        if (_anim != null && _anim.isHuman)
        {
            _spine = _anim.GetBoneTransform(HumanBodyBones.Spine);
            _chest = _anim.GetBoneTransform(HumanBodyBones.Chest);
        }
    }

    // ── API ───────────────────────────────────────────────────────────────────────

    public void SetHandTargets(Transform right, Transform left, float weight = 1f)
    {
        _packedCarryActive = false;
        _rightHand = right; _leftHand = left; _handTgt = weight;
    }

    public void SetPackedCarryTargets(Transform right, Transform left, float weight = 1f)
    {
        SetHandTargets(right, left, weight);
        _packedCarryActive = true;
    }

    public void SetPackedCarryTargets(float weight = 1f)
    {
        if (_packedRightHand == null || _packedLeftHand == null)
        {
            Transform parent = _anim != null ? _anim.transform : transform;
            _packedRightHand = new GameObject("PackedCarryHand_R").transform;
            _packedLeftHand  = new GameObject("PackedCarryHand_L").transform;
            _packedRightHand.SetParent(parent, false);
            _packedLeftHand.SetParent(parent, false);
        }
        _packedRightHand.localPosition = PlayerHandIK.PackedRightLocalPosition;
        _packedLeftHand.localPosition  = PlayerHandIK.PackedLeftLocalPosition;
        _packedRightHand.localRotation = PlayerHandIK.PackedRightLocalRotation;
        _packedLeftHand.localRotation  = PlayerHandIK.PackedLeftLocalRotation;
        SetHandTargets(_packedRightHand, _packedLeftHand, weight);
    }

    public void SetCarryLean(float degrees) => _carryLeanTgt = Mathf.Clamp(degrees, 0f, 70f);

    public void SetFootTargets(Transform right, Transform left, float weight = 1f)
    {
        _rightFoot = right; _leftFoot = left; _footTgt = weight;
    }

    public void ClearHandTargets()
    {
        _handTgt = 0f;
        _carryLeanTgt = 0f;
        _packedCarryActive = false;
    }
    public void ClearFootTargets() => _footTgt = 0f;

    public void ClearAllTargets()
    {
        ClearHandTargets();
        ClearFootTargets();
    }

    // ── Update ────────────────────────────────────────────────────────────────────

    void Update()
    {
        _handW = Mathf.MoveTowards(_handW, _handTgt, Time.deltaTime * BlendSpeed);
        _carryLean = Mathf.MoveTowards(_carryLean, _carryLeanTgt,
                                       Time.deltaTime * BlendSpeed * 8f);
        _footW = Mathf.MoveTowards(_footW, _footTgt, Time.deltaTime * BlendSpeed);
        if (_handW <= 0f) { _rightHand = null; _leftHand = null; }
        if (_footW <= 0f) { _rightFoot = null; _leftFoot = null; }
    }

    // ── IK ────────────────────────────────────────────────────────────────────────

    void OnAnimatorIK(int layerIndex)
    {
        if (_anim == null) return;

        bool hasR = _rightHand != null && _handW > 0f;
        bool hasL = _leftHand  != null && _handW > 0f;

        _anim.SetIKPositionWeight(AvatarIKGoal.RightHand, hasR ? _handW        : 0f);
        float handRotationWeight = _packedCarryActive ? 0.20f : 0.60f;
        _anim.SetIKRotationWeight(AvatarIKGoal.RightHand,
                                  hasR ? _handW * handRotationWeight : 0f);
        if (hasR)
        {
            _anim.SetIKPosition(AvatarIKGoal.RightHand, _rightHand.position);
            _anim.SetIKRotation(AvatarIKGoal.RightHand, _rightHand.rotation);
        }

        _anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, hasL ? _handW        : 0f);
        _anim.SetIKRotationWeight(AvatarIKGoal.LeftHand,
                                  hasL ? _handW * handRotationWeight : 0f);
        if (hasL)
        {
            _anim.SetIKPosition(AvatarIKGoal.LeftHand, _leftHand.position);
            _anim.SetIKRotation(AvatarIKGoal.LeftHand, _leftHand.rotation);
        }

        bool hasRF = _rightFoot != null && _footW > 0f;
        bool hasLF = _leftFoot  != null && _footW > 0f;

        _anim.SetIKPositionWeight(AvatarIKGoal.RightFoot, hasRF ? _footW        : 0f);
        _anim.SetIKRotationWeight(AvatarIKGoal.RightFoot, hasRF ? _footW * 0.7f : 0f);
        if (hasRF)
        {
            _anim.SetIKPosition(AvatarIKGoal.RightFoot, _rightFoot.position);
            _anim.SetIKRotation(AvatarIKGoal.RightFoot, _rightFoot.rotation);
        }

        _anim.SetIKPositionWeight(AvatarIKGoal.LeftFoot, hasLF ? _footW        : 0f);
        _anim.SetIKRotationWeight(AvatarIKGoal.LeftFoot, hasLF ? _footW * 0.7f : 0f);
        if (hasLF)
        {
            _anim.SetIKPosition(AvatarIKGoal.LeftFoot, _leftFoot.position);
            _anim.SetIKRotation(AvatarIKGoal.LeftFoot, _leftFoot.rotation);
        }
    }

    void LateUpdate()
    {
        if (_anim == null || _carryLean <= 0.01f || _handW <= 0f) return;
        Vector3 right = _anim.transform.right;
        if (_spine != null)
            _spine.rotation = Quaternion.AngleAxis(-_carryLean * 0.70f, right) * _spine.rotation;
        if (_chest != null)
            _chest.rotation = Quaternion.AngleAxis(-_carryLean * 0.30f, right) * _chest.rotation;
    }
}
