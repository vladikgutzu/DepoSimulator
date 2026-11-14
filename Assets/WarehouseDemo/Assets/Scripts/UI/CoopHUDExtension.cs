using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

/// Adds co-op overlay to the existing HUD (player roster, voice indicators, minimap gating).
/// Orders are taken at the Order Board by everyone, the Dispatcher included — there is no
/// separate dispatcher console. Attach alongside HUDController on the same GO.
/// Activates automatically when NetworkManager detects more than 1 player.
[RequireComponent(typeof(HUDController))]
public class CoopHUDExtension : MonoBehaviour
{
    VisualElement _hudRoot;
    VisualElement _rosterPanel;
    VisualElement _rosterList;
    VisualElement _pttHint;
    VisualElement _promptWrapper;
    VisualElement _minimapPanel;

    // ── Role display maps ─────────────────────────────────────────────────────
    static readonly Dictionary<PlayerRoleType, string> RoleLabel = new()
    {
        { PlayerRoleType.Picker,           "PICKER"     },
        { PlayerRoleType.Assembler,        "ASSEMBLER"  },
        { PlayerRoleType.ForkLiftOperator, "FORKLIFT"   },
        { PlayerRoleType.Dispatcher,       "DISPATCHER" },
        { PlayerRoleType.None,             "NO ROLE"    },
    };

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    void Start() => StartCoroutine(InitWhenReady());

    IEnumerator InitWhenReady()
    {
        var doc = GetComponent<UnityEngine.UIElements.UIDocument>();
        while (doc == null || doc.rootVisualElement == null) yield return null;

        _hudRoot = doc.rootVisualElement.Q<VisualElement>("hud-root");
        if (_hudRoot == null) yield break;

        _minimapPanel = doc.rootVisualElement.Q<VisualElement>("minimap-panel");
        _promptWrapper = doc.rootVisualElement.Q<VisualElement>(className: "prompt-wrapper");

        // Show minimap immediately so single-player sees it without waiting for netcode.
        RebuildRoster();

        // Coop panels only make sense when NetworkManager is running.
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
            yield break;

        BuildRosterPanel();
        BuildPttHint();

        NetworkPlayer.OnRosterChanged += RebuildRoster;
        if (VivoxManager.Instance != null)
            VivoxManager.Instance.OnSpeakingChanged += OnSpeakingChanged;

        RebuildRoster();
    }

    void OnDestroy()
    {
        NetworkPlayer.OnRosterChanged -= RebuildRoster;
        if (VivoxManager.Instance != null)
            VivoxManager.Instance.OnSpeakingChanged -= OnSpeakingChanged;
    }

    // ── Build roster panel ────────────────────────────────────────────────────
    void BuildRosterPanel()
    {
        _rosterPanel = new VisualElement();
        _rosterPanel.style.position  = Position.Absolute;
        _rosterPanel.style.top       = new StyleLength(52f);
        _rosterPanel.style.right     = new StyleLength(220f);
        _rosterPanel.style.width     = new StyleLength(175f);
        _rosterPanel.style.backgroundColor = Bg(0.078f, 0.082f, 0.09f, 0.93f);
        SetBorder(_rosterPanel, 1f, new Color(0.19f, 0.19f, 0.21f));
        SetRadius(_rosterPanel, 3f);
        _rosterPanel.style.overflow = Overflow.Hidden;
        _rosterPanel.AddToClassList("hidden");

        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems    = Align.Center;
        header.style.height        = new StyleLength(24f);
        header.style.backgroundColor = Bg(0.10f, 0.10f, 0.12f, 0.98f);
        header.style.borderBottomWidth = 1f;
        header.style.borderBottomColor = Clr(0.19f, 0.19f, 0.22f);
        header.style.paddingLeft   = new StyleLength(7f);

        header.Add(AccentBar(new Color(0.96f, 0.65f, 0.14f)));
        header.Add(Lbl("TEAM", 10f, new Color(0.96f, 0.65f, 0.14f), bold: true));

        _rosterList = new VisualElement();
        _rosterList.style.flexDirection  = FlexDirection.Column;
        _rosterList.style.paddingTop     = new StyleLength(6f);
        _rosterList.style.paddingBottom  = new StyleLength(6f);
        _rosterList.style.paddingLeft    = new StyleLength(6f);
        _rosterList.style.paddingRight   = new StyleLength(6f);

        _rosterPanel.Add(header);
        _rosterPanel.Add(_rosterList);
        _hudRoot.Add(_rosterPanel);
    }

