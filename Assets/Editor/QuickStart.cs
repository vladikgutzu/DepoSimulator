using UnityEngine;
using System.Reflection;

public static class QuickStart
{
    static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly BindingFlags Pub  = BindingFlags.Public    | BindingFlags.Instance;

    public static string FullStart()
    {
        // 1. Hide main menu
        var mmgo = GameObject.Find("MainMenu_UIDocument");
        mmgo?.SetActive(false);

        // 2. Find HUD including inactive objects, activate it
        var allGOs = Object.FindObjectsByType<HUDController>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        HUDController hudCtrl = allGOs.Length > 0 ? allGOs[0] : null;
        if (hudCtrl != null) hudCtrl.gameObject.SetActive(true);

        // 3. Start game
        var gm = Object.FindFirstObjectByType<GameManager>();
        if (gm != null && !gm.GameStarted)
        {
            var sg = typeof(GameManager).GetMethod("StartGame", Pub)
                  ?? typeof(GameManager).GetMethod("StartGame", Priv);
            sg?.Invoke(gm, null);
        }

        // 4. Go to Welcome
        GoToStepInternal("Welcome");

        return $"GameStarted={gm?.GameStarted}  " +
               $"HUD_GO={hudCtrl?.gameObject.activeSelf}  " +
               $"HUDController.Instance={HUDController.Instance != null}  " +
               $"Step={GetStep()}";
    }

    public static string GoToStep(string stepName)
        => $"Step={GoToStepInternal(stepName)}";

    static string GoToStepInternal(string stepName)
    {
        var tm = TutorialManager.Instance
              ?? Object.FindFirstObjectByType<TutorialManager>();
        if (tm == null) return "TutorialManager not found";
        var stepType = typeof(TutorialManager).GetNestedType("Step", BindingFlags.NonPublic);
        var goTo     = typeof(TutorialManager).GetMethod("GoTo",
                           BindingFlags.NonPublic | BindingFlags.Instance);
        if (stepType == null || goTo == null) return "reflection error";
        goTo.Invoke(tm, new[] { System.Enum.Parse(stepType, stepName) });
        return GetStep();
    }

    public static string AcceptOrder()
    {
        var gm = GameManager.Instance;
        if (gm == null) return "FAIL: no GameManager";
        var orders = gm.GetAvailableOrders();
        if (orders == null || orders.Length == 0) return "no orders available";
        bool ok = gm.AcceptOrder(orders[0]);
        return $"Accepted={ok}  Order={orders[0]?.name}  Step={GetStep()}";
    }

    public static string GotoOrderBoard()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        var board  = Object.FindFirstObjectByType<OrderBoard>();
        if (player == null || board == null) return "FAIL";
        player.transform.position = board.transform.position + board.transform.forward * 2f;
        return $"OK  pos={player.transform.position}";
    }

    public static string GotoBench()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        var bench  = AssemblyBench.Instance;
        if (player == null || bench == null) return "FAIL";
        player.transform.position = bench.transform.position + Vector3.forward * 1.5f;
        return $"OK  pos={player.transform.position}";
    }

    public static string GetStatus()
    {
        var gm  = GameManager.Instance;
        return $"Step={GetStep()}  " +
               $"HUD={HUDController.Instance != null}  " +
               $"Order={gm?.ActiveOrder?.name ?? "none"}  " +
               $"Carrying={BoxPhysicsInteractable.GrabbedBox != null}";
    }

    static string GetStep()
    {
        var tm = TutorialManager.Instance
              ?? Object.FindFirstObjectByType<TutorialManager>();
        return typeof(TutorialManager)
            .GetField("_step", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(tm)?.ToString() ?? "null";
    }
}
