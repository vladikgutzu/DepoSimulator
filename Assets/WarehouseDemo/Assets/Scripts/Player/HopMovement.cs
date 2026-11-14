using StarterAssets;
using UnityEngine;

/// Moving on one leg: one whole hop at a time, with a pause between them.
///
/// Holding a direction does not make him walk faster or further — it makes him hop again. Each hop
/// is atomic: the clip plays from start to finish, he covers a little ground partway through it,
/// he lands, he stands for a moment, and only then does the held key buy another one. Tapping the
/// key once buys exactly one hop, and there is no such thing as half a hop.
///
/// That is the whole design, and it is why this is a small state machine rather than a multiplier.
/// Two earlier attempts drove the speed continuously and both were wrong in the same way: Starter
/// Assets derives the animation from how fast the character is moving, so anything that modulates
/// speed also modulates the animation — cut the speed between hops and the clip stops too, halfway
/// through, which is exactly the twitching the second version produced. Here the clip's own length
/// drives the cycle and the movement hangs off it, which is the only order that holds together.
///
/// Jump is folded in on purpose. A man with one leg cannot spring, so pressing it does not start
/// the jump state machine at all — it asks for a hop, the same hop, and produces the one animation
/// rather than a takeoff, a fall and a landing played back to back over the same clip.
///
/// Runs before ThirdPersonController, which has no explicit order and so sits at zero. That
/// matters for one line: the jump press has to be taken out of the input *before* the controller
/// reads it, or he leaves the ground first and the hop is beside the point. LateUpdate is
/// unaffected by this — every LateUpdate runs after every Update — so the animator override at the
/// bottom still gets the last word.
[DefaultExecutionOrder(-50)]
[DisallowMultipleComponent]
public class HopMovement : MonoBehaviour
{
    enum Phase { Waiting, Hopping, Recovering }

    [Tooltip("Pause between hops, in seconds, after one has finished. Short: this is the beat " +
             "between hops, not a rest. Anything much longer and holding a direction feels like " +
             "the input is being ignored — which is what it was doing at 0.12, where the gap from " +
             "one landing to the next takeoff came to nearly two tenths of a second.")]
    [SerializeField] float _recovery = 0.05f;

    [Tooltip("When in the hop he is actually travelling, as a fraction of the clip. Starts early " +
             "on purpose — a press that does nothing for a third of a second reads as a dropped " +
             "input rather than as a wind-up.")]
    [SerializeField] [Range(0f, 1f)] float _travelFrom = 0.10f;
    [SerializeField] [Range(0f, 1f)] float _travelTo   = 0.47f;

    [Tooltip("The phase of the clip at which he is back on the floor, and so where the hop ends. " +
             "Measured off the clip rather than guessed: the hips dip to load at 0.09, peak at " +
             "0.31 and are down again by 0.47, after which nearly half the clip is a settle with " +
             "nothing happening. Running to the end of it is what made the landing feel long. " +
             "Trimmed to 0.51 rather than to 0.47 on purpose: cutting right on the landing shortens " +
             "the gap by a further four hundredths, but the lift arc has not quite run out there, " +
             "so the foot is 0.7 cm higher off the floor at the moment the hop hands back — the " +
             "cheapest tenth of a second in the cycle is not worth a bigger step at the join.")]
    [SerializeField] [Range(0.2f, 1f)] float _clipLanding = 0.51f;

    [Tooltip("Speed while travelling, as a multiple of the normal walk. Deceptively small-looking: " +
             "the walk in this scene is 4 m/s and he is only moving for part of each hop, so this " +
             "buys a little over a metre. At 3.5 it was clearing three, which is an athlete's " +
             "long jump on one leg.")]
    [SerializeField] float _burstSpeed = 1.13f;

    [Tooltip("How high he comes off the floor, in metres. Applied to the body rather than through " +
             "the controller: the animation has no root motion, so without this he slides through " +
             "a hop without ever leaving the ground.")]
    [SerializeField] float _hopLift = 0.18f;

