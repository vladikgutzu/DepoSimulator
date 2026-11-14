using UnityEngine;

// Drives UpperBody animation layer weight based on IsCarryingBox + movement speed.
// When carrying and STILL: BoxIdle pose plays on upper body (weight → 1).
// When carrying and MOVING: weight → 0 so locomotion (walk/run) plays fully.
// Hand IK (PlayerHandIK) positions wrists at the box anchors regardless of layer weight,
// so the hands always grip the box even during run animation.
public class PlayerCarryLayer : MonoBehaviour
{
    [SerializeField] float _blendSpeed = 8f;

    Animator _anim;
    int      _upperBodyLayerIdx = -1;
    int      _isCarryingHash;
    int      _speedHash;
    PlayerHandIK _handIK;

    void Awake()
    {
        _anim           = GetComponentInChildren<Animator>();
        _handIK         = GetComponent<PlayerHandIK>();
        _isCarryingHash = Animator.StringToHash("IsCarryingBox");
        _speedHash      = Animator.StringToHash("Speed");
    }

    void Start()
    {
        if (_anim == null) return;
        _upperBodyLayerIdx = _anim.GetLayerIndex("UpperBody");
        if (_upperBodyLayerIdx >= 0)
            _anim.SetLayerWeight(_upperBodyLayerIdx, 0f);
    }

    void Update()
    {
        if (_anim == null || _upperBodyLayerIdx < 0) return;
        // Guard against animator controller swap (e.g. sitting controller on truck).
        if (_upperBodyLayerIdx >= _anim.layerCount) return;

        bool  isCarrying = _anim.GetBool(_isCarryingHash);
        float speed      = _anim.GetFloat(_speedHash);

        // BoxIdle is authored for ordinary boxes. A PackedOrderBox has its own fixed character-
        // space hand targets and upper-body lean; leaving BoxIdle at full weight makes its arm
        // pose fight those targets at the shoulders and elbows. Keep locomotion underneath and
        // let PlayerHandIK own the packed-order upper body instead.
        bool packedCarry = _handIK != null && _handIK.IsPackedCarryActive;
        float target = isCarrying && !packedCarry ? 1f : 0f;

        float current = _anim.GetLayerWeight(_upperBodyLayerIdx);
        if (!Mathf.Approximately(current, target))
            _anim.SetLayerWeight(_upperBodyLayerIdx,
                Mathf.MoveTowards(current, target, Time.deltaTime * _blendSpeed));
    }
}
