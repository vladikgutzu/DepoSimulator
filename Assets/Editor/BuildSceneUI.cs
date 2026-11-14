using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;

/// One-shot scene builder — Tools → Build Scene UI.
/// Creates HUD as real scene objects (no runtime rebuild).
public class BuildSceneUI
{
    static readonly Color cPanel   = new Color(0.114f, 0.114f, 0.114f, 0.97f);
    static readonly Color cCardAlt = new Color(0.098f, 0.098f, 0.098f, 1.00f);
    static readonly Color cAccent  = new Color(0.961f, 0.651f, 0.137f, 1.00f);
    static readonly Color cGreen   = new Color(0.298f, 0.749f, 0.278f, 1.00f);
    static readonly Color cInfo    = new Color(0.392f, 0.710f, 0.965f, 1.00f);
    static readonly Color cRed     = new Color(0.898f, 0.224f, 0.208f, 1.00f);
    static readonly Color cTextPri = new Color(0.961f, 0.961f, 0.961f, 1.00f);
    static readonly Color cTextSec = new Color(0.667f, 0.667f, 0.667f, 1.00f);
    static readonly Color cTextDim = new Color(0.400f, 0.400f, 0.400f, 1.00f);
    static readonly Color cBorder  = new Color(0.220f, 0.220f, 0.220f, 0.40f);
    static TMP_FontAsset _font;

    [MenuItem("Tools/Build Scene UI")]
    public static void Execute()
    {
        _font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");

        var old = GameObject.Find("GameplayCanvas");
        if (old != null) Object.DestroyImmediate(old);

        var canvasGO = new GameObject("GameplayCanvas");
        var canvas   = canvasGO.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight  = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        var r = canvasGO.transform;
        BuildTopBar(r);
        BuildTasksPanel(r);
        BuildTutorialBar(r);
        BuildNotifPanel(r);
        BuildMiniMapPanel(r);
        BuildQuickActionsBar(r);
        BuildVehiclePanel(r);
        BuildCrosshair(r);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[BuildSceneUI] Done — GameplayCanvas built in scene.");
    }

    // ── TOP BAR ───────────────────────────────────────────────────────────────
    static void BuildTopBar(Transform root)
    {
        var bar = MkImg("TopBar", root, cPanel);
        SetRT(bar, new Vector2(0,1), new Vector2(1,1), Vector2.zero, new Vector2(0.5f,1), new Vector2(0, 52));
        HLine(bar.transform, atBottom: true);

        // Logo block (left)
        var logo = MkGO("Logo", bar.transform);
        SetRT(logo, new Vector2(0,0), new Vector2(0,1), new Vector2(14,0), new Vector2(0,0.5f), new Vector2(185,0));
        var logoImage = logo.AddComponent<RawImage>();
        logoImage.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(
            "Assets/Resources/UI/Brand/ShiftHappensTogetherLogo.png");
        logoImage.color = Color.white;
        logoImage.raycastTarget = false;
        var logoAspect = logo.AddComponent<AspectRatioFitter>();
        logoAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        logoAspect.aspectRatio = logoImage.texture != null
            ? (float)logoImage.texture.width / logoImage.texture.height
            : 1.5f;

        VLine(bar.transform, 210f);

        // Time block
        var time = MkGO("TimeBlock", bar.transform);
        SetRT(time, new Vector2(0,0), new Vector2(0,1), new Vector2(220,0), new Vector2(0,0.5f), new Vector2(120,0));
        var timeVal = MkTxt(time.transform, "09:42", 22, cAccent, TextAlignmentOptions.Center, FontStyles.Bold);
        timeVal.gameObject.name = "TimeText";
        SetRT(timeVal.gameObject, new Vector2(0,0.4f), new Vector2(1,1), Vector2.zero, new Vector2(0.5f,0.5f), Vector2.zero);
        var shiftTxt = MkTxt(time.transform, "СМЕНА 1", 11, cTextDim, TextAlignmentOptions.Center);
        shiftTxt.gameObject.name = "ShiftText";
        SetRT(shiftTxt.gameObject, new Vector2(0,0.1f), new Vector2(1,0.4f), Vector2.zero, new Vector2(0.5f,0.5f), Vector2.zero);

        VLine(bar.transform, 350f);

        // Stats
        MkStat(bar.transform, "Orders", "ЗАКАЗЫ",    "0/3",  cTextPri, 360f, 90f);
        VLine(bar.transform, 455f);
        MkStat(bar.transform, "Budget", "БЮДЖЕТ",    "$0",   cGreen,   465f, 120f);
        VLine(bar.transform, 590f);
        MkStat(bar.transform, "Rep",    "РЕПУТАЦИЯ", "85%",  cAccent,  600f, 115f);

        // FPS (right)
        var fps = MkTxt(bar.transform, "FPS: --", 13, cTextDim, TextAlignmentOptions.Right);
        fps.gameObject.name = "FPSText";
        SetRT(fps.gameObject, new Vector2(1,0), new Vector2(1,1), new Vector2(-80,0), new Vector2(1,0.5f), new Vector2(70,0));

        // Settings button
        var gear = MkImg("GearBtn", bar.transform, cCardAlt);
        SetRT(gear, new Vector2(1,0.5f), new Vector2(1,0.5f), new Vector2(-12,0), new Vector2(1,0.5f), new Vector2(36,36));
        MkSvgIcon(gear.transform, "Assets/WarehouseDemo/Assets/UI/Icons/settings.svg", cTextSec);
    }