    [Tooltip("Constant lift held for the whole hop, in metres, on top of the arc. It is not a feel " +
             "value but a correction: measured against the idle pose, the hop clip stands 0.031 m " +
             "lower, so its sole is under the floor from its very first frame — before the arc has " +
             "begun and again after it has finished.")]
    [SerializeField] float _hopClearance = 0.035f;

    [Tooltip("How far the surviving leg is swung in under the body, in degrees. The hip joint " +
             "stays exactly where the rig put it — the leg is angled inward from it, which is what " +
             "a person actually does to get a foot under their weight.")]
    [SerializeField] float _legCentringAngle = 9f;

    [Header("Balance")]
    [Tooltip("How far he sways while balancing on one leg, in degrees. A few is plenty — this is a " +
             "man who cannot quite hold still, not a drunk.")]
    [SerializeField] float _swayAngle = 3.0f;

    [Tooltip("How quickly the sway wanders. Low and unhurried: a body correcting itself, not a " +
             "vibration.")]
    [SerializeField] float _swaySpeed = 0.9f;

    [Tooltip("How much of the sway survives while he is mid-hop. Small: in the air he is committed " +
             "and the animation is doing the work, so the correcting is what happens between hops.")]
    [SerializeField] [Range(0f, 1f)] float _swayInAir = 0.25f;

    [Tooltip("Chance that a landing goes wrong and puts him on the floor. Per hop, so it compounds " +
             "over a long trip: at 6% a walk across the warehouse is about even odds of one fall. " +
             "Set to 0 to turn it off.")]
    [SerializeField] [Range(0f, 0.5f)] float _stumbleChance = 0.06f;

    [Tooltip("Hops that are always safe after getting up. Without it he can land, fall, get up and " +
             "fall again immediately, which is not a hazard, it is a trap.")]
    [SerializeField] int _stumbleGrace = 3;

    [Tooltip("What the animator is told the speed is during a hop. Any value the locomotion blend " +
             "reads as 'moving' will do — it only has to pick the hop clip out of the tree.")]
    [SerializeField] float _animatorSpeed = 2f;

    [Tooltip("Playback rate of the hop, and so the length of the whole cycle. Well above 1: the " +
             "clip is 1.9 s as authored, which on a body that hops in place reads as slow motion. " +
             "At this rate a hop takes about eight tenths of a second.")]
    [SerializeField] float _animatorMotionSpeed = 1.8f;

    [Tooltip("Seconds spent blending into the start of a hop. Enough to hide the cut from the " +
             "standing pose to the clip's first frame, short enough not to eat the hop.")]
    [SerializeField] float _restartBlend = 0.07f;

    static readonly int SpeedHash       = Animator.StringToHash("Speed");
    static readonly int MotionSpeedHash = Animator.StringToHash("MotionSpeed");

    Animator              _animator;
    InjuredLocomotion     _hobble;
    StarterAssetsInputs   _input;
    ThirdPersonController _tpc;
    RepairCrouch          _repair;
    Transform             _body;

    bool _wasDowned;

    Phase _phase = Phase.Waiting;
    float _timer;
    int   _safeHops;

    // The surviving thigh, and which injury it was resolved for.
    Transform _standingLeg;
    BodyPart  _centredFor = BodyPart.None;

    // The direction the hop was launched in, and the bookkeeping that lets it outlive the key.
    Vector2 _hopMove;   // latched at takeoff, fed to the controller until he lands
    Vector2 _realMove;  // what the player is actually holding, as opposed to what we feed
    Vector2 _fedMove;   // what we wrote last frame, so a fresh press can be told from our own echo
    bool    _feeding;

    /// What the player's speed should be multiplied by right now. 1 whenever he is not hobbling,
    /// so nothing else has to know this exists.
    public float SpeedMultiplier { get; private set; } = 1f;

