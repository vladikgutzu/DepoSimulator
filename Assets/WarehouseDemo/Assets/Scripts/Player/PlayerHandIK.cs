using UnityEngine;

// Attach to the Player root alongside ThirdPersonController.
// Drives hand and foot IK so limbs conform to held objects / vehicle controls.
// Requires IK Pass = true on the Animator layer.
[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(1000)]
public class PlayerHandIK : MonoBehaviour
{
    public static PlayerHandIK Instance { get; private set; }

    [Range(0f, 5f)] public float BlendSpeed = 4f;

    [Header("Packed Order Carry Pose")]
    [Tooltip("Editable targets under the character Animator. Use the component context menu to create them.")]
    [SerializeField] Transform _packedRightTarget;
    [SerializeField] Transform _packedLeftTarget;
    [Tooltip("Where the underside of PackedOrderBox rests. Move it toward the torso to tighten the carry pose.")]
    [SerializeField] Transform _packedBoxSupport;

    static readonly Vector3 DefaultPackedRight  = new( 0.5374f, 0.791f, 0.1545f);
    static readonly Vector3 DefaultPackedLeft   = new(-0.4629f, 0.856f, 0.112f);
    static readonly Vector3 DefaultPackedSupport = new(0f, 0.98f, 0.08f);
    static readonly Quaternion DefaultPackedRightRotation =
        Quaternion.Euler(60.455f, -40.087f, -285.773f);
    static readonly Quaternion DefaultPackedLeftRotation =
        Quaternion.Euler(-206.82f, 110.301f, -15.876f);

    public static Vector3 PackedRightLocalPosition =>
        Instance != null && Instance._packedRightTarget != null
            ? Instance._packedRightTarget.localPosition : DefaultPackedRight;
    public static Vector3 PackedLeftLocalPosition =>
        Instance != null && Instance._packedLeftTarget != null
            ? Instance._packedLeftTarget.localPosition : DefaultPackedLeft;
    public static Vector3 PackedBoxSupportLocalPosition =>
        Instance != null && Instance._packedBoxSupport != null
            ? Instance._packedBoxSupport.localPosition : DefaultPackedSupport;
    public static Quaternion PackedRightLocalRotation =>
        Instance != null && Instance._packedRightTarget != null
            ? Instance._packedRightTarget.localRotation : DefaultPackedRightRotation;
    public static Quaternion PackedLeftLocalRotation =>
        Instance != null && Instance._packedLeftTarget != null
            ? Instance._packedLeftTarget.localRotation : DefaultPackedLeftRotation;

    Animator   _anim;
    Transform  _spine;
    Transform  _chest;
    Transform  _rightUpperArm;
    Transform  _rightLowerArm;
    Transform  _rightHand;
    Transform  _leftUpperArm;
    Transform  _leftLowerArm;
    Transform  _leftHand;

    // ── Hands ─────────────────────────────────────────────────────────────────
    Transform _rightTarget;
    Transform _leftTarget;
    float     _weight;
    float     _targetWeight;
    float     _carryLean;
    float     _targetCarryLean;
    bool      _packedCarryActive;
    bool      _releaseRightValid;
    bool      _releaseLeftValid;
    Vector3   _releaseRightPosition;
    Vector3   _releaseLeftPosition;
    Quaternion _releaseRightRotation;
    Quaternion _releaseLeftRotation;

    // Called at the top of the IK pass, for targets that ride something this frame has already
    // moved. See SetTargets.
    System.Action _refresh;

    // ── Feet ──────────────────────────────────────────────────────────────────
    Transform _rightFootTarget;
    Transform _leftFootTarget;
    float     _footWeight;
    float     _footTargetWeight;

