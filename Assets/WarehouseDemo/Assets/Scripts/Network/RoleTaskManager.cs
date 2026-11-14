using System.Collections.Generic;
using UnityEngine;

/// Local (non-networked) source of per-role bonuses and team-composition hints.
/// One instance per client — reads from NetworkPlayer.Local and CoopGameManager.
public class RoleTaskManager : MonoBehaviour
{
    public static RoleTaskManager Instance { get; private set; }
    public const float MissingPickerFallbackRange = 8f;

    // ── Bonus values per role ─────────────────────────────────────────────────
    static readonly Dictionary<PlayerRoleType, RoleBonus> Bonuses = new()
    {
        { PlayerRoleType.Picker,           new(speedMult: 1.25f, highlightRange: 20f) },
        { PlayerRoleType.Assembler,        new(speedMult: 1.00f, highlightRange: 0f)  },
        { PlayerRoleType.ForkLiftOperator, new(speedMult: 1.10f, highlightRange: 0f)  },
        { PlayerRoleType.Dispatcher,       new(speedMult: 0.90f, highlightRange: 0f)  },
        { PlayerRoleType.None,             new(speedMult: 1.00f, highlightRange: 0f)  },
    };

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    // ── Role bonuses ──────────────────────────────────────────────────────────

    /// Single source of truth for per-role bonuses — read by PlayerSpeedOverride,
    /// and PartHighlighter instead of each keeping its own (unwired) copy.
    public static RoleBonus GetBonus(PlayerRoleType role) =>
        Bonuses.TryGetValue(role, out var bonus) ? bonus : Bonuses[PlayerRoleType.None];

    /// Picker keeps the full warehouse-scale scan. If an incomplete team has no Picker at all,
    /// everyone gets a deliberately short emergency scan so the order remains readable without
    /// erasing the specialist's advantage whenever one is present.
    public static float GetPartHighlightRange(PlayerRoleType role)
    {
        if (role == PlayerRoleType.Picker)
            return GetBonus(role).HighlightRange;

        return TeamHasRole(PlayerRoleType.Picker) ? 0f : MissingPickerFallbackRange;
    }

    // ── Graceful degradation ──────────────────────────────────────────────────

    /// True if any player on the team has this role.
    public static bool TeamHasRole(PlayerRoleType role)
    {
        if (NetworkPlayer.All == null) return false;
        foreach (var p in NetworkPlayer.All)
            if (p.RoleType == role) return true;
        return false;
    }

    /// Returns a human-readable status of the team composition.
    public static string GetTeamCompositionHint()
    {
        int players = NetworkPlayer.All?.Count ?? 0;
        if (players <= 1) return "Solo mode";

        bool hasPicker    = TeamHasRole(PlayerRoleType.Picker);
        bool hasAssembler = TeamHasRole(PlayerRoleType.Assembler);
        bool hasDispatch  = TeamHasRole(PlayerRoleType.Dispatcher);
        bool hasFork      = TeamHasRole(PlayerRoleType.ForkLiftOperator);

        var fallbacks = new List<string>();
        if (!hasPicker)    fallbacks.Add("short-range team scan");
        if (!hasAssembler) fallbacks.Add("shared instant bench");

        if (fallbacks.Count > 0)
            return $"Coverage fallback: {string.Join(", ", fallbacks)}";

        string roles = "";
        if (hasPicker)    roles += "Picker ";
        if (hasAssembler) roles += "Assembler ";
        if (hasDispatch)  roles += "Dispatcher ";
        if (hasFork)      roles += "Forklift ";
        return roles.TrimEnd();
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    public readonly struct RoleBonus
    {
        public readonly float SpeedMultiplier;
        public readonly float HighlightRange;

        public RoleBonus(float speedMult, float highlightRange)
        {
            SpeedMultiplier = speedMult;
            HighlightRange  = highlightRange;
        }
    }
}