    static void MkStat(Transform parent, string id, string lbl, string val, Color valCol, float xPos, float w)
    {
        var b = MkGO(id + "Block", parent);
        SetRT(b, new Vector2(0,0), new Vector2(0,1), new Vector2(xPos,0), new Vector2(0,0.5f), new Vector2(w,0));
        var l = MkTxt(b.transform, lbl, 11, cTextDim, TextAlignmentOptions.Center, FontStyles.Bold);
        l.gameObject.name = id + "Label";
        SetRT(l.gameObject, new Vector2(0,0), new Vector2(1,0.42f), Vector2.zero, new Vector2(0.5f,0.5f), Vector2.zero);
        var v = MkTxt(b.transform, val, 18, valCol, TextAlignmentOptions.Center, FontStyles.Bold);
        v.gameObject.name = id + "Value";
        SetRT(v.gameObject, new Vector2(0,0.42f), new Vector2(1,1), Vector2.zero, new Vector2(0.5f,0.5f), Vector2.zero);
    }

    // ── TASKS PANEL (top-left) ────────────────────────────────────────────────
    static void BuildTasksPanel(Transform root)
    {
        var p = MkImg("TasksPanel", root, cPanel);
        SetRT(p, new Vector2(0,1), new Vector2(0,1), new Vector2(12,-62), new Vector2(0,1), new Vector2(300,290));
        HLine(p.transform, atBottom: false);
        LeftBar(p.transform);
        Brackets(p.transform);

        var hdr = MkTxt(p.transform, "■  ТЕКУЩЕЕ ЗАДАНИЕ", 12, cTextDim, TextAlignmentOptions.Left, FontStyles.Bold);
        hdr.gameObject.name = "TasksHeader";
        SetRT(hdr.gameObject, new Vector2(0,1), new Vector2(1,1), new Vector2(14,-14), new Vector2(0,1), new Vector2(-14,16));

        Sep(p.transform, -34f);

        var taskName = MkTxt(p.transform, "Подойдите к доске заказов", 15, cTextPri, TextAlignmentOptions.Left, FontStyles.Bold);
        taskName.gameObject.name = "TaskName";
        taskName.enableWordWrapping = true;
        SetRT(taskName.gameObject, new Vector2(0,1), new Vector2(1,1), new Vector2(14,-40), new Vector2(0,1), new Vector2(-14,36));

        var client = MkTxt(p.transform, "Клиент: —  •  Деталей: 0/0", 11, cTextDim, TextAlignmentOptions.Left);
        client.gameObject.name = "ClientText";
        SetRT(client.gameObject, new Vector2(0,1), new Vector2(1,1), new Vector2(14,-80), new Vector2(0,1), new Vector2(-14,14));

        Sep(p.transform, -98f);

        // Task checklist rows
        string[] rows = { "Подойти к доске заказов", "Принять заказ", "Собрать детали  1/2", "Разгрузить  0/1", "Проверить пакет" };
        for (int i = 0; i < rows.Length; i++)
        {
            float y = -108f - i * 34f;
            var dot = MkImg("Dot" + i, p.transform, i == 0 ? cAccent : cTextDim);
            SetRT(dot, new Vector2(0,1), new Vector2(0,1), new Vector2(16, y-5), new Vector2(0,1), new Vector2(8,8));
            var row = MkTxt(p.transform, rows[i], 13, i == 0 ? cTextPri : cTextDim, TextAlignmentOptions.Left);
            row.gameObject.name = "TaskRow" + i;
            SetRT(row.gameObject, new Vector2(0,1), new Vector2(1,1), new Vector2(30, y), new Vector2(0,1), new Vector2(-14,18));
        }

        Sep(p.transform, -278f);

        var hint = MkTxt(p.transform, "[4]  Открыть планшет", 11, cTextDim, TextAlignmentOptions.Left);
        SetRT(hint.gameObject, new Vector2(0,0), new Vector2(1,0), new Vector2(14,8), new Vector2(0,0), new Vector2(-14,16));
    }

