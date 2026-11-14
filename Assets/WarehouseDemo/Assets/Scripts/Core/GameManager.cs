using System.Collections;
using System.Linq;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [SerializeField] private OrderDefinition[] _availableOrders;

    public OrderDefinition ActiveOrder       { get; private set; }
    public int             Score             { get; private set; }
    public int             CompletedOrders   { get; private set; }
    public int             ComboCount        { get; private set; }
    public float           SessionStartTime  { get; private set; }
    public bool            GameStarted       { get; private set; }
    public float           OrderTimeRemaining { get; private set; }
    public bool            DemoComplete       { get; private set; }

    // Cumulative breakdown for the demo-complete screen
    public int TotalBaseEarned { get; private set; }
    public int TotalSpeedBonus { get; private set; }
    public int TotalComboBonus { get; private set; }
    public int TotalTeamBonus  { get; private set; }
    public int LastBaseReward  { get; private set; }
    public int LastSpeedBonus  { get; private set; }
    public int LastComboBonus  { get; private set; }
    public int LastTeamBonus   { get; private set; }
    public int LastTotalReward { get; private set; }
    public int LastLevelUp     { get; private set; }
    public string LastOrderGrade { get; private set; } = "C";
    public int FlawlessOrders  { get; private set; }
    public int BestCombo       { get; private set; }
    public bool LastExpiryWasInsured { get; private set; }

    public void AddBonusScore(int amount)
    {
        if (amount <= 0) return;
        Score += amount;
        OnScoreChanged?.Invoke(Score);
    }

    /// Attempts to deduct `amount` from Score. Returns false (no-op) if funds are insufficient.
    /// amount == 0 always succeeds (a free "purchase") without touching Score.
    public bool TrySpend(int amount)
    {
        if (amount < 0 || Score < amount) return false;
        if (amount == 0) return true;
        Score -= amount;
        OnScoreChanged?.Invoke(Score);
        return true;
    }

    float _orderStartTime;
    bool  _orderExpiring;
    readonly System.Collections.Generic.HashSet<OrderDefinition> _completedOrderDefinitions = new();

    // The player may finish the order already in progress after this point. This keeps the
    // ending logical instead of cutting away in the middle of carrying or assembling something.
    public const float DemoFinalOrderWindowSeconds = 55f * 60f;
    public float SessionElapsed => GameStarted ? Mathf.Max(0f, Time.time - SessionStartTime) : 0f;
    public bool IsFinalOrderWindow => SessionElapsed >= DemoFinalOrderWindowSeconds;

    public event System.Action<OrderDefinition> OnOrderAccepted;
    public event System.Action<OrderDefinition> OnOrderCompleted;
    public event System.Action<OrderDefinition> OnOrderExpired;
    public event System.Action<float, float>    OnTimeUpdate;    // remaining, total
    public event System.Action<int>             OnScoreChanged;
    public event System.Action                  OnDemoComplete;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;

        if (_availableOrders == null || _availableOrders.Length == 0)
        {
            // Resources.LoadAll recurses into subfolders, so a naming convention like a
            // "_disabled" folder does NOT actually exclude an order — IsEnabled is the only
            // thing that does.
            var catalog = Resources.LoadAll<OrderDefinition>("Catalog/Orders")
                .Where(o => o.IsEnabled).ToArray();
            if (catalog.Length > 0)
            {
                System.Array.Sort(catalog, (a, b) => a.Reward.CompareTo(b.Reward));
                _availableOrders = catalog;
            }
            else
            {
                var fallback = new System.Collections.Generic.List<OrderDefinition>();
                fallback.AddRange(Resources.LoadAll<OrderDefinition>("MotoOrders"));
                fallback.AddRange(Resources.LoadAll<OrderDefinition>("Orders"));
                fallback.RemoveAll(o => !o.IsEnabled);
                fallback.Sort((a, b) => a.Reward.CompareTo(b.Reward));
                _availableOrders = fallback.ToArray();
            }
        }
    }

    IEnumerator Start()
    {
        yield return null;
        if (!GameStarted && MainMenuController.Instance == null) StartGame();
    }

    void Update()
    {
        if (ActiveOrder == null || !GameStarted || _orderExpiring) return;
        float limit = ActiveOrder.TimeLimitSeconds;
        if (limit <= 0f) return;

        float elapsed = Time.time - _orderStartTime;
        OrderTimeRemaining = Mathf.Max(0f, limit - elapsed);
        OnTimeUpdate?.Invoke(OrderTimeRemaining, limit);

        if (OrderTimeRemaining <= 0f)
        {
            _orderExpiring = true;
            // In multiplayer, only the server expires orders; non-server clients wait for
            // CoopGameManager.BroadcastOrderExpiredClientRpc and call ForceExpireOrder().
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.IsServer)
                ExpireOrder();
        }
    }

    public OrderDefinition[] GetAllOrders()
        => _availableOrders ?? System.Array.Empty<OrderDefinition>();

    public OrderDefinition[] GetAvailableOrders()
    {
        if (_availableOrders == null || _availableOrders.Length == 0)
            return System.Array.Empty<OrderDefinition>();
        if (DemoComplete)
            return System.Array.Empty<OrderDefinition>();
        // Tutorial (first run): show only the first order so the player isn't overwhelmed.
        // After tutorial: show all orders for free practice.
        if (CompletedOrders == 0)
            return new[] { _availableOrders[0] };
        return _availableOrders;
    }

    public void StartGame()
    {
        GameStarted      = true;
        SessionStartTime = Time.time;
    }

    /// Wipes all per-session progress. Singleplayer gets this for free from LoadScene, but the
    /// multiplayer flow deliberately reuses the scene (see MainMenuController.StartMultiplayer),
    /// so without an explicit reset every value below survives into the next room: a stale
    /// ActiveOrder makes AcceptOrder refuse for everyone, a CompletedOrders sitting at the demo
    /// cap empties the order board permanently, and score/combo carry over.
    public void ResetSession()
    {
        ActiveOrder        = null;
        OrderTimeRemaining = 0f;
        _orderStartTime    = 0f;
        _orderExpiring     = false;

        Score           = 0;
        CompletedOrders = 0;
        ComboCount      = 0;
        TotalBaseEarned = 0;
        TotalSpeedBonus = 0;
        TotalComboBonus = 0;
        TotalTeamBonus  = 0;
        LastBaseReward  = 0;
        LastSpeedBonus  = 0;
        LastComboBonus  = 0;
        LastTeamBonus   = 0;
        LastTotalReward = 0;
        LastLevelUp     = 0;
        LastOrderGrade = "C";
        FlawlessOrders = 0;
        BestCombo      = 0;
        LastExpiryWasInsured = false;
        DemoComplete   = false;
        _completedOrderDefinitions.Clear();

        // Deliberately does NOT raise OnOrderCompleted/OnOrderExpired -- those mean "an order
        // ended during play" and carry side effects (PartSpawner teardown, staging clears,
        // player-facing notifications). Only the score readout needs a nudge so the HUD stops
        // showing the previous room's total.
        OnScoreChanged?.Invoke(Score);
    }

    public bool AcceptOrder(OrderDefinition order)
    {
        if (ActiveOrder != null) return false;
        if (DemoComplete) return false;
        ActiveOrder        = order;
        _orderStartTime    = Time.time;
        OrderTimeRemaining = order.TimeLimitSeconds;
        _orderExpiring     = false;
        OnOrderAccepted?.Invoke(order);
        return true;
    }

    public void CompleteOrder(float teamMultiplier = 1f)
    {
        if (ActiveOrder == null) return;
        var completed = ActiveOrder;

        float elapsed = Time.time - _orderStartTime;
        float limit   = completed.TimeLimitSeconds;

        int baseReward = completed.Reward;
        int speedBonus = limit > 0f && elapsed < limit * 0.5f
                         ? Mathf.RoundToInt(baseReward * 0.5f) : 0;
        ComboCount++;
        int comboBonus = ComboCount > 1
                         ? Mathf.RoundToInt(baseReward * Mathf.Min(0.5f, 0.1f * (ComboCount - 1))) : 0;

        int subtotal = baseReward + speedBonus + comboBonus;
        int teamBonus = Mathf.RoundToInt(subtotal
            * (Mathf.Clamp(teamMultiplier, 1f, 1.5f) - 1f));
        int total = subtotal + teamBonus;
        ApplyCompletedOrder(completed, baseReward, speedBonus, comboBonus, teamBonus, total,
            ComboCount, GradeOrder(elapsed, limit));
    }

    /// Applies the server's authoritative result on a non-host multiplayer client.
    /// Clients must not grade their own copy of the timer: network delay would otherwise make
    /// the same team order award different grades, money and career REP on each machine.
    public void CompleteOrderFromNetwork(int baseReward, int speedBonus, int comboBonus,
        int teamBonus, int totalReward, int comboCount, string grade)
    {
        if (ActiveOrder == null) return;
        var completed = ActiveOrder;

        baseReward  = Mathf.Max(0, baseReward);
        speedBonus = Mathf.Max(0, speedBonus);
        comboBonus = Mathf.Max(0, comboBonus);
        teamBonus = Mathf.Max(0, teamBonus);
        totalReward = Mathf.Max(0, totalReward);
        comboCount = Mathf.Max(1, comboCount);
        if (grade != "S" && grade != "A" && grade != "B" && grade != "C")
            grade = "C";

        ApplyCompletedOrder(completed, baseReward, speedBonus, comboBonus, teamBonus, totalReward,
            comboCount, grade);
    }

    void ApplyCompletedOrder(OrderDefinition completed, int baseReward, int speedBonus,
        int comboBonus, int teamBonus, int total, int comboCount, string grade)
    {
        ActiveOrder = null;
        ComboCount = comboCount;
        LastOrderGrade = grade;

        LastBaseReward  = baseReward;
        LastSpeedBonus  = speedBonus;
        LastComboBonus  = comboBonus;
        LastTeamBonus   = teamBonus;
        LastTotalReward = total;
        LastLevelUp     = CareerProgress.AwardOrder(
            total, speedBonus > 0, ComboCount, LastOrderGrade == "S");

        if (LastOrderGrade == "S") FlawlessOrders++;
        BestCombo = Mathf.Max(BestCombo, ComboCount);

        TotalBaseEarned += baseReward;
        TotalSpeedBonus += speedBonus;
        TotalComboBonus += comboBonus;
        TotalTeamBonus  += teamBonus;
        Score           += total;
        CompletedOrders++;
        _completedOrderDefinitions.Add(completed);

        OnScoreChanged?.Invoke(Score);
        OnOrderCompleted?.Invoke(completed);

        bool completedWholeCatalog = _availableOrders != null
                                  && _availableOrders.Length > 0
                                  && _completedOrderDefinitions.Count >= _availableOrders.Length;
        if (IsFinalOrderWindow || completedWholeCatalog)
        {
            DemoComplete = true;
            OnDemoComplete?.Invoke();
        }
    }

    static string GradeOrder(float elapsed, float limit)
    {
        if (limit <= 0f) return "A";
        float used = Mathf.Clamp01(elapsed / limit);
        if (used <= 0.35f) return "S";
        if (used <= 0.50f) return "A";
        if (used <= 0.75f) return "B";
        return "C";
    }

    /// Called by CoopGameManager.BroadcastOrderExpiredClientRpc on non-server clients.
    public void ForceExpireOrder(bool preserveCombo = false) => ExpireOrder(preserveCombo, false);

    void ExpireOrder(bool preserveCombo = false, bool consumeInsurance = true)
    {
        bool insured = preserveCombo;
        if (consumeInsurance && !insured)
            insured = NetworkShopManager.Instance?.TryConsumeOrderInsurance() == true;

        var expired        = ActiveOrder;
        ActiveOrder        = null;
        if (!insured) ComboCount = 0;
        LastExpiryWasInsured = insured;
        OrderTimeRemaining = 0f;
        _orderExpiring     = false;
        OnOrderExpired?.Invoke(expired);
    }
}
