using UnityEngine;

/// Swings the player through a melee blow — a horizontal hook that alternates sides.
///
/// Two things alternate, and they are not the same thing:
///   • which arm drives the blow, and
///   • which way round the arc goes.
///
/// Bare-handed both flip together: left fist hooks from the left. With a weapon the driving arm
/// is always the right one — that is where MeleeCombat parents the model — and only the
/// direction alternates, giving a forehand and then a backhand off the same grip.
///
/// A bat is held in both hands: the off hand is pinned to the shaft by a two-bone IK solve that
/// runs every frame, swing or no swing, so he walks around holding it properly rather than
/// letting it dangle from one fist. A knife stays one-handed, which is how a knife is held.
///
/// Why this is not an actual ragdoll arm, which is the obvious thing to reach for: the ragdoll
/// bodies sit on the very bones the Animator writes every frame. Un-kinematic the arm and the
/// Animator teleports it back on the next animation update, destroying the physics state before
/// anything can read it. Real active ragdolls get around that with a second, physics-only
/// skeleton that the animated one is copied off — a much bigger build than a swing needs.
///
/// So the motion is integrated here instead: angular spring-dampers stepped in LateUpdate, so
/// they land on top of whatever the Animator just wrote rather than fighting it. Still physics,
/// just one dimension per axis instead of a rigid body — and it gives the three things a canned
/// clip would have to fake by hand: wind-up, overshoot past the target, and a follow-through
/// that settles rather than stopping dead.
///
/// A hook needs two axes, and the order matters. The arm hangs down at rest, so rotating it
/// about the vertical would only spin it around its own length and show nothing: it has to be
/// raised to roughly horizontal first (about the player's right axis), and only then swept
/// sideways (about the player's up axis). The lift is sprung as well, so the arm rises into the
/// hook and lowers out of it instead of snapping to guard.
///
/// Works the same bare-handed as armed: the weapon model is parented to the hand bone, so it
/// rides the arm for free.
[DisallowMultipleComponent]
public class MeleeSwingArm : MonoBehaviour
{
    /// How the current weapon is held, which decides what alternates and whether the off hand
    /// is pinned to the shaft.
    public enum GripKind { Fists, OneHanded, TwoHanded }

    [Header("Hands")]
    [Tooltip("Alternate sides on consecutive blows. Bare-handed this swaps the fist as well; " +
             "with a weapon it swaps forehand for backhand off the same grip.")]
    [SerializeField] bool _alternateHands = true;
    [Tooltip("Fallback off-hand grip point, in the weapon's own space, used when the weapon " +
             "has no definition asset. Normally overwritten per weapon by SetGrip. The models " +
             "put the grip at the origin with the shaft running down +Z.")]
    [SerializeField] Vector3 _offHandGrip = new Vector3(0f, 0f, 0.16f);
    [Tooltip("0 leaves the off hand on its animation, 1 pins it to the shaft.")]
    [Range(0f, 1f)]
    [SerializeField] float _offHandWeight = 1f;

    [Header("Hook arc (degrees, about the player's up axis)")]
    // Positive sweeps the raised arm out to his right, negative brings it across his body to
    // the left. So the blow cocks out wide, then comes round in front of him.
    [Tooltip("Where the arm is cocked to before the blow. This is the tell the thief reads.")]
    [SerializeField] float _windupAngle = 70f;
    [Tooltip("How far across his body the hook carries through.")]
    [SerializeField] float _strikeAngle = -55f;

    [Header("Ready stance (two-handed only)")]
    // A two-handed weapon cannot be carried hanging at the right hip: the off hand is on the
    // far side of the body and simply cannot reach the shaft there — measured at 0.68 m from
    // the left shoulder against about 0.50 m of arm, so the IK fell 18 cm short and the hand
    // ended up parked at the chest. Bringing the weapon up and across puts the shaft inside
    // that reach, which is what makes the grip close at all.
    [Tooltip("How far the weapon hand comes up off the hip while holding a two-handed weapon.")]
    [SerializeField] float _readyLift = -45f;
    [Tooltip("How far across the body it is carried. Negative brings it toward his left, where " +
             "the off hand can actually reach it.")]
    [SerializeField] float _readySwing = -28f;
    [Tooltip("How briskly he comes to the ready position and lowers out of it.")]
    [SerializeField] float _readyStiffness = 90f;
    [SerializeField] float _readyDamping   = 16f;

