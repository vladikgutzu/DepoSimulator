using UnityEngine;

/// Persistent, local account progression shared by solo and multiplayer sessions.
/// Gameplay money remains shift-local; reputation survives scene loads and restarts.
public static class CareerProgress
{
    const string XpKey     = "Career.ReputationXp";
    const string OrdersKey = "Career.CompletedOrders";
    const string FlawlessKey = "Career.FlawlessOrders";
    const string BestStreakKey = "Career.BestStreak";

    public static int ReputationXp   => Mathf.Max(0, PlayerPrefs.GetInt(XpKey, 0));
    public static int CompletedOrders => Mathf.Max(0, PlayerPrefs.GetInt(OrdersKey, 0));
    public static int FlawlessOrders => Mathf.Max(0, PlayerPrefs.GetInt(FlawlessKey, 0));
    public static int BestStreak     => Mathf.Max(0, PlayerPrefs.GetInt(BestStreakKey, 0));
    public static int Level           => LevelFromXp(ReputationXp);
    public static string RankTitle    => RankTitleAtLevel(Level);
    public static int LastXpAward     { get; private set; }

    public static int XpAtLevel(int level)
    {
        level = Mathf.Max(1, level);
        return 125 * (level - 1) * level / 2;
    }

    public static float LevelProgress
    {
        get
        {
            int level = Level;
            int start = XpAtLevel(level);
            int end   = XpAtLevel(level + 1);
            return Mathf.InverseLerp(start, end, ReputationXp);
        }
    }

    public static int AwardOrder(int totalReward, bool fast, int combo, bool flawless = false)
    {
        int oldLevel = Level;
        int award = 20 + Mathf.Clamp(totalReward / 10, 5, 50);
        if (fast) award += 20;
        if (flawless) award += 15;
        if (combo > 1) award += Mathf.Min(30, (combo - 1) * 5);

        PlayerPrefs.SetInt(XpKey, ReputationXp + award);
        PlayerPrefs.SetInt(OrdersKey, CompletedOrders + 1);
        if (flawless)
            PlayerPrefs.SetInt(FlawlessKey, FlawlessOrders + 1);
        if (combo > BestStreak)
            PlayerPrefs.SetInt(BestStreakKey, combo);
        PlayerPrefs.Save();

        LastXpAward = award;
        return Level > oldLevel ? Level : 0;
    }

    static int LevelFromXp(int xp)
    {
        int level = 1;
        while (xp >= XpAtLevel(level + 1)) level++;
        return level;
    }

    public static string RankTitleAtLevel(int level)
    {
        if (level >= 20) return "Automation Director";
        if (level >= 16) return "Logistics Master";
        if (level >= 12) return "Operations Lead";
        if (level >= 8)  return "Floor Supervisor";
        if (level >= 5)  return "Shift Specialist";
        if (level >= 3)  return "Warehouse Hand";
        return "Trainee";
    }
}
