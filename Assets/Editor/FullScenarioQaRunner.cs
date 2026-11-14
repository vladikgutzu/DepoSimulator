#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// Editor-only smoke runner for the warehouse's critical solo scenarios.
/// It opens the training scene, enters Play Mode, exercises every event through the real
/// GameEventManager API, prints machine-readable PASS/FAIL lines, then returns to the scene that
/// was open before the run. Runtime assemblies and player builds do not include this file.
[InitializeOnLoad]
public static class FullScenarioQaRunner
{
    const string MenuPath = "Tools/QA/Run Full Scenario Smoke Test";
    const string ScenePath = "Assets/urp_scene_with_training 1.unity";
    const string PendingKey = "FullScenarioQA.Pending";
    const string RestoreKey = "FullScenarioQA.RestoreScene";

    static readonly BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly Stack<IEnumerator> SuiteStack = new();
    static int _pass;
    static int _fail;

    static readonly string[] TaughtEvents =
    {
        "BrokenForklift", "BrokenFork", "PowerFailure", "UrgentPart",
        "RushOrder", "Thief", "FireAlarm", "GateBreach",
    };

    static readonly string[] ObviousEvents =
    {
        "WetFloor", "ShelfCollapse", "BenchMalfunction", "WrongOrder",
        "SupplyDelay", "RadioInterference",
    };

