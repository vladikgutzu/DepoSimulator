using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// The gate's own power box (powerBox_01, on the wall panel beside the doorway), waiting for someone
/// to fix a forced gate.
///
/// The gate a raid comes through does not tidy itself up afterwards. It is left open and cracked,
/// and until this is used the next raid does not have to break in at all — it walks through the
/// hole and gets straight to work, with none of the twenty seconds of hammering that were the
/// team's warning. Repairing it is what buys that warning back.
///
/// A box on the wall rather than the shutter itself for a plain physical reason: a forced gate is
/// standing *open*, which on this rig means the whole assembly and every collider on it has
/// travelled about three metres into the air, and PlayerInteraction only reaches two. Every gate
/// wall already carries its own powerBox_01 beside the opening, so the job goes on the thing a
/// player would actually walk up to, instead of on an invisible panel with a marker floating over it.
///
/// Built on the same bones as PowerBoxRepairTarget: hold E, the part-box glow so it can be found
/// from across the floor, and in a session the repair goes through the server so every client
/// shuts the same shutter through the same code path.
[DisallowMultipleComponent]
public class GateRepairTarget : MonoBehaviour, IHoldInteractable, ICanInteractCheck
{
    /// Longer than a power box's five seconds. A shutter that has had a crowd through it is a
    /// bigger job than a tripped breaker, and the hold is time spent standing still in a warehouse
    /// that may still have raiders in it.
    public const float RepairHoldDuration = 8f;

    const string PanelName = "powerBox_01";

    static readonly List<GateRepairTarget> _live = new();

    public static GateRepairTarget FindByIndex(int gateIndex)
    {
        var gate = GateAnimator.ByIndex(gateIndex);
        if (gate == null) return null;

        foreach (var t in _live) if (t != null && t._gate == gate) return t;
        return null;
    }

    /// Turns the power box beside a gate that has just been forced into its repair point, or
    /// leaves an existing one alone.
    public static GateRepairTarget Fit(GateAnimator gate)
    {
        if (gate == null) return null;

        var existing = FindByIndex(gate.MarkedIndex);
        if (existing != null) return existing;

        var panel = FindPanel(gate);
        if (panel == null)
        {
            Debug.LogWarning($"[GateRepairTarget] No {PanelName} on the wall of '{gate.name}' — " +
                             "this gate cannot be repaired.");
            return null;
        }

        var target = panel.gameObject.AddComponent<GateRepairTarget>();
        target._gate = gate;

        // The wall boxes carry no collider of their own, and PlayerInteraction finds things by
        // raycast. One sized to the mesh is added for as long as the job exists, and taken away
        // with it, so a repaired box goes back to being scenery.
        if (panel.GetComponent<Collider>() == null)
            target._addedCollider = panel.gameObject.AddComponent<BoxCollider>();

        target._glow = RepairGlow.On(panel.gameObject);
        return target;
    }

    /// The gate's own powerBox_01: the nearest one inside the smallest part of the hierarchy around
    /// the gate that has any. Walking up rather than searching the whole scene keeps it to this
    /// gate's wall — every gate wall carries one, and the scene root holds all of them.
    ///
    /// Never the gate group itself — that group is what rises when the shutter opens, and a box
    /// that goes up with it is a box nobody can reach exactly when it is needed.
    static Transform FindPanel(GateAnimator gate)
    {
        Vector3 from = gate.FloorAnchor != null ? gate.FloorAnchor.position : gate.transform.position;

        for (var p = gate.transform.parent; p != null; p = p.parent)
        {
            var nearest = p.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name == PanelName && !t.IsChildOf(gate.transform))
                .OrderBy(t => Vector3.Distance(t.position, from))
                .FirstOrDefault();
            if (nearest != null) return nearest;
        }
        return null;
    }

    GateAnimator _gate;
    RepairGlow   _glow;
    BoxCollider  _addedCollider;
    bool         _done;

    public bool IsRepaired => _done;

    void OnEnable()  => _live.Add(this);
    void OnDisable() => _live.Remove(this);

    void OnDestroy()
    {
        _glow?.Off();
        if (_addedCollider != null) Destroy(_addedCollider);
    }

    public bool  RequiresHold => !_done;
    public float HoldDuration => RepairHoldDuration;

    public string InteractPrompt
    {
        get
        {
            if (_done) return "Gate already repaired";
            if (!ToolkitItem.IsHeld) return "Find the toolkit first";
            return $"{InputHints.EK} Hold to repair the gate ({RepairHoldDuration:0}s)";
        }
    }

    public bool CanInteract(GameObject interactor) => !_done && ToolkitItem.IsHeld;

    public void Interact(GameObject interactor)
    {
        if (_done) return;

        if (Unity.Netcode.NetworkManager.Singleton?.IsListening == true)
        {
            // Not applied here — the server's echo repairs it on every client, this one included,
            // so nobody ends up looking at a shutter their team-mates have already shut.
            CoopGameManager.Instance?.RepairGateServerRpc(_gate != null ? _gate.MarkedIndex : -1);
            return;
        }

        ForceRepair();
    }

    /// Applied directly in solo, and on every client through CoopGameManager's echo in a session.
    public void ForceRepair()
    {
        if (_done) return;
        _done = true;

        _gate?.Repair();

        HUDController.Instance?.AddNotification(
            "Gate repaired",
            "The shutter is down again. Whatever comes next has to break it first.",
            HUDController.NotifType.Success);

        // Only the job goes — the box is part of the wall. OnDestroy puts its material back and
        // takes away the collider that was added for the repair.
        Destroy(this);
    }
}