    [Header("Guard lift (degrees, about the player's right axis)")]
    // Negative raises the down-hanging arm forward: about -80 puts it near horizontal, which is
    // what turns a sideways sweep into a hook instead of a spin around the arm's own length.
    [Tooltip("How far the arm comes up before hooking. Near 0 and there is nothing to sweep.")]
    [SerializeField] float _liftAngle = -80f;
    [Tooltip("Lift is slower than the hook itself — the arm comes up, then whips round.")]
    [SerializeField] float _liftStiffness = 120f;
    [SerializeField] float _liftDamping   = 18f;

    [Header("Hook spring")]
    [Tooltip("How hard the arm is pulled toward the current target angle. Higher is snappier.")]
    [SerializeField] float _stiffness = 260f;
    [Tooltip("How much of the motion is bled off. Below roughly 2*sqrt(stiffness) the arm " +
             "overshoots and swings back — which is exactly the follow-through we want.")]
    [SerializeField] float _damping = 20f;

    [Header("Body")]
    // A hook is thrown from the floor up: the torso turns and the arm is carried by it. Without
    // this the shoulder is the only thing moving, which is what makes an arm-only swing read as
    // a puppet waving one limb.
    //
    // Only Spine/Chest/UpperChest are turned, never Hips — the legs hang off Hips, so twisting
    // it would drag the feet round with the punch while the walk cycle is still driving them.
    [Tooltip("Share of the hook angle the torso turns through. The arm is a descendant of the " +
             "chest, so it inherits this on top of its own swing — raise it and the whole blow " +
             "widens.")]
    [SerializeField] float _torsoFactor = 0.45f;
    [Tooltip("Higher than the arm's stiffness so the body arrives first and the arm follows, " +
             "which is the order a real hook happens in.")]
    [SerializeField] float _torsoStiffness = 320f;
    [SerializeField] float _torsoDamping   = 24f;
    [Tooltip("Forward lean at full follow-through, degrees. Weight going into the blow.")]
    [SerializeField] float _leanAngle = 13f;
    [Tooltip("Slight lean away while he is cocked — the load-up before the weight shifts.")]
    [SerializeField] float _windupLean = 6f;
    [Tooltip("Share of the arm swing the collarbone adds, so the shoulder itself drives in " +
             "rather than the arm rotating in a fixed socket.")]
    [SerializeField] float _shoulderFactor = 0.3f;

    [Header("Elbow")]
    [Tooltip("Share of the hook angle the forearm adds on top. It chases the shoulder through " +
             "its own spring, so it arrives late and whips — the elbow is never driven directly.")]
    [SerializeField] float _elbowFactor = 0.5f;
    [Tooltip("Constant inward tuck once the arm is up. A hook is thrown with a bent elbow; " +
             "without this it reads as a straight-armed slap.")]
    [SerializeField] float _elbowBend = -35f;
    [SerializeField] float _elbowStiffness = 150f;
    [SerializeField] float _elbowDamping   = 12f;

    // ── Runtime ───────────────────────────────────────────────────────────────
    Animator  _animator;
    Transform _spine, _chest, _upperChest;
    bool      _resolved;

    // Both chains are resolved up front; Begin points the working refs at one of them.
    Transform _rUpper, _rLower, _rShoulder;
    Transform _lUpper, _lLower, _lShoulder;

    Transform _upperArm, _lowerArm, _shoulder;   // the side currently swinging

    // +1 forehand, -1 backhand. Every rotation about the up axis — hook, torso twist, elbow
    // tuck, collarbone — mirrors with it. The lift does not: it turns about the player's right
    // axis and raises either arm forward, so mirroring it would drive an arm into the floor.
    //
    // Kept apart from _onLeft on purpose: a two-handed backhand mirrors the arc while the right
    // arm keeps driving, so the two cannot be one flag.
    float _sideSign = 1f;
    bool  _onLeft;

