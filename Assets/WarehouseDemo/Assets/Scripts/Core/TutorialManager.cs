using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// Step-by-step tutorial.
/// Steps advance either by game events, WASD checklist (Movement step only),
/// auto-timer (info steps), or proximity to the target object.
public class TutorialManager : MonoBehaviour
{
    public static TutorialManager Instance { get; private set; }

    // ── Step definitions ──────────────────────────────────────────────────────

    enum Step
    {
        Idle = 0,
        Welcome,            // auto-timer
        LookAround,         // auto-timer
        Movement,           // checklist: W A S D
        OrderBoardIntro,    // proximity: order board
        OrderBoardInteract, // event: order accepted
        OrderAccepted,      // auto-timer
        ShopIntro,          // event: shop tab opened
        FindParts,          // proximity: any highlighted part
        PickupFirst,        // event: a part box is lifted into the hands
        ThrowBox,           // event: player performs a charged box throw
        CollectAll,         // auto-timer: parts travel one box at a time
        BenchIntro,         // proximity: assembly bench
        BenchInteract,      // event: bench complete (all parts placed)
        PackOrder,          // event: player packs the box (E at bench)
        ShippingIntro,      // proximity: shipping zone
        Ship,               // event: order shipped
        Done,
    }

    readonly struct StepData
    {
        public readonly string Title;
        public readonly string Body;
        public readonly string EventKeyHint;
        public StepData(string title, string body, string hint = null)
        { Title = title; Body = body; EventKeyHint = hint; }
    }

    // Only Movement step uses a key checklist — all Space entries removed.
    static readonly (Key key, string label)[][] StepChecklists =
    {
        /* 0  Idle               */ null,
        /* 1  Welcome            */ null,
        /* 2  LookAround         */ null,
        /* 3  Movement           */ new[] { (Key.W, "W"), (Key.A, "A"), (Key.S, "S"), (Key.D, "D") },
        /* 4  OrderBoardIntro    */ null,
        /* 5  OrderBoardInteract */ null,
        /* 6  OrderAccepted      */ null,
        /* 7  ShopIntro          */ null,
        /* 8  FindParts          */ null,
        /* 9  PickupFirst        */ null,
        /* 10 ThrowBox           */ null,
        /* 11 CollectAll         */ null,
        /* 12 BenchIntro         */ null,
        /* 13 BenchInteract      */ null,
        /* 14 PackOrder          */ null,
        /* 15 ShippingIntro      */ null,
        /* 16 Ship               */ null,
        /* 17 Done               */ null,
    };

