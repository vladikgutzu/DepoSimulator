using UnityEngine;

/// Central config for off-specialization penalties.
/// All values are multipliers or absolute values used by activity controllers.
public static class SpecializationPenalties
{
    // ── Forklift — non-ForkLiftOperator ──────────────────────────────────────
    public static class Forklift
    {
        /// Turn speed multiplier for a non-operator (1.0 = normal, 0.4 = sluggish).
        public const float TurnSpeedMult  = 0.40f;
        /// Drive speed multiplier for a non-operator.
        public const float DriveSpeedMult = 0.70f;
        /// How quickly the truck coasts to a stop when releasing the key (lower = more sliding).
        /// Expert: instant stop (set velocity = 0). Novice: lerp at this rate.
        public const float BrakeLerpSpeed = 3.5f;
        /// Perlin-noise lateral drift force (N) applied while driving.
        public const float DriftForce     = 1.8f;
        /// Perlin time scale for the drift (higher = more erratic).
        public const float DriftFrequency = 0.5f;
    }

    // ── Assembly bench — non-Assembler ────────────────────────────────────────
    public static class Bench
    {
        /// Seconds a non-Assembler must work before installing an order or vehicle part.
        public const float PlacementDelay = 3.5f;
        /// Packing is the more involved off-specialty action.
        public const float PackingDelay = 5f;
    }

    // ── Part highlight range for non-Pickers ──────────────────────────────────
    public static class Picking
    {
        /// Non-Pickers have zero highlight range (parts don't glow).
        public const float HighlightRange = 0f;
    }

    // ── Solo/Multiplayer gate ─────────────────────────────────────────────────
    /// True when no multiplayer session is active, OR when a session is active but only one
    /// player is actually connected (teammates disconnected and left this player alone). Role
    /// penalties are disabled in both cases: a lone player can do everything without
    /// restrictions instead of being stuck with a single specialty's bonuses and every other
    /// task's off-specialty penalty just because the Netcode connection itself is still open.
    /// NetworkPlayer.All is live (updated via OnRosterChanged), so this re-evaluates correctly
    /// as players join/leave without any caching here.
    public static bool SoloMode =>
        Unity.Netcode.NetworkManager.Singleton == null
        || !Unity.Netcode.NetworkManager.Singleton.IsListening
        || NetworkPlayer.All.Count <= 1;

    // ── Helper: resolves the local player's role type ─────────────────────────
    public static PlayerRoleType GetLocalRole()
    {
        if (SoloMode) return PlayerRoleType.None;

        // RoleController covers both solo and multiplayer (set via LobbyController).
        var role = RoleController.Instance?.CurrentRole?.RoleType;
        if (role.HasValue && role.Value != PlayerRoleType.None)
            return role.Value;

        // Fallback to NetworkPlayer in online sessions.
        return NetworkPlayer.Local?.RoleType ?? PlayerRoleType.None;
    }

    public static bool IsOperator(PlayerRoleType role) =>
        role == PlayerRoleType.ForkLiftOperator;

    public static bool IsPicker(PlayerRoleType role) =>
        role == PlayerRoleType.Picker;

    public static bool IsAssembler(PlayerRoleType role) =>
        role == PlayerRoleType.Assembler;

    /// Forklift driving is exclusive when the team has an assigned operator. Solo sessions and
    /// incomplete teams without one keep access so an order can never become impossible.
    public static bool CanOperateForklift(PlayerRoleType role)
    {
        if (SoloMode || IsOperator(role)) return true;

        foreach (var player in NetworkPlayer.All)
            if (player != null && IsOperator(player.RoleType)) return false;

        return true;
    }
}
