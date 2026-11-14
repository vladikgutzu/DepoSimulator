using System.Linq;
using StarterAssets;
using UnityEngine;

/// Puts the player down on his haunches for as long as a repair is being held, and stands him back
/// up when it ends.
///
/// A six-second repair performed standing bolt upright reads as nothing happening. Crouching is
/// what makes it read as work — and it costs the player something real, because he is planted and
/// visibly busy in a warehouse that may have raiders in it.
///
/// Three phases, and the middle one is the whole point. He crouches at the clip's own speed, holds
/// the crouch for as long as the button is down, and stands up when it is released or when the
/// repair lands. The holding costs nothing to arrange — neither clip loops, so playback clamps on
/// the last frame, and Crouch's last frame is the crouch. Slowing the clip down to fill the time was
/// the first attempt and it was wrong twice over: a stretched clip is a man moving through treacle,
/// and how long a repair takes has nothing to do with how fast a person kneels.
///
/// It runs on a layer of its own (Tools/Warehouse/Build Repair Crouch Layer). The first version
/// overrode the Idle clip inside the locomotion blend tree instead, and that failed on screen in a
/// way worth remembering: swapping an override changes what the state's *current* time samples,
/// instantly, so anything that then blends is blending from a pose nobody asked for. Releasing the
/// button sampled the stand-up clip at its end — a man standing — and blended back down to its
/// start, a man crouched. Cutting instead of blending was no better: measured across every humanoid
/// bone with the feet pinned to the floor, the joins are 17, 13 and 29 cm apart.
///
/// A layer has its own clock, so the two clips cannot alias onto each other's time, the move between
/// them is a real transition, and its weight fades the whole thing in and out against whatever the
/// legs are doing underneath.
///
/// What the clips are not is grounded. Measured against the standing pose by the lowest drawn corner
/// of the foot geometry, Crouch sinks the sole up to 0.082 m through the floor and
/// Standing_from_crouch opens 0.072 m under it, wandering the whole way — so the body is put back on
/// the floor every frame by measurement rather than by a tuned offset. See Grounding, below —
/// which the puppet other players look at runs too, so both sides ground the same way.
///
/// Runs before ThirdPersonController. That ordering settles one argument: a player with a broken leg
/// has a component feeding a direction into the input to make him hop, and a man repairing himself
/// is not going anywhere. The later writer wins, and it should be this one.
[DefaultExecutionOrder(-40)]
[DisallowMultipleComponent]
public class RepairCrouch : MonoBehaviour
{
    enum Phase { None, Down, Hold, Up }

    /// The layer and its states, named here rather than in the tool that builds them.
    ///
    /// The tool lives in the editor assembly, which runtime code cannot see — so the names have to
    /// live on this side of the wall and be read from there. Same arrangement, and same reason, as
    /// PlayerWeaponLayer.LayerName.
    public const string LayerName = "RepairCrouch";
    public const string IdleState = "None";
    public const string DownState = "CrouchDown";
    public const string UpState   = "StandUp";

    /// The bool that tells the rest of the session he is crouching.
    ///
    /// An animator parameter rather than a NetworkVariable, because one already travels: the bridge
    /// copies parameters onto the puppet and NetworkAnimator replicates them, late joiners
    /// included. A layer weight is not a parameter and cannot ride that road, so the weight is
    /// derived from this on the far side instead.
    public const string CrouchParam = "IsRepairing";

    static readonly int CrouchParamHash = Animator.StringToHash(CrouchParam);

    [Tooltip("Seconds spent fading the crouch layer in and out over the ordinary locomotion. Short " +
             "enough to feel like a decision, long enough that the legs are not snatched away.")]
    [SerializeField] float _fade = 0.18f;

    [Tooltip("Seconds spent blending from the crouch into the stand-up. The two poses are close " +
             "but not identical — 13 cm at the planted foot — so the join needs a little cover.")]
    [SerializeField] float _crossFade = 0.15f;

    [Tooltip("How much of the stand-up is actually spent standing up. Measured: the hips reach " +
             "0.935 of their standing 0.942 by six tenths of the clip, and the rest is a settle — " +
             "running to the end would hold the player still for an extra second and a half after " +
             "he is already upright.")]
    [SerializeField] [Range(0.2f, 1f)] float _standUpTo = 0.6f;

    // Speed and MotionSpeed are deliberately not written here, and an earlier version of this that
    // did write them was quietly doing nothing at all: ThirdPersonController owns both and sets them
    // on every Update of its own, and Unity evaluates the animation after every Update and before
    // every LateUpdate — so the controller's write is always the last one before the pose is built.
    // On its own layer none of that matters; the base layer can play whatever it likes underneath.