    static readonly StepData[] Steps =
    {
        // 0 Idle
        new("", ""),
        // 1 Welcome
        new("WELCOME TO THE WAREHOUSE!",
            "Hi! Ready to learn the ropes? Let's get you moving first."),
        // 2 LookAround
        new("LOOK AROUND",
            "Move the mouse to look around.\n\n" +
            "Look left, right, up and down — check out the warehouse around you!"),
        // 3 Movement — checklist [W][A][S][D]
        new("HOW TO MOVE",
            "Press each movement key at least once:\n\n" +
            "  W — forward        S — backward\n" +
            "  A — left           D — right\n\n" +
            "Hold SHIFT to run faster!"),
        // 4 OrderBoardIntro
        new("ORDER BOARD",
            "See the object glowing with a golden light?\n\n" +
            "That's the Order Board — this is where you get tasks for your shift.\n\n" +
            "Approach the glowing Order Board."),
        // 5 OrderBoardInteract — event
        new("ACCEPT AN ORDER",
            "You're right next to the Order Board.\n\n" +
            "Look at it and press the interaction key shown below to open the order list.\n\n" +
            "Find the first order and hit «Accept».", "E"),
        // 6 OrderAccepted
        new("ORDER ACCEPTED!",
            "Nice — your first order is in!\n\n" +
            "Check the left side of the screen: a list of needed parts just appeared.\n\n" +
            "Time to track them all down."),
        // 7 ShopIntro — event: shop tab opened
        new("THE SHOP",
            "Before you go — the Order Board has a second tab.\n\n" +
            "Use the interaction key on the Board again and switch to the SHOP tab.\n\n" +
            "That's where you can buy a cart, pallet jack, or forklift.", "E"),
        // 8 FindParts
        new("FIND THE NEEDED PARTS",
            "Needed parts are marked with a blue outline on the shelves!\n\n" +
            "Look for glowing boxes — those are exactly the parts needed for the order.\n\n" +
            "Approach the nearest marked box."),
        // 9 PickupFirst — event
        new("PICK UP A PART",
            "You're standing right next to a glowing part!\n\n" +
            "Aim at the box — a prompt will appear at the bottom of the screen.\n\n" +
            "Press the interaction key shown below to pick it up.", "E"),
        // 10 ThrowBox — event
        new("THROW A BOX",
            "Hold the interaction key to charge a throw, then release it.\n\n" +
            "The longer you hold, the farther the box flies.\n\n" +
            "Throw this box once, then pick it up again.", "E"),
        // 11 CollectAll — auto-timer
        new("ONE BOX AT A TIME",
            "You carry everything in your hands — there is no backpack.\n\n" +
            "Each part goes to the bench in its own box, one trip at a time.\n\n" +
            "Watch the task list on the left — it shows what is still missing."),
        // 11 BenchIntro
        new("ASSEMBLY BENCH",
            "Carry the box to the Assembly Bench — it glows with a golden light.\n\n" +
            "Approach the Bench."),
        // 12 BenchInteract — event
        new("PLACE PARTS ON THE BENCH",
            "You've made it to the Assembly Bench.\n\n" +
            "Aim at it with the box in your hands and press the interaction key to install the part.\n\n" +
            "Then bring the remaining boxes the same way.", "E"),
        // 13 PackOrder — event
        new("PACK THE ORDER",
            "All parts installed — the order is assembled!\n\n" +
            "Look at the bench and press the interaction key again.\n\n" +
            "The order will be packed into a box — take it.", "E"),
        // 14 ShippingIntro
        new("SHIPPING ZONE",
            "Got the box? Perfect.\n\n" +
            "Last step: find the glowing Shipping Zone.\n\n" +
            "Approach it to deliver the order."),
        // 15 Ship — event
        new("SHIP THE ORDER",
            "This is it — the final step!\n\n" +
            "Face the zone and press the interaction key shown below.\n\n" +
            "The order ships out and you collect your well-earned reward!", "E"),
        // 16 Done
        new("TRAINING COMPLETE!",
            "Great work! You went through the entire process from start to finish.\n\n" +
            "Now put it into practice — go to the Order Board\n" +
            "and take the next order on your own!\n\n" +
            "This time without hints — you've got this!"),
    };

    // ── State ─────────────────────────────────────────────────────────────────

    Step      _step = Step.Idle;
    bool[]    _checklistDone;
    bool      _benchSubscribed;
    bool      _packSubscribed;
    bool      _throwSubscribed;
    bool      _eventIntroActive;
    bool      _batteryIntroActive;
    bool      _batteryIntroPending;
    bool      _batteryIntroSeen;
    bool      _baseTutorialPresentationComplete;
    bool      _tutorialSkipped;
    Coroutine _autoAdvance;

    public bool BaseTutorialPresentationComplete => _baseTutorialPresentationComplete;
    public bool TutorialSkipped => _tutorialSkipped;

    // Cached scene references for proximity checks
    Transform  _playerTransform;
    OrderBoard _orderBoard;
    ShippingZone _shippingZone;