    /// True while the hop cycle owns his movement.
    public bool Hopping => _phase == Phase.Hopping;

    void Awake() => Rebind();

    /// Gives the input back before going away. A hop that is interrupted by the component being
    /// switched off — a body swap, a death, the player leaving — would otherwise leave the fed
    /// direction key stuck down, and he would walk off on his own for the rest of the shift.
    void OnDisable() => StopFeeding();

    /// Re-finds the animator. The body can be swapped at runtime, which replaces it.
    public void Rebind()
    {
        _animator = GetComponentInChildren<Animator>();
        _phase    = Phase.Waiting;
        _timer    = 0f;

        // The visual body, which is what gets lifted. Found by looking for the node the animator
        // lives under rather than by name, so it follows a body swap.
        _body = null;
        if (_animator != null)
        {
            var hips = _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            for (var t = hips; t != null && t != transform; t = t.parent)
                if (t.parent == transform) { _body = t; break; }
        }

        if (_body != null)
        {
            _body.localPosition = Vector3.zero;
            _body.localRotation = Quaternion.identity;
        }
    }

    void Update()
    {
        if (_hobble == null) _hobble = GetComponent<InjuredLocomotion>();
        if (_input  == null) _input  = GetComponent<StarterAssetsInputs>();

        TrackRealInput();

        if (_hobble == null || !_hobble.Hobbling)
        {
            if (_phase != Phase.Waiting) { _phase = Phase.Waiting; _timer = 0f; }

            StopFeeding();
            SpeedMultiplier = 1f;

            // Put the body straight. LateUpdate stops touching it the moment the leg is repaired,
            // so without this he would keep whatever lean and lift the last frame of the hobble
            // happened to leave him with — standing crooked for the rest of the shift.
            StraightenBody();

            return;
        }

        // Off his feet. Everything below this line assumes a man standing on one leg deciding
        // where to go, and none of it is true of a man on the floor.
        //
        // Left running, it was the reason a fall came in threes: the hop cycle carried on
        // underneath the trip, each imaginary landing spent one of the safe hops that exist
        // precisely to stop a fall repeating, and the crossfade at the start of each one
        // overwrote the standing-up animation with the hop. He would get up with the grace
        // period already gone and go straight back down.
        if (Downed())
        {
            // A press held through the fall must not be cashed in the instant he is upright:
            // nothing else consumes it while the controller is asleep, so it would still be
            // sitting there when the controller wakes.
            if (_input != null) _input.jump = false;

            if (_phase != Phase.Waiting) { _phase = Phase.Waiting; _timer = 0f; }

            SpeedMultiplier = 0f;
            StopFeeding();
            StraightenBody();

            _wasDowned = true;
            return;
        }

        // Back on his foot. However he went down — a bad landing, a wet floor, a box to the
        // head — the first hops afterwards are free ones, so getting up is not immediately
        // followed by going back down.
        if (_wasDowned)
        {
            _wasDowned = false;
            _safeHops  = Mathf.Max(_safeHops, _stumbleGrace);
        }

        // A missing leg cannot jump. The press is taken — cleared before ThirdPersonController sees
        // it, so no takeoff happens — and thrown away: Space does nothing at all. It used to buy a
        // hop, which on screen still read as jumping on a severed leg.
        if (_input != null) _input.jump = false;

        // The player's own input, never the vector we are feeding back — otherwise the first hop
        // would look like a held key to the next one and he would hop across the warehouse on a
        // single tap.
        bool held = _realMove.sqrMagnitude > 0.01f;

        float hop = Mathf.Max(0.1f, _hobble.ClipLength / Mathf.Max(0.01f, _animatorMotionSpeed));

        switch (_phase)
        {
            case Phase.Waiting:
                SpeedMultiplier = 0f;

                // Held or tapped, it buys one hop and no more. The next one is bought when this
                // one has finished and the pause after it has run out.
                if (held)
                {
                    _phase   = Phase.Hopping;
                    _timer   = 0f;
                    _hopMove = _realMove;   // where he is going, decided once, at takeoff
                    RestartHop();
                }
                break;

            case Phase.Hopping:
                _timer += Time.deltaTime;

                // Progress read from the animator, not from a clock of our own.
                //
                // Timing it independently is what left the hop cut short: the two agree only while
                // nothing else touches playback, and "has the animation finished" is a question
                // only the animator can actually answer. Now it ends when the clip ends.
                float t = Progress();

                SpeedMultiplier = t >= _travelFrom && t <= _travelTo ? _burstSpeed : 0f;

                // Runs to the end whatever the player does with the key. Letting go mid-hop and
                // stopping him dead in the air is the thing this component exists to avoid.
                //
                // The timer survives only as a dead man's handle: if the state is swapped out from
                // under us — a stumble, a ragdoll, a body change — nothing would ever report the
                // clip as finished and he would be stuck mid-hop for the rest of the shift.
                if (t >= _clipLanding || _timer > hop * 2f + 1f)
                {
                    _phase = Phase.Recovering;
                    _timer = 0f;
                    Land();
                }
                break;

            case Phase.Recovering:
                SpeedMultiplier = 0f;

                _timer += Time.deltaTime;
                if (_timer >= _recovery) { _phase = Phase.Waiting; _timer = 0f; }
                break;
        }

        FeedInput();
    }

