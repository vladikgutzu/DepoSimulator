using UnityEngine;

/// Patching yourself up: the hold that finishes a repair once you are carrying everything it needs.
///
/// This used to be the whole repair — hold E facing a wall, anywhere in the building, and any
/// injury was gone. Now it is the last step of an errand rather than the errand itself, and what it
/// will mend depends on what the player has actually gone and collected:
///
///   • a **dent**, or any injury with no separable limb to lose such as a back — the toolkit alone;
///   • the **limb in his hand** — the arm or leg he found on the floor and carried here.
///
/// A severed limb still lying on the warehouse floor is deliberately not on that list. Without the
/// split, holding E facing a wall would quietly mend a torn-off arm and the arm on the floor would
/// be scenery.
///
/// The carried limb takes precedence over a dent, because it is the thing he made a trip for.
///
/// It is an IHoldInteractable like a power box or a gate, which is deliberate: it means the hold,
/// the progress bar, the toolkit check and the prompt all come from machinery that already exists
/// and already behaves the way players expect.
///
/// What is unusual is that there is nothing to look at. PlayerInteraction finds its target by
/// raycasting, and you cannot aim at your own arm — so this is offered as the target when the ray
/// finds nothing else and there is something to fix. Looking at anything at all takes priority,
/// which is what stops a repair starting every time somebody holds E facing a wall.
[DisallowMultipleComponent]
public class SelfRepairTarget : MonoBehaviour, IHoldInteractable, ICanInteractCheck, INoOutlineHighlight
{
    /// Long enough to be a decision. Standing still for this in a warehouse with raiders in it is
    /// the cost of the repair — the toolkit is not the expensive part, the eight seconds are.
    public const float RepairHoldDuration = 6f;

    public static SelfRepairTarget Instance { get; private set; }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    /// True when holding E should start patching rather than doing nothing.
    ///
    /// Both halves matter. Without an injury there is nothing to do, and without the toolkit there
    /// is no way to do it — and the prompt says which of the two is missing rather than staying
    /// silent, because a player who cannot see why nothing is happening assumes it is broken.
    public static bool Available => Instance != null && Pending != BodyPart.None;

    /// What a hold would put right, or None if there is nothing this can do.
    ///
    /// The carried limb first: he walked here holding it, and mending a bruise instead would be the
    /// repair ignoring the entire trip. Only then the worst thing a toolkit alone can fix.
    static BodyPart Pending
    {
        get
        {
            var injuries = InjurySystem.Instance;
            if (injuries == null) return BodyPart.None;

            // Whosever limb it is. A left arm fits any robot missing a left arm — see
            // SeveredLimbItem — so what decides is the part, not the name on it.
            var carried = SeveredLimbItem.Carried;
            if (carried != null && injuries.IsSevered(carried.Part)) return carried.Part;

            return injuries.HasOnTheSpotInjury ? injuries.WorstOnTheSpot.part : BodyPart.None;
        }
    }

    public bool  RequiresHold => true;
    public float HoldDuration => RepairHoldDuration;

    public string InteractPrompt
    {
        get
        {
            var pending = Pending;
            if (pending == BodyPart.None) return string.Empty;

            string part = BotDamageVisual.Label(pending);

            if (!ToolkitItem.IsHeld) return $"Your {part} needs the toolkit";

            var carried = SeveredLimbItem.Carried;
            bool refit  = carried != null && carried.Part == pending;

            if (!refit) return $"{InputHints.EK} Hold to repair your {part} ({RepairHoldDuration:0}s)";

            // Named when it is not his, because the arm he is about to bolt on is a different
            // colour and he should know that before the six seconds rather than after.
            return carried.IsMine
                ? $"{InputHints.EK} Hold to refit your {part} ({RepairHoldDuration:0}s)"
                : $"{InputHints.EK} Hold to fit {carried.OwnerName} {part} ({RepairHoldDuration:0}s)";
        }
    }

    public bool CanInteract(GameObject interactor) => ToolkitItem.IsHeld && Pending != BodyPart.None;

    public void Interact(GameObject interactor)
    {
        var injuries = InjurySystem.Instance;
        if (injuries == null) return;

        var part = Pending;
        if (part == BodyPart.None) return;

        // The limb he carried here, so the repair consumes that one and takes its livery, rather
        // than hunting down whichever copy of the part happens to be lying nearest.
        var carried = SeveredLimbItem.Carried;
        var used    = carried != null && carried.Part == part ? carried : null;

        var net = NetworkPlayer.Local;
        if (net != null && net.IsSpawned &&
            Unity.Netcode.NetworkManager.Singleton?.IsListening == true)
        {
            net.RequestRepairServerRpc(
                (int)part,
                used != null,
                used != null ? used.OwnerId : 0UL,
                used != null && used.IsOrphan,
                used != null ? used.NetworkVersion : 0u);
            return;
        }

        if (!injuries.Repair(part, used)) return;

        AudioManager.Instance?.PlayPing();
    }
}
