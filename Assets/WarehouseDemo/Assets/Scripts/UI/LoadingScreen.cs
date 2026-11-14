using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

/// The screen that covers the gap between pressing a button in the menu and standing in the
/// warehouse.
///
/// It exists because that gap is not short. Singleplayer reloads the whole scene, and a client
/// joining a room has NGO reload it for him a second time — both of which used to happen behind a
/// frozen still of the menu, with no way to tell a slow load from a hung game.
///
/// Two things shape the design:
///
/// 1. It lives in DontDestroyOnLoad and builds its own UI from Resources, with no scene wiring at
///    all. It has to: every UIDocument in the scene — the menu included — is destroyed and rebuilt
///    by the very load it is covering, so anything authored in the scene would blink out halfway
///    through. That is also why the coroutines that decide when to hide run on this object rather
///    than on the caller: MainMenuController and LobbyController do not survive the reload either.
///
/// 2. Its panel sorts above the HUD (100 against the HUD's 10), so it also swallows clicks while
///    it is up. On hide the UIDocument is disabled rather than destroyed, or an invisible panel
///    would keep eating input for the rest of the session.
public class LoadingScreen : MonoBehaviour
{
    const string UxmlPath  = "UI/Loading";
    const string PanelPath = "UI/LoadingPanelSettings";

    /// Shortest time the screen stays up. Without it a warm load flashes the overlay for two
    /// frames, which reads as a glitch rather than as loading.
    const float MinVisible = 0.7f;

    const float FadeOut = 0.3f;

    /// How long the connect overlay waits on the network before giving up and getting out of the
    /// way, so a refused or dropped connection cannot leave the player staring at a bar forever.
    const float ConnectTimeout = 20f;

    /// Which of the two layouts to show. Splash is the game opening — nothing has happened yet,
    /// so it is the icon and the name and little else. Full is every load from inside the game,
    /// where the player is waiting on something and there is room to say what.
    public enum Look { Full, Splash }

    const string SplashClass = "loading-root--splash";

    // What the player reads while he waits. Every one of these is a rule of this game that is not
    // visible by looking at it — the point is that the wait teaches something.
    static readonly string[] Tips =
    {
        "Your hands are the bottleneck. You cannot swing a weapon while you are carrying a box.",
        "Hold E to wind a throw up and let go to throw. The last part of the wind-up is worth most of the distance.",
        "Take the order from the board first. Nothing you assemble counts against an order you never accepted.",
        "A ladder can be carried away while someone is still standing on it. Whether you should is between you and your teammate.",
        "Wet floor does not care that you are walking. Moving at all can put you down; running it with a box in your arms is twice as likely to.",
        "Raiders go for the pallets, not for you. A bat left by the door is worth the detour.",
        "ESC opens the menu and nothing else. Whatever is in your hands stays there.",
        "The forklift beats legs for anything heavier than a crate — and it beats walking into a shelf, too.",
    };

    static LoadingScreen _instance;

    UIDocument     _doc;
    VisualElement  _root;
    VisualElement  _fill;
    Label          _status;
    Label          _tip;

    /// What the screen has been asked to say, kept apart from the label that says it.
    ///
    /// Binding can happen after the words arrive — see EnsureBound — and a label found a frame too
    /// late has to be told what it missed rather than come up blank.
    string   _statusText = "";
    string   _tipText    = "";
    bool     _splash;

    float    _shownAt;
    bool     _sweep;          // no real number to show — animate instead of sitting at zero
    float    _progress;       // what the load actually reports
    float    _displayed;      // what the bar is drawn at — see the note in Update
    bool     _sceneCreep;     // scene streaming can report zero for a long time; lobby stages do not
    Coroutine _job;

    public static bool IsVisible => _instance != null && _instance._doc != null && _instance._doc.enabled;

    // ── Public API ────────────────────────────────────────────────────────────

