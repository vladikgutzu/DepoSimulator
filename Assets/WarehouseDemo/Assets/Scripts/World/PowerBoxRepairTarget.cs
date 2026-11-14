using System.Collections.Generic;
using UnityEngine;

/// Added at runtime to one of the randomly chosen power boxes (powerBox_06 / powerBox_07 — see
/// PowerBoxRegistry) while a PowerFailureEvent or FireAlarmEvent is active. Every one of them in
/// the scene has its own (non-trigger) BoxCollider, added manually in the editor. Player holds [E] for
/// RepairHoldDuration seconds to fix THIS box — Configure() sets whether the toolkit is
/// required first (PowerFailureEvent) or not (FireAlarmEvent), and this box's deterministic
/// index into PowerBoxRegistry.GetAll() (identical ordering on every client — same idea as
/// PhysicsShelf.NetId). OnRepaired notifies whichever event owns this box; the box removes
/// itself immediately once fixed.
///
/// Multiplayer: repairing is NOT applied locally. Interact() asks the server via
/// CoopGameManager.RepairPowerBoxServerRpc, which echoes back to every client (including the
/// one who interacted) so all clients call ForceRepair() on the SAME box and their own
/// PowerFailureEvent/FireAlarmEvent instance's repaired-count advances in lockstep — without
/// this, each client's counter only ever saw its own local repairs and never reached the
/// target, so cooperative repair silently required one player to fix every box alone.
[RequireComponent(typeof(Collider))]
public class PowerBoxRepairTarget : MonoBehaviour, IHoldInteractable, ICanInteractCheck
{
    public const float RepairHoldDuration = 5f;

    static readonly Dictionary<int, PowerBoxRepairTarget> _registry = new();
    public static PowerBoxRepairTarget FindByIndex(int boxIndex) =>
        _registry.TryGetValue(boxIndex, out var t) ? t : null;

    /// Called once, right after AddComponent, by the owning event.
    public System.Action<PowerBoxRepairTarget> OnRepaired;

    bool _done;
    bool _requireToolkit = true;
    int  _boxIndex = -1;
    // Glows like a box with a needed part in it — same shader, same gold — so a job on the wall
    // reads as the same kind of thing as a box to fetch, from across the room and in the dark of a
    // power failure, where emission is the one thing that still shows.
    RepairGlow _glow;

    public void Configure(bool requireToolkit, int boxIndex)
    {
        _requireToolkit = requireToolkit;
        _boxIndex       = boxIndex;
        _registry[_boxIndex] = this;
        _glow           = RepairGlow.On(gameObject);
    }

    void OnDestroy()
    {
        if (_boxIndex >= 0 && _registry.TryGetValue(_boxIndex, out var t) && t == this)
            _registry.Remove(_boxIndex);
        // Covers both repair paths: ForceRepair()'s Destroy(this) and
        // PowerFailureEvent/FireAlarmEvent.Remove()'s direct Object.Destroy(target).
        _glow?.Off();
    }

    public bool  RequiresHold => !_done;
    public bool  IsRepaired => _done;
    public bool  RequiresToolkit => _requireToolkit;
    public float HoldDuration => RepairHoldDuration;

    public string InteractPrompt
    {
        get
        {
            if (_done) return "Power box already repaired";
            if (_requireToolkit && !ToolkitItem.IsHeld) return "Find the toolkit first";
            return $"{InputHints.EK} Hold to repair power box ({RepairHoldDuration:0}s)";
        }
    }

    public bool CanInteract(GameObject interactor) => !_done && (!_requireToolkit || ToolkitItem.IsHeld);

    public void Interact(GameObject interactor)
    {
        if (_done) return;

        if (Unity.Netcode.NetworkManager.Singleton?.IsListening == true)
        {
            // Don't apply locally — wait for the server's broadcast so every client
            // (including this one) repairs the same box through the same code path.
            CoopGameManager.Instance?.RepairPowerBoxServerRpc(_boxIndex);
            return;
        }

        ForceRepair();
    }

    /// Applied directly in solo. In multiplayer, applied on every client via
    /// CoopGameManager's ClientRpc echo — never called straight from Interact() there.
    public void ForceRepair()
    {
        if (_done) return;
        _done = true;
        OnRepaired?.Invoke(this);
        // Self-remove immediately so this box's minimap marker/outline disappear as soon
        // as THIS box is fixed, instead of waiting for every targeted box to be done.
        Destroy(this);
    }
}