    // ── TUTORIAL BAR (center, top) ────────────────────────────────────────────
    static void BuildTutorialBar(Transform root)
    {
        var bar = MkImg("TutorialBar", root, new Color(0.08f, 0.08f, 0.08f, 0.92f));
        SetRT(bar, new Vector2(0.5f,1), new Vector2(0.5f,1), new Vector2(0,-62), new Vector2(0.5f,1), new Vector2(580,44));
        Brackets(bar.transform);

        var stripe = MkImg("LeftStripe", bar.transform, cAccent);
        SetRT(stripe, new Vector2(0,0), new Vector2(0,1), Vector2.zero, new Vector2(0,0.5f), new Vector2(3,0));

        var txt = MkTxt(bar.transform,
            "Подойдите к доске заказов и примите заказ  <color=#F5A623><b>[E]</b></color>",
            16, cTextPri, TextAlignmentOptions.Midline);
        txt.gameObject.name = "TutorialText";
        txt.enableWordWrapping = false;
        SetRT(txt.gameObject, new Vector2(0,0), new Vector2(1,1), new Vector2(14,0), new Vector2(0,0.5f), new Vector2(-14,0));
    }

    // ── NOTIFICATIONS PANEL (top-right) ───────────────────────────────────────
    static void BuildNotifPanel(Transform root)
    {
        var p = MkImg("NotifPanel", root, cPanel);
        SetRT(p, new Vector2(1,1), new Vector2(1,1), new Vector2(-12,-62), new Vector2(1,1), new Vector2(300,230));
        HLine(p.transform, atBottom: false);
        Brackets(p.transform);

        var hdr = MkTxt(p.transform, "УВЕДОМЛЕНИЯ", 12, cTextDim, TextAlignmentOptions.Left, FontStyles.Bold);
        hdr.gameObject.name = "NotifHeader";
        SetRT(hdr.gameObject, new Vector2(0,1), new Vector2(0.7f,1), new Vector2(12,-14), new Vector2(0,1), new Vector2(0,16));

        // Toggle button — Button component wired up by NotifPanelController at runtime
        var hideBtn = MkImg("HideBtn", p.transform, cCardAlt);
        hideBtn.gameObject.AddComponent<UnityEngine.UI.Button>();
        SetRT(hideBtn, new Vector2(1,1), new Vector2(1,1), new Vector2(-10,-12), new Vector2(1,1), new Vector2(68,20));
        var hideTxt = MkTxt(hideBtn.transform, "Скрыть", 11, cTextDim, TextAlignmentOptions.Midline);
        hideTxt.gameObject.name = "HideBtnText";
        Stretch(hideTxt.gameObject);

        // NotifContent — toggleable container for all notification rows
        var content = MkGO("NotifContent", p.transform);
        SetRT(content, new Vector2(0,0), new Vector2(1,1), new Vector2(0,-36), new Vector2(0,1), new Vector2(0,-36));

        Sep(content.transform, 0f);

        (string title, string sub, Color col, string svg)[] notifs =
        {
            ("Демо запущено",          "Начните с доски заказов", cGreen,  "Assets/WarehouseDemo/Assets/UI/Icons/check.svg"),
            ("Новый заказ",            "Заказ #07-24  •  сейчас", cAccent, "Assets/WarehouseDemo/Assets/UI/Icons/info.svg"),
            ("Низкий уровень топлива", "Погрузчик #1",            cRed,    "Assets/WarehouseDemo/Assets/UI/Icons/triangle-alert.svg"),
        };
        for (int i = 0; i < notifs.Length; i++)
        {
            float y = -8f - i * 60f;
            var (title, sub, col, svg) = notifs[i];

            var icon = MkImg("NotifIcon" + i, content.transform, col);
            SetRT(icon, new Vector2(0,1), new Vector2(0,1), new Vector2(10, y), new Vector2(0,1), new Vector2(30,30));
            MkSvgIcon(icon.transform, svg, Color.white);

            var titleTxt = MkTxt(content.transform, title, 14, cTextPri, TextAlignmentOptions.Left, FontStyles.Bold);
            titleTxt.gameObject.name = "NotifTitle" + i;
            SetRT(titleTxt.gameObject, new Vector2(0,1), new Vector2(1,1), new Vector2(48, y), new Vector2(0,1), new Vector2(-14,18));

            var subTxt = MkTxt(content.transform, sub, 11, cTextDim, TextAlignmentOptions.Left);
            subTxt.gameObject.name = "NotifSub" + i;
            SetRT(subTxt.gameObject, new Vector2(0,1), new Vector2(1,1), new Vector2(48, y-20), new Vector2(0,1), new Vector2(-14,14));
        }
    }

