using UnityEngine;
using System.Reflection;
using System.Text;

public static class TutorialLiveTest
{
    static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly BindingFlags Pub  = BindingFlags.Public    | BindingFlags.Instance;
    static System.Type StepType =>
        typeof(TutorialManager).GetNestedType("Step", BindingFlags.NonPublic);

    public static string Execute()
    {
        var sb = new StringBuilder();

        // ── 0. Bootstrap: activate HUD, start game ────────────────────────────
        GameObject.Find("MainMenu_UIDocument")?.SetActive(false);

        var hudCtrl = Object.FindObjectsByType<HUDController>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (hudCtrl.Length > 0) hudCtrl[0].gameObject.SetActive(true);

        var gm = Object.FindFirstObjectByType<GameManager>();
        if (gm != null && !gm.GameStarted)
        {
            (typeof(GameManager).GetMethod("StartGame", Pub)
          ?? typeof(GameManager).GetMethod("StartGame", Priv))?.Invoke(gm, null);
        }

        var tm  = Object.FindFirstObjectByType<TutorialManager>(FindObjectsInactive.Exclude);
        var hud = HUDController.Instance
               ?? Object.FindFirstObjectByType<HUDController>(FindObjectsInactive.Exclude);

        sb.AppendLine($"Bootstrap: gm={gm!=null} tm={tm!=null} hud={hud!=null} GameStarted={gm?.GameStarted}");
        if (tm == null || gm == null || hud == null)
            return sb + "\nFAIL: cannot continue without core singletons";

        var goTo  = typeof(TutorialManager).GetMethod("GoTo", Priv);
        var stepF = typeof(TutorialManager).GetField("_step", Priv);
        var chkTk = typeof(HUDController).GetMethod("ChecklistTick", Pub)
                 ?? typeof(HUDController).GetMethod("ChecklistTick", Priv);

        int pass = 0, fail = 0;

        void Check(string label, bool ok, string detail = "")
        {
            sb.AppendLine($"  {(ok?"✓":"✗")} {label}{(detail.Length>0 ? " | "+detail : "")}");
            if (ok) pass++; else fail++;
        }

        void GoTo(string name)
        {
            try { goTo.Invoke(tm, new[]{ System.Enum.Parse(StepType, name) }); }
            catch (System.Exception e)
            { sb.AppendLine($"  ✗ GoTo({name}) EXCEPTION: {e.InnerException?.Message ?? e.Message}"); fail++; }
        }

        string CurStep() => stepF?.GetValue(tm)?.ToString() ?? "null";
        int Highlights()
        {
            var f = typeof(TutorialHighlighter).GetField("_active", BindingFlags.NonPublic | BindingFlags.Static);
            return (f?.GetValue(null) as System.Collections.IList)?.Count ?? -1;
        }

        // ── Welcome ────────────────────────────────────────────────────────────
        sb.AppendLine("\n--- Welcome ---");
        GoTo("Welcome");
        Check("step==Welcome", CurStep()=="Welcome");
        var autoAdv = typeof(TutorialManager).GetField("_autoAdvance", Priv)?.GetValue(tm);
        Check("auto-advance coroutine started", autoAdv != null);

        // ── LookAround ─────────────────────────────────────────────────────────
        sb.AppendLine("\n--- LookAround ---");
        GoTo("LookAround");
        Check("step==LookAround", CurStep()=="LookAround");

        // ── Movement (checklist) ───────────────────────────────────────────────
        sb.AppendLine("\n--- Movement ---");
        GoTo("Movement");
        Check("step==Movement", CurStep()=="Movement");
        var chkDone = typeof(TutorialManager).GetField("_checklistDone", Priv)?.GetValue(tm) as bool[];
        Check("checklist has 4 items [W,A,S,D]", chkDone?.Length == 4, $"got {chkDone?.Length}");
        if (chkTk != null)
        {
            bool tkOk = true;
            try { for (int i = 0; i < 4; i++) chkTk.Invoke(hud, new object[]{i}); }
            catch { tkOk = false; }
            Check("ChecklistTick(0-3) no exception", tkOk);
        }

        // ── OrderBoardIntro ────────────────────────────────────────────────────
        sb.AppendLine("\n--- OrderBoardIntro ---");
        GoTo("OrderBoardIntro");
        Check("step==OrderBoardIntro", CurStep()=="OrderBoardIntro");
        Check("OrderBoard highlighted", Highlights() > 0, $"active={Highlights()}");

        // ── OrderBoardInteract ─────────────────────────────────────────────────
        sb.AppendLine("\n--- OrderBoardInteract ---");
        GoTo("OrderBoardInteract");
        Check("step==OrderBoardInteract", CurStep()=="OrderBoardInteract");

        // ── OrderAccepted (after accepting real order) ─────────────────────────
        sb.AppendLine("\n--- OrderAccepted ---");
        if (gm.ActiveOrder == null)
        {
            var orders = gm.GetAvailableOrders();
            if (orders?.Length > 0) gm.AcceptOrder(orders[0]);
        }
        GoTo("OrderAccepted");
        Check("step==OrderAccepted", CurStep()=="OrderAccepted");
        Check("ActiveOrder present", gm.ActiveOrder != null, gm.ActiveOrder?.name ?? "null");

        // ── FindParts ──────────────────────────────────────────────────────────
        sb.AppendLine("\n--- FindParts ---");
        GoTo("FindParts");
        Check("step==FindParts", CurStep()=="FindParts");

        // ── PickupFirst ────────────────────────────────────────────────────────
        sb.AppendLine("\n--- PickupFirst ---");
        GoTo("PickupFirst");
        Check("step==PickupFirst", CurStep()=="PickupFirst");

        // ── CollectAll ─────────────────────────────────────────────────────────
        sb.AppendLine("\n--- CollectAll ---");
        GoTo("CollectAll");
        Check("step==CollectAll", CurStep()=="CollectAll");

        // ── BenchIntro ─────────────────────────────────────────────────────────
        sb.AppendLine("\n--- BenchIntro ---");
        GoTo("BenchIntro");
        Check("step==BenchIntro", CurStep()=="BenchIntro");
        Check("Bench highlighted", Highlights() > 0, $"active={Highlights()}");

        // ── BenchInteract ──────────────────────────────────────────────────────
        sb.AppendLine("\n--- BenchInteract ---");
        GoTo("BenchInteract");
        Check("step==BenchInteract", CurStep()=="BenchInteract");
        bool benchSub = (bool)(typeof(TutorialManager).GetField("_benchSubscribed", Priv)?.GetValue(tm) ?? false);
        Check("bench subscribed", benchSub);
        Check("Bench highlighted", Highlights() > 0, $"active={Highlights()}");

        // ── PackOrder ──────────────────────────────────────────────────────────
        sb.AppendLine("\n--- PackOrder ---");
        GoTo("PackOrder");
        Check("step==PackOrder", CurStep()=="PackOrder");
        Check("Bench still highlighted", Highlights() > 0, $"active={Highlights()}");

        // ── ShippingIntro ──────────────────────────────────────────────────────
        sb.AppendLine("\n--- ShippingIntro ---");
        GoTo("ShippingIntro");
        Check("step==ShippingIntro", CurStep()=="ShippingIntro");
        Check("ShippingZone highlighted", Highlights() > 0, $"active={Highlights()}");

        // ── Ship ───────────────────────────────────────────────────────────────
        sb.AppendLine("\n--- Ship ---");
        GoTo("Ship");
        Check("step==Ship", CurStep()=="Ship");
        Check("ShippingZone highlighted", Highlights() > 0, $"active={Highlights()}");

        // ── Done ───────────────────────────────────────────────────────────────
        sb.AppendLine("\n--- Done ---");
        GoTo("Done");
        Check("step==Done", CurStep()=="Done");
        Check("all highlights cleared on Done", Highlights() == 0, $"active={Highlights()}");
        bool benchSb1 = (bool)(typeof(TutorialManager).GetField("_benchSubscribed", Priv)?.GetValue(tm) ?? false);
        bool packSb1  = (bool)(typeof(TutorialManager).GetField("_packSubscribed",  Priv)?.GetValue(tm) ?? false);
        Check("no subscription leaks", !benchSb1 && !packSb1,
            $"bench={benchSb1} pack={packSb1}");

        // ── Skip() API ─────────────────────────────────────────────────────────
        sb.AppendLine("\n--- Skip() ---");
        GoTo("FindParts"); // go somewhere mid-tutorial
        typeof(TutorialManager).GetMethod("Skip", Pub)?.Invoke(tm, null);
        Check("Skip() lands on Done", CurStep()=="Done");

        // ── Final ──────────────────────────────────────────────────────────────
        sb.AppendLine($"\n{'=',3} RESULT: {pass} pass, {fail} fail {'=',3}");
        if (fail == 0) sb.AppendLine("ТУТОРИАЛ РАБОТАЕТ КОРРЕКТНО ✓");

        return sb.ToString();
    }
}