    void Awake()
    {
        // This component is serialized only on the local scene player. Do not silently destroy it
        // because a stale static instance survived a reload or because networking wrapped the
        // local rig: every caller uses the singleton conditionally, so that used to disable all
        // packed-order IK without an error and no pose points were ever created.
        Instance = this;
        ResolveRig();

        // Keep the packed-order pose points tangible and editable in Play Mode instead of creating
        // them through a nullable singleton only on the first pickup. They live directly under the
        // Animator (the local Player root in this rig), so they are easy to find in the Hierarchy.
        EnsurePackedCarryTargets();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ── Hand API ──────────────────────────────────────────────────────────────

    /// refresh, when given, is invoked at the start of the IK pass, before the goals are read.
    ///
    /// It exists because of where the IK pass sits in the frame: the animation update runs after
    /// every Update and BEFORE every LateUpdate. A target parented to something positioned in
    /// LateUpdate — a carried box, which is glued to the player there — is therefore still at
    /// LAST frame's pose when the solver reads it, while the body has already moved this frame.
    /// The hands get solved to where the box used to be, so they trail the box by one frame's
    /// worth of travel and the gap grows with speed: at a walk that reads as hands shivering
    /// around the box, and at a run as hands visibly chasing it.
    ///
    /// Handing the owner a chance to put its target where it belongs, right here, costs one
    /// delegate call and removes the lag outright — as opposed to setting a script execution
    /// order, which would only work as long as nobody reorders anything again.
    public void SetTargets(Transform right, Transform left, float weight = 1f,
                           System.Action refresh = null)
    {
        _packedCarryActive = false;
        _rightTarget  = right;
        _leftTarget   = left;
        _targetWeight = weight;
        _refresh      = refresh;
        _releaseRightValid = false;
        _releaseLeftValid  = false;
    }

    public void SetPackedCarryTargets(float weight = 1f, System.Action refresh = null)
    {
        EnsurePackedCarryTargets();
        SetTargets(_packedRightTarget, _packedLeftTarget, weight, refresh);
        _packedCarryActive = true;
    }

    /// Uses grip points supplied by the packed box itself. Generated order cartons can differ
    /// greatly in width and height, so the character-space fallback points cannot put both palms
    /// on the real lower edge of every carton.
    public void SetPackedCarryTargets(Transform right, Transform left, float weight = 1f,
                                      System.Action refresh = null)
    {
        SetTargets(right, left, weight, refresh);
        _packedCarryActive = true;
    }

    /// True only while the finished-order box uses the fixed character-space grip points.
    /// PlayerCarryLayer uses this to remove the ordinary BoxIdle arm pose, which otherwise
    /// fights the packed-order IK at the shoulders and elbows.
    public bool IsPackedCarryActive => _packedCarryActive || IsPackedOrderGrabbed();

    void EnsurePackedCarryTargets()
    {
        if (_packedRightTarget != null && _packedLeftTarget != null && _packedBoxSupport != null) return;
        if (_anim == null) _anim = GetComponentInChildren<Animator>();
        Transform parent = _anim != null ? _anim.transform : transform;
        // Unity can leave a destroyed serialized UnityEngine.Object reference here. Such a
        // reference compares equal to null through Unity's overloaded operator, but it is not a
        // CLR null, so ??= will not replace it. Use Unity null checks explicitly to reconnect the
        // permanent pose points after entering Play Mode or reloading scripts.
        if (_packedRightTarget == null)
            _packedRightTarget = CreatePosePoint(parent, "PackedCarryHand_R",
                                                 DefaultPackedRight, DefaultPackedRightRotation);
        if (_packedLeftTarget == null)
            _packedLeftTarget = CreatePosePoint(parent, "PackedCarryHand_L",
                                                DefaultPackedLeft, DefaultPackedLeftRotation);
        if (_packedBoxSupport == null)
            _packedBoxSupport = CreatePosePoint(parent, "PackedCarryBoxSupport",
                                                DefaultPackedSupport, Quaternion.identity);
    }

    [ContextMenu("Create Packed Carry Pose Points")]
    void CreatePackedCarryPosePoints() => EnsurePackedCarryTargets();

    static Transform CreatePosePoint(Transform parent, string pointName, Vector3 localPosition,
                                     Quaternion localRotation)
    {
        Transform existing = parent.Find(pointName);
        if (existing != null) return existing;

        Transform point = new GameObject(pointName).transform;
        point.SetParent(parent, false);
        point.localPosition = localPosition;
        point.localRotation = localRotation;
        return point;
    }

    public void SetCarryLean(float degrees, bool immediate = false)
    {
        _targetCarryLean = Mathf.Clamp(degrees, 0f, 70f);
        if (immediate) _carryLean = _targetCarryLean;
    }

    /// immediate drops the weight this frame instead of easing it out.
    ///
    /// Easing is right when a prop is put down — the hands drift off it. It is wrong when the arms
    /// are about to be thrown by an animation: for the quarter second the blend takes, the solver
    /// is still pulling the wrists onto a weapon that is now parented to the hand, which is a hand
    /// holding itself in place. The swing plays underneath and nothing of it is visible.
    public void ClearTargets(bool immediate = false)
    {
        // Targets normally belong to the released prop. Freezing their last world pose prevents
        // the fading IK from dragging the wrists after a falling box or a vehicle the player just
        // left. The solver can now blend from the exact visible release pose back to animation.
        if (!immediate && _weight > 0f)
        {
            _refresh?.Invoke();
            _releaseRightValid = _rightTarget != null;
            _releaseLeftValid  = _leftTarget  != null;
            if (_releaseRightValid)
            {
                _releaseRightPosition = _rightTarget.position;
                _releaseRightRotation = _rightTarget.rotation;
            }
            if (_releaseLeftValid)
            {
                _releaseLeftPosition = _leftTarget.position;
                _releaseLeftRotation = _leftTarget.rotation;
            }
        }

        _targetWeight = 0f;
        _targetCarryLean = 0f;
        _packedCarryActive = false;
        _refresh      = null;

        if (!immediate) return;

        _weight      = 0f;
        _rightTarget = null;
        _leftTarget  = null;
        _releaseRightValid = false;
        _releaseLeftValid  = false;
    }

    // ── Foot API ──────────────────────────────────────────────────────────────

    public void SetFootTargets(Transform right, Transform left, float weight = 1f)
    {
        _rightFootTarget  = right;
        _leftFootTarget   = left;
        _footTargetWeight = weight;
    }

    public void ClearFootTargets()
    {
        _footTargetWeight = 0f;
    }

    public void ClearAllTargets()
    {
        ClearTargets();
        ClearFootTargets();
    }

    // ── Update ────────────────────────────────────────────────────────────────

    void Update()
    {
        SyncPackedCarryState();
        _weight = Mathf.MoveTowards(_weight, _targetWeight, Time.deltaTime * BlendSpeed);
        _carryLean = Mathf.MoveTowards(_carryLean, _targetCarryLean,
                                       Time.deltaTime * BlendSpeed * 8f);
        if (_weight <= 0f && _targetWeight <= 0f)
        {
            _rightTarget = null;
            _leftTarget  = null;
            _releaseRightValid = false;
            _releaseLeftValid  = false;
        }

        _footWeight = Mathf.MoveTowards(_footWeight, _footTargetWeight, Time.deltaTime * BlendSpeed);
        if (_footWeight <= 0f && _footTargetWeight <= 0f)
        {
            _rightFootTarget = null;
            _leftFootTarget  = null;
        }
    }

    // ── IK ────────────────────────────────────────────────────────────────────

    void OnAnimatorIK(int layerIndex)
    {
        if (_anim == null) return;

        // Before anything is read: the goals may ride an object whose pose for this frame has not
        // been written yet (see SetTargets).
        if (_weight > 0f) _refresh?.Invoke();

        // Hands
        bool hasRight = (_rightTarget != null || _releaseRightValid) && _weight > 0f;
        bool hasLeft  = (_leftTarget  != null || _releaseLeftValid)  && _weight > 0f;

        _anim.SetIKPositionWeight(AvatarIKGoal.RightHand, hasRight ? _weight       : 0f);
        float handRotationWeight = _packedCarryActive ? 0.20f : 0.60f;
        _anim.SetIKRotationWeight(AvatarIKGoal.RightHand,
                                  hasRight ? _weight * handRotationWeight : 0f);
        if (hasRight)
        {
            _anim.SetIKPosition(AvatarIKGoal.RightHand,
                _releaseRightValid ? _releaseRightPosition : _rightTarget.position);
            _anim.SetIKRotation(AvatarIKGoal.RightHand,
                _releaseRightValid ? _releaseRightRotation : _rightTarget.rotation);
        }

        _anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, hasLeft ? _weight       : 0f);
        _anim.SetIKRotationWeight(AvatarIKGoal.LeftHand,
                                  hasLeft ? _weight * handRotationWeight : 0f);
        if (hasLeft)
        {
            _anim.SetIKPosition(AvatarIKGoal.LeftHand,
                _releaseLeftValid ? _releaseLeftPosition : _leftTarget.position);
            _anim.SetIKRotation(AvatarIKGoal.LeftHand,
                _releaseLeftValid ? _releaseLeftRotation : _leftTarget.rotation);
        }

        // Feet
        bool hasRightFoot = _rightFootTarget != null && _footWeight > 0f;
        bool hasLeftFoot  = _leftFootTarget  != null && _footWeight > 0f;

        _anim.SetIKPositionWeight(AvatarIKGoal.RightFoot, hasRightFoot ? _footWeight       : 0f);
        _anim.SetIKRotationWeight(AvatarIKGoal.RightFoot, hasRightFoot ? _footWeight * 0.7f : 0f);
        if (hasRightFoot)
        {
            _anim.SetIKPosition(AvatarIKGoal.RightFoot, _rightFootTarget.position);
            _anim.SetIKRotation(AvatarIKGoal.RightFoot, _rightFootTarget.rotation);
        }

        _anim.SetIKPositionWeight(AvatarIKGoal.LeftFoot, hasLeftFoot ? _footWeight       : 0f);
        _anim.SetIKRotationWeight(AvatarIKGoal.LeftFoot, hasLeftFoot ? _footWeight * 0.7f : 0f);
        if (hasLeftFoot)
        {
            _anim.SetIKPosition(AvatarIKGoal.LeftFoot, _leftFootTarget.position);
            _anim.SetIKRotation(AvatarIKGoal.LeftFoot, _leftFootTarget.rotation);
        }
    }