    // ── Build PTT hint ────────────────────────────────────────────────────────
    void BuildPttHint()
    {
        _pttHint = new VisualElement();
        _pttHint.style.alignItems  = Align.Center;
        _pttHint.style.marginTop   = new StyleLength(4f);
        _pttHint.pickingMode       = PickingMode.Ignore;
        _pttHint.AddToClassList("hidden");

        var pill = new VisualElement();
        pill.style.flexDirection   = FlexDirection.Row;
        pill.style.alignItems      = Align.Center;
        pill.style.backgroundColor = Bg(0.07f, 0.07f, 0.09f, 0.85f);
        SetBorder(pill, 1f, new Color(0.19f, 0.19f, 0.22f));
        SetRadius(pill, 12f);
        pill.style.paddingTop    = new StyleLength(4f);
        pill.style.paddingBottom = new StyleLength(4f);
        pill.style.paddingLeft   = new StyleLength(10f);
        pill.style.paddingRight  = new StyleLength(10f);
        pill.pickingMode = PickingMode.Ignore;

        var dot = new VisualElement();
        dot.name = "ptt-dot";
        dot.style.width       = new StyleLength(7f);
        dot.style.height      = new StyleLength(7f);
        dot.style.marginRight = new StyleLength(6f);
        SetRadius(dot, 4f);
        dot.style.backgroundColor = Bg(0.47f, 0.47f, 0.50f);
        dot.style.flexShrink      = 0;
        dot.pickingMode           = PickingMode.Ignore;

        var txt = Lbl("[V] Radio", 11f, new Color(0.55f, 0.55f, 0.60f));
        txt.pickingMode = PickingMode.Ignore;

        pill.Add(dot); pill.Add(txt);
        _pttHint.Add(pill);
        (_promptWrapper ?? _hudRoot).Add(_pttHint);
    }

    // ── Roster rebuild ────────────────────────────────────────────────────────
    void RebuildRoster()
    {
        if (_rosterList == null) return;
        _rosterList.Clear();

        // Gated on actually being in a networked session (host/client), not on how many
        // players have joined so far -- a lone host/client should still see the roster and the
        // PTT hint immediately.
        bool multi = IsNetworkedSession();

        if (_rosterPanel != null)
        {
            if (multi) _rosterPanel.RemoveFromClassList("hidden");
            else       _rosterPanel.AddToClassList("hidden");
        }

        if (_pttHint != null)
        {
            if (multi) _pttHint.RemoveFromClassList("hidden");
            else       _pttHint.AddToClassList("hidden");
        }

        // Minimap: visible to everyone outside a networked session, Dispatcher-only inside one.
        if (_minimapPanel != null)
        {
            bool showMinimap = !multi || NetworkPlayer.Local?.RoleType == PlayerRoleType.Dispatcher;
            if (showMinimap) _minimapPanel.RemoveFromClassList("hidden");
            else             _minimapPanel.AddToClassList("hidden");
        }

        if (!multi) return;

        foreach (var np in NetworkPlayer.All)
            _rosterList.Add(BuildPlayerRow(np));

        string hint = RoleTaskManager.GetTeamCompositionHint();
        HUDController.Instance?.AddNotification("Team", hint, HUDController.NotifType.Warning);
    }