    Transform _lHand;
    Transform _weapon;
    GripKind  _grip = GripKind.Fists;

    float _swing, _swingVel;     // hook, about the up axis
    float _lift,  _liftVel;      // guard raise, about the right axis
    float _elbow, _elbowVel;     // forearm, chases the hook
    float _twist, _twistVel;     // torso, about the up axis

    float _ready, _readyVel;     // 0..1 blend into the two-handed carry pose
    float _blow;                 // 0..1, how much of a blow is actually in flight

    float _engaged;              // 0..1, how far the arm has come up
    float _through;              // 0 at cocked, 1 at full follow-through

    float _swingTarget, _liftTarget;
    float _phaseTime = -1f;      // < 0 while no blow is in flight
    float _windupEnd, _strikeEnd, _recoverEnd;

    /// True while there is still visible motion — either a blow in flight or an arm that has
    /// not finished settling back onto the animated pose.
    public bool Active => _phaseTime >= 0f
                       || (_grip == GripKind.TwoHanded && !AnimatorHoldsCarryPose) || _ready > 0.01f
                       || Mathf.Abs(_swing) > RestAngle || Mathf.Abs(_swingVel) > RestVel
                       || Mathf.Abs(_lift)  > RestAngle || Mathf.Abs(_liftVel)  > RestVel
                       || Mathf.Abs(_twist) > RestAngle || Mathf.Abs(_twistVel) > RestVel;

    /// Below these the arm is close enough to the animated pose that dropping out is invisible.
    /// Without them the springs would be applied forever at ever-smaller amplitudes.
    const float RestAngle = 0.15f;
    const float RestVel   = 0.5f;

    /// The springs are stiff, so a long frame integrated in one step can diverge. Stepping at a
    /// bounded dt keeps a hitch from launching the arm.
    const float MaxStep = 1f / 120f;

    void Awake()
    {
        _animator      = FindActiveHumanoidAnimator();
        _remoteRagdoll = GetComponent<RemoteRagdollVisual>();
    }

    /// Switches the procedural swing to the same active body MeleeCombat uses. Disabled body
    /// variants remain in the hierarchy and their bone transforms do not become null, so all
    /// cached references must be cleared explicitly when the visible rig changes.
    public void RebindAnimator(Animator animator)
    {
        if (_animator == animator && (_animator == null || _animator.isActiveAndEnabled)) return;

        ResetSprings();
        _animator = animator;
        _resolved = false;

        _spine = _chest = _upperChest = null;
        _rUpper = _rLower = _rShoulder = null;
        _lUpper = _lLower = _lShoulder = null;
        _upperArm = _lowerArm = _shoulder = null;
        _lHand = null;
    }

    Animator FindActiveHumanoidAnimator()
    {
        foreach (var candidate in GetComponentsInChildren<Animator>(false))
            if (candidate != null && candidate.isActiveAndEnabled && candidate.isHuman)
                return candidate;

        return null;
    }

    // Whose ragdoll state gates this arm.
    //
    // On the local player's rig it is the RagdollController singleton. On a remote player's
    // puppet that singleton describes somebody else entirely -- the local player -- so reading
    // it there would freeze a teammate's swing every time YOU went down, and let theirs keep
    // swinging inside their own heap. The puppet carries its own RemoteRagdollVisual on the
    // same object, and that is the authority for that body.
    RemoteRagdollVisual _remoteRagdoll;

    bool BodyIsRagdolled() =>
        _remoteRagdoll != null
            ? _remoteRagdoll.IsRagdoll
            : RagdollController.Instance != null && RagdollController.Instance.IsRagdoll;

