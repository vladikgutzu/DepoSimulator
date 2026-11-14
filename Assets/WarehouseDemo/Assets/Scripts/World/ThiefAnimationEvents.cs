using UnityEngine;

/// Catches the animation events baked into the StarterAssets locomotion clips.
///
/// The thief prefab is built from PlayerArmature with every MonoBehaviour stripped off it,
/// which takes ThirdPersonController — and with it the OnFootstep/OnLand receivers — with it.
/// The clips still fire those events, and an animation event with no receiver is a Unity
/// error, several per second per walking thief: enough to bury anything real in the console.
///
/// ThiefController adds this next to the Animator itself, so prefabs built before it existed
/// are covered too. It is also the natural place to hang thief footstep audio when the raid
/// gets a sound pass.
public class ThiefAnimationEvents : MonoBehaviour
{
    void OnFootstep(AnimationEvent animationEvent) { }

    void OnLand(AnimationEvent animationEvent) { }
}