    // Proximity thresholds
    const float ProximityBoard    = 5f;
    const float ProximityPart     = 6f;
    const float ProximityBench    = 5f;
    const float ProximityShipping = 5f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        if (Instance != null) { Destroy(this); return; }
        Instance = this;
    }

    void Start()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;
        gm.OnOrderAccepted  += OnOrderAccepted;
        gm.OnOrderCompleted += OnOrderCompleted;
        gm.OnOrderExpired   += OnOrderExpired;
        HUDController.OnShopTabOpened += OnShopTabOpened;
        StartCoroutine(WaitForStart());
    }

    void OnDestroy()
    {
        Instance = null;
        TutorialHighlighter.ClearAll();
        UnsubscribeBench();
        UnsubscribePack();
        UnsubscribeThrow();
        HUDController.OnShopTabOpened -= OnShopTabOpened;
        var gm = GameManager.Instance;
        if (gm == null) return;
        gm.OnOrderAccepted  -= OnOrderAccepted;
        gm.OnOrderCompleted -= OnOrderCompleted;
        gm.OnOrderExpired   -= OnOrderExpired;
    }

    void OnShopTabOpened()
    {
        if (_step != Step.ShopIntro) return;
        GoTo(Step.FindParts);
    }

    // ── Update: WASD checklist + proximity triggers ───────────────────────────

    void Update()
    {
        TryPresentBatteryIntro();

        // Gameplay keeps the pointer locked, so informational cards advance from the keyboard or
        // gamepad instead of requiring an inaccessible mouse click. The visible UI button remains
        // useful as a prompt and still works if a cursor is available for accessibility tooling.
        if (CanContinueCurrentStep())
        {
            var kb = Keyboard.current;
            var gp = Gamepad.current;
            bool continuePressed = (kb != null && kb.enterKey.wasPressedThisFrame)
                                || (gp != null && gp.buttonSouth.wasPressedThisFrame);
            if (continuePressed)
            {
                ContinueInfoStep();
                return;
            }
        }

        // Reconcile real game state before evaluating the currently displayed step. Players are
        // allowed to experiment; completing an action early must advance the lesson rather than
        // leave it asking for an object that has already been consumed or packed.
        if (CatchUpToCompletedAction()) return;

        // WASD checklist (Movement step only)
        var checklist = StepChecklists[(int)_step];
        if (checklist != null && _checklistDone != null)
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                bool allDone = true;
                for (int i = 0; i < checklist.Length; i++)
                {
                    if (_checklistDone[i]) continue;
                    allDone = false;
                    if (kb[checklist[i].key].wasPressedThisFrame)
                    {
                        _checklistDone[i] = true;
                        HUDController.Instance?.ChecklistTick(i);
                    }
                }
                if (!allDone)
                {
                    allDone = true;
                    for (int i = 0; i < _checklistDone.Length; i++)
                        if (!_checklistDone[i]) { allDone = false; break; }
                }
                if (allDone) GoTo(_step + 1);
            }
            return; // don't run proximity while on a checklist step
        }

        CheckProximity();
    }

    bool CatchUpToCompletedAction()
    {
        if (_step >= Step.FindParts && _step < Step.PackOrder &&
            AssemblyBench.Instance?.IsOrderComplete() == true)
        {
            GoTo(Step.PackOrder);
            SubscribePack();
            return true;
        }

        if (_step >= Step.FindParts && _step < Step.ShippingIntro &&
            Object.FindFirstObjectByType<PackedOrderBox>() != null)
        {
            GoTo(Step.ShippingIntro);
            return true;
        }

        if ((_step == Step.FindParts || _step == Step.PickupFirst) && IsCarryingPartBox())
        {
            GoTo(Step.ThrowBox);
            return true;
        }

        return false;
    }

    void CheckProximity()
    {
        // Lifting a part box moves the tutorial on from wherever the search had got to — the player
        // may well grab one before walking close enough to trip the proximity step.
        if ((_step == Step.FindParts || _step == Step.PickupFirst) && IsCarryingPartBox())
        {
            GoTo(Step.ThrowBox);
            return;
        }

        var pos = GetPlayerPos();
        if (pos == Vector3.zero) return;

        switch (_step)
        {
            case Step.OrderBoardIntro:
                if (_orderBoard == null) _orderBoard = Object.FindFirstObjectByType<OrderBoard>();
                if (_orderBoard != null &&
                    Vector3.Distance(pos, _orderBoard.transform.position) < ProximityBoard)
                    GoTo(Step.OrderBoardInteract);
                break;

            case Step.FindParts:
                // Catch-up: player may have already packed the order while tutorial was stuck here
                if (Object.FindFirstObjectByType<PackedOrderBox>() != null)
                { GoTo(Step.ShippingIntro); break; }
                if (TutorialHighlighter.HasAnyNear(pos, ProximityPart))
                    GoTo(Step.PickupFirst);
                break;

            case Step.BenchIntro:
                if (AssemblyBench.Instance != null &&
                    Vector3.Distance(pos, AssemblyBench.Instance.transform.position) < ProximityBench)
                    GoTo(Step.BenchInteract);
                break;

            case Step.ShippingIntro:
                if (_shippingZone == null) _shippingZone = Object.FindFirstObjectByType<ShippingZone>();
                if (_shippingZone != null &&
                    Vector3.Distance(pos, _shippingZone.transform.position) < ProximityShipping)
                    GoTo(Step.Ship);
                break;
        }
    }

    Vector3 GetPlayerPos()
    {
        if (_playerTransform == null)
        {
            var fpc = Object.FindFirstObjectByType<FirstPersonController>();
            if (fpc != null)
            {
                _playerTransform = fpc.transform;
            }
            else
            {
                // Current scene rigs use StarterAssets' ThirdPersonController instead of
                // FirstPersonController (same fallback pattern as ReachTruckController.Enter()
                // and CartController.Grab()). Without this, _playerTransform never gets set on
                // a TPC-only scene, GetPlayerPos() always returns Vector3.zero, and every
                // proximity-gated tutorial step (Order Board, Bench, Shipping Zone) silently
                // never advances no matter how close the player stands.
                var tpc = Object.FindFirstObjectByType<StarterAssets.ThirdPersonController>();
                if (tpc != null) _playerTransform = tpc.transform;
            }
        }
        return _playerTransform != null ? _playerTransform.position : Vector3.zero;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Skip()
    {
        if (_batteryIntroActive)
        {
            _batteryIntroActive = false;
        }
        bool releasePendingEvent = _eventIntroActive;
        StopAutoAdvance();
        TutorialHighlighter.ClearAll();
        UnsubscribeBench();
        UnsubscribeThrow();
        _tutorialSkipped = true;
        _baseTutorialPresentationComplete = true;
        _eventIntroActive = false;
        _step          = Step.Done;
        _checklistDone = null;
        HUDController.Instance?.HideTutorial();
        var gm = GameManager.Instance;
        if (gm != null)
        {
            gm.OnOrderAccepted  -= OnOrderAccepted;
            gm.OnOrderCompleted -= OnOrderCompleted;
        }

        if (releasePendingEvent)
            GameEventManager.Instance?.ContinueIntroducedEvent();
    }

    /// Called by the tutorial's CONTINUE button. Only informational cards accept it; gameplay
    /// steps still advance exclusively when their requested action is actually completed.
    public void ContinueInfoStep()
    {
        if (_batteryIntroActive)
        {
            _batteryIntroActive = false;
            HUDController.Instance?.HideTutorial();
            return;
        }

        if (_eventIntroActive)
        {
            _eventIntroActive = false;
            HUDController.Instance?.HideTutorial();
            GameEventManager.Instance?.ContinueIntroducedEvent();
            return;
        }

        switch (_step)
        {
            case Step.Welcome:       GoTo(Step.LookAround); break;
            case Step.LookAround:    GoTo(Step.Movement); break;
            case Step.OrderAccepted: GoTo(Step.ShopIntro); break;
            case Step.CollectAll:    GoTo(Step.BenchIntro); break;
        }
    }

    bool CanContinueCurrentStep() => _batteryIntroActive || _eventIntroActive
        || _step is (Step.Welcome or Step.LookAround or Step.OrderAccepted or Step.CollectAll);

    public void RequestBatteryIntro()
    {
        if (_batteryIntroSeen || _batteryIntroPending || _batteryIntroActive) return;
        _batteryIntroSeen = true;
        _batteryIntroPending = true;
        TryPresentBatteryIntro();
    }

    void TryPresentBatteryIntro()
    {
        if (!_batteryIntroPending || _batteryIntroActive || _eventIntroActive) return;

        bool multiplayer = Unity.Netcode.NetworkManager.Singleton != null
                        && Unity.Netcode.NetworkManager.Singleton.IsListening;
        if (!multiplayer && !_tutorialSkipped && !_baseTutorialPresentationComplete) return;

        var hud = HUDController.Instance;
        if (hud == null) return;

        _batteryIntroPending = false;
        _batteryIntroActive = true;
        hud.ShowTutorialFull(
            "SYSTEM",
            "BATTERY LOW",
            "Your robot runs on battery and needs regular charging.\n\n" +
            "Charging stations are marked on the Warehouse Map. Go to the nearest station.",
            null,
            null,
            true);
    }

    /// Shows the first-use explanation for one non-obvious event type. The event manager keeps
    /// the selected event pending, so timers, spawned hazards and world changes do not begin
    /// while the player is reading. Returning false means this type needs no teaching card.
    public bool TryShowEventIntro(string eventName)
    {
        if (_tutorialSkipped || !_baseTutorialPresentationComplete || _eventIntroActive
            || _batteryIntroActive || _batteryIntroPending)
            return false;

        var hud = HUDController.Instance;
        if (hud == null) return false;

        string title;
        string body;
        switch (eventName)
        {
            case "BrokenForklift":
                title = "FORKLIFT MALFUNCTION";
                body = "A vehicle has broken down. Find the marked toolkit, then approach the damaged vehicle and hold the interaction key to repair it.";
                break;
            case "BrokenFork":
                title = "FORK JAMMED";
                body = "The forklift hydraulics are jammed. Find the marked toolkit, then approach the affected vehicle and hold the interaction key to repair the fork.";
                break;
            case "PowerFailure":
                title = "POWER FAILURE";
                body = "The warehouse has lost power. Find the marked toolkit, then locate and repair all glowing power boxes to restore electricity.";
                break;
            case "UrgentPart":
                title = "URGENT PART";
                body = "Find the red highlighted part and deliver it to the red Shipping Zone before time runs out. Do not take it to the Assembly Bench.";
                break;
            case "RushOrder":
                title = "RUSH ORDER";
                body = "Find the purple highlighted boxes and deliver them to the purple Shipping Zone before time runs out.";
                break;
            case "Thief":
                title = "WAREHOUSE RAID";
                body = "Intruders are trying to steal and destroy warehouse property. Grab a weapon and stop the threat before they escape.";
                break;
            case "FireAlarm":
                title = "FIRE ALARM";
                body = "The alarm has disabled the Assembly Bench. Find and repair the marked power boxes to clear the emergency.";
                break;
            case "GateBreach":
                title = "GATE BREACH";
                body = "Something is forcing its way through a loading gate. Follow the marker, reach the gate and stop the attackers before they get inside.";
                break;
            default:
                return false;
        }

        _eventIntroActive = true;
        hud.ShowTutorialFull(
            "EVENT",
            title,
            body + "\n\nThe event and its timer will begin after you continue.",
            null,
            null,
            true);
        return true;
    }

    // ── Coroutines ────────────────────────────────────────────────────────────

    IEnumerator WaitForStart()
    {
        while (GameManager.Instance == null || !GameManager.Instance.GameStarted)
            yield return null;

        // Skip tutorial in co-op — each player already knows the warehouse loop.
        if (Unity.Netcode.NetworkManager.Singleton != null
            && Unity.Netcode.NetworkManager.Singleton.IsListening)
        {
            HUDController.Instance?.HideTutorial();
            yield break;
        }

        yield return new WaitForSeconds(0.5f);
        GoTo(Step.Welcome);
    }

    IEnumerator HideAfterDelay(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        HUDController.Instance?.HideTutorial();
    }

    IEnumerator AutoAdvanceAfter(Step from, Step to, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_step == from) GoTo(to);
    }

    void StopAutoAdvance()
    {
        if (_autoAdvance == null) return;
        StopCoroutine(_autoAdvance);
        _autoAdvance = null;
    }

    // ── Game events ───────────────────────────────────────────────────────────

    void OnOrderAccepted(OrderDefinition _)
    {
        if (_step == Step.Idle || _step >= Step.OrderAccepted) return;
        TutorialHighlighter.ClearAll();
        GoTo(Step.OrderAccepted);
    }

    static bool IsCarryingPartBox()
    {
        var box = BoxPhysicsInteractable.GrabbedBox;
        var bwp = box != null ? box.GetComponent<BoxWithPart>() : null;
        return bwp != null && bwp.Part != null;
    }

    void OnBoxThrown()
    {
        if (_step != Step.ThrowBox) return;
        GoTo(Step.CollectAll);
    }

    void OnBenchChanged()
    {
        // Accept from FindParts onwards so bench progress is caught even if tutorial lagged
        if (_step >= Step.PackOrder || _step < Step.FindParts) return;
        if (AssemblyBench.Instance?.IsOrderComplete() != true) return;

        UnsubscribeBench();
        TutorialHighlighter.ClearAll();
        GoTo(Step.PackOrder);
        SubscribePack();
    }

    void OnOrderPacked()
    {
        if (_step >= Step.ShippingIntro) return;
        UnsubscribePack();
        GoTo(Step.ShippingIntro);
    }

    void OnOrderCompleted(OrderDefinition _)
    {
        if (GameManager.Instance?.CompletedOrders != 1) return;
        TutorialHighlighter.ClearAll();
        UnsubscribeBench();
        GoTo(Step.Done);
        StartCoroutine(HideAfterDelay(5f));
        StartCoroutine(PostTutorialRoutine());
        var gm = GameManager.Instance;
        if (gm != null)
        {
            gm.OnOrderAccepted  -= OnOrderAccepted;
            gm.OnOrderCompleted -= OnOrderCompleted;
        }
    }

    // After the tutorial's Done message fades, point the player back to the order board — in
    // words only.
    //
    // This used to light the board up as well. The glow is a teaching aid: it exists so a player
    // who has never seen this warehouse can find the board at all, and once the tutorial has walked
    // him to it twice he knows where it is. Left on afterwards it is just a permanent gold object
    // in the middle of the shift — and it had a way of becoming exactly that, since what cleared it
    // was the next accept, which a quick player triggered before the glow was even lit.
    IEnumerator PostTutorialRoutine()
    {
        yield return new WaitForSeconds(5.5f);
        if (_step != Step.Done) yield break;

        _baseTutorialPresentationComplete = true;

        bool isMultiplayer = Unity.Netcode.NetworkManager.Singleton != null
                          && Unity.Netcode.NetworkManager.Singleton.IsListening;
        if (isMultiplayer) yield break;

        HUDController.Instance?.AddNotification(
            "Free practice",
            "Go to the Order Board and take the next order on your own!",
            HUDController.NotifType.Warning);
    }

    void OnOrderExpired(OrderDefinition _)
    {
        if (_step is Step.Idle or Step.Welcome or Step.Done) return;
        UnsubscribeBench();
        TutorialHighlighter.ClearAll();
        GoTo(Step.OrderBoardInteract);
    }

    // ── Step machine ──────────────────────────────────────────────────────────

    void GoTo(Step step)
    {
        StopAutoAdvance();
        UnsubscribeThrow();

        _step = step;

        var checklist  = StepChecklists[(int)step];
        _checklistDone = checklist != null ? new bool[checklist.Length] : null;

        if (step == Step.Idle)
        {
            HUDController.Instance?.HideTutorial();
            return;
        }

        var d     = Steps[(int)step];
        int num   = (int)step;
        int total = (int)Step.Done - 1;
        var label = step == Step.Done ? "DONE!" : $"STEP {num} / {total}";

        string[] checklistLabels = null;
        if (checklist != null)
        {
            checklistLabels = new string[checklist.Length];
            for (int i = 0; i < checklist.Length; i++)
                checklistLabels[i] = checklist[i].label;
        }

        bool canContinue = CanContinueCurrentStep();
        string keyHint = d.EventKeyHint == "E"
            ? InputHints.Active(GameBindingAction.Interact)
            : d.EventKeyHint;
        HUDController.Instance?.ShowTutorialFull(
            label, d.Title, d.Body, keyHint, checklistLabels, canContinue);

        // Object highlights
        switch (step)
        {
            case Step.OrderBoardIntro:
            case Step.OrderBoardInteract:
            case Step.ShopIntro:
                HighlightOrderBoard();
                break;
            case Step.FindParts:
                HighlightAllRequiredParts();
                SubscribeBench();
                SubscribePack();
                break;
            case Step.PickupFirst:
                // No re-highlight here — PickupFirst is only ever reached via CheckProximity()'s
                // Step.FindParts case, immediately after FindParts already highlighted this exact
                // same set of parts. Re-running HighlightAllRequiredParts() (which starts with
                // TutorialHighlighter.ClearAll()) would just strip and reapply the same outline a
                // frame later for no reason — visible as a brief glow flicker right as the player
                // gets close enough to trigger this transition.
                break;
            case Step.ThrowBox:
                SubscribeThrow();
                break;
            case Step.BenchIntro:
                HighlightBench();
                break;
            case Step.BenchInteract:
                HighlightBench();
                SubscribeBench();
                break;
            case Step.PackOrder:
                // No highlight — bench is right in front of the player at this step
                break;
            case Step.ShippingIntro:
            case Step.Ship:
                HighlightShipping();
                break;
            case Step.Done:
            case Step.Idle:
                TutorialHighlighter.ClearAll();
                UnsubscribeBench();
                UnsubscribePack();
                break;
        }
    }

    // ── Object highlights ─────────────────────────────────────────────────────

    void HighlightAllRequiredParts()
    {
        TutorialHighlighter.ClearAll();
        var order = GameManager.Instance?.ActiveOrder;
        if (order == null) { Debug.LogWarning("[Tutorial] HighlightAllRequiredParts: no active order"); return; }

        int highlighted = 0;
        foreach (var bwp in BoxWithPart.All)
        {
            if (bwp == null) continue;
            if (!bwp.IsRequired) continue;
            if (IsInTrailerPallets(bwp.transform)) continue;
            TutorialHighlighter.Highlight(bwp.gameObject);
            highlighted++;
        }

        Debug.Log($"[Tutorial] Highlighted {highlighted} objects | BoxWithParts={BoxWithPart.All.Count}");
    }

    static bool IsInTrailerPallets(Transform t)
    {
        for (var p = t.parent; p != null; p = p.parent)
            if (p.name == "TrailerPallets") return true;
        return false;
    }

    void HighlightOrderBoard()
    {
        if (_orderBoard == null) _orderBoard = Object.FindFirstObjectByType<OrderBoard>();
        if (_orderBoard != null) TutorialHighlighter.Highlight(_orderBoard.gameObject);
    }

    void HighlightBench()
    {
        if (AssemblyBench.Instance != null)
            TutorialHighlighter.Highlight(AssemblyBench.Instance.gameObject);
    }

    void HighlightShipping()
    {
        if (_shippingZone == null) _shippingZone = Object.FindFirstObjectByType<ShippingZone>();
        if (_shippingZone != null) TutorialHighlighter.Highlight(_shippingZone.gameObject);
    }

    // ── Subscriptions ─────────────────────────────────────────────────────────

    void SubscribeThrow()
    {
        if (_throwSubscribed) return;
        PlayerInteraction.BoxThrown += OnBoxThrown;
        _throwSubscribed = true;
    }

    void UnsubscribeThrow()
    {
        if (!_throwSubscribed) return;
        PlayerInteraction.BoxThrown -= OnBoxThrown;
        _throwSubscribed = false;
    }

    void SubscribeBench()
    {
        if (_benchSubscribed) return;
        var bench = AssemblyBench.Instance;
        if (bench == null) return;
        bench.OnBenchChanged += OnBenchChanged;
        _benchSubscribed = true;
    }

    void UnsubscribeBench()
    {
        if (!_benchSubscribed) return;
        var bench = AssemblyBench.Instance;
        if (bench != null) bench.OnBenchChanged -= OnBenchChanged;
        _benchSubscribed = false;
    }

    void SubscribePack()
    {
        if (_packSubscribed) return;
        var bench = AssemblyBench.Instance;
        if (bench == null) return;
        bench.OnOrderPacked += OnOrderPacked;
        _packSubscribed = true;
    }

    void UnsubscribePack()
    {
        if (!_packSubscribed) return;
        var bench = AssemblyBench.Instance;
        if (bench != null) bench.OnOrderPacked -= OnOrderPacked;
        _packSubscribed = false;
    }
}