    /// Start a blow. The three durations are the phases MeleeCombat is already timing its hit
    /// resolution against, so the arm and the hit test cannot drift apart.
    ///
    public void Begin(float windup, float strike, float recover)
    {
        if (!ResolveBones()) return;

        // Which arm drives, and which way the arc goes. Bare-handed the two move together — the
        // left fist hooks from the left. Holding anything, the right arm keeps the grip and only
        // the arc flips, so a bat reads forehand, backhand, forehand off one pair of hands.
        bool left;
        float sign;

        if (!_alternateHands)
        {
            left = false;                       // the toggle promises "always the right"
            sign = 1f;
        }
        else if (_grip == GripKind.Fists)
        {
            left = !_onLeft;
            sign = left ? -1f : 1f;
        }
        else
        {
            left = false;
            sign = -_sideSign;                  // same hands, other way round
        }

        // The springs are held in one canonical space and mirrored on the way out, so a residual
        // angle carried across a flip would invert and pop. Zeroing also means each blow starts
        // from rest, which it has anyway — the cooldown outlasts the settle. The arm that stops
        // being written falls back to the animated pose on its own.
        ResetSprings();
        UseSide(left);
        _sideSign = sign;

        _phaseTime  = 0f;
        _windupEnd  = Mathf.Max(0.01f, windup);
        _strikeEnd  = _windupEnd + Mathf.Max(0.01f, strike);
        _recoverEnd = _strikeEnd + Mathf.Max(0.01f, recover);
    }

    /// Drop the blow and let the arm fall back to the animated pose. Called when something else
    /// takes the body over mid-swing.
    public void Cancel() => _phaseTime = -1f;

    /// Set by MeleeCombat while the weapon hangs on its carry socket and PlayerHandIK holds both
    /// wrists on it. Two solvers on one wrist is one too many: the Animator's IK pass and this
    /// component's own two-bone solve would each place the off hand, disagree by a few degrees and
    /// leave it shivering. Whoever owns the weapon owns the hands with it.
    public bool SuppressOffHandGrip;

    /// Set by PlayerWeaponLayer while the masked WeaponArms layer is carrying the weapon pose.
    ///
    /// The ready stance below and that layer describe the same thing — arms up, weapon across the
    /// body — so with both on it is applied twice and the weapon ends up round his ear. The layer
    /// wins when it is up, because it comes from a clip and can be replaced with a proper carry
    /// animation later; this stance stays as the fallback for a project with no layer built.
    public bool AnimatorHoldsCarryPose;

    /// Told by MeleeCombat whenever what he is holding changes. weapon may be null.
    ///
    /// offHandGrip comes from the weapon's definition asset, so a sledgehammer can be held
    /// further down the haft than a bat. Passing null keeps the component's own default, which
    /// is what a weapon with no asset authored for it gets.
    public void SetGrip(GripKind grip, Transform weapon, Vector3? offHandGrip = null)
    {
        _grip   = grip;
        _weapon = weapon;

        if (offHandGrip.HasValue) _offHandGrip = offHandGrip.Value;

        // Picking something up mid-flurry must not leave him swinging the empty hand.
        if (grip != GripKind.Fists && _onLeft)
        {
            ResetSprings();
            UseSide(left: false);
            _sideSign = 1f;
        }
    }

    bool ResolveBones()
    {
        if (_animator == null || !_animator.isActiveAndEnabled || !_animator.isHuman)
            RebindAnimator(FindActiveHumanoidAnimator());

        if (_resolved) return _rUpper != null;
        _resolved = true;

        if (_animator == null || !_animator.isHuman) return false;

        _rUpper    = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        _rLower    = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        _rShoulder = _animator.GetBoneTransform(HumanBodyBones.RightShoulder);

        _lUpper    = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        _lLower    = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        _lShoulder = _animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
        _lHand     = _animator.GetBoneTransform(HumanBodyBones.LeftHand);

        UseSide(left: false);

        // UpperChest is optional in the humanoid spec; a rig without it just spreads the same
        // twist across the two bones it does have.
        _spine      = _animator.GetBoneTransform(HumanBodyBones.Spine);
        _chest      = _animator.GetBoneTransform(HumanBodyBones.Chest);
        _upperChest = _animator.GetBoneTransform(HumanBodyBones.UpperChest);

        return _rUpper != null;
    }

