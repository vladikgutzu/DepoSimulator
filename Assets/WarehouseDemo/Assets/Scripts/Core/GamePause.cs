using UnityEngine;
using UnityEngine.SceneManagement;

/// Stops the world — and silences it — while the player is in the menu, alone and only alone.
///
/// In a session it deliberately does nothing. Time is not a thing one client gets to stop: the
/// server keeps running the shift whatever anybody's menu is doing, and freezing a client's clock
/// would only mean freezing that client while raiders carry boxes out of the building behind his
/// back. Netcode's own send loops run on scaled time too, so the frozen player would drift out of
/// step and snap back on resume. Pressing Escape in multiplayer still opens the menu and frees the
/// cursor — it just does not stop the game, which is the normal contract for a co-op game.
///
/// Everything goes through here rather than assigning Time.timeScale where it is wanted, because
/// the failure mode of a pause is a game left frozen forever with no menu on screen to unfreeze it.
/// One owner, one place that can get it wrong, and a scene load always clears it.
public static class GamePause
{
    /// True while the world is actually stopped.
    public static bool IsPaused { get; private set; }

    /// Whether stopping time is allowed at all. False for the whole of a networked session.
    public static bool CanPause =>
        Unity.Netcode.NetworkManager.Singleton == null
        || !Unity.Netcode.NetworkManager.Singleton.IsListening;

    /// Stops or resumes the world. Asking to pause in a session is not an error — it is simply
    /// ignored, so callers do not each have to know the rule.
    public static void Set(bool paused)
    {
        if (paused && !CanPause) paused = false;
        if (paused == IsPaused) return;

        IsPaused       = paused;
        Time.timeScale = paused ? 0f : 1f;

        // The whole mix, not selected sources. Nothing is emitting anyway — the world is stopped —
        // so this is really about what carries on ringing: engine loops, the warehouse ambience,
        // a gate still being hammered on. AudioManager's one source is shared between UI and
        // gameplay sounds, so it is deliberately not exempted: exempting it to keep menu clicks
        // audible would also let a box landing keep making noise in a stopped world.
        AudioListener.pause = paused;
    }

    /// For anywhere that must not leave the game stopped — a panel being torn down, a disconnect,
    /// a scene going away. Safe to call when nothing is paused.
    public static void Resume() => Set(false);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Hook()
    {
        // Subscribed once per process. Unsubscribing first makes that true even if the attribute
        // is somehow reached twice, and costs nothing when it is not.
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;

        // A domain reload in the editor keeps timeScale but resets IsPaused, which would leave the
        // two disagreeing and the world stopped with nothing believing it is. Same for the mix,
        // which is worse to leave stuck: a silent game gives no clue why.
        if (Time.timeScale == 0f) Time.timeScale = 1f;
        AudioListener.pause = false;
        IsPaused = false;
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Whatever was paused belonged to the scene that just went away. Leaving a stopped clock
        // across a load is how a game ends up frozen on a fresh scene with no way back.
        Resume();
    }
}