    void LateUpdate()
    {
        // Animator/controller swaps and play-mode reload settings can invalidate the references
        // cached in Awake. Resolve lazily while carrying instead of silently skipping the pose.
        if (IsPackedOrderGrabbed())
        {
            SyncPackedCarryState();
            ResolveRig();
        }

        if (_anim == null || _weight <= 0f) return;

        // This runs after every Animator layer and every IK pass. The robot rig's local bone axes
        // do not match Unity's X-forward/back convention, so rotate around the character's world
        // right axis. Spine carries 70% and Chest adds the remaining 30%, producing a clear bend
        // while the existing UpperBody mask leaves hips and legs entirely to locomotion.
        if (_carryLean > 0.01f)
        {
            Vector3 right = _anim.transform.right;
            if (_spine != null)
                _spine.rotation = Quaternion.AngleAxis(-_carryLean * 0.70f, right) * _spine.rotation;
            if (_chest != null)
                _chest.rotation = Quaternion.AngleAxis(-_carryLean * 0.30f, right) * _chest.rotation;
        }

        // Humanoid IK is evaluated inside Animator and can be overwritten by later masked layers.
        // Packed orders deliberately remove the ordinary BoxIdle layer, so solve each complete arm
        // once more after animation. Use the active targets, not the old fixed prefab targets:
        // generated cartons provide size-aware grips on their actual lower edge.
        if (!_packedCarryActive) return;

        SolveArm(_rightUpperArm, _rightLowerArm, _rightHand, _rightTarget, _weight, 0.20f);
        SolveArm(_leftUpperArm,  _leftLowerArm,  _leftHand,  _leftTarget,  _weight, 0.20f);
    }

