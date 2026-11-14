using System.Collections;
using StarterAssets;
using UnityEngine;

/// Slip-and-fall reaction: plays Tripping then StandingUp while the player is off his own
/// controls. Triggered by WetFloorEvent while it's active. The Animator states/transitions
/// (AnyState -> Tripping -> StandingUp -> Idle Walk Run Blend) live on
/// StarterAssetsThirdPerson.controller — the same asset the remote players' CharacterVisual runs.
///
/// What travels is the trigger, not the bool. `IsSlipping` is copied by PlayerAnimatorBridge and
/// gates nothing on arrival; the transition is opened by `Slip`, which is a trigger and so cannot
/// be copied by a poller at all. It goes through NetworkPlayer.FireSlipTrigger instead.
///
/// Control is taken by zeroing the input rather than by raising
/// ThirdPersonController.MovementLocked, and the difference is the whole reason a trip used to look
/// like a mannequin being switched off. That flag early-returns out of the controller's entire
/// Update — Move, GroundedCheck and JumpAndGravity with it — so a man sprinting at 7 m/s went to a
/// dead stop inside a single frame and then played a fall animation that carries no travel of its
/// own (measured: Tripping.fbx has 0.000 m of root motion, and root motion is off on this animator
/// anyway). He stopped, then fell over, in that order, which is not how tripping works. Gravity was
/// off for the whole five and a half seconds too, so a trip taken off a step left him falling
/// nowhere.
///
/// With the input zeroed instead, the controller keeps running: no direction means it decelerates
/// to a stop on its own curve — about a third of a second, most of a metre from a sprint — so he
/// carries his momentum into the fall and slides to a halt while going down. Rotation and jumping
/// are already gated on the input being non-zero, so nothing else has to be held shut.
[DefaultExecutionOrder(-45)]
public class PlayerSlipController : MonoBehaviour
{
    public static PlayerSlipController Instance { get; private set; }

    public bool IsSlipping { get; private set; }

    // Matches the controller: Tripping clip is 1.57s (exitTime 0.9 => ~1.41s), StandingUp
    // clip is 11.4s at speed 2.5 (exitTime 0.9 => ~4.1s).
    [SerializeField] float _trippingDuration   = 1.4f;
    [SerializeField] float _standingUpDuration = 4.1f;
    [SerializeField] float _returnBlendDuration = 0.3f;

    // "Slip" (Trigger) actually fires the AnyState -> Tripping transition — a Trigger
    // auto-consumes after firing once, unlike a Bool, which would keep re-satisfying the
    // AnyState condition every frame and endlessly restart the Tripping clip from near its
    // start (confirmed via normalizedTime never advancing past ~0.15 when this used a Bool).
    // "IsSlipping" (Bool) stays purely informational — copied by PlayerAnimatorBridge, but it
    // gates no transition, so it is not what makes the puppet fall. FireSlipTrigger is.
    static readonly int _slipTriggerHash = Animator.StringToHash(NetworkPlayer.SlipTrigger);
    static readonly int _isSlippingHash  = Animator.StringToHash("IsSlipping");
    // Kept only for the no-controller fallback below. The stale-speed problem these used to solve
    // is gone with the lock that caused it: Move() now runs throughout the trip, so it winds its
    // own Speed down as he decelerates and the blend tree is already at zero by the time he is back
    // on his feet. Zeroing them by hand here would also have cost the slide — ResetLocomotionSpeed
    // clears the controller's velocity along with the animator parameters.
    static readonly int _speedHash       = Animator.StringToHash("Speed");
    static readonly int _motionSpeedHash = Animator.StringToHash("MotionSpeed");

    Animator _animator;
    ThirdPersonController _controller;
    StarterAssetsInputs   _input;
    Coroutine _sequence;

    /// False mid-jump/fall — slipping is a ground-contact reaction, so a trip animation
    /// starting while airborne would play with the character floating instead of on the floor.
    public bool IsGrounded => _controller == null || _controller.Grounded;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        _animator   = GetComponentInChildren<Animator>();
        _controller = GetComponent<ThirdPersonController>();
        _input      = GetComponent<StarterAssetsInputs>();
    }

    /// Holds the controls shut for as long as he is on the floor.
    ///
    /// Every frame rather than once, because the input is written by the Input System's own
    /// callbacks and a value cleared at the start of the trip would be back by the next key event.
    /// Runs before ThirdPersonController, which sits at the default order of zero.
    void Update()
    {
        if (!IsSlipping || _input == null) return;

        _input.move = Vector2.zero;
        _input.jump = false;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    public void TriggerSlip()
    {
        // Slipping is exclusively a WetFloorEvent reaction — a real impact (box, pallet
        // explosion, etc.) always goes through RagdollController instead, so don't stack a
        // scripted trip animation on top of an already-active ragdoll. Also skip mid-air —
        // see IsGrounded's doc comment.
        if (IsSlipping || !IsGrounded || (RagdollController.Instance != null && RagdollController.Instance.IsRagdoll))
            return;
        _sequence = StartCoroutine(SlipSequence());
    }

    /// Called by RagdollController if a ragdoll-worthy impact happens mid-slip, so the
    /// trip animation doesn't keep running (and re-lock movement) underneath/after the
    /// ragdoll recovery.
    public void CancelSlip()
    {
        if (!IsSlipping) return;
        if (_sequence != null) StopCoroutine(_sequence);
        _animator?.SetBool(_isSlippingHash, false);

        // A trip cancelled by a real impact never played. Left pending, the trigger would fire the
        // Tripping state on the puppet the moment the ragdoll handed the animator back.
        _animator?.ResetTrigger(_slipTriggerHash);
        NetworkPlayer.Local?.ClearSlipTrigger();

        // No lock to release any more. It used to clear MovementLocked here, which was a hazard in
        // its own right: the caller is RagdollController, which raises that same flag for its own
        // reasons, and clearing it on the way past could hand the controls back to a player who is
        // in the middle of being a ragdoll.
        IsSlipping = false;
    }

    IEnumerator SlipSequence()
    {
        IsSlipping = true;
        _animator?.SetBool(_isSlippingHash, true);
        _animator?.SetTrigger(_slipTriggerHash);

        // And on the puppet the rest of the team is looking at. The bool above travels through
        // PlayerAnimatorBridge and does nothing on arrival — it gates no transition — so without
        // this line a man tripping over his own hobble was seen standing perfectly still. A
        // trigger cannot be copied by a poller (it consumes itself), so it goes by the road NGO
        // keeps for triggers; see NetworkPlayer.FireSlipTrigger.
        NetworkPlayer.Local?.FireSlipTrigger();

        // Deliberately no ResetLocomotionSpeed: it zeroes the controller's velocity as well as the
        // animator parameters, which is exactly the momentum the fall is supposed to carry. With
        // the controller left running, Move() winds both down on its own as he slides to a stop.
        //
        // The branch survives for a player object with no controller at all — a test rig, a puppet
        // — where nothing else would ever bring the blend tree back down.
        if (_controller == null)
        {
            _animator?.SetFloat(_speedHash, 0f);
            _animator?.SetFloat(_motionSpeedHash, 0f);
        }

        // Whatever the player's holding gets fumbled — drop it, don't throw it.
        BoxPhysicsInteractable.ReleaseGrabbed(0f);

        yield return new WaitForSeconds(_trippingDuration + _standingUpDuration);

        _animator?.SetBool(_isSlippingHash, false);
        yield return new WaitForSeconds(_returnBlendDuration);

        IsSlipping = false;
    }
}
