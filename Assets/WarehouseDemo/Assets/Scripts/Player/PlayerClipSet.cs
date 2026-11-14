using UnityEngine;

/// The hand-added clips the player retargets, in one asset that ships with the build.
///
/// They used to be found by `AssetDatabase.LoadAllAssetsAtPath` inside `#if UNITY_EDITOR`, which
/// works perfectly in the editor and produces null in a build — no hobble, no crouch, and nothing
/// to see that would tell you why. That was survivable while the only consumers were runtime-added
/// components on the local player; it stopped being survivable when the puppet other players look
/// at needed the same clips.
///
/// A ScriptableObject in Resources rather than serialized fields on the components, because those
/// components are added at runtime by InjurySystem and so have nothing to serialize into.
///
/// Built and filled by Tools/Warehouse/Setup Player Clips.
[CreateAssetMenu(menuName = "Warehouse/Player Clip Set")]
public class PlayerClipSet : ScriptableObject
{
    public const string ResourcePath = "PlayerClips";

    [Tooltip("Moving on one leg. Replaces the walk, the run and the jump while a leg is missing.")]
    public AnimationClip Hobble;

    [Tooltip("Crouching down to work, and the pose held while repairing. Must END crouched.")]
    public AnimationClip Crouch;

    [Tooltip("Standing back up out of that crouch. Must START crouched.")]
    public AnimationClip StandUp;

    static PlayerClipSet _loaded;
    static bool          _tried;

    /// The set, or null if it has not been built yet. Loaded once and remembered — including the
    /// miss, so a project without the asset does not hit Resources every time a leg breaks.
    public static PlayerClipSet Load()
    {
        if (_tried) return _loaded;

        _tried  = true;
        _loaded = Resources.Load<PlayerClipSet>(ResourcePath);

        if (_loaded == null)
            Debug.LogWarning($"[PlayerClipSet] nothing at Resources/{ResourcePath} — run " +
                             "Tools/Warehouse/Setup Player Clips. The injury systems still work; " +
                             "the player just will not hobble or crouch.");

        return _loaded;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetStatics() { _loaded = null; _tried = false; }
}