    /// Puts the screen up immediately. Progress starts indeterminate; SetProgress switches it to
    /// a real bar the moment there is a number worth showing.
    public static void Show(string status = null, Look look = Look.Full)
    {
        var ls = GetOrCreate();
        if (ls == null) return;

        ls._doc.enabled = true;
        ls.EnsureBound();

        ls._shownAt   = Time.unscaledTime;
        ls._sweep     = true;
        ls._progress  = 0f;
        ls._displayed = 0f;
        ls._sceneCreep = false;

        // The look is a class on the root rather than two prefabs or two documents, so both
        // variants stay one layout that cannot drift apart. Remembered as well as applied, because
        // the root may not exist yet — see EnsureBound, which applies it again once it does.
        ls._splash = look == Look.Splash;

        ls._statusText = status ?? "";
        ls._tipText    = Tips[Random.Range(0, Tips.Length)];

        ls.Write();
    }

    public static void SetStatus(string status)
    {
        if (_instance == null) return;

        _instance._statusText = status ?? "";
        _instance.EnsureBound();
        _instance.Write();
    }

    /// 0..1. Turns the sweep off — call it only when the number means something.
    public static void SetProgress(float t)
    {
        if (_instance == null) return;
        _instance._sweep    = false;
        _instance._progress = Mathf.Clamp01(t);
    }

    /// Fades out, honouring the minimum visible time. Safe to call when nothing is showing.
    public static void Hide()
    {
        if (_instance == null || !IsVisible) return;
        _instance.Run(_instance.FadeAndDisable());
    }

    // ── Singleplayer ──────────────────────────────────────────────────────────

    /// Loads a scene asynchronously behind the screen, then holds it until the game on the other
    /// side is actually running.
    ///
    /// Asynchronously is the whole point: SceneManager.LoadScene blocks the main thread, so the
    /// overlay would never draw a single frame of what it is covering.
    /// <param name="waitForGameStart">
    /// True when the scene being loaded is a shift starting — the screen then stays up until
    /// GameManager reports it running. False when loading back into the menu, where nothing ever
    /// starts and waiting would just stall the screen until its own deadline.
    /// </param>
    public static void LoadScene(string sceneName, string status = null,
                                 bool waitForGameStart = true, Look look = Look.Full)
    {
        Show(status, look);
        _instance._sceneCreep = true;
        _instance.Run(_instance.LoadRoutine(sceneName, waitForGameStart));
    }

    IEnumerator LoadRoutine(string sceneName, bool waitForGameStart)
    {
        DropUnstartedNetworkManager();

        // Timed out loud, in phases. "The first launch of a new build hangs and Windows offers to
        // kill it" is a report with three completely different possible causes — streaming the
        // scene off a cold disk, the driver compiling shaders it has never seen, and the
        // activation frame where Unity builds static batches and runs every Awake in the scene —
        // and they are told apart by which phase ate the seconds, not by looking at the window.
        float t0 = Time.realtimeSinceStartup;

        var op = SceneManager.LoadSceneAsync(sceneName);
        op.allowSceneActivation = false;

        // Unity stalls the operation at 0.9 and waits for permission to swap scenes, so that is
        // what the bar has to treat as full.
        while (op.progress < 0.9f)
        {
            SetProgress(op.progress / 0.9f);
            yield return null;
        }

        float tStreamed = Time.realtimeSinceStartup;

        SetProgress(0.9f);
        SetStatus("Opening the depot...");
        op.allowSceneActivation = true;
        yield return op;

        float tActivated = Time.realtimeSinceStartup;

        // A standalone build can reach the first gameplay Update much faster than the Editor.
        // Hold offline movement/gravity until the physics world confirms a real surface below
        // the scene player. This is deliberately skipped for menu loads and live NGO sessions.
        var activeNetwork = NetworkManager.Singleton;
        if (waitForGameStart && (activeNetwork == null || !activeNetwork.IsListening))
            yield return SoloSpawnDiagnostics.WaitForGround();

        // The scene is loaded but not yet alive: every Awake and Start in it runs on the frames
        // after this, and that is the most expensive hitch of the whole sequence. Stay up for it.
        if (waitForGameStart) yield return WaitForGameRunning();
        else for (int i = 0; i < 3; i++) yield return new WaitForEndOfFrame();

        Debug.Log($"[LoadingScreen] '{sceneName}' loaded: streamed {tStreamed - t0:0.0}s, " +
                  $"activated {tActivated - tStreamed:0.0}s, first frames " +
                  $"{Time.realtimeSinceStartup - tActivated:0.0}s, total " +
                  $"{Time.realtimeSinceStartup - t0:0.0}s");

        SetProgress(1f);

        yield return FadeAndDisable();
    }

