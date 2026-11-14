#if UNITY_EDITOR
// Editor-only: MonoBehaviours in an Editor folder cannot be added to objects, so this lives in the game
// assembly but never ships in a build.
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using StarterAssets;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

/// Plays the game for listing screenshots: walks the player, turns the camera, presses keys.
///
/// Everything goes through the same doors a player's hands do — StarterAssetsInputs for movement,
/// the Input System keyboard and mouse for every interaction — so what ends up in a frame is the
/// game's own behaviour, not a pose. The one shortcut is the camera: it is turned by setting the
/// yaw/pitch the mouse would have accumulated, because a mouse delta has to be fed every frame and
/// the result is the same angle.
///
/// Driven from outside (execute_script) through the static methods; the MonoBehaviour only exists
/// to have an Update and coroutines inside the running game.
[DefaultExecutionOrder(-500)]
public class SteamGameplayPilot : MonoBehaviour
{
    public static SteamGameplayPilot Instance { get; private set; }

    const string Folder = "Screenshots/SteamSession";
    const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    // ── Setup ────────────────────────────────────────────────────────────────

    public static string Ensure()
    {
        if (!Application.isPlaying) return "not playing";
        if (Instance != null) return "pilot ready";

        var go = new GameObject("~SteamGameplayPilot");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<SteamGameplayPilot>();

        // Queued device events must reach the game even while the editor window, not the Game
        // view, has focus — which is always, when the editor is being driven from outside.
        _prevEditorBehaviour = InputSystem.settings.editorInputBehaviorInPlayMode;
        _prevBackground      = InputSystem.settings.backgroundBehavior;
        InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        EditorApplication.playModeStateChanged += RestoreSettings;
        return "pilot created";
    }

    static InputSettings.EditorInputBehaviorInPlayMode _prevEditorBehaviour;
    static InputSettings.BackgroundBehavior _prevBackground;

    static void RestoreSettings(PlayModeStateChange s)
    {
        if (s != PlayModeStateChange.ExitingPlayMode) return;
        InputSystem.settings.editorInputBehaviorInPlayMode = _prevEditorBehaviour;
        InputSystem.settings.backgroundBehavior = _prevBackground;
        EditorApplication.playModeStateChanged -= RestoreSettings;
    }

    /// The main menu is an overlay over an already running shift; only its document is hidden
    /// (the HUD may live on the same object).
    public static string HideMenu()
    {
        if (MainMenuController.Instance == null) return "no menu";
        var doc = MainMenuController.Instance.GetComponent<UnityEngine.UIElements.UIDocument>();
        if (doc != null) doc.enabled = false; else MainMenuController.Instance.gameObject.SetActive(false);
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        return "menu hidden";
    }

    // ── Player handles ───────────────────────────────────────────────────────

    static GameObject PlayerGO => GameObject.FindGameObjectWithTag("Player");
    static Transform PlayerRoot => PlayerGO != null ? PlayerGO.transform.root : null;
    static StarterAssetsInputs Inputs => PlayerRoot != null ? PlayerRoot.GetComponentInChildren<StarterAssetsInputs>() : null;
    static ThirdPersonController Controller => PlayerRoot != null ? PlayerRoot.GetComponentInChildren<ThirdPersonController>() : null;

    // ── Walking ──────────────────────────────────────────────────────────────

    Vector3? _goal;
    float _stop = 0.4f;
    bool _sprint;
    Vector2 _hold;          // raw camera-space move held (when no goal)
    string _last = "";

    /// Walk (or sprint) to a floor point; stops within `stop` metres.
    public static string GoTo(float x, float z, bool sprint = false, float stop = 0.4f)
    {
        Ensure();
        Instance._goal = new Vector3(x, 0, z);
        Instance._sprint = sprint;
        Instance._stop = stop;
        Instance._hold = Vector2.zero;
        return "going to " + x + "," + z;
    }

    /// Hold a camera-relative stick input (x = right, y = forward); zero releases.
    public static string Hold(float x, float y, bool sprint = false)
    {
        Ensure();
        Instance._goal = null;
        Instance._hold = new Vector2(x, y);
        Instance._sprint = sprint;
        return "holding " + Instance._hold;
    }

    public static string Stop()
    {
        if (Instance == null) return "no pilot";
        Instance._goal = null; Instance._hold = Vector2.zero; Instance._sprint = false;
        return "stopped";
    }

    // -- Tracking a raider ----------------------------------------------------

