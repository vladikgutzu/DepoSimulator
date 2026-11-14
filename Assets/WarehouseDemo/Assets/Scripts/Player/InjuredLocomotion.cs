using System.Collections.Generic;
using UnityEngine;

/// Swaps the player's walk and run for a hobble while a leg is broken.
///
/// The clip is Mixamo's injured hop, and it earns the job on measurement rather than on faith: its
/// last frame differs from its first by 0.0 degrees summed over all 51 humanoid bones, so it runs
/// continuously with no seam. (Walk_N, a known-good cycle, measures 0.2 by the same test.) It
/// arrived as a Generic rig, which is why it did nothing at all when first tried on this body —
/// see Tools/Warehouse/Setup Player Clips.
///
/// Done with an AnimatorOverrideController rather than by adding states to the controller. Two
/// reasons: the Starter Assets controller is a package asset that would have to be edited in place
/// and re-edited on every update, and an override changes what a state plays without touching what
/// the state machine is doing — no new transitions to get wrong, and it works the same whatever
/// else is later added to the locomotion.
///
/// The mechanics live in Swap, which is a plain class rather than part of this component, because
/// the puppet other players look at needs exactly the same swap — see RemoteInjuryVisual. Sharing
/// the code is what stops the two sides drifting into showing different things.
[DisallowMultipleComponent]
public class InjuredLocomotion : MonoBehaviour
{
    /// Puts the hobble over an animator's locomotion, and takes it off again.
    ///
    /// Deliberately not a MonoBehaviour: it is attached to an Animator, not to a GameObject, and
    /// the puppet's animator lives on a child of an object that already has its own components.
    public class Swap
    {
        /// Every clip a missing leg replaces.
        ///
        /// The walk and the run, and the jump with them: a robot on one leg does not walk and does
        /// not spring, it hops, and the hop is the same motion either way. Idle is deliberately not
        /// on the list — a player standing still should look hurt, not be bouncing on the spot.
        ///
        /// Timing, measured off the controller's own transitions, because two of these states leave
        /// on exit time and the hobble is longer than what it replaces:
        ///   • JumpStart leaves at 0.66 of its clip. Was 0.40 s, so 0.26 s; becomes 1.25 s.
        ///   • JumpLand leaves at 0.40 of its clip, so its recovery roughly doubles.
        ///   • InAir leaves on Grounded, not on time, so its length does not matter at all.
        /// None of that delays the actual jump — the Starter Assets controller applies the leap in
        /// code the moment the button goes down, and these numbers only govern which clip is
        /// playing while it happens. The effect is a heavier, more laboured jump, which is the point.
        static readonly string[] Replaced =
        {
            "Walk_N", "Run_N",
            "JumpStart", "InAir",
            "JumpLand", "Walk_N_Land", "Run_N_Land",
        };

        readonly Animator                   _animator;
        readonly AnimatorOverrideController _override;
        readonly AnimationClip              _hobble;

        // The clips as the controller was authored, captured before anything is overridden.
        //
        // Kept rather than looked up on demand because an override is keyed by the *original* clip,
        // and once one is in place the live clip list reports the replacement instead — searching it
        // by name afterwards finds nothing, and the hobble could never be taken off again.
        readonly List<AnimationClip> _originals = new();

        bool _applied;

        /// True when there is something to swap. False on an animator with no controller, or with
        /// no hobble clip available.
        public bool Ready => _override != null && _hobble != null && _originals.Count > 0;

        /// What this is currently replacing, for anything that wants to report it.
        public int ReplacedCount => _originals.Count;

        /// How long one hop lasts at 1x playback. HopMovement times a whole hop against this rather
        /// than against a number typed in beside it, so replacing the clip changes the rhythm and
        /// nothing else has to be touched.
        public float ClipLength => _hobble != null ? _hobble.length : 1.9f;

        public Swap(Animator animator)
        {
            _animator = animator;
            if (_animator == null || _animator.runtimeAnimatorController == null) return;

            _hobble = PlayerClipSet.Load()?.Hobble;
            if (_hobble == null) return;

            // Wrapped now, while nothing is broken, so that the state machine reset this causes
            // lands on the frame the body appears rather than in the middle of a shift.
            //
            // Reused if somebody else already wrapped it: assigning a runtimeAnimatorController
            // resets the state machine, and wrapping twice would mean two controllers arguing over
            // one animator.
            _override = _animator.runtimeAnimatorController as AnimatorOverrideController;

            if (_override == null)
            {
                _override = new AnimatorOverrideController(_animator.runtimeAnimatorController)
                {
                    name = _animator.runtimeAnimatorController.name + " (injury)"
                };
                _animator.runtimeAnimatorController = _override;
            }

            CaptureOriginals();
        }

        void CaptureOriginals()
        {
            var source = _override.runtimeAnimatorController != null
                ? _override.runtimeAnimatorController
                : (RuntimeAnimatorController)_override;

            foreach (string name in Replaced)
                foreach (var clip in source.animationClips)
                    if (clip != null && clip.name == name && !_originals.Contains(clip))
                    {
                        _originals.Add(clip);
                        break;
                    }
        }

        /// Puts the hobble on, or takes it off. Cheap to call repeatedly — it does nothing until
        /// the answer changes.
        public void Apply(bool hobbling)
        {
            if (!Ready || hobbling == _applied) return;

            _applied = hobbling;

            foreach (var original in _originals)
                _override[original] = hobbling ? _hobble : original;
        }

        /// True while the hobble is what is actually playing.
        public bool Applied => _applied;
    }

    Swap _swap;

    void Awake() => Rebind();

    /// Re-attaches to whatever body is being worn now. The player's body can be swapped at
    /// runtime, which replaces the animator along with it.
    public void Rebind() => _swap = new Swap(GetComponentInChildren<Animator>());

    void Update() => _swap?.Apply(LegBroken());

    /// What the hobble is currently replacing, for anything that wants to report it.
    public int ReplacedCount => _swap != null ? _swap.ReplacedCount : 0;

    /// How long one hop lasts at 1x playback.
    public float ClipLength => _swap != null ? _swap.ClipLength : 1.9f;

    /// True while the hobble is what the player is actually playing — PlayerLimp reads this so the
    /// two do not both bend the same body at once.
    public bool Hobbling => _swap != null && _swap.Applied;

    /// A leg that is actually gone, not merely dented.
    ///
    /// It used to answer yes to any leg injury at all, which meant a bruise put the player on one
    /// leg with both of them plainly still attached. That was always wrong and is now easy to walk
    /// into: the rule that stops both legs being torn off downgrades the second one to a dent, so
    /// "dented leg" is no longer a corner of the debug menu.
    static bool LegBroken()
    {
        var injuries = InjurySystem.Instance;
        return injuries != null
            && (injuries.IsSevered(BodyPart.LeftLeg) || injuries.IsSevered(BodyPart.RightLeg));
    }
}
