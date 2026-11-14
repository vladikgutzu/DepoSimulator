#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Human-in-the-loop regression tour. The tester performs the visible interaction and explicitly
/// grades it; the controller only isolates the case, starts real game events, and records evidence.
/// </summary>
public sealed class GuidedManualPlaytest : MonoBehaviour
{
    public const string ActiveKey = "GuidedQA.Active";
    public const string IndexKey = "GuidedQA.Index";
    public const string ReportKey = "GuidedQA.Report";

    const float PanelWidth = 520f;
    const string ScenePath = "Assets/urp_scene_with_training 1.unity";

    sealed class TestCase
    {
        public readonly string Id;
        public readonly string Title;
        public readonly string Action;
        public readonly string Expected;
        public readonly string EventName;

        public TestCase(string id, string title, string action, string expected, string eventName = null)
        {
            Id = id;
            Title = title;
            Action = action;
            Expected = expected;
            EventName = eventName;
        }
    }

    static readonly TestCase[] Cases =
    {
        new("QA-01", "Запуск одиночной игры", "Убедись, что сцена полностью загрузилась и игрок может двигаться.", "HUD, персонаж и склад отображаются без ошибок."),
        new("QA-02", "Базовое обучение", "Пройди обучение от Welcome до Training Complete.", "Все шаги идут по порядку, подсказки и иконки соответствуют активному устройству ввода."),
        new("QA-03", "Первый обычный заказ", "Прими, собери, упакуй и доставь первый заказ.", "Список деталей, верстак, готовая коробка и награда работают корректно."),
        new("QA-04", "Второй обычный заказ", "Полностью выполни второй заказ без подсказок обучения.", "Заказ завершается самостоятельно, прогресс и награда обновляются."),
        new("QA-05", "Третий обычный заказ", "Полностью выполни третий заказ.", "После завершения нет зависшей активной задачи."),
        new("QA-06", "Коробки разных размеров", "Подними маленькую, среднюю и большую обычную коробку.", "Коробка не пересекает тело, руки стоят естественно для каждого размера."),
        new("QA-07", "Готовый заказ: IK и наклон", "Возьми PackedOrderBox, стой и походи с ним.", "Руки удерживают нижние точки, корпус наклонён сразу, IK не сбрасывается во время ходьбы."),
        new("QA-08", "Свечение коробки", "Наведи курсор на коробку со всех сторон.", "Шейдер равномерно выделяет все стороны и не пересвечивает объект."),
        new("QA-09", "Готовый заказ на тележке", "Поставь готовую коробку на тележку.", "Коробка стоит устойчиво и не перекрывает рукоятку."),
        new("QA-10", "Тележка", "Толкай, поворачивай, отпусти и снова возьми тележку.", "Тележка не застревает, камера удобна, повторный захват не срабатывает в тот же кадр."),
        new("QA-11", "Погрузчик", "Сядь, проедь, поверни, проверь вилы и выйди.", "Чувствительность камеры комфортна, вилы над полом, техника не застревает, нет мгновенного повторного входа."),
        new("QA-12", "Лестница", "Возьми лестницу, пробеги рядом со стеллажом, установи и слезь.", "Лестница находится на правильной дистанции/высоте, не проходит сквозь стеллаж и не тормозит игрока."),
        new("QA-13", "Rush Order — первое появление", "Нажми START TEST, прочитай попап, затем доставь фиолетовые коробки.", "Таймер ждёт Continue; коробки и Shipping Zone фиолетовые; эмиссия умеренная.", "RushOrder"),
        new("QA-14", "Rush Order — повтор", "Запусти повторный Rush Order и выполни его.", "Обучающий попап больше не показывается; остаются уведомление, цель и цветовые маркеры.", "RushOrder"),
        new("QA-15", "Urgent Part — первое появление", "Прочитай попап и доставь красную деталь в Shipping Zone.", "Таймер ждёт Continue; цель и зона красные; верстак не принимает срочную деталь.", "UrgentPart"),
        new("QA-16", "Urgent Part — повтор", "Запусти и выполни событие повторно.", "Нет повторного обучающего попапа; событие завершается доставкой в красную зону.", "UrgentPart"),
        new("QA-17", "Поломка погрузчика", "Найди отмеченный набор инструментов и почини погрузчик.", "Первый попап объясняет цель; маркеры ведут к набору и месту ремонта.", "BrokenForklift"),
        new("QA-18", "Поломка вил", "Найди набор инструментов и почини вилы.", "Ремонт восстанавливает работу вил и событие освобождает слот.", "BrokenFork"),
        new("QA-19", "Отключение электричества", "Найди набор и восстанови питание.", "Освещение и постобработка меняются, затем полностью восстанавливаются.", "PowerFailure"),
        new("QA-20", "Мокрый пол", "Пройди и пробеги через мокрый участок.", "Маркер понятен, скольжение срабатывает, управление затем восстанавливается.", "WetFloor"),
        new("QA-21", "Обрушение стеллажа", "Осмотри место события и дождись завершения.", "Объекты падают физически корректно, нет зависших коллайдеров или ошибок.", "ShelfCollapse"),
        new("QA-22", "Поломка верстака", "Попробуй использовать верстак во время события и устрани проблему.", "Верстак блокируется, подсказка объясняет действие, после ремонта снова работает.", "BenchMalfunction"),
        new("QA-23", "Неправильный заказ", "Проверь уведомление и изменение текущей задачи.", "Название понятно игроку, внутренние имена prefab не отображаются.", "WrongOrder"),
        new("QA-24", "Задержка поставки", "Проверь список заказа и недоступные детали.", "Задержанные позиции и восстановление поставки понятно отображаются.", "SupplyDelay"),
        new("QA-25", "Радиопомехи", "Проверь звук/радио и выполни ремонт, если цель доступна.", "Эффект, цель и восстановление работают без постоянного искажения.", "RadioInterference"),
        new("QA-26", "Кража / зомби", "Останови появившуюся угрозу оружием или коробкой.", "Попап и задача понятны; противники двигаются, получают удары и завершают событие.", "Thief"),
        new("QA-27", "Смерть зомби", "Нанеси максимальный удар, затем убей другого зомби брошенной коробкой.", "В обоих случаях проигрывается смерть/ragdoll, противник не застывает в стойке."),
        new("QA-28", "Пожарная тревога", "Следуй первой подсказке и выполни требуемую реакцию.", "Сирена, красный эффект, цель и восстановление состояния работают.", "FireAlarm"),
        new("QA-29", "Прорыв ворот", "Следуй маркеру и останови угрозу у выбранных ворот.", "Ворота, противники, подсказка и завершение события синхронны.", "GateBreach"),
        new("QA-30", "Финал смены и Steam", "Заверши оставшиеся заказы и проверь финальный экран.", "Появляется читаемый popup; Wishlist открывает правильную Steam-страницу, Main Menu доступна."),
    };

