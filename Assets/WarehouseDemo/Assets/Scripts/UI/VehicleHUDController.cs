using UnityEngine;
using TMPro;

/// Attach to any persistent GameObject (e.g. GameplayCanvas).
/// Finds VehiclePanel by name, shows/hides it when player boards a forklift.
public class VehicleHUDController : MonoBehaviour
{
    public static VehicleHUDController Instance { get; private set; }

    private GameObject          _panel;
    private TextMeshProUGUI     _vehicleNameTxt;
    private TextMeshProUGUI     _forkHeightTxt;
    private RectTransform       _fuelFill;
    private RectTransform       _chargeFill;
    private TextMeshProUGUI     _fuelPctTxt;
    private TextMeshProUGUI     _chargePctTxt;
    private int                 _lastForkHeightDecimetres = int.MinValue;

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        _panel = GameObject.Find("VehiclePanel");
        if (_panel == null) { Debug.LogWarning("[VehicleHUD] VehiclePanel not found in scene."); return; }

        _vehicleNameTxt = FindTMP("VehicleName");
        _forkHeightTxt  = FindTMP("ForkHeight");
        _fuelPctTxt     = FindTMP("FuelPct");
        _chargePctTxt   = FindTMP("ChargePct");
        _fuelFill       = FindRT("FuelFill");
        _chargeFill     = FindRT("ChargeFill");
    }

    public void Show(string vehicleName, float fuelNorm = 1f, float chargeNorm = 0f)
    {
        if (_panel == null) return;
        _panel.SetActive(true);
        if (_vehicleNameTxt != null) _vehicleNameTxt.text = vehicleName;
        SetBar(_fuelFill,   _fuelPctTxt,   fuelNorm);
        SetBar(_chargeFill, _chargePctTxt, chargeNorm);
    }

    public void Hide()
    {
        if (_panel != null) _panel.SetActive(false);
    }

    public void UpdateForkHeight(float heightAboveGround)
    {
        // ReachTruckController reports every frame, but the HUD displays only tenths of a metre.
        // Rebuilding identical TMP geometry every frame was pure main-thread work while driving.
        int decimetres = Mathf.RoundToInt(heightAboveGround * 10f);
        if (decimetres == _lastForkHeightDecimetres) return;
        _lastForkHeightDecimetres = decimetres;

        if (_forkHeightTxt != null)
            _forkHeightTxt.text = (decimetres * 0.1f).ToString(
                "F1", System.Globalization.CultureInfo.InvariantCulture) + " m";
    }

    static void SetBar(RectTransform fill, TextMeshProUGUI pctTxt, float norm)
    {
        if (fill != null)
        {
            fill.anchorMax = new Vector2(Mathf.Clamp01(norm), 1f);
            fill.sizeDelta = Vector2.zero;
        }
        if (pctTxt != null)
            pctTxt.text = Mathf.RoundToInt(norm * 100) + "%";
    }

    TextMeshProUGUI FindTMP(string goName)
    {
        foreach (var t in _panel.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (t.gameObject.name == goName) return t;
        return null;
    }

    RectTransform FindRT(string goName)
    {
        foreach (var rt in _panel.GetComponentsInChildren<RectTransform>(true))
            if (rt.gameObject.name == goName) return rt;
        return null;
    }
}