    ThiefController _chase;     // walk toward him every frame
    ThiefController _face;      // keep the camera on him every frame
    float _facePitch = 8f;
    float _faceOffset;          // yaw offset so he is not dead centre (degrees)

    static ThiefController Pick(string name)
    {
        var root = PlayerRoot;
        var live = ThiefController.All.Where(t => t != null && t.Current != ThiefController.State.Down);
        if (!string.IsNullOrEmpty(name) && name != "nearest")
            return live.FirstOrDefault(t => t.name == name);
        return root == null ? null : live.OrderBy(t => (t.transform.position - root.position).sqrMagnitude).FirstOrDefault();
    }

    /// Run at a raider (nearest if name is empty/"nearest") and stop `stop` metres short.
    public static string Chase(string name, float stop = 2f, bool sprint = true)
    {
        Ensure();
        var t = Pick(name);
        if (t == null) return "no raider";
        Instance._chase = t; Instance._stop = stop; Instance._sprint = sprint; Instance._goal = null; Instance._hold = Vector2.zero;
        return "chasing " + t.name;
    }

    /// Keep the camera turned toward a raider. offset shifts him off-centre (degrees of yaw).
    public static string Face(string name, float pitch = 8f, float offset = 0f)
    {
        Ensure();
        var t = Pick(name);
        if (t == null) return "no raider";
        Instance._face = t; Instance._facePitch = pitch; Instance._faceOffset = offset;
        return "facing " + t.name;
    }

    public static string FaceOff() { if (Instance != null) Instance._face = null; return "free camera"; }

    /// Several frames `interval` seconds apart - real motion is picked, not posed.
    public static string Burst(string name, int count, float interval, int super = 1)
    {
        Ensure();
        Instance.StartCoroutine(Instance.BurstRoutine(name, count, interval, super));
        return "burst " + name + " x" + count;
    }

    IEnumerator BurstRoutine(string name, int count, float interval, int super)
    {
        for (int i = 0; i < count; i++)
        {
            Shot(name + "_" + i.ToString("00"), super);
            yield return new WaitForSecondsRealtime(interval);
        }
    }

    /// Burst that waits until the nearest live raider is within `range` (timeout seconds).
    public static string BurstWhenNear(string name, float range, int count, float interval, float timeout = 40f)
    {
        Ensure();
        Instance.StartCoroutine(Instance.WhenNear(name, range, count, interval, timeout));
        return "armed " + name + " at " + range + "m";
    }