    /// Gets a NetworkManager that was never started out of the way before the scene goes.
    ///
    /// It sits in DontDestroyOnLoad, so it outlives the unload — and every one of the scene's
    /// several thousand NetworkObjects walks its own OnDestroy against it on the way out, which in
    /// this version of NGO throws on each one. Roughly a hundred NullReferenceExceptions per
    /// reload, all of them noise: singleplayer never starts networking, so there is no session
    /// here to preserve, and the scene being loaded brings its own NetworkManager with it.
    ///
    /// The IsListening guard is the whole safety of this: a live host or client is never touched,
    /// so quitting a real co-op session to the menu still shuts down through NGO's own path.
    static void DropUnstartedNetworkManager()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && !nm.IsListening) Destroy(nm.gameObject);
    }

    /// GameManager.StartGame is what actually begins the shift — the HUD intro, the order timer
    /// and everything else wait on it, so it is the honest moment to call the game loaded.
    IEnumerator WaitForGameRunning()
    {
        float deadline = Time.unscaledTime + 10f;

        while (Time.unscaledTime < deadline &&
               (GameManager.Instance == null || !GameManager.Instance.GameStarted))
            yield return null;

        // A few frames past that: the first rendered frame of a fresh scene is where shader
        // compilation lands, and it is better spent behind the overlay than in front of it.
        for (int i = 0; i < 3; i++) yield return new WaitForEndOfFrame();
    }

    // ── Multiplayer ───────────────────────────────────────────────────────────

    /// Holds the screen until this client is synchronised with the host.
    ///
    /// IsConnectedClient is the signal rather than a scene event, because it turns true only after
    /// NGO has finished sending the client through its own scene load — which is the part of
    /// joining that actually takes time, and the part that destroys whoever asked for this screen.
    public static void HoldUntilConnected(string status = null)
    {
        // Joining starts this overlay before authentication. Keep that same progress session alive
        // when NGO takes over; calling Show again here used to reset a partly-filled bar back to
        // an indeterminate sweep exactly when the lobby hand-off began.
        if (!IsVisible)
        {
            Show(status);
        }
        else
        {
            SetStatus(status);
            SetProgress(Mathf.Max(_instance._progress, 0.65f));
        }
        _instance.Run(_instance.ConnectRoutine());
    }

    IEnumerator ConnectRoutine()
    {
        float deadline = Time.unscaledTime + ConnectTimeout;
        float started  = Time.unscaledTime;
        var   nm       = NetworkManager.Singleton;

        while (Time.unscaledTime < deadline)
        {
            nm = NetworkManager.Singleton;   // re-read: the scene reload can hand us a new one
            if (nm != null && (nm.IsConnectedClient || nm.IsHost)) break;
            if (nm != null && !nm.IsListening && Time.unscaledTime > _shownAt + 1f) break;  // refused

            // NGO exposes completion but no intermediate percentage. Advance only through the
            // reserved synchronisation portion, stopping at 95% until the connection is real.
            float syncElapsed = Time.unscaledTime - started;
            SetProgress(Mathf.Lerp(0.65f, 0.95f,
                Mathf.Clamp01(syncElapsed / ConnectTimeout)));
            yield return null;
        }

        SetProgress(1f);
        for (int i = 0; i < 2; i++) yield return new WaitForEndOfFrame();
        yield return FadeAndDisable();
    }

    /// The lobby-to-warehouse handoff. There is no scene load on this path — the room reuses the
    /// scene it is already in — but the session reset and the first frame of spawned players are
    /// still a visible hitch, and hiding it costs nothing.
    public static void HoldForFrames(string status = null, int frames = 4)
    {
        Show(status);
        _instance.Run(_instance.FramesRoutine(frames));
    }

    IEnumerator FramesRoutine(int frames)
    {
        for (int i = 0; i < frames; i++) yield return new WaitForEndOfFrame();
        yield return FadeAndDisable();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    void Run(IEnumerator routine)
    {
        if (_job != null) StopCoroutine(_job);
        _job = StartCoroutine(routine);
    }

    IEnumerator FadeAndDisable()
    {
        float wait = MinVisible - (Time.unscaledTime - _shownAt);
        if (wait > 0f) yield return new WaitForSecondsRealtime(wait);

        // A determinate load should visibly arrive before the overlay fades. Without this frame,
        // callers that set 100% and hide in the same continuation only ever showed the previous
        // stage, making successful lobby entry look as though loading stopped early.
        if (!_sweep && _fill != null)
        {
            _progress  = 1f;
            _displayed = 1f;
            _fill.style.width = new StyleLength(Length.Percent(100f));
            _fill.style.left  = new StyleLength(Length.Percent(0f));
            yield return null;
        }

        for (float t = 0f; t < FadeOut; t += Time.unscaledDeltaTime)
        {
            if (_root != null) _root.style.opacity = 1f - t / FadeOut;
            yield return null;
        }

        if (_root != null) _root.style.opacity = 0f;
        _doc.enabled = false;   // disabled, not destroyed — see the note at the top
        _job = null;
    }

    void Update()
    {
        // The one place that runs every frame, so it is also the one that recovers a screen put up
        // before its document had a tree — see EnsureBound.
        EnsureBound();

        if (_doc == null || !_doc.enabled || _fill == null) return;

        // Everything here runs on unscaled time: a loading screen must not stop moving because
        // the game on the other side of the load has not started its clock yet.
        float elapsed = Time.unscaledTime - _shownAt;
        float width, left = 0f;

        if (_sweep)
        {
            // Nothing to report a number — a band travelling back and forth says "working" without
            // claiming to know how much is left.
            width = Mathf.PingPong(Time.unscaledTime * 0.55f, 0.75f) + 0.08f;
            left  = Mathf.PingPong(Time.unscaledTime * 0.35f, 0.25f);
        }
        else
        {
            // Stage targets and scene progress always fill left-to-right. Smooth the displayed
            // edge toward the newest target, never backwards; each opaque lobby operation owns a
            // portion of the range instead of masquerading as an indeterminate travelling band.
            float target = Mathf.Max(_displayed, _progress);
            if (_sceneCreep)
            {
                float creep = 0.8f * (1f - Mathf.Exp(-elapsed / 2.5f));
                target = Mathf.Max(target, creep);
            }
            _displayed = Mathf.MoveTowards(_displayed, target,
                                           Time.unscaledDeltaTime * 0.65f);
            width       = _displayed;
        }

        _fill.style.width = new StyleLength(Length.Percent(width * 100f));
        _fill.style.left  = new StyleLength(Length.Percent(left * 100f));
    }

    static LoadingScreen GetOrCreate()
    {
        if (_instance != null) return _instance;

        // An editor-only orphan: recompiling a script during play reloads the domain, which
        // clears the static and every non-serialised field on this component while leaving the
        // GameObject itself alive in DontDestroyOnLoad. Rebinding it beats stacking a second
        // overlay on top of the stuck one.
        var orphan = FindAnyObjectByType<LoadingScreen>(FindObjectsInactive.Include);
        if (orphan != null)
        {
            _instance = orphan;
            orphan.Bind(orphan.GetComponent<UIDocument>());
            return _instance;
        }

        var uxml  = Resources.Load<VisualTreeAsset>(UxmlPath);
        var panel = Resources.Load<PanelSettings>(PanelPath);

        if (uxml == null || panel == null)
        {
            Debug.LogWarning($"[LoadingScreen] Missing Resources/{UxmlPath} or Resources/{PanelPath} " +
                             $"— loading will happen without a screen over it.");
            return null;
        }

        var go = new GameObject("LoadingScreen");
        DontDestroyOnLoad(go);

        // The document is furnished before anything is bound to it. AddComponent runs OnEnable
        // there and then, and a UIDocument enabled with no panel and no tree builds nothing —
        // so the old order (component, then assets, then Bind) queried a tree that did not exist
        // yet.
        var doc = go.AddComponent<UIDocument>();
        doc.panelSettings   = panel;
        doc.visualTreeAsset = uxml;

        _instance = go.AddComponent<LoadingScreen>();
        _instance.Bind(doc);
        return _instance;
    }

    void Bind(UIDocument doc)
    {
        if (doc == null) return;

        _doc    = doc;
        _root   = doc.rootVisualElement;
        _fill   = _root?.Q<VisualElement>("bar-fill");
        _status = _root?.Q<Label>("loading-status");
        _tip    = _root?.Q<Label>("loading-tip");

        // One line per tree, not per bind. A screen that comes up with no words on it has three
        // causes that look identical from the outside — a label never found, a label found and
        // handed an empty string, and a label found in a tree that is no longer on screen — and
        // the number of trees is precisely what tells the third from the other two.
        if (_root != null && !ReferenceEquals(_root, _reported))
        {
            _reported = _root;

            Debug.Log($"[LoadingScreen] bound to tree #{_root.GetHashCode():X}: " +
                      $"fill={_fill != null} status={_status != null} tip={_tip != null} " +
                      $"splash={_splash} tipText='{_tipText}'");
        }
    }

    /// The tree the line above has already been written for.
    VisualElement _reported;


    /// Binds again whenever what we are holding is no longer what the document is showing.
    ///
    /// A UIDocument builds its rootVisualElement when it is enabled, and the boot scene asks for
    /// this screen from BootLoader.Start — the first frame of the process, before the document has
    /// had one. Bound once and never again, every label stayed null for the whole session and the
    /// screen came up correct in every respect except that it had no words on it.
    ///
    /// **And nullness is not the question.** A document rebuilds its tree — on enable, and when its
    /// asset or panel is assigned, which GetOrCreate does a line before binding — and the elements
    /// from the previous build stay perfectly valid objects that are simply no longer attached to
    /// anything. A guard that only asks "did I find labels" is satisfied by those, returns early
    /// for the rest of the session, and every word written lands in a tree nobody is drawing. On
    /// screen that is the raw UXML: the heading still reading LOADING because that is what it says
    /// in the file, an empty status, an empty tip, a bar that never fills, and no splash class —
    /// which is exactly the screen this comment was written next to.
    ///
    /// So the test is identity, not nullness: the root we hold must be the root the document is
    /// showing right now. Cheap to call — one reference compare once the two agree.
    void EnsureBound()
    {
        if (_doc == null) return;

        if (ReferenceEquals(_root, _doc.rootVisualElement)
            && _root != null && _fill != null && _status != null && _tip != null) return;

        Bind(_doc);
        Write();
    }

    /// Puts the words the screen has been given onto whatever labels it now has. Guarded rather
    /// than assumed: a UXML that fails to import hands back an empty tree, and a screen going blank
    /// is a great deal easier to diagnose than an exception per frame.
    void Write()
    {
        if (_root != null)
        {
            _root.style.opacity = 1f;
            _root.EnableInClassList(SplashClass, _splash);
        }

        if (_status != null) _status.text = _statusText;
        if (_tip    != null) _tip.text    = _tipText;
    }

    void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // Statics outlive a scene reload but not a play session; domain reload being off means this
    // has to be cleared by hand or the second run finds a reference to a destroyed object.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => _instance = null;
}
