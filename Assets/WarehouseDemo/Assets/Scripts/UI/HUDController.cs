using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;

/// Main HUD controller — task panel, notifications, radio strip, menu overlay, vehicle status.
[RequireComponent(typeof(UIDocument))]
public class HUDController : MonoBehaviour
{
    static HUDController _instance;
    public static HUDController Instance
    {
        get => _instance != null ? _instance
                                 : _instance = Object.FindFirstObjectByType<HUDController>(
                                                   FindObjectsInactive.Include);
        private set => _instance = value;
    }

    [SerializeField] float  _sessionDuration  = GameManager.DemoFinalOrderWindowSeconds;

    // ── Element cache ─────────────────────────────────────────────────────────
    VisualElement  _documentRoot;
    VisualElement  _hudRoot;
    bool           _screenshotUiHidden;
    VisualElement  _topBar, _leftCol, _rightCol;
    VisualElement  _activeTaskBox;
    Label          _timeValue, _shiftLabel;
    Label          _ordersValue, _budgetValue, _repValue;
    Label          _fpsCounter;
    Label          _activeTaskText, _taskHint;
    VisualElement  _taskList;
    Label          _promptText;
    VisualElement  _interactionPrompt;
    VisualElement  _rewardToast;
    Label          _rewardToastTitle, _rewardToastDetail;
    Label          _controlsHintText;
    VisualElement  _controlsHint;
    VisualElement  _notifContent;
    Label          _notifEmptyState;
    Button         _notifToggleBtn;
    readonly Dictionary<string, VisualElement> _persistentNotifications = new();
    VisualElement  _radioHudPanel, _radioHudHeader;
    Label          _vehicleName, _fuelPct, _forkHeight;
    VisualElement  _fuelFill;
    VisualElement  _statusPanel;
    VisualElement  _lowPowerOverlay;
    Label          _lowPowerWarning;
    readonly VisualElement[] _lowPowerArtifacts = new VisualElement[24];
    static readonly Color[] LowPowerGlitchColors =
    {
        new(0.961f, 0.651f, 0.137f, 1f),  // HUD amber channel tear
        new(0.980f, 0.502f, 0.102f, 1f),  // warm orange artifact
        new(0.898f, 0.224f, 0.208f, 1f),  // warning-red pixel corruption
        new(0.035f, 0.031f, 0.027f, 1f),  // dropped/charcoal image blocks
        new(1.000f, 0.757f, 0.322f, 1f)   // bright accent pixels
    };
    float          _lowPowerPercent = 100f;
    float          _lowPowerBurstEnds;
    float          _nextLowPowerArtifactUpdate;
    bool           _lowPowerBurstActive;

    // ── Tutorial ──────────────────────────────────────────────────────────────
    VisualElement _tutorialBanner;
    Label         _tutorialStepNum;
    Label         _tutorialTitle;
    Label         _tutorialText;
    VisualElement _tutorialKeyRow;
    Label         _tutorialKey;
    Button        _tutorialNextBtn;
    Label         _tutorialWaiting;
    Label         _tutorialContinue;
    VisualElement _tutorialChecklist;
    Label[]       _checklistChips;

    // ── Order deadline timer ──────────────────────────────────────────────────
    VisualElement _orderTimerTrack;
    VisualElement _orderTimerFill;
    TimerState    _orderTimerState = TimerState.Normal;
    Coroutine     _orderTimerPulse;

    // ── Timed delivery event (separate from the regular order deadline) ───────
    VisualElement _eventTimerBlock;
    VisualElement _eventTimerFill;
    Label         _eventTimerTitle;
    Label         _eventTimerValue;
    Label         _eventTimerDetail;
    string        _eventTimerOwner;

    // ── Crosshair ─────────────────────────────────────────────────────────────
    VisualElement _crosshairLayer;
    VisualElement _actionProgress, _actionProgressFill;
    Label         _actionProgressLabel;
    bool          _actionProgressVisible;

    // ── Radio HUD strip (persistent, bottom-left) ─────────────────────────────
    VisualElement _radioHudLog;
    const int     k_radioMaxMessages = 3;
    int           _radioVisibleMessages = k_radioMaxMessages;

    // ── Overlay panels ────────────────────────────────────────────────────────
    VisualElement _overlayMenu,       _overlayMenuBox, _menuContent;
    VisualElement _overlayOrderboard, _orderboardContent;
    VisualElement _shopContent;
    Button        _tabOrdersBtn, _tabShopBtn;
    VisualElement _shopCategoryTabs;
    Button        _shopVehiclesBtn, _shopEquipmentBtn, _shopSuppliesBtn, _shopServicesBtn;
    bool          _shopTabActive;
    enum ShopCategory { Vehicles, Equipment, Supplies, Services }
    ShopCategory _shopCategory = ShopCategory.Vehicles;
    readonly HashSet<NetworkShopManager.VehicleType> _shopRequestsPending = new();
    readonly HashSet<NetworkShopManager.VehicleType> _repeatablePurchases = new();
    readonly Dictionary<NetworkShopManager.VehicleType, float> _shopFeedbackUntil = new();
    VisualElement                 _deathOverlay;
    UnityEngine.UIElements.Label _deathTimerLabel;
    Coroutine                     _deathPulse;

    bool _menuOpen       = false;
    bool _orderboardOpen = false;

    bool       _notifVisible  = true;
    bool       _promptVisible = false;
    bool       _controlsHintVisible = false;
    bool       _demoComplete  = false;
    int        _frameCount;
    float      _fpsTimer;
    TimerState _timerState = TimerState.Normal;
    Coroutine  _timerPulse;
    Coroutine  _rewardToastRoutine;
    int        _assemblerBenchPlaced;

    enum TimerState { Normal, Orange, Red }

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;