    /// Keeps a crouching body's feet on the floor, by measurement.
    ///
    /// A plain class rather than part of this component, and for the same reason
    /// InjuredLocomotion.Swap is one: the puppet the rest of the team looks at needs exactly this
    /// correction and cannot have this component. It was written here first and left here, so a
    /// team-mate watching a repair saw the ungrounded clip raw — the pose right and the body
    /// visibly off the floor, which is not a subtle artefact at eight centimetres.
    ///
    /// It works off the root and the animator alone, which is all either side has in common: the
    /// body is found as whichever direct child of the root the hips hang under. On the player that
    /// is the model under PlayerArmature; on the puppet it is CharacterVisual under NetworkPlayer.
    public class Grounding
    {
        readonly Transform  _root;
        readonly Transform  _body;
        readonly Animator   _animator;
        readonly Renderer[] _soles;

        /// Where the body sits when nothing is correcting it. Remembered rather than assumed to be
        /// zero: on the puppet, RemoteRagdollVisual parks CharacterVisual at its own home, and
        /// snapping it to the origin on the way out of a crouch would undo that.
        readonly Vector3 _home;

        float _standingSole;   // where the sole sits when he is upright, relative to the root
        bool  _captured;       // whether that reference has been taken for this repair

        /// True when there is a body to correct. False on a rig whose hips are not under a single
        /// child of the root, where this stands aside rather than writing to the wrong transform.
        public bool Ready => _body != null;

        public Grounding(Transform root, Animator animator)
        {
            _root     = root;
            _animator = animator;
            _soles    = System.Array.Empty<Renderer>();

            if (root == null || animator == null || !animator.isHuman) return;

            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            for (var t = hips; t != null && t != root; t = t.parent)
                if (t.parent == root) { _body = t; break; }

            if (_body == null) return;

            _home = _body.localPosition;

            // Whatever is drawn at the bottom of a leg. Matched loosely on purpose: the names carry
            // a pack prefix that differs between the chassis and the plating, and a body variant
            // that calls them something else should degrade to the bone fallback rather than to
            // nothing.
            _soles = _body.GetComponentsInChildren<Renderer>(true)
                          .Where(r => r.GetComponent<MeshFilter>() != null &&
                                      (r.name.Contains("Foot") || r.name.Contains("Toe")))
                          .ToArray();
        }

        /// Takes the standing reference, while he is still upright. Any locomotion pose will do:
        /// the lowest of the two feet is the planted one, and the planted one is on the ground.
        public void Capture()
        {
            if (!Ready || _captured) return;

            _standingSole = SoleHeight();
            _captured     = true;
        }

        /// Lets the reference go and puts the body back where it hangs when nothing corrects it.
        public void Release()
        {
            if (!Ready) return;

            _captured = false;
            _body.localPosition = _home;
        }

        /// Keeps the feet on the floor for whatever the clip does.
        ///
        /// Measured rather than tuned, and it corrects in both directions, because the two clips
        /// err both ways: taken against the standing pose, Crouch buries the sole up to 0.082 m and
        /// Standing_from_crouch starts 0.072 m under, and neither is a constant across the clip. A
        /// hand-picked offset could only ever be right at one instant of one clip.
        ///
        /// Scaled by the layer's weight, so the correction arrives and leaves with the pose that
        /// needs it — at weight zero the legs belong to the locomotion again and are already where
        /// they should be.
        ///
        /// The root itself is deliberately not moved. It is what the CharacterController stands on
        /// and what gravity acts against, and pushing it around to satisfy an animation is how a
        /// character ends up hovering. Only the visual body is shifted; the capsule stays put.
        ///
        /// Call it after the animation has built the pose — LateUpdate — which is the only moment
        /// the feet can be measured.
        public void Apply(float weight)
        {
            if (!Ready || !_captured) return;

            float raw        = SoleHeight() - (_body.localPosition.y - _home.y);
            float correction = (_standingSole - raw) * weight;

            _body.localPosition = new Vector3(_home.x, _home.y + correction, _home.z);
        }