    VisualElement BuildPlayerRow(NetworkPlayer np)
    {
        var rc = PlayerRoleColors.For(np.RoleType);
        RoleLabel.TryGetValue(np.RoleType, out var rl);

        var row = new VisualElement();
        row.name = $"player-row-{np.OwnerClientId}";
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems    = Align.Center;
        row.style.paddingTop    = new StyleLength(3f);
        row.style.paddingBottom = new StyleLength(3f);
        row.style.marginBottom  = new StyleLength(4f);  // simulates rowGap:4

        // Microphone silhouette: dim while silent, green while this player is transmitting.
        var microphone = BuildMicrophone(np.OwnerClientId, np.IsSpeaking.Value);

        // Role strip
        var strip = new VisualElement();
        strip.style.width           = new StyleLength(3f);
        strip.style.alignSelf       = Align.Stretch;
        strip.style.backgroundColor = new StyleColor(rc);
        strip.style.marginRight     = new StyleLength(6f);
        SetRadius(strip, 1f);
        strip.style.flexShrink = 0;

        // Name + role text (or "DOWN" if crushed — synced via NetworkPlayer.IsDeadNet)
        var col = new VisualElement();
        col.style.flexGrow      = 1f;
        col.style.flexDirection = FlexDirection.Column;
        col.Add(Lbl(np.PlayerName, 11f, Color.white, bold: true));
        bool isDown = np.IsDeadNet.Value;
        var status = Lbl(isDown ? "DOWN — needs rescue" : (rl ?? "—"), 9f,
            isDown ? Clr(0.92f, 0.24f, 0.22f).value : rc);
        status.name = $"player-status-{np.OwnerClientId}";
        col.Add(status);

        row.Add(microphone); row.Add(strip); row.Add(col);

        return row;
    }

    // ── Voice indicator update ────────────────────────────────────────────────
    void OnSpeakingChanged(string vivoxPlayerId, bool isSpeaking)
    {
        var pttDot = _pttHint?.Q<VisualElement>("ptt-dot");
        if (pttDot != null && VivoxManager.Instance != null)
        {
            pttDot.style.backgroundColor = VivoxManager.Instance.IsPushToTalk
                ? Bg(0.30f, 0.85f, 0.35f)
                : Bg(0.47f, 0.47f, 0.50f);
        }
    }

    static bool IsNetworkedSession() =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    bool _lastMinimapShown;
    PlayerRoleType _lastHudRole = (PlayerRoleType)(-1);
    bool _lastHudNetworked;

    void Update()
    {
        bool networked = IsNetworkedSession();
        var localRole  = NetworkPlayer.Local?.RoleType ?? PlayerRoleType.None;
        if (networked != _lastHudNetworked || localRole != _lastHudRole)
        {
            _lastHudNetworked = networked;
            _lastHudRole      = localRole;
            HUDController.Instance?.ApplyRoleHudLayout(localRole, networked);
        }

        // Cheap safety net: the roster/minimap normally refresh off NetworkPlayer.OnRosterChanged,
        // but that event can fire (role confirmation round-trip) before this component finishes
        // subscribing during startup, silently leaving the minimap hidden for a Dispatcher who
        // picked their role before the HUD caught up. Re-evaluate every frame instead of trusting
        // the event alone — it's a single bool comparison, not worth its own timer.
        if (_minimapPanel != null)
        {
            bool showMinimap = !networked || localRole == PlayerRoleType.Dispatcher;
            if (showMinimap != _lastMinimapShown)
            {
                _lastMinimapShown = showMinimap;
                if (showMinimap) _minimapPanel.RemoveFromClassList("hidden");
                else             _minimapPanel.AddToClassList("hidden");
            }
        }

        if (_rosterList == null || NetworkPlayer.All == null) return;
        foreach (var np in NetworkPlayer.All)
        {
            var microphone = _rosterList.Q<VisualElement>($"voice-{np.OwnerClientId}");
            if (microphone != null) SetMicrophoneState(microphone, np.IsSpeaking.Value);

            var status = _rosterList.Q<Label>($"player-status-{np.OwnerClientId}");
            if (status == null) continue;
            RoleLabel.TryGetValue(np.RoleType, out var roleLabel);
            if (np.IsDeadNet.Value)
            {
                status.text = "DOWN — needs rescue";
                status.style.color = Clr(0.92f, 0.24f, 0.22f);
            }
            else if (np.IsChargingNet.Value)
            {
                status.text = $"CHARGING  {np.BatteryPercentNet.Value:0}% / {np.BatteryCapacityNet.Value:0}%";
                status.style.color = Clr(0.30f, 0.85f, 0.35f);
            }
            else
            {
                status.text = $"{roleLabel ?? "—"}  ·  {np.BatteryPercentNet.Value:0}% / {np.BatteryCapacityNet.Value:0}%";
                status.style.color = new StyleColor(PlayerRoleColors.For(np.RoleType));
            }
        }
    }

