using Unity.Cinemachine;
using UnityEngine;

/// Shakes the camera. Call CameraShake.AddTrauma(0..1) for a knock, or Kick() when the blow has
/// a direction worth feeling.
///
/// It shakes through Cinemachine Impulse rather than by writing to the camera transform, because
/// the camera is not ours to write to: Cinemachine3rdPersonFollow places it every LateUpdate, and
/// anything that assigns a rotation afterwards is one script-order change away from being
/// overwritten or from fighting CinemachineGroundClamp. Impulse is the seam Cinemachine provides
/// for exactly this, and it composes with everything else the rig does.
///
/// It also builds itself. The previous version was a component that had to sit on the camera —
/// and did not, anywhere in the project, so every AddTrauma call in the game (a raider's bat, a
/// pallet coming down, a shelf collapsing, every weapon's Trauma value) had been doing nothing at
/// all. Nothing to attach means nothing to forget, and nothing a body swap or a scene reload can
/// leave behind.
[DefaultExecutionOrder(-50)]
public class CameraShake : MonoBehaviour
{
    static CameraShake _instance;

    CinemachineImpulseSource _source;

    /// Rough metres-per-second of camera kick at trauma 1. Tuned so a fist reads as a jolt and a
    /// pallet landing on your head reads as being hit by a pallet.
    const float TraumaToVelocity = 3.5f;

    // ── API ───────────────────────────────────────────────────────────────────

    /// Add trauma [0..1]. Direction-free: the camera is knocked about rather than shoved.
    public static void AddTrauma(float amount)
    {
        var cs = GetOrCreate();
        if (cs == null || amount <= 0f) return;

        // Down-and-sideways rather than a pure axis, so two hits in a row do not shake
        // identically and read as a repeating effect.
        Vector3 v = new Vector3(Random.Range(-1f, 1f), -1f, Random.Range(-1f, 1f)).normalized;
        cs._source.GenerateImpulseWithVelocity(v * (Mathf.Clamp01(amount) * TraumaToVelocity));
    }

    /// A blow with a direction — the camera is shoved the way the hit travelled.
    public static void Kick(Vector3 direction, float amount)
    {
        var cs = GetOrCreate();
        if (cs == null || amount <= 0f) return;

        if (direction.sqrMagnitude < 0.001f) { AddTrauma(amount); return; }

        // Tilted down a little: a blow that only pushes sideways reads as the world sliding,
        // while a touch of drop reads as it landing on you.
        Vector3 v = (direction.normalized + Vector3.down * 0.4f).normalized;
        cs._source.GenerateImpulseWithVelocity(v * (Mathf.Clamp01(amount) * TraumaToVelocity));
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    static CameraShake GetOrCreate()
    {
        if (_instance != null) return _instance;

        var go = new GameObject("CameraShake");
        DontDestroyOnLoad(go);

        _instance = go.AddComponent<CameraShake>();
        _instance._source = go.AddComponent<CinemachineImpulseSource>();

        var def = _instance._source.ImpulseDefinition;
        def.ImpulseShape       = CinemachineImpulseDefinition.ImpulseShapes.Recoil;
        def.ImpulseDuration    = 0.35f;
        // Uniform: the shake is a thing that happened to the player, not to a place in the world,
        // so it must not fade with distance from wherever the source object happens to sit.
        def.ImpulseType        = CinemachineImpulseDefinition.ImpulseTypes.Uniform;

        return _instance;
    }

    void Update()
    {
        // The listener is what turns an impulse into camera movement, and it lives on the vcam —
        // which is spawned with the player and replaced on every scene load. Checking for it here
        // costs a null test per frame and means no setup step to remember or to redo after a body
        // swap; the alternative is a shake system that silently does nothing again.
        if (_listener == null) EnsureListener();
    }

    CinemachineImpulseListener _listener;

    void EnsureListener()
    {
        var brain = CinemachineBrain.GetActiveBrain(0);
        var vcam  = brain != null ? brain.ActiveVirtualCamera as CinemachineVirtualCameraBase : null;
        if (vcam == null) return;

        _listener = vcam.GetComponent<CinemachineImpulseListener>();
        if (_listener != null) return;

        _listener = vcam.gameObject.AddComponent<CinemachineImpulseListener>();
        _listener.ApplyAfter    = CinemachineCore.Stage.Body;
        _listener.ReactionSettings.AmplitudeGain = 1f;
    }

    void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // Statics outlive a scene load but not a play session, and domain reload being off means this
    // has to be cleared by hand or the second run finds a reference to a destroyed object.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => _instance = null;
}