    /// Remembers what the player is actually pressing, as distinct from what we are pressing for him.
    ///
    /// While a hop is being fed, the input is our own vector, and reading it back would be reading
    /// our own echo. A callback from the Input System is the only thing that can change it out from
    /// under us, so a value that differs from what we wrote is, by elimination, the player's.
    void TrackRealInput()
    {
        if (_input == null) return;
        if (!_feeding || _input.move != _fedMove) _realMove = _input.move;
    }

    /// Holds the direction key down on the player's behalf until he lands.
    ///
    /// A hop is atomic in time — it always plays out whole — but until now it was not atomic in
    /// space: the travel was a speed multiplier, and ThirdPersonController zeroes its target speed
    /// outright when the stick is centred, so a multiplier of anything times nothing is nothing.
    /// Tapping a direction therefore bought the whole animation and none of the ground, which reads
    /// as the character bobbing on the spot rather than hopping anywhere.
    ///
    /// Feeding the latched direction back into the input is what makes a tap buy a whole hop. It is
    /// done this way rather than by moving the CharacterController ourselves because the controller
    /// derives the facing, the acceleration and the camera-relative direction from that one vector;
    /// driving movement alongside it would mean owning all three, and disagreeing with it about any
    /// of them. The real input is tracked above and put back the moment he lands, so nothing outside
    /// a hop ever sees the substitution.
    void FeedInput()
    {
        if (_input == null) return;

        // A hop asked for with the jump key and no direction is a hop on the spot, and stays one.
        if (_phase == Phase.Hopping && _hopMove.sqrMagnitude > 0.01f)
        {
            _input.move = _fedMove = _hopMove;
            _feeding    = true;
            return;
        }

        StopFeeding();
    }

    void StopFeeding()
    {
        if (!_feeding || _input == null) return;

        _input.move = _realMove;
        _feeding    = false;
    }

    /// After ThirdPersonController has written its own blend, and overriding it.
    ///
    /// The controller sets Speed from the character's velocity, which this component spends most
    /// of its time holding at zero — so left alone the hop would stop the moment the movement did,
    /// a third of the way in. Driving the parameter from the phase instead is what lets the clip
    /// play out whole and then genuinely stop between hops, rather than looping under a body that
    /// is standing still.
    void LateUpdate()
    {
        if (_hobble == null || !_hobble.Hobbling) return;
        if (_animator == null || _animator.runtimeAnimatorController == null) return;

        // Hands off while he is on the floor: the trip and the standing-up are states of their
        // own on the controller, and writing Speed underneath them puts him back in the
        // locomotion tree halfway through getting up.
        if (Downed()) return;

        _animator.SetFloat(SpeedHash, _phase == Phase.Hopping ? _animatorSpeed : 0f);
        _animator.SetFloat(MotionSpeedHash, _animatorMotionSpeed);

        ApplyLift();
    }

