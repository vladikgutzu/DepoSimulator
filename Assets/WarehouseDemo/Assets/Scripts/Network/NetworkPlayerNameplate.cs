using TMPro;
using UnityEngine;

/// <summary>
/// Lightweight world-space label for a remote network player. The label is built at runtime so
/// every player prefab shares the same presentation and no scene/prefab references can drift.
/// </summary>
public sealed class NetworkPlayerNameplate : MonoBehaviour
{
    const float MaxVisibleDistance = 28f;
    const int   MaxNicknameCharacters = 10;
    const float HorizontalPadding = 12f;
    const float MinPlateWidth = 86f;
    const float MaxPlateWidth = 170f;
    const float PlateHeight = 44f;

    static TMP_FontAsset _gameFont;

    NetworkPlayer _player;
    Canvas _canvas;
    RectTransform _plateRect;
    TextMeshProUGUI _nameText;
    TextMeshProUGUI _roleText;
    Camera _camera;

    public void Initialize(NetworkPlayer player)
    {
        _player = player;
        if (_canvas == null)
            BuildLabel();

        Refresh();
    }

    public void Refresh()
    {
        if (_player == null || _nameText == null || _roleText == null) return;

        _nameText.text = ShortenNickname(_player.PlayerName);
        _roleText.text = RoleName(_player.RoleType);
        _roleText.color = PlayerRoleColors.For(_player.RoleType);

        ResizePlateToContent();
    }

    void LateUpdate()
    {
        if (_canvas == null || _player == null) return;

        if (_camera == null || !_camera.isActiveAndEnabled)
            _camera = Camera.main;

        if (_camera == null)
        {
            _canvas.enabled = false;
            return;
        }

        float distance = Vector3.Distance(_camera.transform.position, transform.position);
        _canvas.enabled = distance <= MaxVisibleDistance;
        if (!_canvas.enabled) return;

        // World-space canvases face along their forward axis. Looking away from the camera keeps
        // TMP readable instead of showing its mirrored back face.
        Vector3 awayFromCamera = transform.position - _camera.transform.position;
        if (awayFromCamera.sqrMagnitude > 0.001f)
            _canvas.transform.rotation = Quaternion.LookRotation(awayFromCamera, Vector3.up);
    }

    void BuildLabel()
    {
        var root = new GameObject("Player Nameplate", typeof(RectTransform), typeof(Canvas));
        root.transform.SetParent(transform, false);

        _plateRect = (RectTransform)root.transform;
        _plateRect.localPosition = new Vector3(0f, 2.35f, 0f);
        _plateRect.sizeDelta = new Vector2(MinPlateWidth, PlateHeight);
        _plateRect.localScale = Vector3.one * 0.005f;

        _canvas = root.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.sortingOrder = 20;

        var background = root.AddComponent<UnityEngine.UI.Image>();
        background.color = new Color(0.035f, 0.04f, 0.05f, 0.82f);
        background.raycastTarget = false;

        _nameText = CreateText(root.transform, "Name", 8f, 22f, 18f);
        _nameText.fontStyle = FontStyles.Bold;
        _nameText.color = Color.white;

        _roleText = CreateText(root.transform, "Role", -11f, 16f, 12f);
        _roleText.fontStyle = FontStyles.Bold;
    }

    void ResizePlateToContent()
    {
        if (_plateRect == null) return;

        float nameWidth = _nameText.GetPreferredValues(_nameText.text).x;
        float roleWidth = _roleText.GetPreferredValues(_roleText.text).x;
        float width = Mathf.Clamp(
            Mathf.Ceil(Mathf.Max(nameWidth, roleWidth) + HorizontalPadding * 2f),
            MinPlateWidth,
            MaxPlateWidth);

        _plateRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
    }

    static string ShortenNickname(string playerName)
    {
        string value = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
        int[] elements = System.Globalization.StringInfo.ParseCombiningCharacters(value);
        if (elements.Length <= MaxNicknameCharacters) return value;

        int keptCharacters = MaxNicknameCharacters - 1;
        return value.Substring(0, elements[keptCharacters]) + "…";
    }

    static TextMeshProUGUI CreateText(Transform parent, string objectName, float y,
        float height, float fontSize)
    {
        var go = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(1f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, y);
        rect.sizeDelta = new Vector2(-HorizontalPadding * 2f, height);

        var text = go.GetComponent<TextMeshProUGUI>();
        text.font = ResolveGameFont();
        text.fontSize = fontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    static TMP_FontAsset ResolveGameFont()
    {
        if (_gameFont != null) return _gameFont;

        foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font == null || !font.name.StartsWith(
                    "ZillaSlab-Regular", System.StringComparison.OrdinalIgnoreCase)) continue;
            _gameFont = font;
            break;
        }

        if (_gameFont == null)
            _gameFont = TMP_Settings.defaultFontAsset;
        return _gameFont;
    }

    static string RoleName(PlayerRoleType role) => role switch
    {
        PlayerRoleType.Picker           => "PICKER",
        PlayerRoleType.Assembler        => "ASSEMBLER",
        PlayerRoleType.ForkLiftOperator => "FORKLIFT",
        PlayerRoleType.Dispatcher       => "DISPATCHER",
        _                               => "NO ROLE",
    };
}
