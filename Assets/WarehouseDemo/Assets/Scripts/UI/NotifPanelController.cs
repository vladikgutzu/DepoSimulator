using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Attach to any persistent GameObject (e.g. GameplayCanvas).
/// Wires the Hide/Show button to toggle the notification list.
public class NotifPanelController : MonoBehaviour
{
    public static NotifPanelController Instance { get; private set; }

    private GameObject          _content;
    private TextMeshProUGUI     _btnText;
    private bool                _visible = true;

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        var panel = GameObject.Find("NotifPanel");
        if (panel == null) { Debug.LogWarning("[NotifPanel] NotifPanel not found."); return; }

        _content = FindChild(panel.transform, "NotifContent");

        var hideBtn = FindChild(panel.transform, "HideBtn");
        if (hideBtn != null)
        {
            _btnText = hideBtn.GetComponentInChildren<TextMeshProUGUI>();
            var btn  = hideBtn.GetComponent<Button>();
            if (btn != null) btn.onClick.AddListener(Toggle);
        }
    }

    public void Toggle()
    {
        _visible = !_visible;
        if (_content != null) _content.SetActive(_visible);
        if (_btnText  != null) _btnText.text = _visible ? "Hide" : "Show";
    }

    public void AddNotification(string title, string sub)
    {
        // Runtime notifications can be added here in future
    }

    static GameObject FindChild(Transform root, string name)
    {
        foreach (Transform t in root)
        {
            if (t.name == name) return t.gameObject;
            var found = FindChild(t, name);
            if (found != null) return found;
        }
        return null;
    }
}
