using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// Manages player death (crushed by shelf) and respawn logic.
/// Solo: auto-respawn after 60s with score penalty.
/// Multiplayer: teammate rescue (3s hold) or forklift rescue; auto-respawn after 90s with penalty.
/// Add this component to the player root. Supports both the legacy first-person controller and
/// the current Starter Assets third-person controller.
public class PlayerDeathManager : MonoBehaviour
{
    public static PlayerDeathManager Instance { get; private set; }

    public bool IsDead { get; private set; }
    public Transform PlayerTransform => transform;

    const float AutoRespawnTimeSolo = 60f;
    const float ScorePenaltyPct     = 0.30f;   // 30% solo / 50% MP
    const float ScorePenaltyPctMP   = 0.50f;

    Vector3    _spawnPoint;
    Coroutine  _respawnTimer;

    FirstPersonController _fpc;
    StarterAssets.ThirdPersonController _tpc;
    PlayerInteraction     _interaction;

    void Awake()
    {
        var np = GetComponent<NetworkPlayer>();
        if (np == null || Unity.Netcode.NetworkManager.Singleton?.IsListening != true)
            Instance = this;
        _fpc         = GetComponent<FirstPersonController>();
        _tpc         = GetComponent<StarterAssets.ThirdPersonController>();
        _interaction = GetComponent<PlayerInteraction>();
        if (_fpc == null && _tpc == null)
            Debug.LogError("[PlayerDeathManager] No supported movement controller found on " + name);
        _spawnPoint  = transform.position;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public static void BindLocal(PlayerDeathManager manager)
    {
        if (manager != null) Instance = manager;
    }

    public static void UnbindLocal(PlayerDeathManager manager)
    {
        if (Instance == manager) Instance = null;
    }

    // ── Death ─────────────────────────────────────────────────────────────────

    public void Die(Vector3 crushPosition)
    {
        if (IsDead) return;

        bool isMP = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;
        if (isMP)
        {
            // Prediction is deliberately not used for death: controls and the rescue marker change
            // only after the server accepts the crush and sends back the canonical version.
            NetworkPlayer.Local?.NotifyDeathServerRpc(crushPosition);
            return;
        }

        EnterDeath(crushPosition);
        _respawnTimer = StartCoroutine(AutoRespawnCountdown(AutoRespawnTimeSolo, false));
    }

    public void ApplyNetworkDeath(Vector3 crushPosition, double respawnDeadline)
    {
        if (IsDead) return;
        EnterDeath(crushPosition);
        _respawnTimer = StartCoroutine(NetworkRespawnCountdown(respawnDeadline));
    }

    void EnterDeath(Vector3 crushPosition)
    {
        IsDead = true;

        // Disable controls
        SetMovementEnabled(false);
        if (_interaction != null) _interaction.enabled = false;

        // Drop any carried box
        BoxPhysicsInteractable.ReleaseGrabbed();

        // Show death UI
        HUDController.Instance?.ShowDeathScreen();
        HUDController.Instance?.AddNotification(
            "YOU DIED",
            "A shelf crushed you!",
            HUDController.NotifType.Danger);

        // Spawn body marker so teammates can rescue
        CrushedPlayerMarker.Spawn(crushPosition, this);

    }

    IEnumerator AutoRespawnCountdown(float total, bool isMP)
    {
        float remaining = total;
        while (remaining > 0f)
        {
            HUDController.Instance?.UpdateDeathTimer(remaining);
            yield return new WaitForSeconds(1f);
            remaining -= 1f;
        }

        float penaltyPct = isMP ? ScorePenaltyPctMP : ScorePenaltyPct;
        ApplyScorePenalty(penaltyPct);

        HUDController.Instance?.AddNotification(
            "Auto-respawn",
            isMP ? "−50% points" : "−30% points",
            HUDController.NotifType.Warning);

        Respawn(_spawnPoint);
    }

    IEnumerator NetworkRespawnCountdown(double deadline)
    {
        while (IsDead)
        {
            var np = NetworkPlayer.Local;
            var nm = Unity.Netcode.NetworkManager.Singleton;
            double now = nm != null && nm.IsListening ? nm.ServerTime.Time : 0d;
            float remaining = Mathf.Max(0f, (float)(deadline - now));
            HUDController.Instance?.UpdateDeathTimer(remaining);
            yield return null;
        }
    }

    // ── Rescue ────────────────────────────────────────────────────────────────

    /// Called by CrushedPlayerMarker when a teammate completes the rescue hold.
    public void RescueByTeammate(Vector3 rescuerPosition)
    {
        if (!IsDead) return;

        HUDController.Instance?.AddNotification(
            "Rescued!",
            "A teammate pulled you out from under the shelf",
            HUDController.NotifType.Success);

        // Respawn next to the rescuer
        Vector3 respawnPos = rescuerPosition + Random.insideUnitSphere * 1.2f;
        respawnPos.y = rescuerPosition.y;
        Respawn(respawnPos);
    }

    /// Called by ReachTruckController when forklift lifts a shelf near the dead player.
    public void RescueByForklift(Vector3 nearPosition)
    {
        if (!IsDead) return;

        HUDController.Instance?.AddNotification(
            "Rescued by forklift!",
            "The operator lifted the shelf",
            HUDController.NotifType.Success);

        Respawn(nearPosition + Vector3.right * 1.5f);
    }

    public void ApplyNetworkRespawn(Vector3 position, bool viaForklift, bool autoRespawn)
    {
        if (!IsDead) return;

        if (autoRespawn)
            HUDController.Instance?.AddNotification(
                "Auto-respawn", "−50% points", HUDController.NotifType.Warning);
        else if (viaForklift)
            HUDController.Instance?.AddNotification(
                "Rescued by forklift!", "The operator lifted the shelf",
                HUDController.NotifType.Success);
        else
            HUDController.Instance?.AddNotification(
                "Rescued!", "A teammate pulled you out from under the shelf",
                HUDController.NotifType.Success);

        Respawn(position);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    void Respawn(Vector3 position)
    {
        if (_respawnTimer != null) StopCoroutine(_respawnTimer);
        _respawnTimer = null;

        IsDead = false;

        HUDController.Instance?.HideDeathScreen();

        // Teleport
        var cc = GetComponent<UnityEngine.CharacterController>();
        if (cc != null)
        {
            cc.enabled = false;
            transform.position = position;
            cc.enabled = true;
        }
        else
        {
            transform.position = position;
        }

        // Re-enable controls
        SetMovementEnabled(true);
        if (_interaction != null) _interaction.enabled = true;

        HUDController.Instance?.HidePrompt();
        CrushedPlayerMarker.DestroyMarker();
    }

    void SetMovementEnabled(bool enabled)
    {
        _fpc?.SetMovementEnabled(enabled);
        if (_tpc == null) return;

        _tpc.MovementLocked = !enabled;
        if (enabled) _tpc.ResetLocomotionSpeed();
    }

    void ApplyScorePenalty(float pct) => ApplySharedScorePenalty(pct);

    public static void ApplySharedScorePenalty(float pct)
    {
        var gm = GameManager.Instance;
        if (gm == null) return;
        int penalty = Mathf.RoundToInt(gm.Score * Mathf.Clamp01(pct));
        if (penalty > 0) gm.TrySpend(penalty);
    }
}