    static void SolveArm(Transform upperArm, Transform lowerArm, Transform hand,
                         Transform target, float weight, float rotationWeight)
    {
        if (upperArm == null || lowerArm == null || hand == null || target == null) return;

        Vector3 shoulder = upperArm.position;
        Vector3 elbow    = lowerArm.position;
        Vector3 wrist    = hand.position;
        float upperLength = Vector3.Distance(shoulder, elbow);
        float lowerLength = Vector3.Distance(elbow, wrist);
        if (upperLength < 0.0001f || lowerLength < 0.0001f) return;

        // Blend the destination rather than the bone rotations so the chain remains geometrically
        // valid while pickup IK fades in.
        Vector3 destination = Vector3.Lerp(wrist, target.position, Mathf.Clamp01(weight));
        Vector3 shoulderToTarget = destination - shoulder;
        float rawDistance = shoulderToTarget.magnitude;
        if (rawDistance < 0.0001f) return;

        Vector3 direction = shoulderToTarget / rawDistance;
        float minReach = Mathf.Abs(upperLength - lowerLength) + 0.0001f;
        float maxReach = upperLength + lowerLength - 0.0001f;
        float distance = Mathf.Clamp(rawDistance, minReach, maxReach);

        // Preserve the animation's current elbow side as the pole direction. It prevents the arm
        // from flipping when the character turns while still allowing the shoulder to follow the
        // upper-body lean.
        Vector3 elbowFromShoulder = elbow - shoulder;
        Vector3 bendDirection = Vector3.ProjectOnPlane(elbowFromShoulder, direction);
        if (bendDirection.sqrMagnitude < 0.000001f)
            bendDirection = Vector3.ProjectOnPlane(upperArm.root.up, direction);
        if (bendDirection.sqrMagnitude < 0.000001f)
            bendDirection = Vector3.ProjectOnPlane(upperArm.root.right, direction);
        bendDirection.Normalize();

        float along = (upperLength * upperLength - lowerLength * lowerLength
                       + distance * distance) / (2f * distance);
        float height = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
        Vector3 desiredElbow = shoulder + direction * along + bendDirection * height;

        Vector3 currentUpper = lowerArm.position - upperArm.position;
        Vector3 desiredUpper = desiredElbow - upperArm.position;
        if (currentUpper.sqrMagnitude > 0.000001f && desiredUpper.sqrMagnitude > 0.000001f)
            upperArm.rotation = Quaternion.FromToRotation(currentUpper, desiredUpper)
                              * upperArm.rotation;

        // Rotating the upper arm moved both remaining joints; read their new positions before
        // aiming the forearm.
        Vector3 currentLower = hand.position - lowerArm.position;
        Vector3 desiredLower = destination - lowerArm.position;
        if (currentLower.sqrMagnitude > 0.000001f && desiredLower.sqrMagnitude > 0.000001f)
            lowerArm.rotation = Quaternion.FromToRotation(currentLower, desiredLower)
                              * lowerArm.rotation;

        hand.rotation = Quaternion.Slerp(hand.rotation, target.rotation,
                                         Mathf.Clamp01(weight) * rotationWeight);
    }