    /// Puts the locomotion state back to its first frame, so the hop starts at the start.
    ///
    /// Without this the hop never played out whole, and the reason is easy to miss: a blend tree
    /// does not restart when the parameter that selects a clip changes. Blending back from Idle
    /// picks the hop up at whatever phase the state happened to be at — mid-air, landing, halfway
    /// down — while the timer above faithfully allots it one clip's worth of seconds and then cuts
    /// it off. The animation was complete in duration and arbitrary in content.
    ///
    /// Play on the current state rather than a named one: the base layer's locomotion state is the
    /// Starter Assets tree, and naming it here would be one more thing to keep in step with a
    /// package asset.
    /// The moment he comes down, and the moment a landing can go wrong.
    ///
    /// Rolled on landing rather than on takeoff so the fall reads as a consequence of the hop and
    /// not as a random seizure. It goes through PlayerSlipController, which already owns the whole
    /// business — trip, movement lock, stand up, and the bool other clients watch to see it — so a
    /// fall here looks exactly like a fall on a wet floor, which is the point.
    void Land()
    {
        if (_safeHops > 0) { _safeHops--; return; }
        if (_stumbleChance <= 0f || Random.value >= _stumbleChance) return;

        var slip = PlayerSlipController.Instance;
        if (slip == null || slip.IsSlipping || !slip.IsGrounded) return;

        slip.TriggerSlip();
        _safeHops = _stumbleGrace;

        // Straight, not sprawled: the ragdoll and the get-up animation take the body from here,
        // and a lean of ours left on top of them would fight both.
        StraightenBody();
    }

    /// Whether the player has been taken off his own controls.
    ///
    /// Three questions rather than one, because there are two ways of taking control away and both
    /// are in use on purpose.
    ///
    /// MovementLocked is the heavy one, and the ragdoll and the ladder still want it: it stops the
    /// controller dead, gravity included, which is right when something else is moving the body.
    ///
    /// The repair and the trip pin the input instead, and both arrived there the same way — the
    /// flag turned out to freeze more than was wanted. A repair begun off the floor left him
    /// crouching in mid-air; a sprint that ended in a trip stopped dead and then fell over, in that
    /// order. Neither leaves a flag to read, hence the extra two questions.
    bool Downed()
    {
        if (_tpc == null) _tpc = GetComponent<ThirdPersonController>();
        if (_tpc != null && _tpc.MovementLocked) return true;

        if (_repair == null) _repair = GetComponent<RepairCrouch>();
        if (_repair != null && _repair.Busy) return true;

        // And the trip, for the same reason and by the same route: it took control by zeroing the
        // input rather than by raising MovementLocked, so that a man who trips carries his momentum
        // into the fall instead of stopping dead. There is no flag left to read.
        var slip = PlayerSlipController.Instance;
        return slip != null && slip.IsSlipping;
    }

    /// Undoes the lean and the lift. Nothing else writes the body node, so this is a plain reset
    /// rather than a restore.
    void StraightenBody()
    {
        if (_body == null) return;
        if (_body.localPosition == Vector3.zero && _body.localRotation == Quaternion.identity) return;

        _body.localPosition = Vector3.zero;
        _body.localRotation = Quaternion.identity;
    }