    static readonly string[] EventNames =
    {
        "RushOrder", "UrgentPart", "BrokenForklift", "BrokenFork", "PowerFailure",
        "WetFloor", "ShelfCollapse", "BenchMalfunction", "WrongOrder", "SupplyDelay",
        "RadioInterference", "Thief", "FireAlarm", "GateBreach",
    };

    Rect _windowRect;
    Vector2 _scroll;
    GameEventManager _eventManager;
    bool _started;
    bool _mouseMode = true;
    bool _finished;
    string _setupStatus = "Нажми START TEST, когда готов.";
    string _note = string.Empty;
    float _startedAt;
    readonly List<string> _caseErrors = new();

    int CurrentIndex
    {
        get => Mathf.Clamp(PlayerPrefs.GetInt(IndexKey, 0), 0, Cases.Length - 1);
        set
        {
            PlayerPrefs.SetInt(IndexKey, Mathf.Clamp(value, 0, Cases.Length - 1));
            PlayerPrefs.Save();
        }
    }

    TestCase Current => Cases[CurrentIndex];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (PlayerPrefs.GetInt(ActiveKey, 0) != 1) return;
        if (FindFirstObjectByType<GuidedManualPlaytest>() != null) return;

        var go = new GameObject("Guided Manual Playtest");
        DontDestroyOnLoad(go);
        go.AddComponent<GuidedManualPlaytest>();
    }

    void Awake()
    {
        _windowRect = new Rect(18f, 80f, PanelWidth, Mathf.Min(650f, Screen.height - 100f));
        Application.logMessageReceived += OnLog;
        SetMouseMode(true);
        StartCoroutine(InitializeScene());
    }

    IEnumerator InitializeScene()
    {
        yield return null;
        _eventManager = GameEventManager.Instance ?? FindFirstObjectByType<GameEventManager>();
        if (_eventManager != null)
        {
            _eventManager.enabled = false;
            _setupStatus = "Случайные события отключены. Нажми START TEST.";
        }
        else
        {
            _setupStatus = "GameEventManager не найден. Этот тест можно оценить только вручную.";
        }
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnLog;
        if (_eventManager != null) _eventManager.enabled = true;
        SetMouseMode(true);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F10)) SetMouseMode(!_mouseMode);
        if (Input.GetKeyDown(KeyCode.F5) && !_started) StartCurrent();
        if (Input.GetKeyDown(KeyCode.F6) && _started) Grade("PASS");
        if (Input.GetKeyDown(KeyCode.F7) && _started) Grade("FAIL");
        if (Input.GetKeyDown(KeyCode.F8)) Retry();
        if (Input.GetKeyDown(KeyCode.F9)) Grade("SKIP");
    }

    void OnGUI()
    {
        if (_finished)
        {
            GUI.Box(new Rect(18f, 80f, PanelWidth, 110f), "GUIDED QA COMPLETE\n\nОтчёт сохранён. Нажми Stop в Unity.");
            return;
        }

        _windowRect.height = Mathf.Min(650f, Screen.height - 100f);
        _windowRect = GUI.Window(GetInstanceID(), _windowRect, DrawWindow, "GUIDED QA MODE");
    }

    void DrawWindow(int id)
    {
        var test = Current;
        GUILayout.Label($"Тест {CurrentIndex + 1}/{Cases.Length}  •  {test.Id}");
        GUILayout.Space(4f);
        GUILayout.Label(test.Title, GUI.skin.box);

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Mathf.Max(210f, _windowRect.height - 260f)));
        GUILayout.Label("ЧТО СДЕЛАТЬ");
        GUILayout.TextArea(test.Action, GUILayout.MinHeight(55f));
        GUILayout.Space(6f);
        GUILayout.Label("ОЖИДАЕМЫЙ РЕЗУЛЬТАТ");
        GUILayout.TextArea(test.Expected, GUILayout.MinHeight(70f));
        GUILayout.Space(6f);
        GUILayout.Label("СТАТУС");
        GUILayout.TextArea(BuildStatus(test), GUILayout.MinHeight(45f));
        GUILayout.EndScrollView();

        GUILayout.Label("Комментарий к результату:");
        _note = GUILayout.TextField(_note, GUILayout.Height(24f));

        GUILayout.BeginHorizontal();
        GUI.enabled = !_started;
        if (GUILayout.Button("START TEST  [F5]")) StartCurrent();
        GUI.enabled = _started;
        if (GUILayout.Button("PASS  [F6]")) Grade("PASS");
        if (GUILayout.Button("FAIL  [F7]")) Grade("FAIL");
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("RETRY  [F8]")) Retry();
        if (GUILayout.Button("SKIP  [F9]")) Grade("SKIP");
        if (GUILayout.Button(_mouseMode ? "PLAY MODE  [F10]" : "MOUSE  [F10]")) SetMouseMode(!_mouseMode);
        if (GUILayout.Button("STOP")) StopRun();
        GUILayout.EndHorizontal();

        GUILayout.Label(_mouseMode
            ? "Курсор свободен. Нажми F10, чтобы вернуться к управлению."
            : "Управление игрой активно. F6 = PASS, F7 = FAIL, F10 = курсор.");
        GUI.DragWindow(new Rect(0f, 0f, _windowRect.width, 25f));
    }

    string BuildStatus(TestCase test)
    {
        if (string.IsNullOrEmpty(test.EventName)) return _setupStatus;
        if (_eventManager == null) return _setupStatus;

        string state = _eventManager.IsEventActive(test.EventName)
            ? (_eventManager.GetActiveEvent(test.EventName) != null ? "active" : "waiting for Continue")
            : (_started ? "completed or refused" : "not started");
        return $"{_setupStatus}\nEvent {test.EventName}: {state}";
    }

    void StartCurrent()
    {
        if (_started || _finished) return;

        _caseErrors.Clear();
        _startedAt = Time.realtimeSinceStartup;
        _started = true;
        _setupStatus = "Тест запущен. Выполни действие и поставь оценку.";

        if (!string.IsNullOrEmpty(Current.EventName))
        {
            _eventManager = GameEventManager.Instance ?? FindFirstObjectByType<GameEventManager>();
            bool accepted = _eventManager != null && _eventManager.ForceStartEvent(Current.EventName);
            _setupStatus = accepted
                ? $"{Current.EventName} принят GameEventManager."
                : $"{Current.EventName} не запущен: проверь активное событие, роль или сцену.";
        }

        SetMouseMode(false);
    }

    void Grade(string verdict)
    {
        if (_finished) return;
        if (verdict != "SKIP" && !_started) return;

        string screenshot = string.Empty;
        if (verdict == "FAIL") screenshot = CaptureFailure();
        AppendResult(verdict, screenshot);
        CleanupCurrentEvent();

        if (CurrentIndex >= Cases.Length - 1)
        {
            FinishRun();
            return;
        }

        CurrentIndex++;
        _started = false;
        _note = string.Empty;
        _setupStatus = "Предыдущий результат сохранён. Нажми START TEST.";
        _caseErrors.Clear();
        SetMouseMode(true);
    }

    void Retry()
    {
        if (_finished) return;
        CleanupAllEvents();
        _started = false;
        _note = string.Empty;
        _caseErrors.Clear();
        PlayerPrefs.Save();
        SceneManager.LoadScene(SceneManager.GetActiveScene().path);
    }

    void CleanupCurrentEvent()
    {
        if (string.IsNullOrEmpty(Current.EventName) || _eventManager == null) return;
        CleanupEvent(Current.EventName);
    }

    void CleanupAllEvents()
    {
        _eventManager = GameEventManager.Instance ?? FindFirstObjectByType<GameEventManager>();
        if (_eventManager == null) return;
        foreach (string eventName in EventNames) CleanupEvent(eventName);
    }

    void CleanupEvent(string eventName)
    {
        var active = _eventManager.GetActiveEvent(eventName);
        if (active != null) active.Remove();
        if (_eventManager.IsEventActive(eventName)) _eventManager.MarkEventDone(eventName);
    }

    void AppendResult(string verdict, string screenshot)
    {
        string report = PlayerPrefs.GetString(ReportKey, string.Empty);
        if (string.IsNullOrEmpty(report)) return;

        float duration = _started ? Time.realtimeSinceStartup - _startedAt : 0f;
        var text = new StringBuilder();
        text.AppendLine($"## {Current.Id} — {Current.Title}");
        text.AppendLine();
        text.AppendLine($"- Result: **{verdict}**");
        text.AppendLine($"- Duration: {duration:F1} s");
        if (!string.IsNullOrWhiteSpace(_note)) text.AppendLine($"- Note: {Sanitize(_note)}");
        if (!string.IsNullOrEmpty(screenshot)) text.AppendLine($"- Screenshot: `{screenshot}`");
        if (_caseErrors.Count > 0)
        {
            text.AppendLine("- Console errors:");
            foreach (string error in _caseErrors) text.AppendLine($"  - {Sanitize(error)}");
        }
        else
        {
            text.AppendLine("- Console errors: none observed");
        }
        text.AppendLine();
        File.AppendAllText(report, text.ToString());
    }

    string CaptureFailure()
    {
        string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        string folder = Path.Combine(root, "production", "qa", "screenshots");
        Directory.CreateDirectory(folder);
        string file = $"guided-{Current.Id.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        string absolute = Path.Combine(folder, file);
        ScreenCapture.CaptureScreenshot(absolute);
        return Path.Combine("production", "qa", "screenshots", file).Replace('\\', '/');
    }

    void FinishRun()
    {
        string report = PlayerPrefs.GetString(ReportKey, string.Empty);
        if (!string.IsNullOrEmpty(report))
            File.AppendAllText(report, $"\nCompleted: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        PlayerPrefs.SetInt(ActiveKey, 0);
        PlayerPrefs.Save();
        _finished = true;
        _started = false;
        SetMouseMode(true);
    }

    void StopRun()
    {
        string report = PlayerPrefs.GetString(ReportKey, string.Empty);
        if (!string.IsNullOrEmpty(report))
            File.AppendAllText(report, $"\nStopped at {Current.Id}: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        CleanupAllEvents();
        PlayerPrefs.SetInt(ActiveKey, 0);
        PlayerPrefs.Save();
        _finished = true;
        SetMouseMode(true);
    }

    void SetMouseMode(bool mouseMode)
    {
        _mouseMode = mouseMode;
        Cursor.lockState = mouseMode ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = mouseMode;
    }

    void OnLog(string condition, string stackTrace, LogType type)
    {
        if (!_started || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
        if (_caseErrors.Count >= 20) return;
        _caseErrors.Add(condition);
    }

    static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    public static void BeginNewRun()
    {
        string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        string folder = Path.Combine(root, "production", "qa");
        Directory.CreateDirectory(folder);
        string report = Path.Combine(folder, $"manual-playtest-{DateTime.Now:yyyy-MM-dd-HHmm}.md");
        File.WriteAllText(report,
            $"# Guided Manual Playtest — {DateTime.Now:yyyy-MM-dd HH:mm}\n\n" +
            $"- Scene: `{ScenePath}`\n- Cases: {Cases.Length}\n- Mode: local single-player\n\n");

        PlayerPrefs.SetInt(ActiveKey, 1);
        PlayerPrefs.SetInt(IndexKey, 0);
        PlayerPrefs.SetString(ReportKey, report);
        PlayerPrefs.Save();
    }
}
#endif