    /// Points the working refs at one arm: once on resolve to establish a default, and from
    /// Begin on each hand change. Never mid-blow, so the side cannot flip under a swing that is
    /// still in flight.
    void UseSide(bool left)
    {
        // A rig missing the left arm falls back to the right rather than swinging nothing.
        if (left && _lUpper == null) left = false;

        _onLeft = left;

        _upperArm = left ? _lUpper    : _rUpper;
        _lowerArm = left ? _lLower    : _rLower;
        _shoulder = left ? _lShoulder : _rShoulder;
    }

    // LateUpdate, not Update: the Animator has written the bones by now, so what this applies
    // survives to the render. In Update it would be overwritten and nothing would show.
    void LateUpdate()
    {
        // A ragdolled player has handed his bones to the physics engine — writing them here
        // would fight it and leave the arm jittering inside the heap.
        if (BodyIsRagdolled())
        {
            if (Active) ResetSprings();
            return;
        }

        if (!ResolveBones()) return;

        if (Active)
        {
            Step(Time.deltaTime);
            Apply();
        }

        // Last, and unconditionally: the off hand has to be placed after the swing has moved
        // the weapon, and it holds the shaft while he is just walking about too.
        ApplyOffHandGrip();
    }

    // ── Two-handed grip ─────────────────────────────────────────────────

    void ApplyOffHandGrip()
    {
        if (SuppressOffHandGrip) return;
        if (_grip != GripKind.TwoHanded || _offHandWeight <= 0f) return;
        if (_weapon == null || _lUpper == null || _lLower == null || _lHand == null) return;

        Vector3 target = _weapon.TransformPoint(_offHandGrip);

        if (_offHandWeight < 1f)
            target = Vector3.Lerp(_lHand.position, target, _offHandWeight);

        // Elbow hint out to his left and a little back, so the arm bends outward like an elbow
        // rather than folding through the chest.
        Vector3 hint = _lUpper.position - transform.right * 0.5f - transform.forward * 0.2f;

        SolveTwoBone(_lUpper, _lLower, _lHand, target, hint);
    }