    /// How far through the hop the animator actually is: 0 at the first frame, 1 at the last.
    ///
    /// Returns 1 when there is nothing to ask, so a missing or uninitialised animator ends the hop
    /// immediately rather than parking him in one forever.
    float Progress()
    {
        if (_animator == null || !_animator.isInitialized) return 1f;

        // While a crossfade is running, "current" is still the state being left — and its
        // normalized time is whatever it had wandered to, which would read as a finished hop and
        // end this one on its first frame. The state being blended *into* is the hop.
        var state = _animator.IsInTransition(0)
            ? _animator.GetNextAnimatorStateInfo(0)
            : _animator.GetCurrentAnimatorStateInfo(0);

        return state.normalizedTime;
    }

    void RestartHop()
    {
        if (_animator == null || _animator.runtimeAnimatorController == null) return;
        if (!_animator.isInitialized) return;

        var state = _animator.GetCurrentAnimatorStateInfo(0);

        // Parameters first, so the tree is already on the hop when the blend starts and does not
        // spend the crossfade morphing through the idle pose.
        _animator.SetFloat(SpeedHash, _animatorSpeed);
        _animator.SetFloat(MotionSpeedHash, _animatorMotionSpeed);

        // Crossfaded rather than snapped. Play with a zero offset cuts instantly from whatever
        // pose he is standing in to the clip's first frame, and that jump is the twitch that
        // appeared just before every hop. A short blend covers it, and it is short enough that the
        // hop still starts on the frame the key was pressed.
        _animator.CrossFadeInFixedTime(state.fullPathHash, _restartBlend, 0, 0f);
    }

    /// Lifts the body off the floor for the airborne part of the hop.
    ///
    /// Done to the visual body and not through the CharacterController on purpose. The controller
    /// owns gravity and grounding, and a component fighting it over the vertical would produce
    /// exactly the kind of intermittent, hard-to-reproduce bug that is not worth a few centimetres
    /// of arc. Nothing else depends on the body node's local position, so this is free.
    ///
    /// Assigned rather than added, so it cannot accumulate across frames.
    float _swaySeed;

    void ApplyLift()
    {
        if (_body == null) return;

        if (_swaySeed <= 0f) _swaySeed = Random.Range(10f, 90f);

        float lift = 0f;

        if (_phase == Phase.Hopping)
        {
            // Off the floor from the first frame, and for the whole hop.
            //
            // Measured on the player's own body, taking the lowest drawn corner of the foot and toe
            // geometry rather than the bone: standing, the sole sits 0.010 m below the root, which
            // is where the floor is. The hop clip puts it at 0.041 — three centimetres of boot
            // through the concrete, and not only mid-jump: it is deepest at phase 0.00, and under
            // again for the whole settle after landing. The arc alone could never have covered that,
            // because the arc is zero at both ends and those ends are exactly where he is buried.
            //
            // Ramped over the same crossfade that brings the clip in, and that is the point of the
            // ramp rather than politeness: during the blend the pose is part idle, part hop, so the
            // sole descends towards its 0.031 gradually. Lift rising at the same rate holds the sole
            // exactly on the floor the whole way down instead of popping him up and then sinking.
            float faded = _restartBlend > 0.001f ? Mathf.Clamp01(_timer / _restartBlend) : 1f;
            lift = _hopClearance * faded;

            if (_hopLift > 0.001f)
            {
                // The clip's own phase, so the arc is tied to the animation rather than running
                // alongside it — the same reason the hop itself is.
                float t = Mathf.Clamp01(Progress());

                // A single arch over the travelling part: off the ground as he pushes, down by the
                // time he lands. Sine rather than a parabola because the ends matter more than the
                // middle — it has to leave and meet the floor smoothly, or the landing reads as a
                // snap.
                float span = Mathf.Max(0.01f, _travelTo - _travelFrom);
                float u    = Mathf.Clamp01((t - _travelFrom) / span);

                lift += Mathf.Sin(u * Mathf.PI) * _hopLift;
            }
        }

        _body.localPosition = new Vector3(_body.localPosition.x, lift, _body.localPosition.z);

        ApplySway();
        ApplyLegCentring();
    }