        var docRoot = GetComponent<UIDocument>()?.rootVisualElement;
        if (docRoot != null) docRoot.style.display = DisplayStyle.None;
    }

    void OnEnable()
    {
        InputSystem.onDeviceChange += OnInputDeviceChange;
        NetworkPlayer.OnRosterChanged += RefreshRoleTaskFromRoster;
        NetworkShopManager.OnPurchaseResolved += HandleShopPurchaseResolved;
    }

    void OnDisable()
    {
        InputSystem.onDeviceChange -= OnInputDeviceChange;
        NetworkPlayer.OnRosterChanged -= RefreshRoleTaskFromRoster;
        NetworkShopManager.OnPurchaseResolved -= HandleShopPurchaseResolved;

        // The menu is the only way to lift a pause, so a HUD going away while one is up would
        // leave a stopped world and nothing on screen able to start it again.
        GamePause.Resume();
    }

    void RefreshRoleTaskFromRoster()
    {
        // OnEnable runs before Start, so the first roster callback can arrive before UI fields
        // are cached. Start establishes the initial task; subsequent callbacks keep it current.
        if (_activeTaskText == null) return;

        var activeOrder = GameManager.Instance?.ActiveOrder;
        if (activeOrder != null) SetActiveRoleTask(activeOrder);
        else                     SetIdleRoleTask();
    }

    void OnInputDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (device is not Gamepad) return;
        if (change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected)
            AddNotification("Gamepad connected", "", NotifType.Success);
    }

    void Start()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;
        UiAudioFeedback.Bind(root);
        CacheElements(root);
        WireButtons(root);
        BuildMenuContent();
        BuildRadioHudPanel();
        BindGameManager();
        BindBench();
        SetIdleRoleTask();
        StartCoroutine(IntroSequence());

        if (Gamepad.current != null)
            AddNotification("Gamepad connected", "", NotifType.Success);
    }

    void CacheElements(VisualElement root)
    {
        _documentRoot      = root;
        _hudRoot           = root.Q<VisualElement>("hud-root");
        _topBar            = root.Q<VisualElement>("top-bar");
        _leftCol           = root.Q<VisualElement>("left-col");
        _rightCol          = root.Q<VisualElement>("right-col");
        _activeTaskBox     = root.Q<VisualElement>("active-task-box");
        _timeValue         = root.Q<Label>("time-value");
        _shiftLabel        = root.Q<Label>("shift-label");
        _ordersValue       = root.Q<Label>("orders-value");
        _budgetValue       = root.Q<Label>("budget-value");
        _repValue          = root.Q<Label>("rep-value");
        _fpsCounter        = root.Q<Label>("fps-counter");
        _activeTaskText    = root.Q<Label>("active-task-text");
        _taskHint          = root.Q<Label>("task-hint");
        _taskList          = root.Q<VisualElement>("task-list");
        _promptText        = root.Q<Label>("prompt-text");
        _interactionPrompt = root.Q<VisualElement>("interaction-prompt");
        _rewardToast       = root.Q<VisualElement>("reward-toast");
        _rewardToastTitle  = root.Q<Label>("reward-toast-title");
        _rewardToastDetail = root.Q<Label>("reward-toast-detail");
        _controlsHintText  = root.Q<Label>("controls-hint-text");
        _controlsHint      = root.Q<VisualElement>("controls-hint");
        _notifContent      = root.Q<VisualElement>("notif-content");
        _notifToggleBtn    = root.Q<Button>("notif-toggle-btn");
        if (_notifContent != null)
        {
            _notifEmptyState = new Label("No notifications yet");
            _notifEmptyState.AddToClassList("notif-desc");
            _notifEmptyState.pickingMode = PickingMode.Ignore;
            _notifContent.Add(_notifEmptyState);
            RefreshNotificationEmptyState();
        }
        _radioHudPanel     = root.Q<VisualElement>("radio-hud-panel");
        _radioHudHeader    = _radioHudPanel?.Q<VisualElement>(className: "panel-header");
        _vehicleName       = root.Q<Label>("vehicle-name");
        _fuelPct           = root.Q<Label>("fuel-pct");
        _forkHeight        = root.Q<Label>("fork-height");
        _fuelFill          = root.Q<VisualElement>("fuel-fill");
        _statusPanel       = root.Q<VisualElement>("status-panel");

        _tutorialBanner    = root.Q<VisualElement>("tutorial-banner");
        _tutorialStepNum   = root.Q<Label>("tutorial-step-num");
        _tutorialTitle     = root.Q<Label>("tutorial-title");
        _tutorialText      = root.Q<Label>("tutorial-text");
        _tutorialKeyRow    = root.Q<VisualElement>("tutorial-key-row");
        _tutorialKey       = root.Q<Label>("tutorial-key");
        _tutorialNextBtn   = root.Q<Button>("tutorial-next-btn");
        _tutorialWaiting   = root.Q<Label>("tutorial-waiting");
        _tutorialContinue  = root.Q<Label>("tutorial-continue");
        _tutorialChecklist = root.Q<VisualElement>("tutorial-checklist");
        _tutorialNextBtn?.RegisterCallback<ClickEvent>(_ => TutorialManager.Instance?.ContinueInfoStep());

        _orderTimerTrack   = root.Q<VisualElement>("order-timer-track");
        _orderTimerFill    = root.Q<VisualElement>("order-timer-fill");
        _eventTimerBlock   = root.Q<VisualElement>("event-timer-block");
        _eventTimerFill    = root.Q<VisualElement>("event-timer-fill");
        _eventTimerTitle   = root.Q<Label>("event-timer-title");
        _eventTimerValue   = root.Q<Label>("event-timer-value");
        _eventTimerDetail  = root.Q<Label>("event-timer-detail");

        _crosshairLayer    = root.Q<VisualElement>("crosshair-layer");
        _actionProgress      = root.Q<VisualElement>("action-progress");
        _actionProgressLabel = root.Q<Label>("action-progress-label");
        _actionProgressFill  = root.Q<VisualElement>("action-progress-fill");

        _overlayMenu         = root.Q<VisualElement>("overlay-menu");
        _overlayMenuBox      = root.Q<VisualElement>("overlay-menu-box");
        _menuContent         = root.Q<VisualElement>("menu-content");
        _overlayOrderboard   = root.Q<VisualElement>("overlay-orderboard");
        _orderboardContent   = root.Q<VisualElement>("orderboard-content");
        _shopContent         = root.Q<VisualElement>("shop-content");
        _tabOrdersBtn        = root.Q<Button>("tab-orders");
        _tabShopBtn          = root.Q<Button>("tab-shop");
        _shopCategoryTabs    = root.Q<VisualElement>("shop-category-tabs");
        _shopVehiclesBtn     = root.Q<Button>("shop-cat-vehicles");
        _shopEquipmentBtn    = root.Q<Button>("shop-cat-equipment");
        _shopSuppliesBtn     = root.Q<Button>("shop-cat-supplies");
        _shopServicesBtn     = root.Q<Button>("shop-cat-services");

        _radioHudLog = root.Q<VisualElement>("radio-hud-log");
    }

    /// UI Toolkit can replace a UIDocument's root when the HUD GameObject is activated after the
    /// main menu. Cached elements from the previous root then remain valid C# objects but are no
    /// longer rendered. Rebind the notification area on demand so client RPC feedback is never
    /// silently written into a detached visual tree.
    void EnsureNotificationBinding()
    {
        var currentRoot = GetComponent<UIDocument>()?.rootVisualElement;
        if (currentRoot == null ||
            (_documentRoot == currentRoot && _notifContent != null && _notifContent.panel != null))
            return;

        _documentRoot = currentRoot;
        _hudRoot = currentRoot.Q<VisualElement>("hud-root");
        _rightCol = currentRoot.Q<VisualElement>("right-col");
        _notifContent = currentRoot.Q<VisualElement>("notif-content");
        _notifToggleBtn = currentRoot.Q<Button>("notif-toggle-btn");

        // Entries belonging to the detached tree cannot be reused in the new document.
        _persistentNotifications.Clear();
        _notifEmptyState = null;
        if (_notifContent != null)
        {
            _notifEmptyState = new Label("No notifications yet");
            _notifEmptyState.AddToClassList("notif-desc");
            _notifEmptyState.pickingMode = PickingMode.Ignore;
            _notifContent.Add(_notifEmptyState);
            RefreshNotificationEmptyState();
            _notifContent.EnableInClassList("hidden", !_notifVisible);
        }

        _notifToggleBtn?.RegisterCallback<ClickEvent>(_ => ToggleNotifications());
        if (_notifToggleBtn != null)
        {
            string key = GameInputBindings.GetDisplayName(
                GameBindingAction.Notifications, gamepad: false);
            _notifToggleBtn.text = $"{key} — {(_notifVisible ? "Hide" : "Show")}";
        }
    }

    void WireButtons(VisualElement root)
    {
        _notifToggleBtn?.RegisterCallback<ClickEvent>(_ => ToggleNotifications());

        root.Q<Button>("close-menu")?.RegisterCallback<ClickEvent>(_ => CloseAllOverlays());
        root.Q<Button>("close-orderboard")?.RegisterCallback<ClickEvent>(_ => CloseAllOverlays());

        _overlayMenu?.RegisterCallback<ClickEvent>(_ => CloseAllOverlays());
        root.Q<VisualElement>("overlay-menu-box")?.RegisterCallback<ClickEvent>(e => e.StopPropagation());

        _overlayOrderboard?.RegisterCallback<ClickEvent>(_ => CloseAllOverlays());
        root.Q<VisualElement>("overlay-orderboard-box")?.RegisterCallback<ClickEvent>(e => e.StopPropagation());

        _tabOrdersBtn?.RegisterCallback<ClickEvent>(_ => SwitchOrderboardTab(shop: false));
        _tabShopBtn?.RegisterCallback<ClickEvent>(_ => SwitchOrderboardTab(shop: true));
        _shopVehiclesBtn?.RegisterCallback<ClickEvent>(_ => SwitchShopCategory(ShopCategory.Vehicles));
        _shopEquipmentBtn?.RegisterCallback<ClickEvent>(_ => SwitchShopCategory(ShopCategory.Equipment));
        _shopSuppliesBtn?.RegisterCallback<ClickEvent>(_ => SwitchShopCategory(ShopCategory.Supplies));
        _shopServicesBtn?.RegisterCallback<ClickEvent>(_ => SwitchShopCategory(ShopCategory.Services));
    }

    // ── Radio HUD strip ───────────────────────────────────────────────────────
    void BuildRadioHudPanel()
    {
        AppendRadioLine("system", "— Channel 1 — Main —");
        // "First order ready" read as "an order has already been placed for you" — this line
        // shows at session start, when nothing is accepted yet and the board is merely stocked.
        AppendRadioLine("",       "Dispatcher: Shift started. Orders are up on the board.");
    }

    /// Adds a message to the persistent radio strip visible to all players.
    public void AddRadioMessage(string text, string sender = "")
    {
        string full = string.IsNullOrEmpty(sender) ? text : $"{sender}: {text}";
        AppendRadioLine("new", full);
        StartCoroutine(DimRadioMessage());
    }

    void AppendRadioLine(string cls, string text)
    {
        if (_radioHudLog == null) return;

        while (_radioHudLog.childCount >= _radioVisibleMessages)
            _radioHudLog.RemoveAt(0);

        var msg = new Label(text);
        msg.AddToClassList("radio-msg");
        if (!string.IsNullOrEmpty(cls)) msg.AddToClassList($"radio-msg--{cls}");
        msg.style.whiteSpace = WhiteSpace.Normal;
        _radioHudLog.Add(msg);
    }

    IEnumerator DimRadioMessage()
    {
        yield return new WaitForSeconds(5f);
        if (_radioHudLog == null) yield break;
        var newest = _radioHudLog.Q<Label>(className: "radio-msg--new");
        newest?.RemoveFromClassList("radio-msg--new");
    }

    // ── Menu ──────────────────────────────────────────────────────────────────
    void BuildMenuContent()
    {
        if (_menuContent == null) return;

        GameInputBindings.CancelRebind();
        _menuContent.Clear();
        if (_overlayMenuBox != null) _overlayMenuBox.style.width = 320f;

        var buttons = new (string text, bool isDanger, System.Action action)[]
        {
            ("Resume",                 false, CloseAllOverlays),
            ("Settings / Controls",     false, ShowControlsSettings),
            ("Restart demo",           false, RestartDemo),
            ("Main menu",              true,  GoToMainMenu),
        };

        foreach (var (text, isDanger, action) in buttons)
        {
            var btn = new Button();
            btn.text = text;
            btn.AddToClassList("menu-btn");
            if (isDanger) btn.AddToClassList("menu-btn--danger");
            btn.RegisterCallback<ClickEvent>(_ => action());
            _menuContent.Add(btn);
        }

    }

    void ShowControlsSettings()
    {
        if (_menuContent == null) return;
        _menuContent.Clear();
        if (_overlayMenuBox != null) _overlayMenuBox.style.width = 900f;

        var title = new Label("SETTINGS");
        title.AddToClassList("panel-title");
        title.style.fontSize = 22f;
        title.style.marginBottom = 12f;
        _menuContent.Add(title);

        var tabRow = new VisualElement();
        tabRow.AddToClassList("ob-tab-row");
        _menuContent.Add(tabRow);

        var audioPage = new VisualElement();
        var cameraPage = new VisualElement();
        var controlsPage = new VisualElement();
        _menuContent.Add(audioPage);
        _menuContent.Add(cameraPage);
        _menuContent.Add(controlsPage);

        AddPauseAudioSettings(audioPage);
        AddPauseCameraSettings(cameraPage);
        controlsPage.Add(ControlsBindingTable.Build());

        var tabs = new[]
        {
            CreateSettingsTab("AUDIO", tabRow),
            CreateSettingsTab("CAMERA", tabRow),
            CreateSettingsTab("CONTROLS", tabRow)
        };
        var pages = new[] { audioPage, cameraPage, controlsPage };
        for (int i = 0; i < tabs.Length; i++)
        {
            int selectedIndex = i;
            tabs[i].RegisterCallback<ClickEvent>(_ => SelectPauseSettingsTab(tabs, pages, selectedIndex));
        }
        SelectPauseSettingsTab(tabs, pages, 0);

        var back = new Button(BuildMenuContent) { text = "Back" };
        back.AddToClassList("menu-btn");
        _menuContent.Add(back);
    }

    Button CreateSettingsTab(string text, VisualElement parent)
    {
        var button = new Button { text = text };
        button.AddToClassList("ob-tab-btn");
        parent.Add(button);
        return button;
    }

    static void SelectPauseSettingsTab(Button[] tabs, VisualElement[] pages, int selectedIndex)
    {
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i].EnableInClassList("ob-tab-btn--active", i == selectedIndex);
            pages[i].EnableInClassList("hidden", i != selectedIndex);
        }
    }

    void AddPauseCameraSettings(VisualElement page)
    {
        var sensitivityValue = new Label($"Look sensitivity: {GameSettings.LookSensitivity:0.00}×");
        sensitivityValue.style.color = new Color(0.88f, 0.88f, 0.9f);
        sensitivityValue.style.fontSize = 16f;
        page.Add(sensitivityValue);

        var sensitivity = new Slider(GameSettings.MinLookSensitivity,
                                     GameSettings.MaxLookSensitivity)
        {
            value = GameSettings.LookSensitivity
        };
        sensitivity.style.marginBottom = 10f;
        sensitivity.RegisterValueChangedCallback(evt =>
        {
            GameSettings.LookSensitivity = evt.newValue;
            sensitivityValue.text = $"Look sensitivity: {evt.newValue:0.00}×";
        });
        page.Add(sensitivity);

        var invertY = new Toggle("Invert vertical look") { value = GameSettings.InvertY };
        invertY.style.color = new Color(0.82f, 0.82f, 0.85f);
        invertY.style.fontSize = 16f;
        invertY.style.marginBottom = 14f;
        invertY.RegisterValueChangedCallback(evt => GameSettings.InvertY = evt.newValue);
        page.Add(invertY);
    }

    void AddPauseAudioSettings(VisualElement page)
    {
        var audioTitle = new Label("AUDIO");
        audioTitle.AddToClassList("panel-title");
        audioTitle.style.fontSize = 18f;
        audioTitle.style.marginBottom = 8f;
        page.Add(audioTitle);

        AddVolumeSlider(page, "Master volume", GameSettings.MasterVolume,
            value => GameSettings.MasterVolume = value);

        AddVolumeSlider(page, "Background music", GameSettings.MusicVolume,
            value => GameSettings.MusicVolume = value);

        AddVolumeSlider(page, "Game sounds", GameSettings.SoundVolume,
            value => GameSettings.SoundVolume = value);

        var voiceNote = new Label("Voice chat volume is independent from master volume.");
        voiceNote.style.color = new Color(0.52f, 0.54f, 0.58f);
        voiceNote.style.fontSize = 12f;
        voiceNote.style.marginTop = 4f;
        voiceNote.style.marginBottom = 8f;
        page.Add(voiceNote);
    }

    Slider AddVolumeSlider(VisualElement page, string caption, float initialValue, System.Action<float> onChanged)
    {
        var valueLabel = new Label($"{caption}: {Mathf.RoundToInt(initialValue * 100f)}%");
        valueLabel.style.color = new Color(0.88f, 0.88f, 0.9f);
        valueLabel.style.fontSize = 16f;
        page.Add(valueLabel);

        var slider = new Slider(0f, 1f) { value = initialValue };
        slider.style.marginBottom = 8f;
        slider.RegisterValueChangedCallback(evt =>
        {
            onChanged(evt.newValue);
            valueLabel.text = $"{caption}: {Mathf.RoundToInt(evt.newValue * 100f)}%";
        });
        page.Add(slider);
        return slider;
    }

    void RestartDemo()
    {
        CloseAllOverlays();
        // AutoStart makes MainMenuController stand aside as the scene comes back up, so the reload
        // lands straight in a fresh shift rather than on the menu — which is what the button's
        // label always said it did.
        MainMenuController.AutoStart = true;
        LoadingScreen.LoadScene(GameScenes.Warehouse, "Restarting the shift...");
    }

    void GoToMainMenu()
    {
        CloseAllOverlays();
        // waitForGameStart is off here: the menu never starts a shift, so there is nothing to wait
        // for and the screen would only sit there until its own deadline ran out.
        LoadingScreen.LoadScene(GameScenes.Warehouse, "Back to the menu...",
                                waitForGameStart: false);
    }

    void OpenMenu()
    {
        BuildMenuContent();
        _overlayMenu?.RemoveFromClassList("hidden");
        _menuOpen = true;
        UIManager.OpenPanel();
        ShowCrosshair(false);

        // Alone, this is a pause menu and stops the world. In a session it is just a menu — see
        // GamePause, which ignores the request rather than making every caller know the rule.
        GamePause.Set(true);
    }

    public void CloseAllOverlays()
    {
        _overlayMenu?.AddToClassList("hidden");
        _overlayOrderboard?.AddToClassList("hidden");

        bool wasOpen = _menuOpen || _orderboardOpen;
        _menuOpen       = false;
        _orderboardOpen = false;

        if (wasOpen)
        {
            UIManager.ClosePanel();
            ShowCrosshair(true);
        }

        // Unconditional, not inside the wasOpen branch: this is the one path back to the game, and
        // it has to undo a pause even if the flags have got out of step with what is on screen.
        GamePause.Resume();
    }

    // ── Order Board ───────────────────────────────────────────────────────────
    public void ShowOrderBoard()
    {
        CloseAllOverlays();
        SwitchOrderboardTab(shop: false);
        _overlayOrderboard?.RemoveFromClassList("hidden");
        UIManager.OpenPanel();
        ShowCrosshair(false);
        _orderboardOpen = true;
    }

    public static event System.Action OnShopTabOpened;

    void SwitchOrderboardTab(bool shop)
    {
        _shopTabActive = shop;

        _tabOrdersBtn?.RemoveFromClassList("ob-tab-btn--active");
        _tabShopBtn?.RemoveFromClassList("ob-tab-btn--active");
        (shop ? _tabShopBtn : _tabOrdersBtn)?.AddToClassList("ob-tab-btn--active");

        if (shop)
        {
            _orderboardContent?.AddToClassList("hidden");
            _shopContent?.RemoveFromClassList("hidden");
            _shopCategoryTabs?.RemoveFromClassList("hidden");
            BuildShopContent();
            OnShopTabOpened?.Invoke();
        }
        else
        {
            _shopContent?.AddToClassList("hidden");
            _shopCategoryTabs?.AddToClassList("hidden");
            _orderboardContent?.RemoveFromClassList("hidden");
            BuildOrderboardContent();
        }
    }

    void BuildOrderboardContent()
    {
        if (_orderboardContent == null) return;
        _orderboardContent.Clear();

        var gm     = GameManager.Instance;
        var orders = gm?.GetAllOrders();

        if (orders == null || orders.Length == 0)
        {
            var empty = new Label("No orders available.");
            empty.AddToClassList("ob-empty");
            _orderboardContent.Add(empty);
            return;
        }

        for (int i = 0; i < orders.Length; i++)
        {
            var order = orders[i];
            if (order == null) continue;

            bool isActive    = gm.ActiveOrder == order;
            bool isCompleted = i < gm.CompletedOrders;
            bool canAccept   = !isActive && !isCompleted && gm.ActiveOrder == null;

            _orderboardContent.Add(BuildOrderCard(order, isActive, isCompleted, canAccept));

            if (i < orders.Length - 1)
            {
                var div = new VisualElement();
                div.AddToClassList("ob-card-divider");
                _orderboardContent.Add(div);
            }
        }
    }

    VisualElement BuildOrderCard(OrderDefinition order, bool isActive, bool isCompleted, bool canAccept)
    {
        var card = new VisualElement();
        card.AddToClassList("ob-card");
        card.AddToClassList(isActive ? "ob-card--active" : isCompleted ? "ob-card--completed" : "ob-card--available");
        if (canAccept) card.AddToClassList("ob-card--clickable");

        var header = new VisualElement();
        header.AddToClassList("ob-header");

        var nameLabel = new Label(order.OrderName);
        nameLabel.AddToClassList("ob-name");

        var badge = new Label(isActive ? "IN PROGRESS" : isCompleted ? "DONE" : "CLICK");
        badge.AddToClassList("ob-badge");
        badge.AddToClassList(isActive ? "ob-badge--active" : isCompleted ? "ob-badge--completed" : "ob-badge--available");

        header.Add(nameLabel);
        header.Add(badge);
        card.Add(header);

        var meta = new VisualElement();
        meta.AddToClassList("ob-meta");

        var clientLabel = new Label($"Client: {order.ClientName}");
        clientLabel.AddToClassList("ob-client");
        meta.Add(clientLabel);

        if (order.TimeLimitSeconds > 0f)
        {
            int mins = Mathf.CeilToInt(order.TimeLimitSeconds / 60f);
            var timeLabel = new Label($"{mins} min");
            timeLabel.AddToClassList("ob-time");
            meta.Add(timeLabel);
        }

        var rewardLabel = new Label($"${order.Reward:N0}");
        rewardLabel.AddToClassList("ob-reward");
        meta.Add(rewardLabel);

        card.Add(meta);

        if (isActive)
        {
            var hint = new Label("Order in progress — complete the current assembly.");
            hint.AddToClassList("ob-hint");
            hint.AddToClassList("ob-hint--active");
            card.Add(hint);
        }
        else if (isCompleted)
        {
            var hint = new Label("✓  Order completed.");
            hint.AddToClassList("ob-hint");
            hint.AddToClassList("ob-hint--completed");
            card.Add(hint);
        }

        if (canAccept)
        {
            card.RegisterCallback<ClickEvent>(_ =>
            {
                bool isMultiplayer = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;
                if (isMultiplayer)
                {
                    CoopGameManager.Instance?.AcceptOrderServerRpc(order.OrderName);
                    CloseAllOverlays();
                }
                else if (GameManager.Instance?.AcceptOrder(order) == true)
                {
                    AddNotification("Order accepted", order.OrderName, NotifType.Success);
                    CloseAllOverlays();
                }
            });
        }

        return card;
    }

    // ── Shop ──────────────────────────────────────────────────────────────────
    void SwitchShopCategory(ShopCategory category)
    {
        _shopCategory = category;

        _shopVehiclesBtn?.RemoveFromClassList("shop-category-btn--active");
        _shopEquipmentBtn?.RemoveFromClassList("shop-category-btn--active");
        _shopSuppliesBtn?.RemoveFromClassList("shop-category-btn--active");
        _shopServicesBtn?.RemoveFromClassList("shop-category-btn--active");

        Button active = category switch
        {
            ShopCategory.Equipment => _shopEquipmentBtn,
            ShopCategory.Supplies  => _shopSuppliesBtn,
            ShopCategory.Services  => _shopServicesBtn,
            _                      => _shopVehiclesBtn,
        };
        active?.AddToClassList("shop-category-btn--active");
        BuildShopContent();
    }

    void BuildShopContent()
    {
        if (_shopContent == null) return;
        _shopContent.Clear();

        var shop = NetworkShopManager.Instance;
        if (shop != null)
        {
            var entries = shop.Entries;
            bool addedAny = false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (GetShopCategory(entries[i].Type) != _shopCategory) continue;
                if (addedAny) AddShopDivider();
                _shopContent.Add(BuildShopCard(entries[i]));
                addedAny = true;
            }
        }

        if (_shopCategory != ShopCategory.Vehicles) return;

        var previewTitle = new Label($"FULL VERSION UPGRADES  ·  CAREER LV {CareerProgress.Level}");
        previewTitle.AddToClassList("shop-preview-title");
        _shopContent.Add(previewTitle);

        _shopContent.Add(BuildLockedShopCard("action-icon--settings", "Robot Upgrades",
            "Movement modules, reinforced arms and battery upgrades.", 3));
        AddShopDivider();
        _shopContent.Add(BuildLockedShopCard("action-icon--inventory", "Robot Skins",
            "Alternative chassis colors and role-themed cosmetic sets.", 5));
        AddShopDivider();
        _shopContent.Add(BuildLockedShopCard("action-icon--forklift", "Warehouse Expansion",
            "Additional storage sectors, industrial belts and automated equipment.", 8));
    }

    static ShopCategory GetShopCategory(NetworkShopManager.VehicleType type) => type switch
    {
        NetworkShopManager.VehicleType.HandScanner    => ShopCategory.Equipment,
        NetworkShopManager.VehicleType.RepairKit      => ShopCategory.Equipment,
        NetworkShopManager.VehicleType.ExtendedPowerCore => ShopCategory.Equipment,
        NetworkShopManager.VehicleType.EmptyPallets   => ShopCategory.Supplies,
        NetworkShopManager.VehicleType.OrderInsurance => ShopCategory.Services,
        _                                               => ShopCategory.Vehicles,
    };

    void AddShopDivider()
    {
        var div = new VisualElement();
        div.AddToClassList("ob-card-divider");
        _shopContent?.Add(div);
    }

    VisualElement BuildLockedShopCard(string iconClass, string title, string description,
        int requiredLevel)
    {
        bool requirementMet = CareerProgress.Level >= requiredLevel;
        var card = new VisualElement();
        card.AddToClassList("ob-card");
        card.AddToClassList("shop-preview-card");
        card.EnableInClassList("shop-preview-card--qualified", requirementMet);

        var row = new VisualElement();
        row.AddToClassList("shop-preview-row");

        var icon = new VisualElement();
        icon.AddToClassList("action-icon");
        icon.AddToClassList(iconClass);

        var copy = new VisualElement();
        copy.AddToClassList("shop-preview-copy");
        var name = new Label(title);
        name.AddToClassList("ob-name");
        var desc = new Label(description);
        desc.AddToClassList("shop-preview-desc");
        var careerGoal = new Label(requirementMet
            ? "CAREER REQUIREMENT MET"
            : $"UNLOCKS AT CAREER LV {requiredLevel}");
        careerGoal.AddToClassList("shop-preview-goal");
        careerGoal.EnableInClassList("shop-preview-goal--met", requirementMet);
        copy.Add(name);
        copy.Add(desc);
        copy.Add(careerGoal);

        var badge = new Label("FULL VERSION");
        badge.AddToClassList("shop-preview-badge");

        row.Add(icon);
        row.Add(copy);
        row.Add(badge);
        card.Add(row);
        return card;
    }

    VisualElement BuildShopCard(NetworkShopManager.ShopEntry entry)
    {
        var shop = NetworkShopManager.Instance;
        bool limitedStock = NetworkShopManager.IsLimitedStock(entry.Type);
        bool finiteStock = limitedStock || entry.Type == NetworkShopManager.VehicleType.ExtendedPowerCore;
        bool outOfStock = finiteStock && shop != null && shop.IsOutOfStock(entry.Type);
        bool requestPending = _shopRequestsPending.Contains(entry.Type);
        bool recentPurchase = _shopFeedbackUntil.TryGetValue(entry.Type, out float feedbackUntil)
                           && feedbackUntil > Time.unscaledTime;

        var card = new VisualElement();
        card.AddToClassList("ob-card");
        card.AddToClassList("ob-card--available");

        var header = new VisualElement();
        header.AddToClassList("ob-header");

        var nameLabel = new Label(entry.DisplayName);
        nameLabel.AddToClassList("ob-name");
        header.Add(nameLabel);
        card.Add(header);

        string description = GetShopDescription(entry.Type);
        if (!string.IsNullOrEmpty(description))
        {
            var descriptionLabel = new Label(description);
            descriptionLabel.AddToClassList("shop-preview-desc");
            card.Add(descriptionLabel);
        }

        var meta = new VisualElement();
        meta.AddToClassList("ob-meta");

        var priceLabel = new Label($"${entry.Price:N0}");
        priceLabel.AddToClassList("ob-reward");
        priceLabel.style.flexGrow = 1f;
        meta.Add(priceLabel);

        int partCount = entry.PartKit?.Parts?.Length ?? 0;
        if (partCount > 0)
        {
            var deliveryLabel = new Label($"{partCount} PART KIT");
            deliveryLabel.AddToClassList("shop-kit-count");
            meta.Add(deliveryLabel);
        }
        else
        {
            string status = GetShopStatus(entry.Type);
            if (!string.IsNullOrEmpty(status))
            {
                var statusLabel = new Label(status);
                statusLabel.AddToClassList("shop-kit-count");
                meta.Add(statusLabel);
            }
        }
        if (limitedStock)
        {
            var stockLabel = new Label(outOfStock ? "STOCK 0/1 · OUT OF STOCK" : "STOCK 1/1 · IN STOCK");
            stockLabel.AddToClassList("shop-kit-count");
            meta.Add(stockLabel);
        }
        else if (entry.Type == NetworkShopManager.VehicleType.ExtendedPowerCore)
        {
            int stock = shop != null ? shop.PowerCoreStock : NetworkShopManager.PowerCoreInitialStock;
            var stockLabel = new Label(stock > 0
                ? $"STOCK {stock}/{NetworkShopManager.PowerCoreInitialStock}"
                : $"STOCK 0/{NetworkShopManager.PowerCoreInitialStock} · SOLD OUT");
            stockLabel.AddToClassList("shop-kit-count");
            meta.Add(stockLabel);
        }
        card.Add(meta);

        var buyBtn = new Button();
        buyBtn.style.whiteSpace = WhiteSpace.Normal;
        buyBtn.style.unityTextAlign = TextAnchor.MiddleCenter;

        if (outOfStock)
        {
            buyBtn.text = shop.RequiresDelivery(entry.Type)
                ? "OUT OF STOCK\nDELIVERY PENDING"
                : "OUT OF STOCK\nPURCHASED";
            buyBtn.SetEnabled(false);
        }
        else if (requestPending)
        {
            buyBtn.text = "PROCESSING...";
            buyBtn.SetEnabled(false);
        }
        else if (recentPurchase)
        {
            buyBtn.text = "PURCHASED ✓";
            buyBtn.SetEnabled(false);
        }
        else
        {
            bool buyAgain = _repeatablePurchases.Contains(entry.Type);
            buyBtn.text = buyAgain ? "IN STOCK · BUY AGAIN" : "IN STOCK · BUY";
        }
        buyBtn.AddToClassList("ob-accept-btn");
        buyBtn.RegisterCallback<ClickEvent>(_ =>
        {
            if (_shopRequestsPending.Contains(entry.Type) || outOfStock) return;

            _shopRequestsPending.Add(entry.Type);
            buyBtn.text = "PROCESSING...";
            buyBtn.SetEnabled(false);

            bool isMultiplayer = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;
            if (isMultiplayer) TryBuyMultiplayer(entry.Type);
            else NetworkShopManager.Instance?.BuyLocal(entry.Type);
        });
        card.Add(buyBtn);

        return card;
    }

    static string GetShopDescription(NetworkShopManager.VehicleType type) => type switch
    {
        NetworkShopManager.VehicleType.Cart =>
            "Carry several loose boxes in one trip instead of moving them by hand one at a time.",
        NetworkShopManager.VehicleType.PalletJack =>
            "Move loaded pallets quickly across the warehouse floor. It cannot reach upper racks.",
        NetworkShopManager.VehicleType.Forklift =>
            "Lift, transport and place loaded pallets on every rack level.",
        NetworkShopManager.VehicleType.HandScanner =>
            "Highlights every part needed for the active order for the whole team for 45 seconds.",
        NetworkShopManager.VehicleType.RepairKit =>
            "Adds one consumable kit used for vehicle faults, damaged gates and emergency repairs.",
        NetworkShopManager.VehicleType.EmptyPallets =>
            "Delivers three reusable empty pallets to a random warehouse gate.",
        NetworkShopManager.VehicleType.OrderInsurance =>
            "Protects the team's current combo from one missed order deadline, then is consumed.",
        NetworkShopManager.VehicleType.ExtendedPowerCore =>
            "Raises the purchasing robot's maximum battery capacity by 25%. Does not restore current charge.",
        _ => string.Empty,
    };

    static string GetShopStatus(NetworkShopManager.VehicleType type) => type switch
    {
        NetworkShopManager.VehicleType.HandScanner => "45 SEC TEAM SCAN",
        NetworkShopManager.VehicleType.RepairKit => $"OWNED {ToolkitItem.Charges}",
        NetworkShopManager.VehicleType.EmptyPallets => "3 PALLETS",
        NetworkShopManager.VehicleType.OrderInsurance =>
            $"OWNED {NetworkShopManager.Instance?.InsuranceCharges ?? 0}",
        NetworkShopManager.VehicleType.ExtendedPowerCore => "PERSONAL +25% CAPACITY",
        _ => string.Empty,
    };

    bool TryBuyMultiplayer(NetworkShopManager.VehicleType type)
    {
        NetworkShopManager.Instance?.BuyVehicleServerRpc((int)type);
        return true; // result (success/insufficient funds) comes back via server notification
    }

    void HandleShopPurchaseResolved(NetworkShopManager.VehicleType type, bool success, bool awaitingDelivery)
    {
        _shopRequestsPending.Remove(type);

        if (success)
            AudioManager.Instance?.PlayPurchaseSuccess();

        if (success && !NetworkShopManager.IsLimitedStock(type))
        {
            _repeatablePurchases.Add(type);
            float until = Time.unscaledTime + 1.5f;
            _shopFeedbackUntil[type] = until;
            StartCoroutine(RefreshShopAfterPurchaseFeedback(type, until));
        }

        if (_orderboardOpen && _shopTabActive)
            BuildShopContent();
    }

    IEnumerator RefreshShopAfterPurchaseFeedback(NetworkShopManager.VehicleType type, float expectedUntil)
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, expectedUntil - Time.unscaledTime));
        if (_shopFeedbackUntil.TryGetValue(type, out float currentUntil) && currentUntil <= Time.unscaledTime)
            _shopFeedbackUntil.Remove(type);

        if (_orderboardOpen && _shopTabActive)
            BuildShopContent();
    }

    void ShowCrosshair(bool visible)
    {
        if (_crosshairLayer == null) return;
        if (visible) _crosshairLayer.RemoveFromClassList("hidden");
        else         _crosshairLayer.AddToClassList("hidden");
    }

    // ── Intro animation ───────────────────────────────────────────────────────
    IEnumerator IntroSequence()
    {
        while (GameManager.Instance == null || !GameManager.Instance.GameStarted)
            yield return null;

        if (_topBar   != null) _topBar.style.opacity   = 0f;
        if (_leftCol  != null) _leftCol.style.opacity  = 0f;
        if (_rightCol != null) _rightCol.style.opacity = 0f;

        GetComponent<UIDocument>().rootVisualElement.style.display = DisplayStyle.Flex;
        yield return null;

        float dur = 0.45f;
        if (_topBar   != null) StartCoroutine(UITween.SlideIn(_topBar,   new Vector2(0f,   -70f), dur));
        yield return new WaitForSeconds(0.08f);
        if (_leftCol  != null) StartCoroutine(UITween.SlideIn(_leftCol,  new Vector2(-230f,  0f), dur));
        if (_rightCol != null) StartCoroutine(UITween.SlideIn(_rightCol, new Vector2( 230f,  0f), dur));
    }

    // ── Update ────────────────────────────────────────────────────────────────
    void Update()
    {
        UpdateLowPowerOverlay();
        CheckScreenshotToggle();
        CheckNotificationToggle();
        UpdateClock();
        UpdateFPS();
        CheckEscape();
        PollTaskList();
    }

    public void SetBatteryLowPowerLevel(float percent)
    {
        _lowPowerPercent = Mathf.Clamp(percent, 0f, 100f);
        EnsureLowPowerOverlay();
        UpdateLowPowerWarning();
    }

    void EnsureLowPowerOverlay()
    {
        var root = GetComponent<UIDocument>()?.rootVisualElement;
        if (root == null) return;
        if (_lowPowerOverlay != null && _lowPowerOverlay.parent == root)
        {
            AttachLowPowerWarning(root);
            return;
        }

        _lowPowerOverlay?.RemoveFromHierarchy();
        _lowPowerOverlay = new VisualElement
        {
            name = "battery-low-power-overlay",
            pickingMode = PickingMode.Ignore
        };
        _lowPowerOverlay.style.position = Position.Absolute;
        _lowPowerOverlay.style.left = 0f;
        _lowPowerOverlay.style.right = 0f;
        _lowPowerOverlay.style.top = 0f;
        _lowPowerOverlay.style.bottom = 0f;
        _lowPowerOverlay.style.backgroundColor = Color.clear;
        _lowPowerOverlay.style.opacity = 1f;

        for (int i = 0; i < _lowPowerArtifacts.Length; i++)
        {
            var artifact = new VisualElement { name = $"battery-glitch-artifact-{i}" };
            artifact.pickingMode = PickingMode.Ignore;
            artifact.style.position = Position.Absolute;
            artifact.style.opacity = 0f;
            _lowPowerArtifacts[i] = artifact;
            _lowPowerOverlay.Add(artifact);
        }

        // Keep the readable alert above the opaque corruption blocks.
        AttachLowPowerWarning(root);
        root.Add(_lowPowerOverlay);
    }

    void AttachLowPowerWarning(VisualElement root)
    {
        if (_lowPowerWarning != null && _lowPowerWarning.parent == _lowPowerOverlay) return;
        _lowPowerWarning = root.Q<Label>("low-battery-alert");
        if (_lowPowerWarning == null) return;

        _lowPowerWarning.RemoveFromHierarchy();
        _lowPowerWarning.pickingMode = PickingMode.Ignore;
        _lowPowerOverlay.Add(_lowPowerWarning);
    }

    void UpdateLowPowerWarning()
    {
        if (_lowPowerWarning == null) return;
        _lowPowerWarning.EnableInClassList("hidden", _lowPowerPercent >= 20f);
    }

    void UpdateLowPowerOverlay()
    {
        if (_lowPowerOverlay == null) return;
        if (_lowPowerPercent >= 20f || UIManager.IsAnyPanelOpen)
        {
            HideLowPowerGlitch();
            return;
        }

        float severity = 1f - Mathf.Clamp01(_lowPowerPercent / 20f);
        float now = Time.unscaledTime;
        if (now < _nextLowPowerArtifactUpdate) return;
        _nextLowPowerArtifactUpdate = now + Random.Range(0.035f, 0.11f);

        if (!_lowPowerBurstActive)
        {
            // A stochastic trigger, not a repeating pulse: intervals and burst lengths vary.
            float triggerChance = Mathf.Lerp(0.012f, 0.12f, severity);
            if (Random.value > triggerChance) return;

            _lowPowerBurstActive = true;
            _lowPowerBurstEnds = now + Random.Range(0.09f, Mathf.Lerp(0.20f, 0.48f, severity));
        }
        else if (now >= _lowPowerBurstEnds)
        {
            _lowPowerBurstActive = false;
            HideLowPowerGlitch();
            _nextLowPowerArtifactUpdate = now + Random.Range(0.12f, Mathf.Lerp(0.4f, 1.25f, severity));
            return;
        }

        ConfigureLowPowerGlitch(severity);
    }

    void ConfigureLowPowerGlitch(float severity)
    {
        int visibleCount = Random.Range(5, Mathf.RoundToInt(Mathf.Lerp(11f, 24f, severity)) + 1);
        for (int i = 0; i < _lowPowerArtifacts.Length; i++)
        {
            var artifact = _lowPowerArtifacts[i];
            if (i >= visibleCount)
            {
                artifact.style.opacity = 0f;
                continue;
            }

            bool tearingLine = Random.value < 0.58f;
            artifact.style.top = Length.Percent(Random.Range(1f, 99f));
            artifact.style.left = Length.Percent(Random.Range(0f, 78f));
            artifact.style.width = Length.Percent(tearingLine
                ? Random.Range(18f, 100f)
                : Random.Range(2f, Mathf.Lerp(9f, 24f, severity)));
            artifact.style.height = Length.Percent(tearingLine
                ? Random.Range(0.35f, Mathf.Lerp(1.4f, 3.2f, severity))
                : Random.Range(1.5f, Mathf.Lerp(8f, 22f, severity)));
            artifact.style.backgroundColor = LowPowerGlitchColors[Random.Range(0, LowPowerGlitchColors.Length)];
            artifact.style.opacity = Random.Range(0.78f, 1f);
        }
    }

    void HideLowPowerGlitch()
    {
        _lowPowerBurstActive = false;
        foreach (var artifact in _lowPowerArtifacts)
            if (artifact != null) artifact.style.opacity = 0f;
    }

    void CheckNotificationToggle()
    {
        if (UIManager.IsAnyPanelOpen) return;
        if (GameInputBindings.WasPressed(GameBindingAction.Notifications))
            ToggleNotifications();
    }

    void CheckScreenshotToggle()
    {
        var kb = Keyboard.current;
        if (kb == null || !kb.f10Key.wasPressedThisFrame) return;

        _screenshotUiHidden = !_screenshotUiHidden;
        var root = GetComponent<UIDocument>()?.rootVisualElement;
        if (root != null)
            root.style.visibility = _screenshotUiHidden ? Visibility.Hidden : Visibility.Visible;
    }

    // The task list used to redraw only on OnBenchChanged / OnOrderAccepted. Any path that
    // missed one of those (a bench bound before a scene reload, a part merely picked up rather
    // than installed) left it frozen, and with the one-part orders nobody noticed — only the last,
    // fourteen-part order made it obvious. A cheap signature poll makes staleness impossible.
    const float TaskPollInterval = 0.25f;
    float _nextTaskPoll;
    int   _taskListSig;

    void PollTaskList()
    {
        if (Time.unscaledTime < _nextTaskPoll) return;
        _nextTaskPoll = Time.unscaledTime + TaskPollInterval;

        var order = GameManager.Instance?.ActiveOrder;
        if (order == null || _taskList == null) return;
        if (TaskListSignature(order) != _taskListSig) RefreshTaskList();
    }

    static int TaskListSignature(OrderDefinition order)
    {
        var bench   = AssemblyBench.Instance;
        var carried = CarriedPart();
        unchecked
        {
            int h = order.GetInstanceID();
            h = h * 31 + (bench != null && bench.IsOrderComplete() ? 1 : 0);
            foreach (var req in order.RequiredParts)
            {
                if (req.Part == null) continue;
                h = h * 31 + (bench != null ? bench.GetPlacedCount(req.Part) : 0);
                h = h * 31 + (carried == req.Part ? 1 : 0);
            }
            return h;
        }
    }

    /// The part in the box the local player is carrying, if any — the only way a part is ever
    /// "held" now that players have no inventory.
    static PartDefinition CarriedPart()
    {
        var box = BoxPhysicsInteractable.GrabbedBox;
        var bwp = box != null ? box.GetComponent<BoxWithPart>() : null;
        return bwp != null ? bwp.Part : null;
    }

    void CheckEscape()
    {
        var kb = Keyboard.current;
        var gp = Gamepad.current;

        // Start opens and closes the menu like Esc; B only backs out of an open overlay, since on
        // foot it is the box-drop button.
        bool overlayOpen = _menuOpen || _orderboardOpen;
        bool pressed = GameInputBindings.WasPressed(GameBindingAction.Menu)
                    || (gp != null && overlayOpen && PadControls.Back(gp).wasPressedThisFrame);
        if (pressed)
        {
            // The demo is over: the shift is not something to go back to, so Escape leaves for the
            // main menu rather than opening an in-shift menu over a finished game.
            if (_demoComplete) { GoToMainMenu(); return; }

            if (_menuOpen || _orderboardOpen)
                CloseAllOverlays();
            else
                OpenMenu();
        }
    }

    void UpdateClock()
    {
        var gm = GameManager.Instance;
        if (gm == null || !gm.GameStarted || _demoComplete || _timeValue == null) return;
        float elapsed = Time.time - gm.SessionStartTime;
        int m = (int)(elapsed / 60f);
        int s = (int)(elapsed % 60f);
        _timeValue.text = $"{m:D2}:{s:D2}";
        if (_sessionDuration > 0f) TickTimerWarning(elapsed);
    }

    void TickTimerWarning(float elapsed)
    {
        float pct = elapsed / _sessionDuration;
        if (pct >= 0.95f && _timerState != TimerState.Red)
        {
            _timerState = TimerState.Red;
            if (_timerPulse != null) StopCoroutine(_timerPulse);
            _timerPulse = StartCoroutine(UITween.PulseText(
                _timeValue,
                new Color(0.90f, 0.20f, 0.20f),
                new Color(1.00f, 0.50f, 0.50f),
                0.55f));
        }
        else if (pct >= 0.80f && _timerState == TimerState.Normal)
        {
            _timerState = TimerState.Orange;
            _timeValue.style.color = new StyleColor(new Color(0.96f, 0.65f, 0.14f));
        }
    }

    void UpdateFPS()
    {
        _frameCount++;
        _fpsTimer += Time.unscaledDeltaTime;
        if (_fpsTimer < 0.5f) return;
        int fps = Mathf.RoundToInt(_frameCount / _fpsTimer);
        if (_fpsCounter != null) _fpsCounter.text = $"FPS {fps}";
        _frameCount = 0;
        _fpsTimer   = 0f;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void ShowPrompt(string text)
    {
        if (_interactionPrompt == null) return;
        if (_promptText != null) _promptText.text = text;
        if (_promptVisible) return;
        _promptVisible = true;
        _interactionPrompt.RemoveFromClassList("hidden");
        _interactionPrompt.style.opacity = 0f;
        StartCoroutine(UITween.FadeIn(_interactionPrompt, 0.2f));
    }

    public void HidePrompt()
    {
        if (_interactionPrompt == null || !_promptVisible) return;
        _promptVisible = false;
        StartCoroutine(UITween.FadeOut(_interactionPrompt, 0.15f,
            () => _interactionPrompt.AddToClassList("hidden")));
    }

    /// Shared, labelled meter for every continuous gameplay action.
    public void ShowActionProgress(string label, float progress)
    {
        if (_actionProgress == null || _actionProgressFill == null) return;

        progress = Mathf.Clamp01(progress);
        if (_actionProgressLabel != null)
            _actionProgressLabel.text = string.IsNullOrWhiteSpace(label)
                ? "ACTION PROGRESS"
                : label.ToUpperInvariant();

        if (!_actionProgressVisible)
        {
            _actionProgressVisible = true;
            _actionProgress.RemoveFromClassList("hidden");
        }

        _actionProgressFill.style.width = Length.Percent(progress * 100f);

        bool full = progress >= 0.999f;

        _actionProgressFill.EnableInClassList("action-progress-fill--full", full);
        _actionProgressFill.EnableInClassList("action-progress-fill--strong", !full && progress >= 0.66f);
    }

    public void HideActionProgress()
    {
        if (_actionProgress == null || !_actionProgressVisible) return;
        _actionProgressVisible = false;
        _actionProgress.AddToClassList("hidden");
        if (_actionProgressFill != null)
        {
            _actionProgressFill.style.width = Length.Percent(0f);
            _actionProgressFill.RemoveFromClassList("action-progress-fill--strong");
            _actionProgressFill.RemoveFromClassList("action-progress-fill--full");
        }
    }

    public void ShowThrowCharge(float forceNorm) => ShowActionProgress("THROW POWER", forceNorm);
    public void HideThrowCharge() => HideActionProgress();

    /// Persistent bottom-of-screen controls reminder — separate element from ShowPrompt/HidePrompt
    /// (the look-at interact prompt), used while occupying a vehicle/cart/ladder. Callers own this
    /// element exclusively for the duration since PlayerInteraction stops driving the look-at
    /// prompt entirely during that time (see PlayerOnTruck/PlayerOnCart/IsActorBusyWithLadder).
    public void ShowControlsHint(string text)
    {
        if (_controlsHint == null) return;
        // Vehicles and ladders call this while occupied. Assigning the same string every frame
        // needlessly dirties UI Toolkit text/layout even though nothing visible changed.
        if (_controlsHintText != null && _controlsHintText.text != text)
            _controlsHintText.text = text;
        if (_controlsHintVisible) return;
        _controlsHintVisible = true;
        _controlsHint.RemoveFromClassList("hidden");
        _controlsHint.style.opacity = 0f;
        StartCoroutine(UITween.FadeIn(_controlsHint, 0.2f));
    }

    public void HideControlsHint()
    {
        if (_controlsHint == null || !_controlsHintVisible) return;
        _controlsHintVisible = false;
        StartCoroutine(UITween.FadeOut(_controlsHint, 0.15f,
            () => _controlsHint.AddToClassList("hidden")));
    }

    public void SetActiveTask(string text, string hint = "")
    {
        if (_activeTaskText != null) _activeTaskText.text = text;
        if (_taskHint       != null) _taskHint.text       = hint;
        if (_activeTaskBox  != null)
            StartCoroutine(UITween.FlashBg(_activeTaskBox,
                new Color(0.35f, 0.75f, 0.35f, 0.25f), 0.5f));
    }

    public void SetTaskList(List<(string name, int done, int total)> tasks)
    {
        if (_taskList == null) return;
        _taskList.Clear();
        foreach (var (name, done, total) in tasks)
            _taskList.Add(BuildTaskRow(name, done, total));
        if (tasks.Count > 0)
            StartCoroutine(UITween.StaggerChildren(
                _taskList, new Vector2(-24f, 0f), 0.3f, 0.055f, this));
    }

    void RefreshTaskList()
    {
        var order = GameManager.Instance?.ActiveOrder;
        if (order == null || _taskList == null) return;

        var bench        = AssemblyBench.Instance;
        bool complete = bench?.IsOrderComplete() == true;

        _taskList.Clear();
        _taskListSig = TaskListSignature(order);

        foreach (var req in order.RequiredParts)
        {
            if (req.Part == null) continue;
            int placed = bench?.GetPlacedCount(req.Part) ?? 0;
            _taskList.Add(BuildTaskRow(req.Part.PartName, placed, req.Count));
        }

        if (_taskHint != null)
            _taskHint.text = complete
                ? "Ready — take it to the shipping zone!"
                : "Bring the required parts to the assembly bench.";
    }

    VisualElement BuildTaskRow(string name, int done, int total)
    {
        var row = new VisualElement();
        row.AddToClassList("task-row");

        var dot = new VisualElement();
        dot.AddToClassList("task-row-dot");
        if (done >= total) dot.AddToClassList("task-row-dot--done");

        var lbl = new Label(name);
        lbl.AddToClassList("task-row-name");

        var cnt = new Label($"{done}/{total}");
        cnt.AddToClassList("task-row-count");

        row.Add(dot); row.Add(lbl); row.Add(cnt);
        return row;
    }

    public void UpdateOrdersCounter(int completed, int total)
    {
        if (_ordersValue != null)
            _ordersValue.text = total > 0 ? $"{completed}/{total}" : completed.ToString();
    }

    public void UpdateBudget(int score)
    {
        if (_budgetValue == null) return;
        _budgetValue.text = $"${score:N0}";
        StartCoroutine(UITween.FlashText(_budgetValue, new Color(1f, 0.85f, 0.22f), 0.7f));
    }

    // ── Notifications ─────────────────────────────────────────────────────────
    public enum NotifType { Success, Warning, Danger }

    public void AddNotification(string title, string sub, NotifType type = NotifType.Warning)
    {
        EnsureNotificationBinding();
        if (_notifContent == null) return;

        // Warnings and errors must be visible immediately. This is especially important for
        // rejected interactions such as charging with insufficient funds: silently adding an item
        // to a collapsed panel makes the rejection look like a movement/physics failure.
        if (type != NotifType.Success)
        {
            _rightCol?.RemoveFromClassList("hidden");
            SetNotificationsVisible(true);
        }

        foreach (var child in _notifContent.Children())
        {
            var existTitle = child.Q<Label>(className: "notif-title");
            if (existTitle != null && existTitle.text == title)
            {
                StartCoroutine(UITween.FlashBg(child, new Color(1f, 0.85f, 0.22f, 0.35f), 0.4f));
                return;
            }
        }

        var item = CreateNotificationItem(title, sub, type);
        _notifContent.Add(item);
        TrimNotifications();
        RefreshNotificationEmptyState();

        StartCoroutine(UITween.SlideIn(item, new Vector2(40f, 0f), 0.3f));

        float lifetime = type switch
        {
            NotifType.Success => 5f,
            NotifType.Warning => 8f,
            _                 => 12f
        };
        StartCoroutine(AutoDismissNotification(item, lifetime));
    }

    public void SetPersistentNotification(string key, string title, string sub,
                                          NotifType type = NotifType.Warning)
    {
        EnsureNotificationBinding();
        if (_notifContent == null || string.IsNullOrWhiteSpace(key)) return;

        if (!_persistentNotifications.TryGetValue(key, out var item) || item?.parent == null)
        {
            item = CreateNotificationItem(title, sub, type);
            item.userData = key;
            _persistentNotifications[key] = item;
            _notifContent.Add(item);
            TrimNotifications();
            RefreshNotificationEmptyState();
            StartCoroutine(UITween.SlideIn(item, new Vector2(40f, 0f), 0.3f));
            return;
        }

        var titleLabel = item.Q<Label>(className: "notif-title");
        var descLabel  = item.Q<Label>(className: "notif-desc");
        if (titleLabel != null) titleLabel.text = title;
        if (descLabel != null)
        {
            descLabel.text = sub;
            if (string.IsNullOrWhiteSpace(sub)) descLabel.AddToClassList("hidden");
            else                                descLabel.RemoveFromClassList("hidden");
        }
    }

    public void RemovePersistentNotification(string key)
    {
        EnsureNotificationBinding();
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!_persistentNotifications.Remove(key, out var item) || item == null) return;
        item.RemoveFromHierarchy();
        RefreshNotificationEmptyState();
    }

    VisualElement CreateNotificationItem(string title, string sub, NotifType type)
    {
        var item = new VisualElement();
        item.AddToClassList("notif-item");
        item.AddToClassList(type switch {
            NotifType.Success => "notif-item--success",
            NotifType.Warning => "notif-item--warning",
            _                 => "notif-item--danger"
        });

        var dot = new VisualElement();
        dot.AddToClassList("notif-dot");
        dot.AddToClassList(type switch {
            NotifType.Success => "notif-dot--success",
            NotifType.Warning => "notif-dot--warning",
            _                 => "notif-dot--danger"
        });

        var body = new VisualElement();
        body.AddToClassList("notif-body");

        var titleLabel = new Label(title);
        titleLabel.AddToClassList("notif-title");
        body.Add(titleLabel);

        var descLabel = new Label(sub);
        descLabel.AddToClassList("notif-desc");
        if (string.IsNullOrWhiteSpace(sub)) descLabel.AddToClassList("hidden");
        body.Add(descLabel);

        item.Add(dot);
        item.Add(body);
        return item;
    }

    void TrimNotifications()
    {
        while (NotificationItemCount() > 3)
        {
            VisualElement removable = null;
            foreach (var child in _notifContent.Children())
            {
                if (!child.ClassListContains("notif-item") || child.userData != null) continue;
                removable = child;
                break;
            }

            if (removable == null) break;
            removable.RemoveFromHierarchy();
        }

        RefreshNotificationEmptyState();
    }

    int NotificationItemCount()
    {
        if (_notifContent == null) return 0;

        int count = 0;
        foreach (var child in _notifContent.Children())
        {
            if (child.ClassListContains("notif-item")) count++;
        }
        return count;
    }

    void RefreshNotificationEmptyState()
    {
        if (_notifEmptyState == null) return;
        _notifEmptyState.EnableInClassList("hidden", NotificationItemCount() > 0);
    }

    IEnumerator AutoDismissNotification(VisualElement item, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (item.parent == null) yield break;
        yield return UITween.FadeOut(item, 0.35f, () =>
        {
            item.RemoveFromHierarchy();
            RefreshNotificationEmptyState();
        });
    }

    void ToggleNotifications()
    {
        SetNotificationsVisible(!_notifVisible);
    }

    void SetNotificationsVisible(bool visible)
    {
        _notifVisible = visible;
        if (_notifContent != null)
        {
            if (_notifVisible) _notifContent.RemoveFromClassList("hidden");
            else               _notifContent.AddToClassList("hidden");
        }
        if (_notifToggleBtn != null)
        {
            string key = GameInputBindings.GetDisplayName(
                GameBindingAction.Notifications, gamepad: false);
            _notifToggleBtn.text = $"{key} — {(_notifVisible ? "Hide" : "Show")}";
        }
    }

    /// Applies the default information density for the local role. This runs only when the
    /// network state or role changes, so a player's manual notification toggle is respected after
    /// the initial layout has been chosen.
    public void ApplyRoleHudLayout(PlayerRoleType role, bool networked)
    {
        bool dispatcherLayout = networked && role == PlayerRoleType.Dispatcher;
        bool compactLayout    = networked && !dispatcherLayout;

        SetNotificationsVisible(!compactLayout);
        _radioVisibleMessages = compactLayout ? 1 : k_radioMaxMessages;

        while (_radioHudLog != null && _radioHudLog.childCount > _radioVisibleMessages)
            _radioHudLog.RemoveAt(0);

        _radioHudHeader?.EnableInClassList("hidden", compactLayout);
        _radioHudPanel?.EnableInClassList("radio-hud-panel--compact", compactLayout);
    }

    // ── Vehicle Status ────────────────────────────────────────────────────────
    public void ShowVehicleStatus(string vehicleName, float fuelNorm)
    {
        if (_statusPanel != null)
        {
            _statusPanel.RemoveFromClassList("hidden");
            _statusPanel.style.opacity = 0f;
            StartCoroutine(UITween.FadeIn(_statusPanel, 0.25f));
        }
        if (_vehicleName != null) _vehicleName.text = vehicleName;
        SetFuel(fuelNorm);
    }

    public void HideVehicleStatus()
    {
        if (_statusPanel == null) return;
        StartCoroutine(UITween.FadeOut(_statusPanel, 0.2f,
            () => _statusPanel.AddToClassList("hidden")));
    }

    // ── Death screen ──────────────────────────────────────────────────────────

    public void ShowDeathScreen()
    {
        HideDeathScreen();

        var docRoot = GetComponent<UIDocument>()?.rootVisualElement;
        if (docRoot == null) return;

        // Full-screen dark-red overlay
        _deathOverlay = new VisualElement();
        _deathOverlay.pickingMode = PickingMode.Ignore;
        _deathOverlay.style.position   = UnityEngine.UIElements.Position.Absolute;
        _deathOverlay.style.left       = 0;
        _deathOverlay.style.top        = 0;
        _deathOverlay.style.right      = 0;
        _deathOverlay.style.bottom     = 0;
        _deathOverlay.style.alignItems        = UnityEngine.UIElements.Align.Center;
        _deathOverlay.style.justifyContent    = UnityEngine.UIElements.Justify.Center;
        _deathOverlay.style.flexDirection     = UnityEngine.UIElements.FlexDirection.Column;
        _deathOverlay.style.backgroundColor  = new Color(0.55f, 0f, 0f, 0f); // start transparent

        // Title label
        var title = new UnityEngine.UIElements.Label("YOU DIED");
        title.style.fontSize                    = 57;
        title.style.color                       = new Color(1f, 0.9f, 0.9f, 1f);
        title.style.unityFontStyleAndWeight      = UnityEngine.FontStyle.Bold;
        title.style.unityTextAlign               = TextAnchor.MiddleCenter;
        title.style.marginBottom                 = 12;
        title.style.textShadow = new UnityEngine.UIElements.TextShadow
        {
            offset = new Vector2(2f, 2f),
            color  = new Color(0f, 0f, 0f, 0.8f)
        };

        // Subtitle
        var sub = new UnityEngine.UIElements.Label("A shelf crushed you");
        sub.style.fontSize       = 22;
        sub.style.color          = new Color(1f, 0.7f, 0.7f, 1f);
        sub.style.unityTextAlign = TextAnchor.MiddleCenter;

        // Countdown timer
        _deathTimerLabel = new UnityEngine.UIElements.Label("");
        _deathTimerLabel.style.fontSize       = 42;
        _deathTimerLabel.style.color          = new Color(1f, 0.75f, 0.75f, 1f);
        _deathTimerLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        _deathTimerLabel.style.marginTop      = 24;

        var hint = new UnityEngine.UIElements.Label("or ask a teammate to rescue you");
        hint.style.fontSize       = 18;
        hint.style.color          = new Color(1f, 0.65f, 0.65f, 0.8f);
        hint.style.unityTextAlign = TextAnchor.MiddleCenter;
        hint.style.marginTop      = 6;

        _deathOverlay.Add(title);
        _deathOverlay.Add(sub);
        _deathOverlay.Add(_deathTimerLabel);
        _deathOverlay.Add(hint);
        docRoot.Add(_deathOverlay);

        // Fade in + start pulse
        _deathPulse = StartCoroutine(DeathScreenPulse());
    }

    public void UpdateDeathTimer(float secondsRemaining)
    {
        if (_deathTimerLabel == null) return;
        int s = Mathf.CeilToInt(secondsRemaining);
        _deathTimerLabel.text = $"Auto-respawn in  {s}s";
    }

    public void HideDeathScreen()
    {
        if (_deathPulse != null) { StopCoroutine(_deathPulse); _deathPulse = null; }
        _deathOverlay?.RemoveFromHierarchy();
        _deathOverlay    = null;
        _deathTimerLabel = null;
    }

    System.Collections.IEnumerator DeathScreenPulse()
    {
        // Fade in over 0.5s
        float elapsed = 0f;
        while (elapsed < 0.5f)
        {
            elapsed += Time.deltaTime;
            float a = Mathf.Clamp01(elapsed / 0.5f);
            if (_deathOverlay != null)
                _deathOverlay.style.backgroundColor = new Color(0.55f, 0f, 0f, a * 0.68f);
            yield return null;
        }

        // Pulse: smoothly oscillate opacity between 0.55 and 0.72
        while (_deathOverlay != null)
        {
            float pulse = 0.55f + Mathf.Sin(Time.time * 1.4f) * 0.085f;
            _deathOverlay.style.backgroundColor = new Color(0.55f, 0f, 0f, pulse);
            yield return null;
        }
    }

    public void SetFuel(float norm)
    {
        norm = Mathf.Clamp01(norm);
        if (_fuelPct  != null) _fuelPct.text = $"{Mathf.RoundToInt(norm * 100)}%";
        if (_fuelFill != null)
        {
            _fuelFill.style.width = new StyleLength(Length.Percent(norm * 100f));
            _fuelFill.style.backgroundColor = new StyleColor(
                norm > 0.3f ? new Color(0.298f, 0.749f, 0.278f) :
                norm > 0.1f ? new Color(0.961f, 0.651f, 0.137f) :
                              new Color(0.898f, 0.224f, 0.208f));
        }
    }

    public void SetForkHeight(float metres)
    {
        if (_forkHeight != null)
            _forkHeight.text = metres.ToString("F1",
                System.Globalization.CultureInfo.InvariantCulture) + " m";
    }

    // ── Demo Complete overlay ─────────────────────────────────────────────────
    void ShowDemoComplete()
    {
        CloseAllOverlays();

        // Hands off the warehouse. The scoreboard is the end of the shift, and a player who can
        // still walk, swing and throw boxes behind it is being invited to keep playing a game that
        // has already added up his score. OpenPanel is what everything else uses for "the world is
        // not yours right now": ThirdPersonController stops taking movement, look and jump,
        // PlayerInteraction stops taking E, and the cursor comes back for the buttons below.
        UIManager.OpenPanel();
        ShowCrosshair(false);

        var gm      = GameManager.Instance;
        var docRoot = GetComponent<UIDocument>().rootVisualElement;

        var overlay = new VisualElement();
        overlay.style.position        = Position.Absolute;
        overlay.style.left            = new StyleLength(0f);
        overlay.style.right           = new StyleLength(0f);
        overlay.style.top             = new StyleLength(0f);
        overlay.style.bottom          = new StyleLength(0f);
        overlay.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.92f));
        overlay.style.alignItems      = Align.Center;
        overlay.style.justifyContent  = Justify.Center;
        overlay.style.flexDirection   = FlexDirection.Column;
        overlay.style.opacity         = 0f;

        var brand = new Label(SteamBranding.GameName.ToUpperInvariant());
        brand.style.fontSize                = 20;
        brand.style.color                   = new StyleColor(new Color(0.92f, 0.93f, 0.96f));
        brand.style.unityFontStyleAndWeight = FontStyle.Bold;
        brand.style.unityTextAlign          = TextAnchor.MiddleCenter;
        brand.style.marginBottom            = 8f;

        var title = new Label("DEMO COMPLETE");
        title.style.fontSize                = 48;
        title.style.color                   = new StyleColor(new Color(0.96f, 0.65f, 0.14f));
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.unityTextAlign          = TextAnchor.MiddleCenter;
        title.style.marginBottom            = new StyleLength(28);

        var card = new VisualElement();
        card.style.backgroundColor      = new StyleColor(new Color(0.09f, 0.09f, 0.11f, 0.98f));
        card.style.borderTopWidth        = 1f;
        card.style.borderBottomWidth     = 1f;
        card.style.borderLeftWidth       = 1f;
        card.style.borderRightWidth      = 1f;
        card.style.borderTopColor        = new StyleColor(new Color(0.19f, 0.19f, 0.21f, 0.9f));
        card.style.borderBottomColor     = new StyleColor(new Color(0.19f, 0.19f, 0.21f, 0.9f));
        card.style.borderLeftColor       = new StyleColor(new Color(0.19f, 0.19f, 0.21f, 0.9f));
        card.style.borderRightColor      = new StyleColor(new Color(0.19f, 0.19f, 0.21f, 0.9f));
        card.style.borderTopLeftRadius     = 6f;
        card.style.borderTopRightRadius    = 6f;
        card.style.borderBottomLeftRadius  = 6f;
        card.style.borderBottomRightRadius = 6f;
        card.style.paddingTop    = 24f;
        card.style.paddingBottom = 24f;
        card.style.paddingLeft   = 48f;
        card.style.paddingRight  = 48f;
        card.style.minWidth      = 380f;
        card.style.marginBottom  = 28f;

        int baseEarned = gm?.TotalBaseEarned ?? 0;
        int speedBonus = gm?.TotalSpeedBonus ?? 0;
        int comboBonus = gm?.TotalComboBonus ?? 0;
        int teamBonus  = gm?.TotalTeamBonus ?? 0;
        int total      = gm?.Score ?? 0;

        void AddBreakdownRow(string label, string value, bool separator = false, bool isBig = false)
        {
            if (separator)
            {
                var sep = new VisualElement();
                sep.style.height          = 1f;
                sep.style.backgroundColor = new StyleColor(new Color(0.22f, 0.22f, 0.24f));
                sep.style.marginTop       = new StyleLength(10f);
                sep.style.marginBottom    = new StyleLength(10f);
                card.Add(sep);
            }
            var row = new VisualElement();
            row.style.flexDirection  = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.alignItems     = Align.Center;
            row.style.marginBottom   = isBig ? new StyleLength(0f) : new StyleLength(6f);

            var lbl = new Label(label);
            lbl.style.fontSize                = isBig ? 18 : 14;
            lbl.style.color                   = new StyleColor(isBig
                ? new Color(0.94f, 0.94f, 0.96f)
                : new Color(0.55f, 0.60f, 0.65f));
            lbl.style.unityFontStyleAndWeight = isBig ? FontStyle.Bold : FontStyle.Normal;

            var val = new Label(value);
            val.style.fontSize                = isBig ? 24 : 15;
            val.style.color                   = new StyleColor(isBig
                ? new Color(0.96f, 0.65f, 0.14f)
                : new Color(0.85f, 0.85f, 0.90f));
            val.style.unityFontStyleAndWeight = isBig ? FontStyle.Bold : FontStyle.Normal;
            val.style.unityTextAlign          = TextAnchor.MiddleRight;

            row.Add(lbl); row.Add(val);
            card.Add(row);
        }

        AddBreakdownRow("Base income",  $"${baseEarned:N0}");
        AddBreakdownRow("Speed bonus",  speedBonus > 0 ? $"+${speedBonus:N0}" : "—");
        AddBreakdownRow("Combo bonus",  comboBonus > 0 ? $"+${comboBonus:N0}" : "—");
        AddBreakdownRow("Team diversity", teamBonus > 0 ? $"+${teamBonus:N0}" : "—");
        AddBreakdownRow("Flawless orders", (gm?.FlawlessOrders ?? 0).ToString());
        AddBreakdownRow("Best streak", (gm?.BestCombo ?? 0) > 1 ? $"x{gm.BestCombo}" : "—");
        AddBreakdownRow("TOTAL",        $"${total:N0}", separator: true, isBig: true);

        int careerLevel = CareerProgress.Level;
        int levelStart  = CareerProgress.XpAtLevel(careerLevel);
        int levelEnd    = CareerProgress.XpAtLevel(careerLevel + 1);
        int levelXp     = CareerProgress.ReputationXp - levelStart;
        int levelSpan   = levelEnd - levelStart;
        AddBreakdownRow("Career rank",
            $"{CareerProgress.RankTitle}  ·  LV {careerLevel}", separator: true);
        AddBreakdownRow("Next level", $"{levelXp:N0} / {levelSpan:N0} REP");

        var ctaText = new Label("Thank you for playing!\nWishlist the full game on Steam and continue the shift when it releases.");
        ctaText.style.fontSize       = 17;
        ctaText.style.color          = new StyleColor(new Color(0.72f, 0.72f, 0.75f));
        ctaText.style.unityTextAlign = TextAnchor.MiddleCenter;
        ctaText.style.whiteSpace     = WhiteSpace.Normal;
        ctaText.style.marginBottom   = new StyleLength(14);

        var hint = new Label("Your shift is complete.");
        hint.style.fontSize       = 14;
        hint.style.color          = new StyleColor(new Color(0.38f, 0.40f, 0.42f));
        hint.style.unityTextAlign = TextAnchor.MiddleCenter;

        overlay.Add(brand);
        overlay.Add(title);
        overlay.Add(card);
        overlay.Add(ctaText);

        {
            var wishlistBtn = new Button(SteamBranding.OpenWishlist);
            wishlistBtn.text = "WISHLIST ON STEAM";
            wishlistBtn.style.fontSize                = 16;
            wishlistBtn.style.color                   = new StyleColor(new Color(0.08f, 0.065f, 0.035f));
            wishlistBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            wishlistBtn.style.backgroundColor         = new StyleColor(new Color(0.90f, 0.55f, 0.08f));
            wishlistBtn.style.borderTopWidth          = 1f;
            wishlistBtn.style.borderBottomWidth       = 1f;
            wishlistBtn.style.borderLeftWidth         = 1f;
            wishlistBtn.style.borderRightWidth        = 1f;
            wishlistBtn.style.borderTopColor          = new StyleColor(new Color(1f, 0.72f, 0.18f));
            wishlistBtn.style.borderBottomColor       = new StyleColor(new Color(1f, 0.72f, 0.18f));
            wishlistBtn.style.borderLeftColor         = new StyleColor(new Color(1f, 0.72f, 0.18f));
            wishlistBtn.style.borderRightColor        = new StyleColor(new Color(1f, 0.72f, 0.18f));
            wishlistBtn.style.borderTopLeftRadius     = 4f;
            wishlistBtn.style.borderTopRightRadius    = 4f;
            wishlistBtn.style.borderBottomLeftRadius  = 4f;
            wishlistBtn.style.borderBottomRightRadius = 4f;
            wishlistBtn.style.paddingTop    = 12f;
            wishlistBtn.style.paddingBottom = 12f;
            wishlistBtn.style.paddingLeft   = 28f;
            wishlistBtn.style.paddingRight  = 28f;
            wishlistBtn.style.marginBottom  = new StyleLength(14f);
            overlay.Add(wishlistBtn);
        }

        var menuBtn = new Button(GoToMainMenu) { text = "MAIN MENU" };
        menuBtn.style.fontSize = 14;
        menuBtn.style.color = new StyleColor(new Color(0.96f, 0.72f, 0.25f));
        menuBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
        menuBtn.style.backgroundColor = new StyleColor(new Color(0.10f, 0.095f, 0.085f, 0.98f));
        menuBtn.style.borderTopWidth = 1f;
        menuBtn.style.borderBottomWidth = 1f;
        menuBtn.style.borderLeftWidth = 1f;
        menuBtn.style.borderRightWidth = 1f;
        menuBtn.style.borderTopColor = new StyleColor(new Color(0.58f, 0.36f, 0.10f));
        menuBtn.style.borderBottomColor = new StyleColor(new Color(0.58f, 0.36f, 0.10f));
        menuBtn.style.borderLeftColor = new StyleColor(new Color(0.58f, 0.36f, 0.10f));
        menuBtn.style.borderRightColor = new StyleColor(new Color(0.58f, 0.36f, 0.10f));
        menuBtn.style.borderTopLeftRadius = 4f;
        menuBtn.style.borderTopRightRadius = 4f;
        menuBtn.style.borderBottomLeftRadius = 4f;
        menuBtn.style.borderBottomRightRadius = 4f;
        menuBtn.style.paddingTop = 8f;
        menuBtn.style.paddingBottom = 8f;
        menuBtn.style.paddingLeft = 22f;
        menuBtn.style.paddingRight = 22f;
        menuBtn.style.marginBottom = 12f;
        overlay.Add(menuBtn);

        overlay.Add(hint);
        docRoot.Add(overlay);

        UnityEngine.Cursor.lockState = CursorLockMode.None;
        UnityEngine.Cursor.visible   = true;

        StartCoroutine(UITween.FadeIn(overlay, 0.9f));
    }

    // ── GameManager event binding ─────────────────────────────────────────────
    void BindGameManager()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;

        UpdateCareerProgress();

        gm.OnOrderAccepted += order =>
        {
            _assemblerBenchPlaced = 0;
            SetActiveRoleTask(order);
            AddRadioMessage($"Order accepted: {order.OrderName}", "Dispatcher");
            UpdateOrdersCounter(gm.CompletedOrders, 0);
            RefreshTaskList();

            ResetOrderTimer();
            if (order.TimeLimitSeconds > 0f)
                _orderTimerTrack?.RemoveFromClassList("hidden");
        };

        gm.OnOrderCompleted += order =>
        {
            _assemblerBenchPlaced = 0;
            ResetOrderTimer();
            SetIdleRoleTask();
            string rewardDetail = $"+${gm.LastTotalReward:N0}  •  +{CareerProgress.LastXpAward} REP";
            if (gm.LastSpeedBonus > 0) rewardDetail += $"  •  +${gm.LastSpeedBonus:N0} SPEED";
            if (gm.LastComboBonus > 0) rewardDetail += $"  •  x{gm.ComboCount} STREAK";
            if (gm.LastTeamBonus > 0) rewardDetail += $"  •  +${gm.LastTeamBonus:N0} TEAM";

            string gradeTitle = gm.LastOrderGrade switch
            {
                "S" => "FLAWLESS ORDER — S",
                "A" => "EXCELLENT ORDER — A",
                "B" => "GOOD ORDER — B",
                _   => "ORDER COMPLETE — C"
            };
            if (gm.LastLevelUp > 0)
                rewardDetail += $"  •  PROMOTED: {CareerProgress.RankTitle.ToUpperInvariant()}";

            ShowRewardToast(gradeTitle, rewardDetail, gm.LastOrderGrade, gm.LastLevelUp > 0);
            AddRadioMessage($"Completed: {order.OrderName}. +${gm.LastTotalReward}");
            UpdateBudget(gm.Score);
            UpdateOrdersCounter(gm.CompletedOrders, 0);
            UpdateCareerProgress();
            _taskList?.Clear();
            if (_taskHint != null) _taskHint.text = "";
        };

        gm.OnOrderExpired += order =>
        {
            _assemblerBenchPlaced = 0;
            ResetOrderTimer();
            SetIdleRoleTask(orderOverdue: true);
            AddNotification(
                "Deadline missed!",
                $"Order «{order.OrderName}» wasn't completed in time. Combo reset.",
                NotifType.Danger);
            AddRadioMessage($"Deadline: {order.OrderName}", "System");
            UpdateOrdersCounter(gm.CompletedOrders, 0);
            UpdateCareerProgress();
            _taskList?.Clear();
            if (_taskHint != null) _taskHint.text = "";
        };

        gm.OnTimeUpdate   += UpdateOrderTimer;
        gm.OnScoreChanged += score => UpdateBudget(score);
        gm.OnDemoComplete += () => { _demoComplete = true; ShowDemoComplete(); };
    }

    void UpdateCareerProgress()
    {
        if (_repValue == null) return;
        string bestStreak = CareerProgress.BestStreak > 1
            ? $"x{CareerProgress.BestStreak}"
            : "—";
        _repValue.text = $"LV {CareerProgress.Level}";
        _repValue.tooltip = $"{CareerProgress.RankTitle} • {CareerProgress.ReputationXp} REP • "
                          + $"{CareerProgress.CompletedOrders} orders • "
                          + $"{CareerProgress.FlawlessOrders} flawless • "
                          + $"best streak {bestStreak}";
        if (CareerProgress.LastXpAward > 0)
            StartCoroutine(UITween.FlashText(_repValue, new Color(1f, 0.85f, 0.22f), 0.7f));
    }

    void SetActiveRoleTask(OrderDefinition order)
    {
        if (order == null) return;
        bool networked = !SpecializationPenalties.SoloMode;
        bool noPicker = networked && !RoleTaskManager.TeamHasRole(PlayerRoleType.Picker);
        bool noAssembler = networked && !RoleTaskManager.TeamHasRole(PlayerRoleType.Assembler);
        bool noForklift = networked && !RoleTaskManager.TeamHasRole(PlayerRoleType.ForkLiftOperator);

        switch (SpecializationPenalties.GetLocalRole())
        {
            case PlayerRoleType.Picker:
            {
                string hint = "Find the highlighted boxes and deliver them to the assembly bench";
                if (noForklift) hint += " • Use the forklift for upper racks";
                if (noAssembler) hint += " • Install and pack delivered parts";
                SetActiveTask($"Pick parts: {order.OrderName}",
                    hint);
                break;
            }
            case PlayerRoleType.Assembler:
            {
                string hint = "Install delivered parts and pack the completed order";
                if (noPicker) hint += " • Collect highlighted parts when the bench is clear";
                if (noForklift) hint += " • Use the forklift for upper racks";
                SetActiveTask($"Assemble: {order.OrderName}",
                    hint);
                break;
            }
            case PlayerRoleType.ForkLiftOperator:
            {
                string hint = "Handle pallets, upper racks and keep the delivery route clear";
                if (noPicker) hint += " • Collect highlighted parts between lifts";
                if (noAssembler) hint += " • Help install and pack at the bench";
                SetActiveTask($"Move cargo: {order.OrderName}",
                    hint);
                break;
            }
            case PlayerRoleType.Dispatcher:
            {
                string hint = "Monitor the warehouse map, timers and team positions";
                if (noPicker) hint += " • Assist with highlighted parts";
                if (noAssembler) hint += " • Help at the assembly bench";
                if (noForklift) hint += " • Anyone may operate the forklift";
                SetActiveTask($"Coordinate: {order.OrderName}",
                    hint);
                break;
            }
            default:
                SetActiveTask($"Complete: {order.OrderName}", order.ClientName);
                break;
        }
    }

    void SetIdleRoleTask(bool orderOverdue = false)
    {
        bool hasDispatcher = !SpecializationPenalties.SoloMode
                          && RoleTaskManager.TeamHasRole(PlayerRoleType.Dispatcher);
        var role = SpecializationPenalties.GetLocalRole();

        if (!hasDispatcher || role == PlayerRoleType.Dispatcher)
        {
            SetActiveTask(orderOverdue ? "Order overdue" : "Select next order",
                "Choose the next job at the Order Board");
            return;
        }

        switch (role)
        {
            case PlayerRoleType.Picker:
                SetActiveTask("Prepare for next order", "Move near the storage aisles and scan the shelves");
                break;
            case PlayerRoleType.Assembler:
                SetActiveTask("Prepare the assembly bench", "Keep the bench and packing area clear");
                break;
            case PlayerRoleType.ForkLiftOperator:
                SetActiveTask("Stage the warehouse", "Clear travel lanes and position accessible pallets");
                break;
            default:
                SetActiveTask("Waiting for next order", "The Dispatcher is selecting the next job");
                break;
        }
    }

    void ShowRewardToast(string title, string detail, string grade, bool promoted)
    {
        if (_rewardToast == null) return;
        if (_rewardToastRoutine != null) StopCoroutine(_rewardToastRoutine);
        _rewardToastRoutine = StartCoroutine(RewardToastRoutine(title, detail, grade, promoted));
    }

    IEnumerator RewardToastRoutine(string title, string detail, string grade, bool promoted)
    {
        _rewardToast.EnableInClassList("reward-toast--s", grade == "S");
        _rewardToast.EnableInClassList("reward-toast--a", grade == "A");
        _rewardToast.EnableInClassList("reward-toast--b", grade == "B");
        _rewardToast.EnableInClassList("reward-toast--c", grade != "S" && grade != "A" && grade != "B");
        _rewardToast.EnableInClassList("reward-toast--promoted", promoted);
        if (_rewardToastTitle != null) _rewardToastTitle.text = title;
        if (_rewardToastDetail != null) _rewardToastDetail.text = detail;
        _rewardToast.RemoveFromClassList("hidden");
        _rewardToast.style.opacity = 0f;
        yield return UITween.FadeIn(_rewardToast, 0.22f);
        yield return new WaitForSeconds(2.6f);
        yield return UITween.FadeOut(_rewardToast, 0.35f,
            () => _rewardToast.AddToClassList("hidden"));
        _rewardToastRoutine = null;
    }

    void UpdateOrderTimer(float remaining, float total)
    {
        if (_orderTimerFill == null) return;
        float pct = total > 0f ? remaining / total : 1f;
        _orderTimerFill.style.width = new StyleLength(Length.Percent(pct * 100f));

        if (pct <= 0.2f && _orderTimerState != TimerState.Red)
        {
            _orderTimerState = TimerState.Red;
            _orderTimerFill.RemoveFromClassList("order-timer-fill--orange");
            _orderTimerFill.AddToClassList("order-timer-fill--red");
            if (_orderTimerPulse != null) StopCoroutine(_orderTimerPulse);
            if (_activeTaskText != null)
                _orderTimerPulse = StartCoroutine(UITween.PulseText(
                    _activeTaskText,
                    new Color(0.90f, 0.20f, 0.20f),
                    new Color(1.00f, 0.50f, 0.50f),
                    0.55f));
        }
        else if (pct <= 0.5f && _orderTimerState == TimerState.Normal)
        {
            _orderTimerState = TimerState.Orange;
            _orderTimerFill.AddToClassList("order-timer-fill--orange");
        }
    }

    void ResetOrderTimer()
    {
        _orderTimerTrack?.AddToClassList("hidden");
        if (_orderTimerPulse != null) { StopCoroutine(_orderTimerPulse); _orderTimerPulse = null; }
        if (_activeTaskText != null) _activeTaskText.style.color = new StyleColor(StyleKeyword.Null);
        _orderTimerFill?.RemoveFromClassList("order-timer-fill--orange");
        _orderTimerFill?.RemoveFromClassList("order-timer-fill--red");
        if (_orderTimerFill != null)
            _orderTimerFill.style.width = new StyleLength(Length.Percent(100f));
        _orderTimerState = TimerState.Normal;
    }

    /// Displays a delivery-event clock without replacing the active order deadline.
    public void UpdateEventTimer(string owner, string title, float remaining, float total,
                                 string detail, Color color)
    {
        if (_eventTimerBlock == null || string.IsNullOrEmpty(owner)) return;
        _eventTimerOwner = owner;
        _eventTimerBlock.RemoveFromClassList("hidden");

        float safeRemaining = Mathf.Max(0f, remaining);
        float pct = total > 0f ? Mathf.Clamp01(safeRemaining / total) : 0f;
        int seconds = Mathf.CeilToInt(safeRemaining);
        if (_eventTimerTitle != null)  _eventTimerTitle.text = title;
        if (_eventTimerValue != null)  _eventTimerValue.text = $"{seconds / 60:D2}:{seconds % 60:D2}";
        if (_eventTimerDetail != null) _eventTimerDetail.text = detail ?? string.Empty;
        if (_eventTimerFill != null)
        {
            _eventTimerFill.style.width = new StyleLength(Length.Percent(pct * 100f));
            _eventTimerFill.style.backgroundColor = new StyleColor(
                pct <= 0.2f ? new Color(0.90f, 0.20f, 0.20f) : color);
        }
        _eventTimerBlock.style.borderLeftColor = new StyleColor(
            pct <= 0.2f ? new Color(0.90f, 0.20f, 0.20f) : color);
        if (_eventTimerTitle != null) _eventTimerTitle.style.color = new StyleColor(color);
        if (_eventTimerValue != null) _eventTimerValue.style.color = new StyleColor(
            pct <= 0.2f ? new Color(1f, 0.45f, 0.45f) : color);
    }

    public void HideEventTimer(string owner)
    {
        if (!string.IsNullOrEmpty(_eventTimerOwner) && _eventTimerOwner != owner) return;
        _eventTimerBlock?.AddToClassList("hidden");
        _eventTimerOwner = null;
    }

    // ── Tutorial banner ───────────────────────────────────────────────────────

    public void ShowTutorial(string stepLabel, string text)
        => ShowTutorialFull(stepLabel, "", text, null, null, false);

    public void ShowTutorialFull(string stepLabel, string title, string body,
                                  string eventKeyHint, string[] checklistLabels,
                                  bool canContinue = false)
    {
        if (_tutorialStepNum != null) _tutorialStepNum.text = stepLabel;
        if (_tutorialTitle   != null) _tutorialTitle.text   = title;
        if (_tutorialText    != null) _tutorialText.text     = body;

        if (_tutorialNextBtn != null)
        {
            if (canContinue) _tutorialNextBtn.RemoveFromClassList("hidden");
            else             _tutorialNextBtn.AddToClassList("hidden");
        }
        _tutorialWaiting?.AddToClassList("hidden");
        _tutorialContinue?.AddToClassList("hidden");

        if (checklistLabels != null)
        {
            _tutorialKeyRow?.AddToClassList("hidden");
            SetupChecklist(checklistLabels);
        }
        else
        {
            HideChecklist();
            if (_tutorialKeyRow != null)
            {
                bool hasKey = !string.IsNullOrEmpty(eventKeyHint);
                if (hasKey)
                {
                    _tutorialKeyRow.RemoveFromClassList("hidden");
                    if (_tutorialKey != null) _tutorialKey.text = eventKeyHint;
                }
                else
                {
                    _tutorialKeyRow.AddToClassList("hidden");
                }
            }
        }

        if (_tutorialBanner == null) return;
        SetTutorialFocus(true);
        if (_tutorialBanner.ClassListContains("hidden"))
        {
            _tutorialBanner.RemoveFromClassList("hidden");
            _tutorialBanner.style.opacity = 0f;
            StartCoroutine(UITween.FadeIn(_tutorialBanner, 0.35f));
        }
    }

    public void HideTutorial()
    {
        if (_tutorialBanner == null || _tutorialBanner.ClassListContains("hidden")) return;
        StartCoroutine(UITween.FadeOut(_tutorialBanner, 0.3f,
            () =>
            {
                _tutorialBanner.AddToClassList("hidden");
                SetTutorialFocus(false);
            }));
    }

    void SetTutorialFocus(bool focused)
    {
        // Tutorial cards must not remove operational information. Charging failures, current
        // tasks and Dispatcher messages remain visible while a tutorial step is on screen.
        _leftCol?.RemoveFromClassList("hidden");
        _rightCol?.RemoveFromClassList("hidden");
        if (_radioHudLog?.parent != null)
            _radioHudLog.parent.EnableInClassList("hidden", focused);
    }

    void SetupChecklist(string[] labels)
    {
        if (_tutorialChecklist == null) return;
        _tutorialChecklist.Clear();
        _checklistChips = new Label[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            var chip = new Label(labels[i]);
            chip.AddToClassList("tut-check-chip");
            _tutorialChecklist.Add(chip);
            _checklistChips[i] = chip;
        }
        _tutorialChecklist.RemoveFromClassList("hidden");
    }

    void HideChecklist()
    {
        _tutorialChecklist?.AddToClassList("hidden");
        _checklistChips = null;
    }

    public void ChecklistTick(int index)
    {
        if (_checklistChips == null || index >= _checklistChips.Length) return;
        _checklistChips[index].AddToClassList("tut-check-chip--done");
    }

    void BindBench()
    {
        if (AssemblyBench.Instance != null)
        {
            AssemblyBench.Instance.OnBenchChanged += RefreshTaskList;
            AssemblyBench.Instance.OnBenchChanged += NotifyAssemblerBenchProgress;
        }
    }

    void NotifyAssemblerBenchProgress()
    {
        if (SpecializationPenalties.SoloMode
            || SpecializationPenalties.GetLocalRole() != PlayerRoleType.Assembler)
            return;

        var order = GameManager.Instance?.ActiveOrder;
        var bench = AssemblyBench.Instance;
        if (order == null || bench == null) { _assemblerBenchPlaced = 0; return; }

        int placed = 0;
        int total = 0;
        foreach (var requirement in order.RequiredParts)
        {
            if (requirement.Part == null) continue;
            total += requirement.Count;
            placed += bench.GetPlacedCount(requirement.Part);
        }

        bool newPartArrived = placed > _assemblerBenchPlaced;
        _assemblerBenchPlaced = placed;
        if (!newPartArrived || placed >= total) return;

        AddNotification(
            "Part at assembly bench",
            $"{placed}/{total} installed — return when ready, or keep helping the team",
            NotifType.Success);
        SetActiveTask($"Bench updated: {order.OrderName}",
            $"{placed}/{total} parts installed • Continue assisting until the next delivery");
    }

    public void NotifyAssemblerBenchReady()
    {
        bool hasAssembler = !SpecializationPenalties.SoloMode
                         && RoleTaskManager.TeamHasRole(PlayerRoleType.Assembler);
        bool localAssembler = SpecializationPenalties.GetLocalRole() == PlayerRoleType.Assembler;
        if (hasAssembler && !localAssembler) return;

        AddNotification(
            localAssembler ? "Assembly ready" : "Bench ready",
            "All parts installed — return to the bench and press [E] to pack the order",
            NotifType.Success);

        if (localAssembler && GameManager.Instance?.ActiveOrder != null)
            SetActiveTask($"Pack now: {GameManager.Instance.ActiveOrder.OrderName}",
                "All components are installed • Return to the assembly bench");
    }
}