    // ── MINIMAP PANEL (bottom-left) ───────────────────────────────────────────
    static void BuildMiniMapPanel(Transform root)
    {
        var p = MkImg("MiniMapPanel", root, cPanel);
        SetRT(p, new Vector2(0,0), new Vector2(0,0), new Vector2(12,12), new Vector2(0,0), new Vector2(220,240));
        HLine(p.transform, atBottom: false);
        Brackets(p.transform);

        var hdr = MkTxt(p.transform, "КАРТА СКЛАДА", 12, cTextDim, TextAlignmentOptions.Left, FontStyles.Bold);
        SetRT(hdr.gameObject, new Vector2(0,1), new Vector2(1,1), new Vector2(10,-14), new Vector2(0,1), new Vector2(-10,16));

        Sep(p.transform, -34f);

        // Map viewport — top-anchored, fixed height, stays within panel
        var vp = MkImg("MapViewport", p.transform, new Color(0.04f, 0.06f, 0.10f, 1f));
        SetRT(vp, new Vector2(0,1), new Vector2(1,1), new Vector2(8,-40), new Vector2(0,1), new Vector2(-16, 168));

        // Colored marker dots on the map (like in reference)
        MkMapDot(vp.transform, new Vector2(0.50f, 0.45f), 8,  cAccent);  // player (yellow)
        MkMapDot(vp.transform, new Vector2(0.28f, 0.62f), 6,  cInfo);    // waypoint (blue)
        MkMapDot(vp.transform, new Vector2(0.70f, 0.30f), 6,  cGreen);   // objective (green)
        MkMapDot(vp.transform, new Vector2(0.20f, 0.22f), 5,  cTextSec); // marker (gray)

        // N compass — bottom-left corner of the map itself
        var compass = MkTxt(vp.transform, "N", 12, cAccent, TextAlignmentOptions.Left, FontStyles.Bold);
        SetRT(compass.gameObject, new Vector2(0,0), new Vector2(0,0), new Vector2(6,6), new Vector2(0,0), new Vector2(18,16));

        // Footer: rotate hint anchored to panel bottom
        var rotateTxt = MkTxt(p.transform, "Повернуть карту", 11, cTextDim, TextAlignmentOptions.Right);
        SetRT(rotateTxt.gameObject, new Vector2(0,0), new Vector2(1,0), new Vector2(0,6), new Vector2(1,0), new Vector2(-10,16));
    }