    /// Angles the surviving leg in under the body.
    ///
    /// A rotation at the hip, not a move of it. Pulling the thigh's attachment towards the middle
    /// was the first attempt and it was wrong in an obvious way once seen: the leg was no longer
    /// joined where a leg joins. What a person actually does to stand on one leg is swing it
    /// inward from the hip until the foot is under their weight, and that is a hip angle.
    ///
    /// Applied to the world rotation after the animation has written the pose, so it stacks on
    /// whatever the clip is doing rather than replacing it. Nothing needs restoring afterwards:
    /// the animator rewrites limb rotations every frame, so the moment this stops being applied
    /// the leg is back where the clip puts it.
    void ApplyLegCentring()
    {
        var broken = BrokenLeg();

        if (broken != _centredFor) { _centredFor = broken; CacheStandingLeg(broken); }

        if (_standingLeg == null || Mathf.Abs(_legCentringAngle) < 0.01f) return;

        // Around the body's own forward, so the leg swings sideways rather than kicking. The sign
        // is what carries the foot towards the middle, and it flips with which leg is left.
        float sign = _centredFor == BodyPart.LeftLeg ? -1f : 1f;
        var  tilt  = Quaternion.AngleAxis(_legCentringAngle * sign, transform.forward);

        _standingLeg.rotation = tilt * _standingLeg.rotation;
    }

    /// The leg that is gone — and, since both can never go, the only one there is.
    ///
    /// Severed rather than merely injured, matching InjuredLocomotion: a dented leg is still a leg
    /// to stand on, and angling the other one in under the body would be correcting a balance
    /// problem he does not have.
    BodyPart BrokenLeg()
    {
        var injuries = InjurySystem.Instance;
        if (injuries == null) return BodyPart.None;

        if (injuries.IsSevered(BodyPart.LeftLeg))  return BodyPart.LeftLeg;
        if (injuries.IsSevered(BodyPart.RightLeg)) return BodyPart.RightLeg;

        return BodyPart.None;
    }

    void CacheStandingLeg(BodyPart broken)
    {
        _standingLeg = null;
        if (broken == BodyPart.None || _animator == null || !_animator.isHuman) return;

        _standingLeg = _animator.GetBoneTransform(
            broken == BodyPart.LeftLeg ? HumanBodyBones.RightUpperLeg : HumanBodyBones.LeftUpperLeg);
    }



    /// The small, constant correcting of a man standing on one leg.
    ///
    /// Standing still on one leg is not a pose, it is an activity — the body is always falling
    /// slightly and catching itself. Without it a robot missing a leg stands as squarely as one
    /// with two, and the missing leg reads as a rendering glitch rather than as an injury.
    ///
    /// Two sines at frequencies that do not divide into each other, so the wander never settles
    /// into a visible loop, and Perlin on top so the amplitude itself breathes. Applied to the
    /// body node's rotation, which nothing else writes — the animator drives the bones beneath it.
    ///
    /// Quieter mid-hop: in the air he is committed, and the clip is already doing the moving.
    void ApplySway()
    {
        if (_swayAngle < 0.01f) { _body.localRotation = Quaternion.identity; return; }

        float amount = _phase == Phase.Hopping ? _swayInAir : 1f;

        float t = Time.time * _swaySpeed + _swaySeed;

        // The 1.37 is deliberate: a ratio that is not a simple fraction, so roll and pitch drift
        // in and out of step instead of tracing the same figure over and over.
        float roll  = Mathf.Sin(t)          * _swayAngle;
        float pitch = Mathf.Sin(t * 1.37f)  * _swayAngle * 0.55f;

        // Breathes the whole thing, so the sway is not always the same size.
        float breath = 0.6f + 0.4f * Mathf.PerlinNoise(t * 0.4f, _swaySeed);

        _body.localRotation = Quaternion.Euler(pitch * amount * breath,
                                               0f,
                                               roll  * amount * breath);
    }
}
