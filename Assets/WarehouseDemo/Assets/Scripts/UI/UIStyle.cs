using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Central design tokens — Shift Happens: Together palette.
public static class UIStyle
{
    // ── Palette (warm dark, yellow accent) ───────────────────────────────────
    public static readonly Color Bg        = new Color(0.078f, 0.078f, 0.078f, 0.98f);  // #141414
    public static readonly Color Panel     = new Color(0.114f, 0.114f, 0.114f, 0.97f);  // #1D1D1D
    public static readonly Color Card      = new Color(0.153f, 0.153f, 0.153f, 1.00f);  // #272727
    public static readonly Color CardAlt   = new Color(0.098f, 0.098f, 0.098f, 1.00f);  // #191919
    public static readonly Color Border    = new Color(0.220f, 0.220f, 0.220f, 1.00f);  // #383838
    public static readonly Color Accent    = new Color(0.961f, 0.651f, 0.137f, 1.00f);  // #F5A623 yellow
    public static readonly Color AccentHi  = new Color(1.000f, 0.757f, 0.322f, 1.00f);  // #FFBE52
    public static readonly Color Green     = new Color(0.298f, 0.749f, 0.278f, 1.00f);  // #4CBF47
    public static readonly Color Yellow    = new Color(0.961f, 0.651f, 0.137f, 1.00f);  // same as Accent
    public static readonly Color Orange    = new Color(0.980f, 0.502f, 0.102f, 1.00f);  // #FA801A damage bar
    public static readonly Color Red       = new Color(0.898f, 0.224f, 0.208f, 1.00f);  // #E53935
    public static readonly Color Info      = new Color(0.392f, 0.710f, 0.965f, 1.00f);  // #64B5F6
    public static readonly Color TextPri   = new Color(0.961f, 0.961f, 0.961f, 1.00f);  // #F5F5F5
    public static readonly Color TextSec   = new Color(0.667f, 0.667f, 0.667f, 1.00f);  // #AAAAAA
    public static readonly Color TextDim   = new Color(0.400f, 0.400f, 0.400f, 1.00f);  // #666666
    public static readonly Color SteamBg   = new Color(0.114f, 0.306f, 0.867f, 1.00f);  // #1D4ED8
    public static readonly Color GreenBtn  = new Color(0.173f, 0.557f, 0.141f, 1.00f);  // #2C8E24
    public static readonly Color OrangeBtn = new Color(0.784f, 0.392f, 0.000f, 1.00f);  // #C86400
    public static readonly Color DarkBtn   = new Color(0.153f, 0.153f, 0.153f, 1.00f);
    public static readonly Color Clear     = new Color(0, 0, 0, 0);

    // ── Typography ───────────────────────────────────────────────────────────
    public const int SzTitle  = 64;
    public const int SzH1     = 28;
    public const int SzH2     = 20;
    public const int SzBody   = 16;
    public const int SzSmall  = 13;
    public const int SzMicro  = 11;

    // ── Font ─────────────────────────────────────────────────────────────────
    private static TMP_FontAsset _font;
    public static TMP_FontAsset Font
    {
        get
        {
            if (_font == null)
                _font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            return _font;
        }
    }

    // ── Rich-text helpers ─────────────────────────────────────────────────────
    public static string Orange2(string s)  => $"<color=#F5A623>{s}</color>";
    public static string Green2(string s)   => $"<color=#4CBF47>{s}</color>";
    public static string Red2(string s)     => $"<color=#E53935>{s}</color>";
    public static string Yellow2(string s)  => $"<color=#F5A623>{s}</color>";
    public static string Dim(string s)      => $"<color=#666666>{s}</color>";
    public static string Bold(string s)     => $"<b>{s}</b>";
    public static string Sz(string s, int pt) => $"<size={pt}>{s}</size>";

    // ── Shared UI builder helpers ─────────────────────────────────────────────
    public static GameObject Pnl(string name, Transform parent, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = color;
        return go;
    }

    public static GameObject Sec(Transform parent, string name, Color color, float height)
    {
        var go = Pnl(name, parent, color);
        go.AddComponent<LayoutElement>().preferredHeight = height;
        return go;
    }

    public static void Sep(Transform parent, Color color, float height = 1f)
    {
        var go = Pnl("Sep", parent, color);
        go.AddComponent<LayoutElement>().preferredHeight = height;
    }