    static bool IsPackedOrderGrabbed()
    {
        BoxPhysicsInteractable grabbed = BoxPhysicsInteractable.GrabbedBox;
        return grabbed != null && grabbed.GetComponent<PackedOrderBox>() != null;
    }

    void SyncPackedCarryState()
    {
        bool holdingPackedOrder = IsPackedOrderGrabbed();
        if (holdingPackedOrder)
        {
            // Do not rely solely on the one pickup callback. Enter Play Mode options, a stale
            // singleton, or a controller swap can make that callback miss this component while
            // the box is nevertheless genuinely held. The global grabbed-box state is the source
            // of truth used by movement, shipping, and interaction as well. Ask that box to bind
            // its size-aware lower-edge targets; assigning the old character-space targets here
            // every frame used to silently undo the adaptive grip set during Grab().
            BoxPhysicsInteractable.GrabbedBox.BindPackedCarryIK(this);
            return;
        }

        if (!_packedCarryActive) return;

        _packedCarryActive = false;
        if (_rightTarget == _packedRightTarget) _rightTarget = null;
        if (_leftTarget  == _packedLeftTarget)  _leftTarget  = null;
        _targetWeight = 0f;
    }

    void ResolveRig()
    {
        Animator currentAnimator = GetComponentInChildren<Animator>();
        if (currentAnimator != _anim)
        {
            _anim = currentAnimator;
            _spine = null;
            _chest = null;
            _rightUpperArm = null;
            _rightLowerArm = null;
            _rightHand = null;
            _leftUpperArm = null;
            _leftLowerArm = null;
            _leftHand = null;
        }

        if (_anim == null || !_anim.isHuman) return;

        // Use Unity's overloaded null check rather than ??=: destroyed Transform wrappers are
        // non-null CLR references but compare equal to null and must be resolved again.
        if (_spine == null) _spine = _anim.GetBoneTransform(HumanBodyBones.Spine);
        if (_chest == null) _chest = _anim.GetBoneTransform(HumanBodyBones.Chest);
        if (_rightUpperArm == null)
            _rightUpperArm = _anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
        if (_rightLowerArm == null)
            _rightLowerArm = _anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
        if (_rightHand == null) _rightHand = _anim.GetBoneTransform(HumanBodyBones.RightHand);
        if (_leftUpperArm == null)
            _leftUpperArm = _anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        if (_leftLowerArm == null)
            _leftLowerArm = _anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        if (_leftHand == null) _leftHand = _anim.GetBoneTransform(HumanBodyBones.LeftHand);
    }

}
