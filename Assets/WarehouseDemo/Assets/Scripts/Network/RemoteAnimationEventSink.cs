using UnityEngine;

/// Catches the StarterAssets locomotion animation events (OnFootstep, OnLand) that are baked
/// into the Run_N/Walk_N/Jump clips — CharacterVisual plays these same clips via NetworkAnimator
/// but has ThirdPersonController (the normal receiver) stripped out by NetworkPlayerSetupTool, so
/// without this, every footstep/landing on every remote player spams "AnimationEvent has no
/// receiver" — at running pace that's several LogErrors a second per remote player, each one
/// capturing a full stack trace, which is what was tanking framerate on observing clients.
/// The animation events are also the network audio clock: remote players play their already
/// replicated animations locally, so footsteps need no extra RPC traffic.
public class RemoteAnimationEventSink : MonoBehaviour
{
    static readonly int JumpId = Animator.StringToHash("Jump");

    Animator _animator;
    RobotLocomotionAudio _audio;
    bool _wasJumping;

    void Awake()
    {
        _animator = GetComponent<Animator>();
        _audio = GetComponent<RobotLocomotionAudio>();
        if (_audio == null) _audio = gameObject.AddComponent<RobotLocomotionAudio>();
    }

    void OnEnable()
    {
        _wasJumping = _animator != null && _animator.GetBool(JumpId);
    }

    void Update()
    {
        if (_animator == null) return;

        bool jumping = _animator.GetBool(JumpId);
        if (jumping && !_wasJumping) _audio.PlayJump();
        _wasJumping = jumping;
    }

    void OnFootstep(AnimationEvent animationEvent)
    {
        if (animationEvent.animatorClipInfo.weight >= 0.2f) _audio.PlayFootstep();
    }

    void OnLand(AnimationEvent animationEvent)
    {
        if (animationEvent.animatorClipInfo.weight > 0.5f) _audio.PlayLand();
    }
}