        /// The lowest drawn point of either foot, relative to the root.
        ///
        /// Corners of the foot geometry through their own bones, rather than the bone positions: in
        /// a crouch the foot rolls onto its toe, so the distance from ankle to sole is not a
        /// constant and a bone-based answer drifts by centimetres exactly where it matters most.
        /// These pieces are plain MeshRenderers parented to bones, so their corners are exact and
        /// cheap.
        ///
        /// Falls back to the ankles on a body whose feet are not separate objects, which is wrong
        /// by a fixed amount — and a fixed amount cancels, because the reference is taken the same
        /// way.
        float SoleHeight()
        {
            float root   = _root.position.y;
            float lowest = float.MaxValue;

            foreach (var r in _soles)
            {
                if (r == null || !r.gameObject.activeInHierarchy) continue;

                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null) continue;

                var b = mesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var local = new Vector3((c & 1) == 0 ? b.min.x : b.max.x,
                                            (c & 2) == 0 ? b.min.y : b.max.y,
                                            (c & 4) == 0 ? b.min.z : b.max.z);

                    lowest = Mathf.Min(lowest, r.transform.TransformPoint(local).y - root);
                }
            }

            if (lowest < float.MaxValue) return lowest;

            if (_animator != null && _animator.isHuman)
            {
                var l  = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                var r2 = _animator.GetBoneTransform(HumanBodyBones.RightFoot);

                if (l != null && r2 != null)
                    return Mathf.Min(l.position.y, r2.position.y) - root;
            }

