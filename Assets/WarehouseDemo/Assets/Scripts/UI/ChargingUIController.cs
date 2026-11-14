using UnityEngine;
using UnityEngine.UIElements;

/// Charging selection overlay, active-charge readout, and top-bar battery indicator.
[RequireComponent(typeof(UIDocument))]
public sealed class ChargingUIController : MonoBehaviour
{
    public static ChargingUIController Instance { get; private set; }

    public static ChargingUIController Ensure()
    {
        if (Instance != null)
        {
            Instance.BindUi();
            return Instance;
        }
        var hud = Object.FindAnyObjectByType<HUDController>(FindObjectsInactive.Include);
        if (hud == null) return null;
        return hud.GetComponent<ChargingUIController>() ?? hud.gameObject.AddComponent<ChargingUIController>();
    }

    VisualElement _root;
    VisualElement _selection;
    VisualElement _active;
    Label _selectionBattery;
    Label _selectionHint;
    Label _slowDetail;
    Label _fastDetail;
    Label _activePercent;
    Label _activeMode;
    Label _activeCost;
    Label _topPercent;
    Button _slowButton;
    Button _fastButton;
    Button _cancelButton;
    ChargingStation _selectedStation;
    RobotBatteryController _battery;
    float _sessionStartCharge;
    bool _selectionOpen;
    bool _activeOpen;

    void Awake()
    {
        Instance = this;
        BindUi();
    }

    bool BindUi()
    {
        var currentRoot = GetComponent<UIDocument>()?.rootVisualElement;
        if (currentRoot == null) return false;
        if (_root == currentRoot && _selection != null && _active != null) return true;

        UnbindButtons();
        _root = currentRoot;
        _selection = _root.Q<VisualElement>("charging-selection");
        _active = _root.Q<VisualElement>("charging-active");
        _selectionBattery = _root.Q<Label>("charging-selection-battery");
        _selectionHint = _root.Q<Label>("charging-selection-hint");
        _slowDetail = _root.Q<Label>("charging-slow-detail");
        _fastDetail = _root.Q<Label>("charging-fast-detail");
        _activePercent = _root.Q<Label>("charging-active-percent");
        _activeMode = _root.Q<Label>("charging-active-mode");
        _activeCost = _root.Q<Label>("charging-active-cost");
        _slowButton = _root.Q<Button>("charging-slow");
        _fastButton = _root.Q<Button>("charging-fast");
        _cancelButton = _root.Q<Button>("charging-cancel");
        _slowButton?.RegisterCallback<ClickEvent>(OnSlowClicked);
        _fastButton?.RegisterCallback<ClickEvent>(OnFastClicked);
        _cancelButton?.RegisterCallback<ClickEvent>(OnCancelClicked);
        if (_selection != null)
            _selection.style.display = _selectionOpen ? DisplayStyle.Flex : DisplayStyle.None;
        if (_active != null)
            _active.style.display = _activeOpen ? DisplayStyle.Flex : DisplayStyle.None;
        _topPercent = _root.Q<Label>("battery-percent");
        return _selection != null && _active != null;
    }

    void UnbindButtons()
    {
        _slowButton?.UnregisterCallback<ClickEvent>(OnSlowClicked);
        _fastButton?.UnregisterCallback<ClickEvent>(OnFastClicked);
        _cancelButton?.UnregisterCallback<ClickEvent>(OnCancelClicked);
        _slowButton = null;
        _fastButton = null;
        _cancelButton = null;
    }

    void OnSlowClicked(ClickEvent _) => SelectMode(false);
    void OnFastClicked(ClickEvent _) => SelectMode(true);
    void OnCancelClicked(ClickEvent _) => HideSelection();

    void OnDestroy()
    {
        UnbindButtons();
        if (Instance == this) Instance = null;
    }

    public void ShowSelection(ChargingStation station, RobotBatteryController battery)
    {
        if (station == null || battery == null || !BindUi()) return;
        _selectedStation = station;
        _battery = battery;
        float missing = battery.MaxChargePercent - battery.ChargePercent;
        _selectionBattery.text = $"BATTERY  {battery.ChargePercent:0}% / {battery.MaxChargePercent:0}%";
        if (_selectionHint != null)
            _selectionHint.text = $"Choose a charging mode. Press {InputHints.Active(GameBindingAction.Interact)} again to disconnect at any time.";
        _slowDetail.text = $"Economical · {RobotBatteryController.SlowRatePerSecond:0.##}%/sec · ${RobotBatteryController.SlowPricePerPercent} per 1%\n" +
                           $"Full in {missing / RobotBatteryController.SlowRatePerSecond:0}s · approx. ${Mathf.CeilToInt(missing * RobotBatteryController.SlowPricePerPercent):N0}";
        _fastDetail.text = $"4× faster · {RobotBatteryController.FastRatePerSecond:0.##}%/sec · ${RobotBatteryController.FastPricePerPercent} per 1%\n" +
                           $"Full in {missing / RobotBatteryController.FastRatePerSecond:0}s · approx. ${Mathf.CeilToInt(missing * RobotBatteryController.FastPricePerPercent):N0}";
        bool wasOpen = _selectionOpen;
        _selectionOpen = true;
        _selection.style.display = DisplayStyle.Flex;
        if (!wasOpen) UIManager.OpenPanel();
    }

    public void HideSelection()
    {
        BindUi();
        if (_selection != null) _selection.style.display = DisplayStyle.None;
        _selectedStation = null;
        if (_selectionOpen) UIManager.ClosePanel();
        _selectionOpen = false;
    }

    void SelectMode(bool fast)
    {
        var station = _selectedStation;
        var battery = _battery;
        HideSelection();
        if (station == null || battery == null) return;
        _sessionStartCharge = battery.ChargePercent;
        battery.RequestStart(station, fast);
    }

    public void ShowCharging()
    {
        _activeOpen = true;
        if (BindUi() && _active != null) _active.style.display = DisplayStyle.Flex;
    }

    public void HideCharging()
    {
        _activeOpen = false;
        BindUi();
        if (_active != null) _active.style.display = DisplayStyle.None;
    }

    public void UpdateBattery(RobotBatteryController battery)
    {
        if (battery == null || !BindUi()) return;
        float pct = battery.ChargePercent;
        if (_topPercent != null) _topPercent.text = $"{pct:0}% / {battery.MaxChargePercent:0}%";

        if (!battery.IsCharging) return;
        if (_activePercent != null) _activePercent.text = $"{pct:0}%";
        if (_activeMode != null) _activeMode.text = battery.IsFastCharging ? "FAST CHARGE" : "SLOW CHARGE";
        int price = battery.IsFastCharging ? RobotBatteryController.FastPricePerPercent : RobotBatteryController.SlowPricePerPercent;
        if (_activeCost != null) _activeCost.text = $"SPENT  ${Mathf.Max(0, Mathf.FloorToInt((pct - _sessionStartCharge) * price)):N0}";
    }
}