    IEnumerator WhenNear(string name, float range, int count, float interval, float timeout)
    {
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < timeout)
        {
            var near = Pick("nearest");
            if (near != null && Vector3.Distance(near.transform.position, PlayerRoot.position) <= range) break;
            yield return null;
        }
        yield return BurstRoutine(name, count, interval, 1);
    }

    void LateUpdate()
    {
        if (_face == null || PlayerRoot == null || Camera.main == null) return;
        if (_face.Current == ThiefController.State.Down) { }
        var d = _face.transform.position + Vector3.up * 1.2f - Camera.main.transform.position;
        float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + _faceOffset;
        var c = Controller;
        if (c == null) return;
        // Eased like a hand on a mouse, not snapped.
        float cur = (float)c.GetType().GetField("_cinemachineTargetYaw", F).GetValue(c);
        float next = Mathf.LerpAngle(cur, yaw, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
        c.GetType().GetField("_cinemachineTargetYaw", F).SetValue(c, next);
        c.GetType().GetField("_cinemachineTargetPitch", F).SetValue(c, _facePitch);
    }

    void Update()
    {
        ApplyDevices();

        var inputs = Inputs;
        var root = PlayerRoot;
        if (inputs == null || root == null) return;

        if (_chase != null)
        {
            if (_chase.Current == ThiefController.State.Down) _chase = null;
            else _goal = _chase.transform.position;
        }

        Vector2 move = _hold;
        if (_goal.HasValue)
        {
            var to = _goal.Value - root.position; to.y = 0;
            if (to.magnitude <= _stop) { _goal = null; _last = "arrived"; move = Vector2.zero; }
            else
            {
                // Around the racks, not into them: steer at the next corner of a NavMesh path
                // (the raiders' own mesh), re-planned twice a second as the goal moves.
                var steer = NextCorner(root.position, _goal.Value) - root.position; steer.y = 0;
                if (steer.sqrMagnitude < 0.0001f) steer = to;

                // Props the NavMesh does not carve (a parked pallet jack) stop the body dead.
                // Not getting anywhere for half a second means sidestep for a moment.
                if (Time.unscaledTime >= _stuckCheckAt)
                {
                    _stuckCheckAt = Time.unscaledTime + 0.5f;
                    if ((root.position - _stuckFrom).magnitude < 0.15f) _sidestepUntil = Time.unscaledTime + 0.7f;
                    _stuckFrom = root.position;
                }
                if (Time.unscaledTime < _sidestepUntil) steer = Vector3.Cross(Vector3.up, steer.normalized) + steer.normalized * 0.3f;
                var cam = Camera.main.transform;
                var f = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
                var r = Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized;
                var d = steer.normalized;
                move = new Vector2(Vector3.Dot(d, r), Vector3.Dot(d, f)).normalized;
            }
        }
        if (_goal.HasValue || _hold != Vector2.zero || _wasMoving)
        {
            inputs.move = move;
            inputs.sprint = _sprint && move != Vector2.zero;
            _wasMoving = move != Vector2.zero;
        }
    }
    bool _wasMoving;

    // Created in Awake: NavMeshPath throws when built in a field initializer, and that aborts the
    // constructor, leaving every field declared after it null.
    UnityEngine.AI.NavMeshPath _path;
    float _stuckCheckAt, _sidestepUntil;
    Vector3 _stuckFrom;
    void Awake() => _path = new UnityEngine.AI.NavMeshPath();
    float _replanAt;
    int _corner;

    Vector3 NextCorner(Vector3 from, Vector3 goal)
    {
        if (Time.unscaledTime >= _replanAt)
        {
            _replanAt = Time.unscaledTime + 0.5f;
            _corner = 1;
            if (UnityEngine.AI.NavMesh.SamplePosition(from, out var a, 2f, UnityEngine.AI.NavMesh.AllAreas)
                && UnityEngine.AI.NavMesh.SamplePosition(goal, out var b, 3f, UnityEngine.AI.NavMesh.AllAreas))
                UnityEngine.AI.NavMesh.CalculatePath(a.position, b.position, UnityEngine.AI.NavMesh.AllAreas, _path);
            else _path.ClearCorners();
        }
        var c = _path.corners;
        if (c == null || c.Length < 2) return goal;
        while (_corner < c.Length - 1)
        {
            var d = c[_corner] - from; d.y = 0;
            if (d.magnitude > 0.6f) break;
            _corner++;
        }
        return c[Mathf.Min(_corner, c.Length - 1)];
    }

    // ── Camera ───────────────────────────────────────────────────────────────

    /// Point the orbit camera: yaw in world degrees (0 = looking along +Z), pitch down positive.
    public static string View(float yaw, float pitch)
    {
        var c = Controller;
        if (c == null) return "no controller";
        c.GetType().GetField("_cinemachineTargetYaw", F).SetValue(c, yaw);
        c.GetType().GetField("_cinemachineTargetPitch", F).SetValue(c, pitch);
        return "view " + yaw + "/" + pitch;
    }

    /// Turn the camera so the screen centre sits on a world point — what aiming with the mouse
    /// does, and what the interaction raycast needs before E means anything.
    public static string LookAt(float x, float y, float z, float pitchBias = 0f)
    {
        if (Instance != null) Instance._face = null;
        var cam = Camera.main.transform;
        var d = new Vector3(x, y, z) - cam.position;
        float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        float pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg + pitchBias;
        return View(yaw, pitch);
    }

    /// Closed-loop aim: turns the camera a little each frame until the point sits under the
    /// screen centre. The orbit camera moves as it turns, so a one-shot angle is never exact —
    /// and the interaction ray goes through the exact centre.
    public static string Aim(float x, float y, float z, float seconds = 0.8f)
    {
        Ensure();
        Instance._face = null;
        Instance.StartCoroutine(Instance.AimRoutine(new Vector3(x, y, z), seconds));
        return "aiming at " + x + "," + y + "," + z;
    }

    IEnumerator AimRoutine(Vector3 target, float seconds)
    {
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < seconds)
        {
            var cam = Camera.main; var c = Controller;
            if (cam == null || c == null) yield break;
            var vp = cam.WorldToViewportPoint(target);
            float yaw = (float)c.GetType().GetField("_cinemachineTargetYaw", F).GetValue(c);
            float pitch = (float)c.GetType().GetField("_cinemachineTargetPitch", F).GetValue(c);
            float vfov = cam.fieldOfView, hfov = Camera.VerticalToHorizontalFieldOfView(vfov, cam.aspect);
            if (vp.z < 0) yaw += 180f;
            else { yaw += (vp.x - 0.5f) * hfov * 0.5f; pitch -= (vp.y - 0.5f) * vfov * 0.5f; }
            View(yaw, Mathf.Clamp(pitch, -40f, 70f));
            yield return null;
        }
    }

    /// Yaw that looks from the player toward a floor point.
    public static float YawTo(float x, float z)
    {
        var p = PlayerRoot.position;
        return Mathf.Atan2(x - p.x, z - p.z) * Mathf.Rad2Deg;
    }

    // ── Keys and buttons (Input System devices) ──────────────────────────────

    // Queued device events are dropped while the editor window is in the background — which it
    // always is when the editor is driven from outside. So the state is written straight into the
    // game's input buffers instead (InputState.Change), from this object's own Update, i.e. inside
    // the player loop and ahead of the game scripts (execution order -500). Reads then see exactly
    // what a physical key produces: isPressed, and wasPressedThisFrame on the change frame.
    readonly HashSet<Key> _keys = new HashSet<Key>();
    bool _kbDirty;
    int _mouseButtons;      // bit 0 left, bit 1 right
    bool _mouseDirty;

    void PushKeyboard() => _kbDirty = true;

    void ApplyDevices()
    {
        if (_kbDirty && Keyboard.current != null)
        {
            InputState.Change(Keyboard.current, new KeyboardState(_keys.ToArray()));
            _kbDirty = false;
        }
        if (_mouseDirty && Mouse.current != null)
        {
            var st = new MouseState { position = new Vector2(Screen.width / 2f, Screen.height / 2f) };
            if ((_mouseButtons & 1) != 0) st = st.WithButton(MouseButton.Left, true);
            if ((_mouseButtons & 2) != 0) st = st.WithButton(MouseButton.Right, true);
            InputState.Change(Mouse.current, st);
            _mouseDirty = false;
        }
    }

    /// Press a key for `hold` seconds (0 = a single-frame tap of ~2 frames).
    public static string Key(string key, float hold = 0f)
    {
        Ensure();
        if (!System.Enum.TryParse<Key>(key, true, out var k)) return "unknown key " + key;
        Instance.StartCoroutine(Instance.PressKey(k, hold));
        return "key " + k + " for " + hold + "s";
    }

    IEnumerator PressKey(Key k, float hold)
    {
        _keys.Add(k); PushKeyboard();
        if (hold > 0f) yield return new WaitForSeconds(hold); else { yield return null; yield return null; }
        _keys.Remove(k); PushKeyboard();
    }

    /// Hold or release a key with no timer (for long holds across calls).
    public static string KeyDown(string key, bool down)
    {
        Ensure();
        if (!System.Enum.TryParse<Key>(key, true, out var k)) return "unknown key " + key;
        if (down) Instance._keys.Add(k); else Instance._keys.Remove(k);
        Instance.PushKeyboard();
        return k + (down ? " down" : " up");
    }

    /// Mouse button: 0 left, 1 right. hold 0 = tap.
    public static string Click(int button = 0, float hold = 0f)
    {
        Ensure();
        Instance.StartCoroutine(Instance.PressMouse(button, hold));
        return "mouse " + button + " for " + hold + "s";
    }

    IEnumerator PressMouse(int button, float hold)
    {
        int bit = button == 0 ? 1 : 2;
        _mouseButtons |= bit; _mouseDirty = true;
        if (hold > 0f) yield return new WaitForSeconds(hold); else { yield return null; yield return null; }
        _mouseButtons &= ~bit; _mouseDirty = true;
    }

    // ── Capture ──────────────────────────────────────────────────────────────

    /// Game view frame with the HUD. super = supersampling factor.
    public static string Shot(string name, int super = 1)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, name + ".png");
        if (File.Exists(path)) File.Delete(path);
        ScreenCapture.CaptureScreenshot(path, super);
        return "queued " + path;
    }

    // ── Game hooks ───────────────────────────────────────────────────────────

    /// Starts a game event by its registered name ("Thief", "WetFloor", "GateBreach"...).
    public static string Event(string name)
    {
        var gem = GameEventManager.Instance;
        return gem == null ? "no event manager" : name + " started=" + gem.ForceStartEvent(name);
    }

    /// Puts the player on a mark, as if he had walked there, and tells Cinemachine he moved so the
    /// camera is not dragged through the racks after him. Only for getting to a set — what happens
    /// once he is there is played.
    public static string Teleport(float x, float z, float yaw)
    {
        var p = PlayerRoot;
        if (p == null) return "no player";
        var from = p.position;
        // Onto the real floor, or not at all: some of the building is floor mesh with no collider
        // (the dock strips by the gates), and a player put there falls out of the world.
        if (!UnityEngine.AI.NavMesh.SamplePosition(new Vector3(x, 0f, z), out var hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas)
            || !Physics.Raycast(hit.position + Vector3.up * 1.5f, Vector3.down, out var ground, 3f, ~0, QueryTriggerInteraction.Ignore))
            return "no walkable floor at " + x + "," + z;
        var to = new Vector3(hit.position.x, ground.point.y + 0.02f, hit.position.z);
        var cc = p.GetComponentInChildren<CharacterController>();
        if (cc) cc.enabled = false;
        p.SetPositionAndRotation(to, Quaternion.Euler(0, yaw, 0));
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
        foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            var m = mb.GetType().GetMethod("OnTargetObjectWarped");
            var follow = mb.GetType().GetProperty("Follow")?.GetValue(mb) as Transform;
            if (m != null && follow != null && m.GetParameters().Length == 2) m.Invoke(mb, new object[] { follow, to - from });
        }
        Stop();
        View(yaw, 8f);
        return "at " + to;
    }

    /// Listing shots are taken without the HUD (the store page shows the game, not its overlay).
    /// Every UI Toolkit document and screen-space canvas is hidden; false puts them back.
    public static string HideHud(bool hide = true)
    {
        int n = 0;
        foreach (var doc in FindObjectsByType<UnityEngine.UIElements.UIDocument>(FindObjectsSortMode.None))
        {
            if (doc.rootVisualElement == null) continue;
            doc.rootVisualElement.style.display = hide ? UnityEngine.UIElements.DisplayStyle.None : UnityEngine.UIElements.DisplayStyle.Flex;
            n++;
        }
        foreach (var cv in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            if (cv.renderMode != RenderMode.WorldSpace) { cv.enabled = !hide; n++; }
        _hudHidden = hide;
        return (hide ? "hud hidden " : "hud shown ") + n;
    }
    static bool _hudHidden;

    /// Presses Single Player the way the menu button does (the pilot survives the reload).
    public static string Solo()
    {
        var m = MainMenuController.Instance;
        if (m == null) return "no menu";
        m.GetType().GetMethod("StartSingleplayer", F).Invoke(m, null);
        return "single player";
    }

    /// Listing hygiene: the tutorial skipped the way its own button does, the debug FPS label hidden.
    public static string Prep()
    {
        var sb = new StringBuilder();
        var tut = FindFirstObjectByType<TutorialManager>();
        if (tut != null) { tut.Skip(); sb.Append("tutorial skipped; "); }
        var hud = FindFirstObjectByType<HUDController>();
        var lbl = hud?.GetType().GetField("_fpsCounter", F)?.GetValue(hud) as UnityEngine.UIElements.Label;
        if (lbl != null) { lbl.style.display = UnityEngine.UIElements.DisplayStyle.None; sb.Append("fps hidden; "); }
        Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        return sb.ToString();
    }

    // ── Scenario runner ──────────────────────────────────────────────────────

    /// Steps run inside the game with no round-trip delay. JSON array of arrays:
    ///   ["wait", seconds]
    ///   ["waitNear", range, timeoutSeconds]          nearest live raider within range
    ///   ["waitArrive", timeoutSeconds]               pilot reached its goal
    ///   ["Method", args...]                          any public static method of this class
    public static string Run(string json)
    {
        Ensure();
        var steps = Newtonsoft.Json.Linq.JArray.Parse(json);
        Instance.StartCoroutine(Instance.RunSteps(steps));
        return "scenario of " + steps.Count + " steps";
    }

    public static string Log = "";
    bool _slippedOnce;
    public static string ResetSlip() { Ensure(); Instance._slippedOnce = false; return "slip flag cleared"; }

    IEnumerator RunSteps(Newtonsoft.Json.Linq.JArray steps)
    {
        foreach (Newtonsoft.Json.Linq.JArray s in steps)
        {
            string op = (string)s[0];
            float t0 = Time.realtimeSinceStartup;
            switch (op)
            {
                case "wait":
                    yield return new WaitForSecondsRealtime((float)s[1]);
                    break;
                case "waitNear":
                    while (Time.realtimeSinceStartup - t0 < (float)s[2])
                    {
                        var n = Pick("nearest");
                        if (n != null && PlayerRoot != null && Vector3.Distance(n.transform.position, PlayerRoot.position) <= (float)s[1]) break;
                        yield return null;
                    }
                    break;
                case "waitSlip":
                    // The game's own slip roll fired: the Tripping state is starting.
                    while (Time.realtimeSinceStartup - t0 < (float)s[1])
                    {
                        var an = PlayerRoot != null ? PlayerRoot.GetComponentsInChildren<Animator>().FirstOrDefault(x => x.isHuman) : null;
                        if (an != null && an.GetBool("IsSlipping")) break;
                        yield return null;
                    }
                    break;
                case "burstOnSlip":
                    // ["burstOnSlip", name, count, interval, timeoutSeconds] — waits for the game's own
                    // slip roll, then shoots; skipped if nothing happened in time.
                    {
                        bool slipped = false;
                        while (Time.realtimeSinceStartup - t0 < (float)s[4])
                        {
                            var an = PlayerRoot != null ? PlayerRoot.GetComponentsInChildren<Animator>().FirstOrDefault(x => x.isHuman) : null;
                            if (an != null && an.GetBool("IsSlipping")) { slipped = true; break; }
                            yield return null;
                        }
                        if (slipped) StartCoroutine(BurstRoutine((string)s[1], (int)s[2], (float)s[3], 1));
                        Log += "burstOnSlip " + (slipped ? "fired" : "timed out") + "\n";
                        if (slipped) _slippedOnce = true;
                    }
                    break;
                case "untilSlipped":
                    // Everything after this runs only if no slip has been caught yet.
                    if (_slippedOnce) { Log += "already slipped, stopping\n"; Log += "scenario finished\n"; yield break; }
                    break;
                case "waitArrive":
                    while (Time.realtimeSinceStartup - t0 < (float)s[1] && _goal.HasValue) yield return null;
                    break;
                default:
                    var m = typeof(SteamGameplayPilot).GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(x => x.Name == op);
                    if (m == null) { Log += "unknown " + op + "\n"; break; }
                    var ps = m.GetParameters();
                    var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                        args[i] = i + 1 < s.Count ? s[i + 1].ToObject(ps[i].ParameterType) : ps[i].DefaultValue;
                    try { Log += op + ": " + m.Invoke(null, args) + "\n"; }
                    catch (System.Exception e) { Log += op + " FAILED " + e.InnerException?.Message + "\n"; }
                    break;
            }
            Log += "[" + Time.realtimeSinceStartup.ToString("F1") + "] " + op + " done\n";
        }
        Log += "scenario finished\n";
    }

    // ── Report ───────────────────────────────────────────────────────────────

    public static string Status()
    {
        var sb = new StringBuilder();
        var root = PlayerRoot;
        sb.AppendLine("playing=" + Application.isPlaying + " ts=" + Time.timeScale + " screen=" + Screen.width + "x" + Screen.height);
        if (root == null) return sb.Append("no player").ToString();
        var c = Controller;
        sb.AppendLine("player " + root.position.ToString("F2") + " fwd=" + root.forward.ToString("F2")
            + (c != null ? " yaw=" + ((float)c.GetType().GetField("_cinemachineTargetYaw", F).GetValue(c)).ToString("F0")
                         + " pitch=" + ((float)c.GetType().GetField("_cinemachineTargetPitch", F).GetValue(c)).ToString("F0") : ""));
        if (Instance != null) sb.AppendLine("pilot goal=" + Instance._goal + " hold=" + Instance._hold + " last=" + Instance._last + " keys=" + string.Join(",", Instance._keys));
        if (Camera.main != null) sb.AppendLine("cam " + Camera.main.transform.position.ToString("F2"));
        if (MeleeCombat.Instance != null) sb.AppendLine("weapon " + MeleeCombat.Instance.CurrentWeapon);
        foreach (var t in ThiefController.All.Where(t => t != null).OrderBy(t => (t.transform.position - root.position).sqrMagnitude).Take(8))
            sb.AppendLine("  raider " + t.name + " " + t.Current + " d=" + Vector3.Distance(t.transform.position, root.position).ToString("F1") + " at " + t.transform.position.ToString("F1"));
        if (Log.Length > 0) { sb.AppendLine("log:\n" + Log); Log = ""; }
        return sb.ToString();
    }
}

#endif

