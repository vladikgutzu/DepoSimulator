using UnityEngine;

/// One answer to "are his hands and his attention already taken?".
///
/// Swinging a weapon and picking one up are both things a warehouse worker can only do with
/// nothing else on: not while a box fills both arms, not from a ladder, not while walking a
/// ladder across the floor, not from the seat of a reach truck, not while sliding on a wet
/// patch. Every one of those states already exists somewhere — LadderController knows who is
/// on it, CartController and ReachTruckController each publish a flag, the box has a static
/// grabbed reference — and asking them one by one at each call site is how the list quietly
/// goes out of date: the ladder was checked in one place, the truck in another, and neither
/// knew about the toolkit.
///
/// So the list lives here once, and both call sites ask the same question. Adding the next
/// occupation (a mop, a pallet jack) means adding one line here rather than hunting for the
/// places that would need to learn about it.
public static class PlayerBusyState
{
    /// What is occupying him right now, phrased for a HUD line — or null when nothing is.
    ///
    /// Returning the reason rather than a bare bool is deliberate: a refusal the player cannot
    /// explain reads as the control being broken, and the caller usually wants to say why.
    public static string Reason(GameObject player)
    {
        if (UIManager.IsAnyPanelOpen) return "the menu is open";

        // Down or on the way back up. Both are their own animation and their own control lock;
        // a swing on top of either fights whatever is driving the body.
        if (RagdollController.Instance != null && RagdollController.Instance.IsRagdoll)
            return "you are on the floor";
        if (PlayerDeathManager.Instance != null && PlayerDeathManager.Instance.IsDead)
            return "you are down";
        if (PlayerSlipController.Instance != null && PlayerSlipController.Instance.IsSlipping)
            return "you are slipping";

        return HandsReason(player);
    }

    /// True while anything on that list is going on.
    public static bool IsBusy(GameObject player) => Reason(player) != null;

    /// The narrower question: has something else got hold of his hands?
    ///
    /// Separate from Reason because the two are asked for different purposes. Whether he may swing
    /// depends on everything — a menu, a fall, a wet floor. Whether his hands can be placed on the
    /// weapon he is holding depends only on whether some other prop already owns them: a paused
    /// game is still a game where the bat is in his fists, and skipping the grip for it was enough
    /// to leave a weapon hanging off a socket with the arms by his sides.
    public static bool HandsTaken(GameObject player) => HandsReason(player) != null;

    static string HandsReason(GameObject player)
    {
        if (BoxPhysicsInteractable.GrabbedBox != null) return "your hands are full";

        // The toolkit deliberately does not appear here. It is not carried in any physical sense:
        // picking one up destroys the object and sets a flag, nothing is parented to a hand, and
        // nothing is drawn. Treating it as occupied hands meant a player who had picked one up
        // could not swing a weapon or pick one up either — an inventory item behaving like a crate.

        // Climbing it or walking it somewhere — LadderController covers both, and it is the only
        // one of these that is per-actor rather than global, because a teammate can be on a
        // ladder without it meaning anything for this player.
        if (player != null && LadderController.IsActorBusyWithLadder(player))
            return "you are on the ladder";

        // Driving.
        if (CartController.PlayerOnCart)        return "you are pushing the cart";
        if (ReachTruckController.PlayerOnTruck) return "you are on the reach truck";

        return null;
    }
}