    static FullScenarioQaRunner()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    [MenuItem(MenuPath, priority = 1)]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[FullQA] Stop Play Mode before starting a new full scenario run.");
            return;
        }

        var active = EditorSceneManager.GetActiveScene();
        if (active.isDirty)
        {
            Debug.LogError("[FullQA] Active scene has unsaved changes. Save it before running QA.");
            return;
        }

        SessionState.SetString(RestoreKey, active.path);
        SessionState.SetBool(PendingKey, true);
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Debug.Log($"[FullQA] Loaded {ScenePath}; entering Play Mode.");
        EditorApplication.isPlaying = true;
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(PendingKey, false))
        {
            _pass = 0;
            _fail = 0;
            SuiteStack.Clear();
            SuiteStack.Push(RunSuite());
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        if (state == PlayModeStateChange.EnteredEditMode)
        {
            EditorApplication.update -= Tick;
            SuiteStack.Clear();

            string restore = SessionState.GetString(RestoreKey, string.Empty);
            SessionState.EraseString(RestoreKey);
            if (!string.IsNullOrEmpty(restore) && restore != ScenePath)
                EditorApplication.delayCall += () => EditorSceneManager.OpenScene(restore, OpenSceneMode.Single);
        }
    }

    static void Tick()
    {
        try
        {
            while (SuiteStack.Count > 0)
            {
                var current = SuiteStack.Peek();
                if (!current.MoveNext())
                {
                    SuiteStack.Pop();
                    continue;
                }

                // Unity's coroutine scheduler automatically descends into nested IEnumerator
                // values. This runner uses EditorApplication.update, so it must mirror that
                // behaviour explicitly or event sub-scenarios are silently skipped.
                if (current.Current is IEnumerator nested)
                {
                    SuiteStack.Push(nested);
                    continue;
                }

                return;
            }
        }
        catch (Exception ex)
        {
            Fail("Runner completed without an unhandled exception", ex.ToString());
        }

        EditorApplication.update -= Tick;
        SuiteStack.Clear();
        SessionState.SetBool(PendingKey, false);
        Debug.Log($"[FullQA] RESULT pass={_pass} fail={_fail}");
        EditorApplication.delayCall += () => EditorApplication.isPlaying = false;
    }

    static IEnumerator RunSuite()
    {
        // Allow Awake/Start and scene registries to settle.
        for (int i = 0; i < 45; i++) yield return null;

        Debug.Log("[FullQA] BEGIN full solo scenario smoke test");

        var gm = GameManager.Instance ?? Object.FindFirstObjectByType<GameManager>();
        var events = GameEventManager.Instance ?? Object.FindFirstObjectByType<GameEventManager>();
        var tutorial = TutorialManager.Instance ?? Object.FindFirstObjectByType<TutorialManager>();
        var hud = HUDController.Instance ?? Object.FindFirstObjectByType<HUDController>();

        Check("core/GameManager exists", gm != null);
        Check("core/GameEventManager exists", events != null);
        Check("core/TutorialManager exists", tutorial != null);
        Check("core/HUDController exists", hud != null);

        Check("scene/Player exists", GameObject.FindGameObjectWithTag("Player") != null);
        Check("scene/OrderBoard exists", Object.FindFirstObjectByType<OrderBoard>() != null);
        Check("scene/AssemblyBench exists", AssemblyBench.Instance != null);
        Check("scene/ShippingZone exists", ShippingZone.Instance != null);
        Check("scene/cart exists", Object.FindFirstObjectByType<CartController>() != null);
        Check("scene/forklift exists", Object.FindFirstObjectByType<ReachTruckController>() != null);
        Check("scene/ladder exists", Object.FindFirstObjectByType<LadderController>() != null);
        ValidateAdditionalShadowBudget();

        if (ShippingZone.Instance != null)
        {
            Color regularZoneColor = GetPrivate<Color>(ShippingZone.Instance, "_activeColor");
            Check("shipping/regular zone uses required-part gold",
                Approximately(regularZoneColor.r, 1f)
                && Approximately(regularZoneColor.g, 0.8f)
                && Approximately(regularZoneColor.b, 0.05f), regularZoneColor.ToString());
        }

        if (gm != null && !gm.GameStarted)
            gm.StartGame();

        if (gm != null && gm.ActiveOrder == null)
        {
            var orders = gm.GetAvailableOrders();
            if (orders != null && orders.Length > 0)
                gm.AcceptOrder(orders[0]);
        }
        Check("orders/an active order can be accepted", gm != null && gm.ActiveOrder != null);

        ValidateTutorialSteps(tutorial);
        ValidateCarryConfiguration();
        ValidateForkliftPhysics();

        if (events != null && tutorial != null)
        {
            SetPrivate(tutorial, "_tutorialSkipped", false);
            SetPrivate(tutorial, "_baseTutorialPresentationComplete", true);

            foreach (string eventName in TaughtEvents)
            {
                yield return RunTaughtEvent(events, tutorial, eventName);
                // Destroy() is deferred; give cleanup two frames before the next event.
                yield return null;
                yield return null;
            }

            foreach (string eventName in ObviousEvents)
            {
                yield return RunObviousEvent(events, eventName);
                yield return null;
                yield return null;
            }
        }

        // This is a summary line, not another assertion: incrementing _fail here made two real
        // failures appear as three in the final RESULT.
        if (_fail == 0)
            Debug.Log("[FullQA] PASS console/no runner-detected failures");
        else
            Debug.LogError($"[FullQA] SUMMARY {_fail} check(s) failed");
        Debug.Log("[FullQA] END full solo scenario smoke test");
    }

    static void ValidateTutorialSteps(TutorialManager tutorial)
    {
        if (tutorial == null) return;

        var stepType = typeof(TutorialManager).GetNestedType("Step", BindingFlags.NonPublic);
        var goTo = typeof(TutorialManager).GetMethod("GoTo", InstancePrivate);
        var stepField = typeof(TutorialManager).GetField("_step", InstancePrivate);
        string[] steps =
        {
            "Welcome", "LookAround", "Movement", "OrderBoardIntro", "OrderBoardInteract",
            "OrderAccepted", "ShopIntro", "FindParts", "PickupFirst", "ThrowBox", "CollectAll",
            "BenchIntro", "BenchInteract", "PackOrder", "ShippingIntro", "Ship", "Done",
        };

        bool allReached = stepType != null && goTo != null && stepField != null;
        string detail = string.Empty;
        if (allReached)
        {
            foreach (string step in steps)
            {
                try
                {
                    object value = Enum.Parse(stepType, step);
                    goTo.Invoke(tutorial, new[] { value });
                    if (!string.Equals(stepField.GetValue(tutorial)?.ToString(), step,
                            StringComparison.Ordinal))
                    {
                        allReached = false;
                        detail = $"state did not enter {step}";
                        break;
                    }
                }
                catch (Exception ex)
                {
                    allReached = false;
                    detail = $"{step}: {ex.InnerException?.Message ?? ex.Message}";
                    break;
                }
            }
        }
        Check("tutorial/all base steps can be presented", allReached, detail);
        SetPrivate(tutorial, "_baseTutorialPresentationComplete", true);
    }

    static void ValidateCarryConfiguration()
    {
        var ladder = Object.FindFirstObjectByType<LadderController>();
        var offsetField = typeof(LadderController).GetField("_carryLocalOffset", InstancePrivate);
        if (ladder != null && offsetField?.GetValue(ladder) is Vector3 offset)
        {
            Check("carry/ladder offset is (-0.3, 0.2, 0.8)",
                Approximately(offset.x, -0.3f) && Approximately(offset.y, 0.2f)
                && Approximately(offset.z, 0.8f), offset.ToString("F3"));
        }
        else
        {
            Fail("carry/ladder offset is readable", "LadderController or field missing");
        }

        Check("carry/toolkit requirement enabled", ToolkitItem.Required);

        var player = GameObject.FindGameObjectWithTag("Player");
        Check("carry/PackedCarryHand_R exists", FindDeepChild(player?.transform, "PackedCarryHand_R") != null);
        Check("carry/PackedCarryHand_L exists", FindDeepChild(player?.transform, "PackedCarryHand_L") != null);
        Check("carry/PackedCarryBoxSupport exists", FindDeepChild(player?.transform, "PackedCarryBoxSupport") != null);
    }

    static void ValidateForkliftPhysics()
    {
        ReachTruckController truck = null;
        foreach (var candidate in Object.FindObjectsByType<ReachTruckController>(FindObjectsSortMode.None))
        {
            if (!GetPrivate<bool>(candidate, "_groundOnlyFork"))
            {
                truck = candidate;
                break;
            }
        }

        if (truck == null)
        {
            Fail("forklift/reach truck is available");
            return;
        }

        var body = truck.GetComponent<Rigidbody>();
        Check("forklift/rigidbody exists", body != null);
        if (body != null)
        {
            Check("forklift/drive damping does not reduce commanded speed",
                Approximately(body.linearDamping, 0f), $"linear damping={body.linearDamping:0.###}");
            Check("forklift/steering has no residual angular damping dependency",
                Approximately(body.angularDamping, 0f), $"angular damping={body.angularDamping:0.###}");
            Check("forklift/collision mode is Continuous Speculative",
                body.collisionDetectionMode == CollisionDetectionMode.ContinuousSpeculative,
                body.collisionDetectionMode.ToString());
        }

        bool invalidMeshEnabled = false;
        foreach (var mesh in truck.GetComponentsInChildren<MeshCollider>(true))
            if (mesh.enabled && !mesh.convex) { invalidMeshEnabled = true; break; }
        Check("forklift/no non-convex mesh collider on dynamic body", !invalidMeshEnabled);

        bool stickyDriveCollider = false;
        foreach (var collider in truck.GetComponentsInChildren<Collider>(true))
        {
            if (!collider.enabled || collider.isTrigger || collider is MeshCollider) continue;
            var material = collider.material;
            if (material == null || material.dynamicFriction > 0.001f
                                 || material.staticFriction > 0.001f)
            {
                stickyDriveCollider = true;
                break;
            }
        }
        Check("forklift/drive hull does not stick to racks", !stickyDriveCollider);
    }

    static void ValidateAdditionalShadowBudget()
    {
        int shadowMaps = 0;
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude))
        {
            if (!light.enabled || light.shadows == LightShadows.None) continue;
            if (light.type == LightType.Point) shadowMaps += 6;
            else if (light.type == LightType.Spot) shadowMaps += 1;
        }

        // Leaves generous headroom in the 8192px atlas instead of merely staying one map below
        // the hard limit, which would regress as soon as an event adds a temporary light.
        Check("rendering/additional shadow map budget <= 128", shadowMaps <= 128,
            $"estimated maps={shadowMaps}");
    }

    static IEnumerator RunTaughtEvent(GameEventManager manager, TutorialManager tutorial, string eventName)
    {
        bool firstStart = false;
        string firstException = string.Empty;
        try { firstStart = manager.ForceStartEvent(eventName); }
        catch (Exception ex) { firstException = ex.ToString(); }

        Check($"event/{eventName}/first start accepted", firstStart, firstException);
        bool waiting = GetPrivate<bool>(manager, "_waitingForEventIntro");
        bool card = GetPrivate<bool>(tutorial, "_eventIntroActive");
        object pending = GetPrivate<object>(manager, "_pendingIntroducedEvent");
        Check($"event/{eventName}/first appearance waits", waiting && card && pending != null);
        Check($"event/{eventName}/not applied before Continue", manager.GetActiveEvent(eventName) == null);

        tutorial.ContinueInfoStep();
        yield return null;

        Check($"event/{eventName}/Continue releases pending event",
            !GetPrivate<bool>(manager, "_waitingForEventIntro"));
        ValidateEventFeedback(eventName);
        CleanupEvent(manager, eventName);
        yield return null;

        bool repeatStart = false;
        string repeatException = string.Empty;
        try { repeatStart = manager.ForceStartEvent(eventName); }
        catch (Exception ex) { repeatException = ex.ToString(); }
        Check($"event/{eventName}/repeat start accepted", repeatStart, repeatException);
        Check($"event/{eventName}/repeat has no popup",
            !GetPrivate<bool>(manager, "_waitingForEventIntro")
            && !GetPrivate<bool>(tutorial, "_eventIntroActive"));
        CleanupEvent(manager, eventName);
    }

    static IEnumerator RunObviousEvent(GameEventManager manager, string eventName)
    {
        bool started = false;
        string exception = string.Empty;
        try { started = manager.ForceStartEvent(eventName); }
        catch (Exception ex) { exception = ex.ToString(); }
        Check($"event/{eventName}/start accepted", started, exception);
        Check($"event/{eventName}/does not block on popup",
            !GetPrivate<bool>(manager, "_waitingForEventIntro"));
        yield return null;
        CleanupEvent(manager, eventName);
    }

    static void ValidateEventFeedback(string eventName)
    {
        switch (eventName)
        {
            case "BrokenForklift":
            case "BrokenFork":
                Check($"event/{eventName}/toolkit spawned",
                    Object.FindFirstObjectByType<ToolkitItem>() != null);
                break;
            case "PowerFailure":
                Check("event/PowerFailure/toolkit spawned",
                    Object.FindFirstObjectByType<ToolkitItem>() != null);
                Check("event/PowerFailure/repair targets created",
                    Object.FindObjectsByType<PowerBoxRepairTarget>(FindObjectsSortMode.None).Length > 0);
                break;
            case "FireAlarm":
                Check("event/FireAlarm/repair targets created",
                    Object.FindObjectsByType<PowerBoxRepairTarget>(FindObjectsSortMode.None).Length > 0);
                break;
            case "UrgentPart":
            case "RushOrder":
                var zone = ShippingZone.Instance;
                Check($"event/{eventName}/shipping zone available", zone != null);
                Check($"event/{eventName}/independent destination is active",
                    zone != null && zone.TryGetEventDeliveryPosition(eventName, out _));
                Check($"event/{eventName}/uses a different gate from regular order",
                    zone != null && zone.EventGateIndex >= 0
                    && (GameManager.Instance?.ActiveOrder == null
                        || zone.EventGateIndex != zone.LastGateIndex),
                    zone == null ? "zone missing"
                                 : $"regular={zone.LastGateIndex}, event={zone.EventGateIndex}");
                Check($"event/{eventName}/has a separate HUD timer",
                    GetPrivate<string>(HUDController.Instance, "_eventTimerOwner") == eventName);

                string otherTimed = eventName == "RushOrder" ? "UrgentPart" : "RushOrder";
                Check($"event/{eventName}/blocks simultaneous {otherTimed}",
                    GameEventManager.Instance != null
                    && !GameEventManager.Instance.ForceStartEvent(otherTimed));
                break;
            case "Thief":
                Check("event/Thief/active instance created",
                    GameEventManager.Instance?.GetActiveEvent(eventName) != null);
                break;
            case "GateBreach":
                Check("event/GateBreach/gate registry available",
                    Object.FindFirstObjectByType<GateAnimator>() != null);
                break;
        }
    }

    static void CleanupEvent(GameEventManager manager, string eventName)
    {
        try { manager.GetActiveEvent(eventName)?.Remove(); }
        catch (Exception ex) { Fail($"event/{eventName}/cleanup", ex.ToString()); }
    }

    static Transform FindDeepChild(Transform root, string childName)
    {
        if (root == null) return null;
        if (root.name == childName) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var found = FindDeepChild(root.GetChild(i), childName);
            if (found != null) return found;
        }
        return null;
    }

    static bool Approximately(float a, float b) => Mathf.Abs(a - b) < 0.001f;

    static T GetPrivate<T>(object target, string fieldName)
    {
        if (target == null) return default;
        var field = target.GetType().GetField(fieldName, InstancePrivate);
        if (field == null) return default;
        object value = field.GetValue(target);
        return value is T typed ? typed : default;
    }

    static void SetPrivate(object target, string fieldName, object value)
    {
        target?.GetType().GetField(fieldName, InstancePrivate)?.SetValue(target, value);
    }

    static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _pass++;
            Debug.Log($"[FullQA] PASS {name}{Suffix(detail)}");
        }
        else
        {
            Fail(name, detail);
        }
    }

    static void Fail(string name, string detail = "")
    {
        _fail++;
        Debug.LogError($"[FullQA] FAIL {name}{Suffix(detail)}");
    }

    static string Suffix(string detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" | {detail}";
}
#endif
