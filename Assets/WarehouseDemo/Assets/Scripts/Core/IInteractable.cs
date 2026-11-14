public interface IInteractable
{
    string InteractPrompt { get; }
    void Interact(UnityEngine.GameObject interactor);
}

// Optional companion interface.
// Implement on IInteractable objects where interaction can be blocked at hover time.
// PlayerInteraction uses this to choose blue (can) vs red (cannot) outline color.
public interface ICanInteractCheck
{
    bool CanInteract(UnityEngine.GameObject interactor);
}

// Marker interface: PlayerInteraction skips the hover outline for objects that
// implement this. Use on large vehicles/machines where per-mesh outline looks wrong.
public interface INoOutlineHighlight { }

// Objects requiring [E] to be held down continuously for HoldDuration seconds instead
// of a single press. PlayerInteraction accumulates hold time and shows a progress bar
// in the prompt; Interact() fires exactly once when the duration is reached. Releasing
// [E] or looking away resets the progress.
//
// RequiresHold lets a single object serve both roles depending on its own state (e.g.
// ReachTruckController is a single press to enter the vehicle, but a hold when it needs
// repairing) — checked every frame; PlayerInteraction only uses the hold path when true.
public interface IHoldInteractable : IInteractable
{
    bool  RequiresHold { get; }
    float HoldDuration { get; }
}