    public static TextMeshProUGUI Txt(Transform parent, string name, string content,
        int size, Color color, TextAlignmentOptions align,
        FontStyles style = FontStyles.Normal)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.text = content; t.fontSize = size; t.color = color;
        t.alignment = align; t.fontStyle = style;
        t.enableWordWrapping = false;
        if (Font != null) t.font = Font;
        go.AddComponent<LayoutElement>().preferredHeight = size + 8;
        return t;
    }

    public static Button Btn(Transform parent, string label, Color bg, Color hi,
        int fontSize, int height, FontStyles fontStyle = FontStyles.Bold)
    {
        var go = new GameObject("Btn");
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = Color.white;

        var btn = go.AddComponent<Button>();
        var cb  = ColorBlock.defaultColorBlock;
        cb.normalColor      = bg;
        cb.highlightedColor = hi;
        cb.pressedColor     = Color.Lerp(bg, Color.black, 0.25f);
        cb.selectedColor    = bg;
        cb.colorMultiplier  = 1f;
        btn.colors = cb;
        go.AddComponent<LayoutElement>().preferredHeight = height;

        var lbl = new GameObject("Label");
        lbl.transform.SetParent(go.transform, false);
        var t = lbl.AddComponent<TextMeshProUGUI>();
        t.text = label; t.fontSize = fontSize; t.fontStyle = fontStyle;
        t.color = TextPri; t.alignment = TextAlignmentOptions.Midline;
        t.enableWordWrapping = false;
        if (Font != null) t.font = Font;
        var lRT = lbl.GetComponent<RectTransform>();
        lRT.anchorMin = Vector2.zero; lRT.anchorMax = Vector2.one;
        lRT.sizeDelta = Vector2.zero;

        return btn;
    }

    public static Button SmallBtn(Transform parent, string label, Color bg,
        int fontSize, float w, float h)
    {
        var go = new GameObject("Btn");
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = Color.white;

        var btn = go.AddComponent<Button>();
        var cb  = ColorBlock.defaultColorBlock;
        cb.normalColor      = bg;
        cb.highlightedColor = Color.Lerp(bg, Color.white, 0.25f);
        cb.pressedColor     = Color.Lerp(bg, Color.black, 0.2f);
        cb.selectedColor    = bg;
        cb.colorMultiplier  = 1f;
        btn.colors = cb;

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = w; le.preferredHeight = h;

        var lbl = new GameObject("Label");
        lbl.transform.SetParent(go.transform, false);
        var t = lbl.AddComponent<TextMeshProUGUI>();
        t.text = label; t.fontSize = fontSize; t.fontStyle = FontStyles.Bold;
        t.color = TextPri; t.alignment = TextAlignmentOptions.Midline;
        t.enableWordWrapping = false;
        if (Font != null) t.font = Font;
        var lRT = lbl.GetComponent<RectTransform>();
        lRT.anchorMin = Vector2.zero; lRT.anchorMax = Vector2.one;
        lRT.sizeDelta = Vector2.zero;

        return btn;
    }

    public static VerticalLayoutGroup VLG(GameObject go,
        int padL = 0, int padR = 0, int padT = 0, int padB = 0, int spacing = 0,
        bool controlW = true, bool controlH = true,
        bool expandW = true, bool expandH = false)
    {
        var vlg = go.GetComponent<VerticalLayoutGroup>();
        if (vlg == null) vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.padding               = new RectOffset(padL, padR, padT, padB);
        vlg.spacing               = spacing;
        vlg.childControlWidth     = controlW;  vlg.childControlHeight     = controlH;
        vlg.childForceExpandWidth = expandW;   vlg.childForceExpandHeight = expandH;
        return vlg;
    }

    public static void CornerBrackets(Transform parent, Color color,
                                       float sz = 10f, float thick = 2f)
    {
        void B(Vector2 anc, Vector2 piv, Vector2 size)
        {
            var go = new GameObject("Brk");
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = color;
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anc;
            rt.pivot = piv;
            rt.sizeDelta = size;
            rt.anchoredPosition = Vector2.zero;
        }
        B(new Vector2(0,1), new Vector2(0,1), new Vector2(sz,    thick));
        B(new Vector2(0,1), new Vector2(0,1), new Vector2(thick, sz   ));
        B(new Vector2(1,1), new Vector2(1,1), new Vector2(sz,    thick));
        B(new Vector2(1,1), new Vector2(1,1), new Vector2(thick, sz   ));
        B(new Vector2(0,0), new Vector2(0,0), new Vector2(sz,    thick));
        B(new Vector2(0,0), new Vector2(0,0), new Vector2(thick, sz   ));
        B(new Vector2(1,0), new Vector2(1,0), new Vector2(sz,    thick));
        B(new Vector2(1,0), new Vector2(1,0), new Vector2(thick, sz   ));
    }

    public static void ApplyShadow(TextMeshProUGUI t,
        float offsetX = 1f, float offsetY = -1f,
        float softness = 0.25f, float opacity = 0.75f)
    {
        if (t.fontSharedMaterial == null) return;
        var mat = UnityEngine.Object.Instantiate(t.fontSharedMaterial);
        mat.EnableKeyword(TMPro.ShaderUtilities.Keyword_Underlay);
        mat.SetColor(TMPro.ShaderUtilities.ID_UnderlayColor,
            new UnityEngine.Color(0f, 0f, 0f, opacity));
        mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlayOffsetX, offsetX);
        mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlayOffsetY, offsetY);
        mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlaySoftness, softness);
        t.fontMaterial = mat;
    }
}