    /// Analytic two-bone IK. Rotates upper and lower so end lands on target, with hint choosing
    /// which way the joint bends. Used instead of the Animator's IK pass because that runs
    /// during the animation update, before this component has moved the weapon — the off hand
    /// would be solved against last frame's grip and lag a frame behind the swing.
    static void SolveTwoBone(Transform upper, Transform lower, Transform end,
                             Vector3 target, Vector3 hint)
    {
        Vector3 a = upper.position;
        Vector3 b = lower.position;
        Vector3 c = end.position;

        float lab = Vector3.Distance(a, b);
        float lcb = Vector3.Distance(c, b);
        if (lab < 1e-4f || lcb < 1e-4f) return;

        // Clamped just inside full extension: at exactly lab+lcb the triangle is degenerate and
        // the arccosines below go to NaN.
        float lat = Mathf.Clamp(Vector3.Distance(a, target), 1e-3f, lab + lcb - 1e-3f);

        Vector3 ab = (b - a).normalized;
        Vector3 ac = (c - a).normalized;
        Vector3 bc = (c - b).normalized;
        Vector3 at = (target - a).normalized;

        float acAb0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac, ab), -1f, 1f));
        float baBc0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(-ab, bc), -1f, 1f));
        float acAt0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac, at), -1f, 1f));

        float acAb1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
        float baBc1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

        Vector3 bendAxis = Vector3.Cross(c - a, hint - a);
        if (bendAxis.sqrMagnitude < 1e-8f) bendAxis = Vector3.Cross(c - a, Vector3.up);
        if (bendAxis.sqrMagnitude < 1e-8f) return;
        bendAxis.Normalize();

        Vector3 reachAxis = Vector3.Cross(c - a, target - a);
        if (reachAxis.sqrMagnitude < 1e-8f) reachAxis = bendAxis;
        else reachAxis.Normalize();

        var bend  = Quaternion.AngleAxis((acAb1 - acAb0) * Mathf.Rad2Deg,
                                         Quaternion.Inverse(upper.rotation) * bendAxis);
        var reach = Quaternion.AngleAxis(acAt0 * Mathf.Rad2Deg,
                                         Quaternion.Inverse(upper.rotation) * reachAxis);
        var fold  = Quaternion.AngleAxis((baBc1 - baBc0) * Mathf.Rad2Deg,
                                         Quaternion.Inverse(lower.rotation) * bendAxis);

        upper.localRotation = upper.localRotation * bend * reach;
        lower.localRotation = lower.localRotation * fold;
    }

    void Step(float dt)
    {
        AdvancePhase(dt);

        // Substep so a dropped frame cannot integrate the stiff springs into orbit.
        int steps = Mathf.Clamp(Mathf.CeilToInt(dt / MaxStep), 1, 8);
        float h   = dt / steps;

        // How much of a blow is actually happening. The lean and the follow-through read off
        // this rather than off the swing angle, which never returns to zero while a ready stance
        // is being held and would otherwise leave him permanently leaning into nothing.
        _blow = Mathf.MoveTowards(_blow, _phaseTime >= 0f ? 1f : 0f, dt * 4f);

        float readyTarget = _grip == GripKind.TwoHanded && !AnimatorHoldsCarryPose ? 1f : 0f;

        for (int i = 0; i < steps; i++)
        {
            _readyVel += (_readyStiffness * (readyTarget - _ready) - _readyDamping * _readyVel) * h;
            _ready    += _readyVel * h;

            _liftVel  += (_liftStiffness * (_liftTarget - _lift) - _liftDamping * _liftVel) * h;
            _lift     += _liftVel * h;

            _swingVel += (_stiffness * (_swingTarget - _swing) - _damping * _swingVel) * h;
            _swing    += _swingVel * h;

            // Same target, scaled down and on a stiffer spring, so the torso leads and the arm
            // trails it rather than the two moving as one rigid piece.
            _twistVel += (_torsoStiffness * (_swingTarget * _torsoFactor - _twist)
                          - _torsoDamping * _twistVel) * h;
            _twist    += _twistVel * h;

            // How far up the arm has actually come, 0..1. The elbow tucks in as it rises, so a
            // half-raised arm is only half-bent and nothing pops on the first frame.
            // Bent either because he has raised the arm to swing or because he is holding a
            // weapon at the ready — whichever is further along.
            float engaged = Mathf.Max(
                Mathf.Approximately(_liftAngle, 0f) ? 0f : Mathf.Clamp01(_lift / _liftAngle),
                Mathf.Clamp01(_ready));

            // The forearm chases the shoulder rather than being driven, so it lags behind and
            // arrives after it — the whip at the end of the hook comes out of that lag.
            float elbowTarget = _swing * _elbowFactor + _elbowBend * engaged;
            _elbowVel += (_elbowStiffness * (elbowTarget - _elbow) - _elbowDamping * _elbowVel) * h;
            _elbow    += _elbowVel * h;

            _engaged = engaged;
        }

        // 0 while cocked, 1 once the hook has carried all the way through. Drives the weight
        // shift, so the lean follows the blow instead of needing a timer of its own.
        _through = Mathf.InverseLerp(_windupAngle, _strikeAngle, _swing);
    }

    void AdvancePhase(float dt)
    {
        if (_phaseTime < 0f) { _swingTarget = 0f; _liftTarget = 0f; return; }

        _phaseTime += dt;

        if (_phaseTime < _windupEnd)
        {
            _liftTarget  = _liftAngle;
            _swingTarget = _windupAngle;
        }
        else if (_phaseTime < _strikeEnd)
        {
            _liftTarget  = _liftAngle;
            _swingTarget = _strikeAngle;
        }
        else if (_phaseTime < _recoverEnd)
        {
            // The arm drops out of guard as the hook unwinds, so he lowers it rather than
            // holding it up in front of him.
            _liftTarget  = 0f;
            _swingTarget = 0f;
        }
        else
        {
            // Phases are over. The springs keep running until Active goes false, which is what
            // blends the arm back onto the animated pose instead of snapping it.
            _liftTarget  = 0f;
            _swingTarget = 0f;
            _phaseTime   = -1f;
        }
    }

    void Apply()
    {
        // Order is the whole trick: lift first, about his right, to get the arm off his hip and
        // out to roughly horizontal. Only then sweep it about his up axis — that sweep is the
        // hook. Reverse the two and the arm spins around its own length on the way up.
        //
        // Both axes are taken from the player, not from world space, so the arc follows him
        // round rather than always hooking toward the same compass bearing.
        ApplyTorso();

        // The collarbone drives in ahead of the arm, so the shoulder itself travels rather than
        // the arm pivoting in a socket that never moves.
        if (_shoulder != null)
            _shoulder.rotation = Quaternion.AngleAxis(_swing * _shoulderFactor * _sideSign,
                                                     transform.up)
                               * _shoulder.rotation;

        // The ready stance is applied innermost, so the swing arcs out of the carry position
        // rather than out of the hip. It is deliberately NOT multiplied by _sideSign: a backhand
        // mirrors the blow, not where the weapon is being held, and mirroring the stance would
        // send it back across to the right hip exactly when the off hand needs it on the left.
        Quaternion readyLift  = Quaternion.AngleAxis(_readyLift  * _ready, transform.right);
        Quaternion readySwing = Quaternion.AngleAxis(_readySwing * _ready, transform.up);

        // Lift is not mirrored either — it raises either arm forward. Only the hook flips.
        Quaternion lift = Quaternion.AngleAxis(_lift, transform.right);
        Quaternion hook = Quaternion.AngleAxis(_swing * _sideSign, transform.up);

        _upperArm.rotation = hook * lift * readySwing * readyLift * _upperArm.rotation;

        // The forearm inherits both from its parent; this is only the extra bend and whip on
        // top, so it needs the up axis alone.
        if (_lowerArm != null)
            _lowerArm.rotation = Quaternion.AngleAxis(_elbow * _sideSign, transform.up)
                               * _lowerArm.rotation;
    }

    /// Spreads the twist and the lean up the spine. Splitting it rather than turning one bone
    /// keeps the silhouette curved instead of hinging the whole upper body at the waist — and
    /// because the bones are parented, the shares accumulate to the full angle by the chest.
    void ApplyTorso()
    {
        if (_spine == null && _chest == null && _upperChest == null) return;

        // Whatever bones this rig actually has, share out to the same total.
        int present = (_spine != null ? 1 : 0) + (_chest != null ? 1 : 0)
                    + (_upperChest != null ? 1 : 0);
        if (present == 0) return;

        // Mirrored with the arm: a left hook turns the shoulders the other way.
        float share = _twist * _sideSign / present;
        Vector3 up  = transform.up;

        // The lean rides on the lowest bone only: leaning at the waist is what a body does,
        // and repeating it up the chain would fold him double. Not mirrored — he leans into
        // the blow whichever hand throws it.
        float lean = Mathf.Lerp(-_windupLean, _leanAngle, _through) * _engaged * _blow;

        if (_spine != null)
            _spine.rotation = Quaternion.AngleAxis(share, up)
                            * Quaternion.AngleAxis(lean, transform.right)
                            * _spine.rotation;

        if (_chest != null)
            _chest.rotation = Quaternion.AngleAxis(share, up) * _chest.rotation;

        if (_upperChest != null)
            _upperChest.rotation = Quaternion.AngleAxis(share, up) * _upperChest.rotation;
    }

    void ResetSprings()
    {
        _phaseTime = -1f;
        _swing = _swingVel = 0f;
        _lift  = _liftVel  = 0f;
        _elbow = _elbowVel = 0f;
        _twist = _twistVel = 0f;
        _engaged = _through = 0f;

        // _ready is deliberately left alone: it tracks what he is holding, not what he is
        // doing, so a stagger or a side change must not drop the weapon to his hip.
        _blow = 0f;
    }
}