    static void MkMapDot(Transform parent, Vector2 anchor, float size, Color col)
    {
        var dot = MkImg("MapDot", parent, col);
        var rt = dot.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(size, size);
    }

    // ── QUICK ACTIONS BAR (bottom-center) ─────────────────────────────────────
    static void BuildQuickActionsBar(Transform root)
    {
        var bar = MkImg("QuickActionsBar", root, cPanel);
        SetRT(bar, new Vector2(0.5f,0), new Vector2(0.5f,0), new Vector2(0,12), new Vector2(0.5f,0), new Vector2(520,72));
        HLine(bar.transform, atBottom: false);
        Brackets(bar.transform);

        string[] labels  = { "СКАНЕР", "ПОГРУЗЧИК", "ИНВЕНТАРЬ", "ПЛАНШЕТ", "РАЦИЯ", "МЕНЮ" };
        string[] hotkeys = { "1", "2", "3", "4", "5", "6" };

        for (int i = 0; i < 6; i++)
        {
            bool  first = i == 0;
            Color col   = first ? cAccent : cTextSec;

            var slot = MkImg("Slot_" + labels[i], bar.transform,
                first ? new Color(cAccent.r, cAccent.g, cAccent.b, 0.22f) : cCardAlt);
            SetRT(slot, new Vector2(i/6f,0), new Vector2((i+1)/6f,1), Vector2.zero, new Vector2(0.5f,0.5f), Vector2.zero);

            var hk = MkTxt(slot.transform, hotkeys[i], 11, cTextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            SetRT(hk.gameObject, new Vector2(0,0.78f), new Vector2(1,1), Vector2.zero, new Vector2(0.5f,1), Vector2.zero);

            var icoGroup = MkGO("Icon_" + labels[i], slot.transform);
            SetRT(icoGroup, new Vector2(0,0.26f), new Vector2(1,0.76f), Vector2.zero, new Vector2(0.5f,0.5f), Vector2.zero);
            BuildSlotIcon(icoGroup.transform, i, col);

            var lbl = MkTxt(slot.transform, labels[i], 11, first ? cAccent : cTextDim, TextAlignmentOptions.Center);
            lbl.gameObject.name = "Label_" + labels[i];
            SetRT(lbl.gameObject, new Vector2(0,0), new Vector2(1,0.28f), Vector2.zero, new Vector2(0.5f,0), Vector2.zero);
        }
    }

    static readonly string[] _svgPaths =
    {
        "Assets/WarehouseDemo/Assets/UI/Icons/scanner.svg",
        "Assets/WarehouseDemo/Assets/UI/Icons/forklift.svg",
        "Assets/WarehouseDemo/Assets/UI/Icons/inventory.svg",
        "Assets/WarehouseDemo/Assets/UI/Icons/clipboard.svg",
        "Assets/WarehouseDemo/Assets/UI/Icons/radio.svg",
        "Assets/WarehouseDemo/Assets/UI/Icons/menu.svg",
    };

    static void BuildSlotIcon(Transform p, int idx, Color col)
    {
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(_svgPaths[idx]);
        if (sprite != null)
        {
            var go = MkGO("SVGIcon", p);
            Stretch(go);
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = col;
            img.preserveAspect = true;
            return;
        }
        BuildSlotIconFallback(p, idx, col);
    }

    static void BuildSlotIconFallback(Transform p, int idx, Color col)
    {
        Color bg = new Color(0.08f, 0.08f, 0.08f, 1f);
        switch (idx)
        {
            case 0:
                IcoPiece(p,  0,  5, 16,  2, col); IcoPiece(p,  0,  1, 10,  2, col);
                IcoPiece(p,  0, -3,  4,  2, col); IcoPiece(p,  0, -6,  2,  4, col); break;
            case 1:
                IcoPiece(p, -3,  3, 11,  2, col); IcoPiece(p, -3, -1, 11,  2, col);
                IcoPiece(p,  5,  1,  2, 12, col); IcoPiece(p,  6, -6,  5,  3, col); break;
            case 2:
                IcoPiece(p, -5,  5,  3,  3, col); IcoPiece(p,  1,  5,  9,  2, col);
                IcoPiece(p, -5,  0,  3,  3, col); IcoPiece(p,  1,  0,  9,  2, col);
                IcoPiece(p, -5, -5,  3,  3, col); IcoPiece(p,  1, -5,  7,  2, col); break;
            case 3:
                IcoPiece(p,  0,  0, 14, 13, col); IcoPiece(p,  0,  0, 11,  9, bg);
                IcoPiece(p,  0,  2,  9,  1, col); IcoPiece(p,  0, -1,  9,  1, col);
                IcoPiece(p,  0, -4,  9,  1, col); break;
            case 4:
                IcoPiece(p,  0, -1,  8, 11, col); IcoPiece(p,  0, -1,  5,  7, bg);
                IcoPiece(p,  2,  6,  2,  5, col); IcoPiece(p,  0,  2,  4,  2, col);
                IcoPiece(p,  0, -1,  4,  1, col); break;
            case 5:
                IcoPiece(p,  0,  5, 16,  2, col); IcoPiece(p,  0,  0, 16,  2, col);
                IcoPiece(p,  0, -5, 12,  2, col); break;
        }
    }

    static void IcoPiece(Transform p, float x, float y, float w, float h, Color col)
    {
        var go = MkImg("p", p, col);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta        = new Vector2(w, h);
    }

    // ── VEHICLE PANEL (bottom-right) ──────────────────────────────────────────
    static void BuildVehiclePanel(Transform root)
    {
        var p = MkImg("VehiclePanel", root, cPanel);
        SetRT(p, new Vector2(1,0), new Vector2(1,0), new Vector2(-12,108), new Vector2(1,0), new Vector2(280,220));
        HLine(p.transform, atBottom: false);
        Brackets(p.transform);

        var hdr = MkTxt(p.transform, "■  СОСТОЯНИЕ", 12, cTextDim, TextAlignmentOptions.Left, FontStyles.Bold);
        hdr.gameObject.name = "VehicleHeader";
        SetRT(hdr.gameObject, new Vector2(0,1), new Vector2(0.7f,1), new Vector2(12,-14), new Vector2(0,1), new Vector2(0,16));

        var sub = MkTxt(p.transform, "Погрузчик #1", 14, cTextSec, TextAlignmentOptions.Left);
        sub.gameObject.name = "VehicleName";
        SetRT(sub.gameObject, new Vector2(0,1), new Vector2(1,1), new Vector2(12,-33), new Vector2(0,1), new Vector2(-12,18));

        Sep(p.transform, -54f);

        MkStatBar(p.transform, "ТОПЛИВО", "FuelTrack",   "FuelFill",   "FuelPct",   cGreen, 0.92f, -64f);
        MkStatBar(p.transform, "ЗАРЯД",   "ChargeTrack", "ChargeFill", "ChargePct", cInfo,  0.05f, -118f);

        Sep(p.transform, -168f);

        var forkLbl = MkTxt(p.transform, "ВЫСОТА ВИЛ", 12, cTextDim, TextAlignmentOptions.Left, FontStyles.Bold);
        SetRT(forkLbl.gameObject, new Vector2(0,0), new Vector2(0.6f,0), new Vector2(12,16), new Vector2(0,0), new Vector2(0,16));

        var forkVal = MkTxt(p.transform, "1.2 м", 19, cTextPri, TextAlignmentOptions.Right, FontStyles.Bold);
        forkVal.gameObject.name = "ForkHeight";
        SetRT(forkVal.gameObject, new Vector2(0.6f,0), new Vector2(1,0), new Vector2(0,12), new Vector2(1,0), new Vector2(-12,22));

        // Forklift icon (top-right of panel)
        var forkImg = MkImg("ForkliftIcon", p.transform, cCardAlt);
        SetRT(forkImg, new Vector2(1,1), new Vector2(1,1), new Vector2(-10,-44), new Vector2(1,1), new Vector2(56,80));
        MkSvgIcon(forkImg.transform, "Assets/WarehouseDemo/Assets/UI/Icons/forklift.svg", new Color(cTextDim.r, cTextDim.g, cTextDim.b, 0.5f));

        // Hidden by default — shown only when player boards a forklift
        p.gameObject.SetActive(false);
    }

    static void MkStatBar(Transform parent, string label, string trackName,
        string fillName, string pctName, Color fillCol, float pct, float yTop)
    {
        var lbl = MkTxt(parent, label, 11, cTextDim, TextAlignmentOptions.Left, FontStyles.Bold);
        SetRT(lbl.gameObject, new Vector2(0,1), new Vector2(0.6f,1), new Vector2(12, yTop), new Vector2(0,1), new Vector2(0,14));

        var pctTxt = MkTxt(parent, Mathf.RoundToInt(pct * 100) + "%", 11, cTextSec, TextAlignmentOptions.Right);
        pctTxt.gameObject.name = pctName;
        SetRT(pctTxt.gameObject, new Vector2(0.6f,1), new Vector2(1,1), new Vector2(0, yTop), new Vector2(1,1), new Vector2(-12,14));

        var track = MkImg(trackName, parent, cCardAlt);
        SetRT(track, new Vector2(0,1), new Vector2(1,1), new Vector2(12, yTop - 18f), new Vector2(0,1), new Vector2(-12,10));

        var fill = MkImg(fillName, track.transform, fillCol);
        fill.gameObject.name = fillName;
        var fillRT = fill.GetComponent<RectTransform>();
        fillRT.anchorMin        = Vector2.zero;
        fillRT.anchorMax        = new Vector2(pct, 1f);
        fillRT.sizeDelta        = Vector2.zero;
        fillRT.anchoredPosition = Vector2.zero;
    }

    // ── CROSSHAIR ─────────────────────────────────────────────────────────────
    static void BuildCrosshair(Transform root)
    {
        var ch = MkGO("Crosshair", root);
        SetRT(ch, new Vector2(0.5f,0.5f), new Vector2(0.5f,0.5f), Vector2.zero, new Vector2(0.5f,0.5f), new Vector2(24,24));

        Color arm = new Color(1, 1, 1, 0.6f);
        CArm(ch.transform, new Vector2(-9, 0), new Vector2(5, 1), arm);
        CArm(ch.transform, new Vector2( 9, 0), new Vector2(5, 1), arm);
        CArm(ch.transform, new Vector2( 0, 9), new Vector2(1, 5), arm);
        CArm(ch.transform, new Vector2( 0,-9), new Vector2(1, 5), arm);
        CArm(ch.transform, Vector2.zero,        new Vector2(3, 3), cAccent);
    }

    static void CArm(Transform p, Vector2 pos, Vector2 sz, Color col)
    {
        var go = MkImg("_", p, col);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = sz;
    }

    // ── FACTORY HELPERS ───────────────────────────────────────────────────────

    static void MkSvgIcon(Transform parent, string svgPath, Color col)
    {
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(svgPath);
        var go = MkGO("SVGIcon", parent);
        Stretch(go);
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.color = sprite != null ? col : new Color(col.r, col.g, col.b, 0.3f);
        img.preserveAspect = true;
    }

    static Image MkImg(string name, Transform parent, Color col)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var img = go.AddComponent<Image>();
        img.color = col;
        return img;
    }

