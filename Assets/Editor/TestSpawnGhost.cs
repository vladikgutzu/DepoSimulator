using UnityEngine;
using System.Reflection;

public class TestSpawnGhost
{
    public static void Execute()
    {
        var gm = Object.FindAnyObjectByType<GameManager>(FindObjectsInactive.Include);
        if (gm == null) { Debug.LogError("[Test] No GameManager in scene"); return; }

        Debug.Log($"[Test] Found GameManager on '{gm.gameObject.name}', active={gm.gameObject.activeInHierarchy}, GameStarted={gm.GameStarted}");

        if (!gm.gameObject.activeInHierarchy)
            gm.gameObject.SetActive(true);

        if (!gm.GameStarted)
        {
            typeof(GameManager)
                .GetMethod("StartGame", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(gm, null);
            Debug.Log("[Test] StartGame() called");
        }

        var ordersField = typeof(GameManager).GetField("_availableOrders", BindingFlags.NonPublic | BindingFlags.Instance);
        var orders = ordersField?.GetValue(gm) as OrderDefinition[];
        Debug.Log($"[Test] _availableOrders count: {orders?.Length ?? 0}");

        OrderDefinition order = null;
        if (orders != null && orders.Length > 0) order = orders[0];
        if (order == null) order = Resources.Load<OrderDefinition>("MotoOrders/order_moto_full");
        if (order == null) order = Resources.Load<OrderDefinition>("MotoOrders/order_moto_drive");
        if (order == null)
        {
            var all = Resources.LoadAll<OrderDefinition>("MotoOrders");
            if (all.Length > 0) order = all[0];
        }

        if (order == null) { Debug.LogError("[Test] No OrderDefinition found in Resources/MotoOrders/"); return; }

        Debug.Log($"[Test] Accepting order: {order.name}");
        bool accepted = gm.AcceptOrder(order);
        Debug.Log($"[Test] AcceptOrder returned: {accepted}  (false = already has active order)");

        // Use Instance (finds active bench set by Awake), not FindAnyObjectByType which may return the inactive duplicate
        var bench = AssemblyBench.Instance;
        if (bench == null)
        {
            // fallback: find any active bench
            bench = Object.FindAnyObjectByType<AssemblyBench>();
        }
        if (bench == null) { Debug.LogError("[Test] AssemblyBench not found (no active instance)"); return; }

        Debug.Log($"[Test] AssemblyBench '{bench.gameObject.name}', active={bench.gameObject.activeInHierarchy}");

        var fGhost   = typeof(AssemblyBench).GetField("_ghostInstance",  BindingFlags.NonPublic | BindingFlags.Instance);
        var fPalletY = typeof(AssemblyBench).GetField("_palletTopY",     BindingFlags.NonPublic | BindingFlags.Instance);
        var fPallet  = typeof(AssemblyBench).GetField("_assemblyPallet", BindingFlags.NonPublic | BindingFlags.Instance);

        var ghost    = fGhost?.GetValue(bench)  as GameObject;
        var palletTr = fPallet?.GetValue(bench) as Transform;
        float palletY = fPalletY != null ? (float)fPalletY.GetValue(bench) : -999f;

        string palletName = (palletTr != null) ? palletTr.name : "NULL";
        Debug.Log($"[Test] _assemblyPallet = {palletName}");
        Debug.Log($"[Test] _palletTopY = {palletY}");

        if (ghost == null)
            Debug.LogWarning("[Test] _ghostInstance is null — BuildGhost() not fired or prefab missing from Resources/AssemblyReferences/");
        else
        {
            string ghostPos = (ghost != null) ? ghost.transform.position.ToString() : "N/A";
            Debug.Log($"[Test] Ghost '{ghost.name}' at world pos {ghostPos}");
        }

        var fOrder = typeof(AssemblyBench).GetField("_activeOrder", BindingFlags.NonPublic | BindingFlags.Instance);
        var benchOrder = fOrder?.GetValue(bench) as OrderDefinition;
        Debug.Log($"[Test] AssemblyBench._activeOrder = {(benchOrder != null ? benchOrder.name : "null")}");
    }
}