    static VisualElement BuildMicrophone(ulong clientId, bool speaking)
    {
        var microphone = new VisualElement { name = $"voice-{clientId}" };
        microphone.style.width = new StyleLength(16f);
        microphone.style.height = new StyleLength(18f);
        microphone.style.marginRight = new StyleLength(6f);
        microphone.style.flexShrink = 0f;
        microphone.style.alignItems = Align.Center;

        var body = new VisualElement { name = "mic-body" };
        body.style.width = new StyleLength(7f);
        body.style.height = new StyleLength(10f);
        SetRadius(body, 4f);

        var stem = new VisualElement { name = "mic-stem" };
        stem.style.width = new StyleLength(2f);
        stem.style.height = new StyleLength(4f);

        var baseLine = new VisualElement { name = "mic-base" };
        baseLine.style.width = new StyleLength(10f);
        baseLine.style.height = new StyleLength(2f);
        SetRadius(baseLine, 1f);

        microphone.Add(body);
        microphone.Add(stem);
        microphone.Add(baseLine);
        SetMicrophoneState(microphone, speaking);
        return microphone;
    }

    static void SetMicrophoneState(VisualElement microphone, bool speaking)
    {
        var color = speaking ? Bg(0.30f, 0.85f, 0.35f) : Bg(0.28f, 0.28f, 0.31f);
        var body = microphone.Q<VisualElement>("mic-body");
        var stem = microphone.Q<VisualElement>("mic-stem");
        var baseLine = microphone.Q<VisualElement>("mic-base");
        if (body != null) body.style.backgroundColor = color;
        if (stem != null) stem.style.backgroundColor = color;
        if (baseLine != null) baseLine.style.backgroundColor = color;
        microphone.style.opacity = speaking ? 1f : 0.65f;
    }

    // ── Style helpers ─────────────────────────────────────────────────────────
    static StyleColor Bg(float r, float g, float b, float a = 1f) =>
        new StyleColor(new Color(r, g, b, a));

    static StyleColor Clr(float r, float g, float b) =>
        new StyleColor(new Color(r, g, b));

    static void SetBorder(VisualElement el, float w, Color c)
    {
        el.style.borderTopWidth    = el.style.borderBottomWidth =
        el.style.borderLeftWidth   = el.style.borderRightWidth  = w;
        var sc = new StyleColor(c);
        el.style.borderTopColor    = el.style.borderBottomColor =
        el.style.borderLeftColor   = el.style.borderRightColor  = sc;
    }

    static void SetRadius(VisualElement el, float r)
    {
        el.style.borderTopLeftRadius     = el.style.borderTopRightRadius =
        el.style.borderBottomLeftRadius  = el.style.borderBottomRightRadius = r;
    }

    static Label Lbl(string text, float size, Color color, bool bold = false)
    {
        var l = new Label(text);
        l.style.fontSize = size <= 16f ? size + 2f : size;   // +2: HUD text was too small to read
        l.style.color    = new StyleColor(color);
        if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
        return l;
    }

    static VisualElement AccentBar(Color color)
    {
        var bar = new VisualElement();
        bar.style.width           = new StyleLength(3f);
        bar.style.alignSelf       = Align.Stretch;
        bar.style.backgroundColor = new StyleColor(color);
        bar.style.marginRight     = new StyleLength(7f);
        bar.style.flexShrink      = 0;
        return bar;
    }
}