    static GameObject MkGO(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return go;
    }

    static TextMeshProUGUI MkTxt(Transform parent, string text, int size, Color col,
        TextAlignmentOptions align = TextAlignmentOptions.Left,
        FontStyles style = FontStyles.Normal)
    {
        var go = new GameObject("Txt");
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var t = go.AddComponent<TextMeshProUGUI>();
        t.text = text; t.fontSize = size; t.color = col;
        t.alignment = align; t.fontStyle = style;
        t.enableWordWrapping = false;
        if (_font != null) t.font = _font;
        return t;
    }

    static void SetRT(GameObject go, Vector2 ancMin, Vector2 ancMax,
        Vector2 pos, Vector2 pivot, Vector2 size)
    {
        var rt = go.GetComponent<RectTransform>();
        if (rt == null) rt = go.AddComponent<RectTransform>();
        rt.anchorMin        = ancMin;
        rt.anchorMax        = ancMax;
        rt.pivot            = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta        = size;
    }

    static void SetRT(Image img, Vector2 ancMin, Vector2 ancMax,
        Vector2 pos, Vector2 pivot, Vector2 size)
        => SetRT(img.gameObject, ancMin, ancMax, pos, pivot, size);

    static void Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
    }

    static void HLine(Transform parent, bool atBottom)
    {
        var go = MkImg("AccentLine", parent, cAccent);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = atBottom ? new Vector2(0,0) : new Vector2(0,1);
        rt.anchorMax = atBottom ? new Vector2(1,0) : new Vector2(1,1);
        rt.pivot     = new Vector2(0.5f, atBottom ? 0f : 1f);
        rt.sizeDelta = new Vector2(0, 2);
        rt.anchoredPosition = Vector2.zero;
    }

    static void VLine(Transform parent, float xPos)
    {
        var go = MkImg("VSep", parent, new Color(1,1,1,0.07f));
        SetRT(go, new Vector2(0,0.15f), new Vector2(0,0.85f), new Vector2(xPos, 0), new Vector2(0,0.5f), new Vector2(1,0));
    }

    static void LeftBar(Transform parent)
    {
        var go = MkImg("LeftAccent", parent, cAccent);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = new Vector2(0,1);
        rt.pivot = new Vector2(0, 0.5f);
        rt.sizeDelta = new Vector2(3, 0);
        rt.anchoredPosition = Vector2.zero;
    }

    static void Sep(Transform parent, float yPos)
    {
        var go = MkImg("Sep", parent, cBorder);
        SetRT(go.gameObject, new Vector2(0,1), new Vector2(1,1), new Vector2(10, yPos), new Vector2(0,1), new Vector2(-20,1));
    }

    static void Brackets(Transform parent)
    {
        Color c = new Color(cAccent.r, cAccent.g, cAccent.b, 0.4f);
        float s = 10f, t = 1.5f;
        void B(Vector2 a, Vector2 pv, Vector2 sz)
        {
            var go = MkImg("Brk", parent, c);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = a; rt.pivot = pv;
            rt.sizeDelta = sz; rt.anchoredPosition = Vector2.zero;
        }
        B(new Vector2(0,1), new Vector2(0,1), new Vector2(s, t));
        B(new Vector2(0,1), new Vector2(0,1), new Vector2(t, s));
        B(new Vector2(1,1), new Vector2(1,1), new Vector2(s, t));
        B(new Vector2(1,1), new Vector2(1,1), new Vector2(t, s));
        B(new Vector2(0,0), new Vector2(0,0), new Vector2(s, t));
        B(new Vector2(0,0), new Vector2(0,0), new Vector2(t, s));
        B(new Vector2(1,0), new Vector2(1,0), new Vector2(s, t));
        B(new Vector2(1,0), new Vector2(1,0), new Vector2(t, s));
    }
}