            return 0f;
        }
    }

    Animator            _animator;
    StarterAssetsInputs _input;
    PlayerInteraction   _interaction;

    int   _layer = -1;
    float _weight;

    Grounding _ground;

    Phase _phase = Phase.None;

    static readonly int DownHash = Animator.StringToHash(DownState);
    static readonly int UpHash   = Animator.StringToHash(UpState);

    /// True while the repair owns his feet. Read by HopMovement, which stands down for it.
    ///
    /// Standing back up counts. It is tempting to hand control back the instant the button is
    /// released — he has finished, after all — but a player with a broken leg would then start
    /// hopping halfway out of a crouch, with two components writing the same body offset from
    /// opposite intentions. The stand-up is cut short instead; see _standUpTo.
    public bool Busy => _phase != Phase.None;

    void Awake() => Rebind();

    void OnDisable() => Restore();

    /// Re-attaches to whatever body is being worn. The player's body can be swapped at runtime,
    /// which replaces the animator along with it.
    public void Rebind()
    {
        Restore();

        // The old body keeps whatever offset the last repair left on it otherwise, and after a body
        // swap nothing will ever come back to clear it.
        _ground?.Release();
        _ground = null;

        _animator = GetComponentInChildren<Animator>();
        _layer    = -1;
        _weight   = 0f;

        if (_animator == null || _animator.runtimeAnimatorController == null) return;

        for (int i = 0; i < _animator.layerCount; i++)
            if (_animator.GetLayerName(i) == LayerName) { _layer = i; break; }

        if (_layer < 0)
        {
            Debug.LogWarning($"[RepairCrouch] no '{LayerName}' layer on " +
                             $"{_animator.runtimeAnimatorController.name} — run " +
                             "Tools/Warehouse/Build Repair Crouch Layer. Repairs still work; the " +
                             "player just will not crouch for them.");
            return;
        }

        _animator.SetLayerWeight(_layer, 0f);

        // Found by walking up from the hips rather than by name, so it follows a body swap — the
        // same approach HopMovement uses, and for the same reason.
        _ground = new Grounding(transform, _animator);
    }

    void Update()
    {
        if (_interaction == null) _interaction = GetComponent<PlayerInteraction>();
        if (_input       == null) _input       = GetComponent<StarterAssetsInputs>();
        if (_layer < 0) return;

        bool wanted = _interaction != null && IsRepair(_interaction.ActiveHold);

        switch (_phase)
        {
            case Phase.None:
                if (wanted) Begin();
                break;

            case Phase.Down:
                if (!wanted) { Rise(); break; }

                // Frozen once the clip has played out. Its last frame is the crouch, so there is
                // nothing left to play and everything left to hold.
                if (Progress(DownHash) >= 1f) _phase = Phase.Hold;
                break;

            case Phase.Hold:
                if (!wanted) Rise();
                break;

            case Phase.Up:
                // Straight back down if he starts the next repair before he is upright.
                if (wanted) { Begin(); break; }

                if (Progress(UpHash) >= _standUpTo) Restore();
                break;
        }

        Fade(_phase == Phase.None ? 0f : 1f);

        // Published for the puppet other players look at. Set on the local animator, which the
        // bridge reads; nothing here consumes it.
        if (_animator != null) _animator.SetBool(CrouchParamHash, Busy);

        // Planted while the button is down. Written into the input rather than through
        // ThirdPersonController.MovementLocked, and that is not a stylistic choice: the controller's
        // whole Update early-returns on that flag, JumpAndGravity along with it, and a
        // CharacterController does not fall on its own — so a repair begun a few centimetres off the
        // floor left him hanging in the air for the entire six seconds, crouching. Zeroing the input
        // stops him walking while leaving gravity and grounding running, which is all that was ever
        // wanted from it.
        if (Busy && _input != null)
        {
            _input.move = Vector2.zero;
            _input.jump = false;
        }
    }

    /// A hold that counts as a repair, and so should put him on his haunches while it runs.
    ///
    /// Both routes back from an injury — refitting the limb he carried here, and patching a dent
    /// where he stands — plus the radio, whose charger sits on the floor: a man working on it while
    /// standing bolt upright reads as a man waiting, which is the whole reason this component
    /// exists.
    ///
    /// The power boxes and the gate panel are deliberately not here. They are mounted at chest and
    /// head height, and kneeling to reach something at eye level looks like a different bug.
    static bool IsRepair(IHoldInteractable hold) =>
        hold is SeveredLimbItem || hold is SelfRepairTarget || hold is RadioRepairPanel;

    void Begin()
    {
        // Taken while he is still upright, so it is this body's own idea of "on the floor" rather
        // than a number typed in against one particular model.
        _ground?.Capture();

        _phase = Phase.Down;

        // From the top. Cutting to frame 0 is safe here in a way it never was on the base layer:
        // the layer's weight is still zero, so nothing of this pose is on screen yet — the fade is
        // what brings it in, and it brings it in from whatever the legs were actually doing.
        _animator.Play($"{LayerName}.{DownState}",
                       _layer, 0f);
    }

    void Rise()
    {
        _phase = Phase.Up;

        // A real transition between two states on the same layer: separate clocks, so the stand-up
        // starts at its own frame 0 no matter where the crouch had got to.
        _animator.CrossFadeInFixedTime(
            $"{LayerName}.{UpState}",
            Mathf.Max(0.01f, _crossFade), _layer, 0f);
    }

    /// Ends the repair. The layer is still on screen at this point and fades out over the next
    /// _fade seconds; the grounding is released with it, not here — see Fade.
    void Restore()
    {
        _phase = Phase.None;
    }

    /// Brings the layer's weight to where it should be, over _fade seconds.
    void Fade(float target)
    {
        if (_layer < 0 || _animator == null) return;

        float step = _fade > 0.001f ? Time.deltaTime / _fade : 1f;
        _weight = Mathf.MoveTowards(_weight, target, step);

        _animator.SetLayerWeight(_layer, _weight);

        if (_weight > 0.001f || target > 0f) return;

        // Fully out. Parked on the empty state so a stale crouch is not sitting at weight zero
        // waiting to be faded back in mid-pose next time.
        _animator.Play($"{LayerName}.{IdleState}", _layer, 0f);

        // And only now is the grounding let go. Released at the end of the repair instead, it
        // would stop correcting while the pose was still fading out — holding the last offset for
        // a fifth of a second and then dropping it in one frame. Carried through the fade, the
        // correction is scaled by the same weight as the pose and reaches zero exactly with it.
        _ground?.Release();
    }

    /// How far through the named state the layer actually is, or 0 if it is not playing it.
    ///
    /// Named rather than "whatever is playing", and that guard is load-bearing. A CrossFade takes a
    /// frame to register, so on the frame the stand-up is asked for the animator is still reporting
    /// the crouch — whose normalized time is past 1, having clamped on its last frame. Read
    /// blindly, that says "the stand-up has already finished" and the whole thing is torn down on
    /// the frame it began.
    ///
    /// Reading the state also replaces the clock this used to keep: the clip's own progress is the
    /// only honest answer to how far along it is, and it needs no clip length to compute.
    float Progress(int stateHash)
    {
        if (_animator == null || !_animator.isInitialized || _layer < 0) return 0f;

        // While a crossfade is running, "current" is still the state being left. The state being
        // blended into is the one that matters.
        var state = _animator.IsInTransition(_layer)
            ? _animator.GetNextAnimatorStateInfo(_layer)
            : _animator.GetCurrentAnimatorStateInfo(_layer);

        return state.shortNameHash == stateHash ? state.normalizedTime : 0f;
    }

    /// After the animation has built the pose, which is the only moment the feet can be measured.
    void LateUpdate()
    {
        if (_layer < 0) return;

        _ground?.Apply(_weight);
    }
}
