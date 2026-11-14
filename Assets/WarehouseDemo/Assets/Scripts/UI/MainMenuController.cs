using System.Collections;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class MainMenuController : MonoBehaviour
{
    public static MainMenuController Instance { get; private set; }

    [SerializeField] GameObject _hudDocument;

    VisualElement _menuCard;
    VisualElement _settingsCard;
    Button[] _settingsTabs;
    VisualElement[] _settingsPages;

    // Set to true by the standalone MainMenu scene before loading the game scene.
    public static bool AutoStart { get; set; }

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }

        if (AutoStart)
        {
            AudioManager.SetContext(GameAudioContext.Gameplay);
            AutoStart = false;
            if (_hudDocument != null) _hudDocument.SetActive(true);
            UIManager.ClosePanel();
            // Don't set Instance so GameManager.Start() auto-calls StartGame().
            Destroy(gameObject);
            return;
        }

        Instance = this;
        AudioManager.SetContext(GameAudioContext.MainMenu);
        if (_hudDocument != null) _hudDocument.SetActive(false);
        UIManager.OpenPanel();
    }

    void Start()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;
        UiAudioFeedback.Bind(root);
        _menuCard = root.Q<VisualElement>("main-card");
        _settingsCard = root.Q<VisualElement>("settings-card");

        var careerLevel    = root.Q<Label>("career-level");
        var careerTitle    = root.Q<Label>("career-title");
        var careerProgress = root.Q<Label>("career-progress");
        var careerOrders   = root.Q<Label>("career-orders");
        var careerBest     = root.Q<Label>("career-best");
        int level          = CareerProgress.Level;
        int levelStart     = CareerProgress.XpAtLevel(level);
        int levelEnd       = CareerProgress.XpAtLevel(level + 1);
        if (careerLevel != null) careerLevel.text = $"LV {level}";
        if (careerTitle != null) careerTitle.text = CareerProgress.RankTitle.ToUpperInvariant();
        if (careerProgress != null)
            careerProgress.text = $"{CareerProgress.ReputationXp - levelStart:N0} / "
                                + $"{levelEnd - levelStart:N0} REP";
        if (careerOrders != null)
            careerOrders.text = $"{CareerProgress.CompletedOrders:N0} ORDERS";
        if (careerBest != null)
            careerBest.text = $"{CareerProgress.FlawlessOrders:N0} S  •  "
                            + (CareerProgress.BestStreak > 1
                                ? $"BEST x{CareerProgress.BestStreak}"
                                : "BEST —");

        root.Q<Button>("btn-singleplayer")?.RegisterCallback<ClickEvent>(_ => StartSingleplayer());
        root.Q<Button>("btn-multiplayer")?.RegisterCallback<ClickEvent>(_ => OnMultiplayerClicked());
        root.Q<Button>("btn-settings")?.RegisterCallback<ClickEvent>(_ => ShowSettings());
        root.Q<Button>("btn-wishlist")?.RegisterCallback<ClickEvent>(_ => SteamBranding.OpenWishlist());
        root.Q<Button>("btn-settings-back")?.RegisterCallback<ClickEvent>(_ => HideSettings());
        root.Q<Button>("btn-exit")?.RegisterCallback<ClickEvent>(_ => ExitGame());

        var sensitivity = root.Q<Slider>("look-sensitivity");
        var sensitivityValue = root.Q<Label>("look-sensitivity-value");
        if (sensitivity != null)
        {
            sensitivity.lowValue  = GameSettings.MinLookSensitivity;
            sensitivity.highValue = GameSettings.MaxLookSensitivity;
            sensitivity.SetValueWithoutNotify(GameSettings.LookSensitivity);
            if (sensitivityValue != null)
                sensitivityValue.text = $"{GameSettings.LookSensitivity:0.00}×";
            sensitivity.RegisterValueChangedCallback(evt =>
            {
                GameSettings.LookSensitivity = evt.newValue;
                if (sensitivityValue != null) sensitivityValue.text = $"{evt.newValue:0.00}×";
            });
        }

        var invertY = root.Q<Toggle>("invert-y");
        if (invertY != null)
        {
            invertY.SetValueWithoutNotify(GameSettings.InvertY);
            invertY.RegisterValueChangedCallback(evt => GameSettings.InvertY = evt.newValue);
        }

        BindAudioSettings(root);
        BindSettingsTabs(root);

        var bindingsHost = root.Q<VisualElement>("controls-binding-host");
        if (bindingsHost != null)
        {
            bindingsHost.Clear();
            bindingsHost.Add(ControlsBindingTable.Build());
        }

        StartCoroutine(CardEntrance());
    }

    static void BindAudioSettings(VisualElement root)
    {
        var master = root.Q<Slider>("master-volume");
        var masterValue = root.Q<Label>("master-volume-value");
        if (master != null)
        {
            master.SetValueWithoutNotify(GameSettings.MasterVolume);
            SetPercent(masterValue, GameSettings.MasterVolume);
            master.RegisterValueChangedCallback(evt =>
            {
                GameSettings.MasterVolume = evt.newValue;
                SetPercent(masterValue, evt.newValue);
            });
        }

        var music = root.Q<Slider>("music-volume");
        var musicValue = root.Q<Label>("music-volume-value");
        if (music != null)
        {
            music.SetValueWithoutNotify(GameSettings.MusicVolume);
            SetPercent(musicValue, GameSettings.MusicVolume);
            music.RegisterValueChangedCallback(evt =>
            {
                GameSettings.MusicVolume = evt.newValue;
                SetPercent(musicValue, evt.newValue);
            });
        }

        var sounds = root.Q<Slider>("sound-volume");
        var soundsValue = root.Q<Label>("sound-volume-value");
        if (sounds != null)
        {
            sounds.SetValueWithoutNotify(GameSettings.SoundVolume);
            SetPercent(soundsValue, GameSettings.SoundVolume);
            sounds.RegisterValueChangedCallback(evt =>
            {
                GameSettings.SoundVolume = evt.newValue;
                SetPercent(soundsValue, evt.newValue);
            });
        }
    }

    static void SetPercent(Label label, float normalized)
    {
        if (label != null) label.text = $"{Mathf.RoundToInt(normalized * 100f)}%";
    }

    void BindSettingsTabs(VisualElement root)
    {
        _settingsTabs = new[]
        {
            root.Q<Button>("settings-tab-audio"),
            root.Q<Button>("settings-tab-camera"),
            root.Q<Button>("settings-tab-controls")
        };
        _settingsPages = new[]
        {
            root.Q<VisualElement>("settings-page-audio"),
            root.Q<VisualElement>("settings-page-camera"),
            root.Q<VisualElement>("settings-page-controls")
        };

        for (int i = 0; i < _settingsTabs.Length; i++)
        {
            int tabIndex = i;
            _settingsTabs[i]?.RegisterCallback<ClickEvent>(_ => SelectSettingsTab(tabIndex));
        }
        SelectSettingsTab(0);
    }

    void SelectSettingsTab(int selectedIndex)
    {
        if (_settingsTabs == null || _settingsPages == null) return;
        for (int i = 0; i < _settingsTabs.Length; i++)
        {
            _settingsTabs[i]?.EnableInClassList("settings-tab--active", i == selectedIndex);
            _settingsPages[i]?.EnableInClassList("hidden", i != selectedIndex);
        }
    }

    void ShowSettings()
    {
        SelectSettingsTab(0);
        _menuCard?.AddToClassList("hidden");
        _settingsCard?.RemoveFromClassList("hidden");
    }

    void HideSettings()
    {
        GameInputBindings.CancelRebind();
        _settingsCard?.AddToClassList("hidden");
        _menuCard?.RemoveFromClassList("hidden");
    }

    IEnumerator CardEntrance()
    {
        yield return null;
        if (_menuCard != null)
            yield return StartCoroutine(UITween.BounceIn(_menuCard, 0.55f));
    }

    void StartSingleplayer()
    {
        AudioManager.SetContext(GameAudioContext.Gameplay);
        AutoStart = true;
        // Asynchronous, so the loading screen actually draws while the warehouse comes up.
        // The old synchronous LoadScene blocked the main thread and froze the menu on screen
        // instead, which is what made a slow load indistinguishable from a hung game.
        LoadingScreen.LoadScene(GameScenes.Warehouse, "Entering the warehouse...");
    }

    void OnMultiplayerClicked()
    {
        LobbyController.Instance?.Show();
    }

    void ExitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    /// Called by LobbyController when host fires OnGameStarted.
    public void StartMultiplayer()
    {
        AudioManager.SetContext(GameAudioContext.Gameplay);
        // Both host and client are already in the game scene — no LoadScene needed.
        // LoadScene would create a second NetworkManager in DontDestroyOnLoad, which
        // causes NGO to despawn all NetworkObjects and break the session.
        if (_hudDocument != null) _hudDocument.SetActive(true);
        UIManager.ClosePanel();

        // No scene load on this path, but the reset below and the first frame of spawned players
        // are still a visible hitch. LoadingScreen runs the hide on its own object, because this
        // one is destroyed at the end of the method.
        LoadingScreen.HoldForFrames("Starting the shift...");

        // Entering a room reuses the scene rather than reloading it, so everything below is
        // still holding the previous room's state when a second room is created in the same
        // play session -- an order that looks active before anyone took one, parts left on the
        // bench, items still in hand. Wipe it all before the session starts.
        GameManager.Instance?.ResetSession();
        AssemblyBench.Instance?.ClearBench();
        // Nothing re-renders the task panel on its own -- it only ever changes on a
        // GameManager order event, so the old "Complete: ..." line would otherwise persist.
        HUDController.Instance?.SetActiveTask("No active task", "Take an order from the board");

        // The singleplayer path gets GameStarted for free: LoadScene re-runs Awake with
        // AutoStart set, Instance is deliberately left null, and GameManager.Start() calls
        // StartGame() itself. Multiplayer deliberately skips LoadScene, so that Start()
        // already ran back in the menu -- with Instance non-null -- and nothing else ever
        // starts the game. Without this the order timer never ticks, orders never expire,
        // and HUDController.IntroSequence() waits on GameStarted forever, so the HUD never
        // unfolds and an accepted order has nowhere to show up.
        GameManager.Instance?.StartGame();

        string roles = string.Join(", ", NetworkPlayer.All.Select(np => $"{np.PlayerName}={np.RoleType}"));
        Debug.Log($"[MainMenuController] Game starting, selected roles: {roles}");

        Destroy(gameObject);
    }
}

/// <summary>
/// Adds one shared click-audio callback to a UI Toolkit root. Event delegation covers both
/// authored and runtime-created buttons without allocating one callback per button.
/// </summary>
public static class UiAudioFeedback
{
    sealed class BindingMarker { }

    static readonly ConditionalWeakTable<VisualElement, BindingMarker> BoundRoots = new();

    public static void Bind(VisualElement root)
    {
        if (root == null || BoundRoots.TryGetValue(root, out _)) return;

        BoundRoots.Add(root, new BindingMarker());
        // Capture before overlay containers stop propagation to keep nested menu buttons audible.
        root.RegisterCallback<ClickEvent>(OnClick, TrickleDown.TrickleDown);
    }

    static void OnClick(ClickEvent evt)
    {
        var current = evt.target as VisualElement;
        while (current != null)
        {
            if (current is Button button)
            {
                if (button.enabledInHierarchy)
                    AudioManager.Instance?.PlayUiButtonClick();
                return;
            }

            current = current.parent;
        }
    }
}
