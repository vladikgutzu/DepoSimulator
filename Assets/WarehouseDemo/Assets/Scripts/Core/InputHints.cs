using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// Returns locale-aware input key labels that adapt to whether a gamepad is connected.
/// Use in InteractPrompt getters and ShowPrompt calls — evaluated every frame so it
/// picks up gamepad connect/disconnect without any additional wiring.
///
/// The labels name the same buttons PadControls reads, so a prompt can never promise a button
/// the code does not listen to. Letters are Xbox layout (A south, B east, X west, Y north).
public static class InputHints
{
    static bool Gp => Gamepad.current != null;
    static string Key(GameBindingAction action) => GameInputBindings.GetDisplayName(action, false);
    static string Pad(GameBindingAction action) => GameInputBindings.GetDisplayName(action, true);
    static string Both(GameBindingAction action) => Gp ? $"[{Key(action)}/{Pad(action)}]" : $"[{Key(action)}]";
    static string Prefix(GameBindingAction action) => Gp ? $"{Key(action)}/{Pad(action)} —" : $"{Key(action)} —";
    public static string Active(GameBindingAction action) => Gp ? Pad(action) : Key(action);

    /// Interact button: "[E]" or "[E/X]"
    public static string E  => Both(GameBindingAction.Interact);

    /// Exit/release button: "[F]" or "[F/B]"
    public static string F  => Both(GameBindingAction.Release);

    /// Interact prefix for "Verb — action" style prompts: "E —" or "E/X —"
    public static string EK => Prefix(GameBindingAction.Interact);

    /// Exit prefix: "F —" or "F/B —"
    public static string FK => Prefix(GameBindingAction.Release);

    /// Secondary action button: "[G]" or "[G/Y]"
    public static string G  => Both(GameBindingAction.Secondary);

    /// Secondary action prefix: "G —" or "G/Y —"
    public static string GK => Prefix(GameBindingAction.Secondary);
}

/// The one place gamepad buttons are assigned for everything polled directly in code (the Starter
/// Assets move/look/jump/sprint go through StarterAssets.inputactions instead).
///
/// Interact used to share A with jump: pressing A at a box both picked it up and hopped, and
/// holding A to wind up a throw or to repair something kept jumping on the spot. Layout now:
///
///   A  jump (input actions)           LT  sprint (input actions)
///   X  interact / hold / enter-exit   RT  melee
///   Y  drop weapon / ladder (G)       RB  ping   · lift up in a vehicle
///   B  drop the carried box (F)       LB  lift down in a vehicle
///   Start  menu (Esc)                 D-pad up (hold)  push-to-talk (V)
public static class PadControls
{
    public static ButtonControl Interact(Gamepad gp)   => gp.buttonWest;
    public static ButtonControl Drop(Gamepad gp)       => gp.buttonNorth;
    public static ButtonControl Release(Gamepad gp)    => gp.buttonEast;
    public static ButtonControl Menu(Gamepad gp)       => gp.startButton;
    public static ButtonControl Back(Gamepad gp)       => gp.buttonEast;
    public static ButtonControl PushToTalk(Gamepad gp) => gp.dpad.up;
    public static ButtonControl Ping(Gamepad gp)       => gp.rightShoulder;
    public static ButtonControl LiftUp(Gamepad gp)     => gp.rightShoulder;
    public static ButtonControl LiftDown(Gamepad gp)   => gp.leftShoulder;
    public static ButtonControl Sprint(Gamepad gp)     => gp.leftTrigger;
    public static ButtonControl Attack(Gamepad gp)     => gp.rightTrigger;
}
