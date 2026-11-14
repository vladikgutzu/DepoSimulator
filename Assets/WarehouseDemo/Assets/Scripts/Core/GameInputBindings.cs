using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Utilities;

public enum GameBindingAction
{
    Interact, Release, Secondary, Jump, Sprint, Attack,
    PushToTalk, Menu, Notifications, LiftUp, LiftDown
}

/// Runtime input bindings shared by UI prompts and gameplay code.
public static class GameInputBindings
{
    public readonly struct Definition
    {
        public readonly GameBindingAction Action;
        public readonly string Label;
        public readonly string KeyboardDefault;
        public readonly string GamepadDefault;

        public Definition(GameBindingAction action, string label, string keyboard, string gamepad)
        {
            Action = action; Label = label; KeyboardDefault = keyboard; GamepadDefault = gamepad;
        }
    }

    public static readonly Definition[] Definitions =
    {
        new(GameBindingAction.Interact,   "Interact / use",  "<Keyboard>/e",         "<Gamepad>/buttonWest"),
        new(GameBindingAction.Release,    "Release / drop",  "<Keyboard>/f",         "<Gamepad>/buttonEast"),
        new(GameBindingAction.Secondary,  "Secondary action", "<Keyboard>/g",        "<Gamepad>/buttonNorth"),
        new(GameBindingAction.Jump,       "Jump",            "<Keyboard>/space",     "<Gamepad>/buttonSouth"),
        new(GameBindingAction.Sprint,     "Sprint",          "<Keyboard>/leftShift", "<Gamepad>/leftTrigger"),
        new(GameBindingAction.Attack,     "Attack",          "<Mouse>/leftButton",   "<Gamepad>/rightTrigger"),
        new(GameBindingAction.PushToTalk, "Push-to-talk",    "<Keyboard>/v",         "<Gamepad>/dpad/up"),
        new(GameBindingAction.Menu,       "Pause menu",      "<Keyboard>/escape",    "<Gamepad>/start"),
        new(GameBindingAction.Notifications, "Notifications", "<Keyboard>/n",        "<Gamepad>/select"),
        new(GameBindingAction.LiftUp,     "Lift / forks up", "<Keyboard>/q",         "<Gamepad>/rightShoulder"),
        new(GameBindingAction.LiftDown,   "Lift / forks down", "<Keyboard>/z",       "<Gamepad>/leftShoulder"),
    };

    const string Prefix = "Controls.Binding.";
    static IDisposable _rebindSubscription;
    static readonly HashSet<GameBindingAction> _suppressedUntilReleased = new();
    static readonly ButtonControl[] _keyboardButtons = new ButtonControl[Definitions.Length];
    static readonly ButtonControl[] _gamepadButtons = new ButtonControl[Definitions.Length];
    static readonly bool[] _keyboardButtonResolved = new bool[Definitions.Length];
    static readonly bool[] _gamepadButtonResolved = new bool[Definitions.Length];

    static GameInputBindings()
    {
        // A cached null means that device is currently absent. Clear that result when the Input
        // System's device set changes so a newly connected controller becomes available at once.
        InputSystem.onDeviceChange += (_, __) => ClearButtonCache();
    }

    static Definition GetDefinition(GameBindingAction action)
    {
        foreach (var definition in Definitions)
            if (definition.Action == action) return definition;
        throw new ArgumentOutOfRangeException(nameof(action));
    }

    public static string GetPath(GameBindingAction action, bool gamepad)
    {
        var d = GetDefinition(action);
        return PlayerPrefs.GetString(Prefix + action + (gamepad ? ".Gamepad" : ".Keyboard"),
            gamepad ? d.GamepadDefault : d.KeyboardDefault);
    }

    public static string GetDisplayName(GameBindingAction action, bool gamepad)
    {
        string path = GetPath(action, gamepad);
        string text = InputControlPath.ToHumanReadableString(path,
            InputControlPath.HumanReadableStringOptions.OmitDevice);
        return string.IsNullOrWhiteSpace(text) ? path : text;
    }

    static ButtonControl FindButton(GameBindingAction action, bool gamepad)
    {
        int index = (int)action;
        if (index < 0 || index >= Definitions.Length) return null;

        var controls = gamepad ? _gamepadButtons : _keyboardButtons;
        var resolved = gamepad ? _gamepadButtonResolved : _keyboardButtonResolved;
        if (resolved[index]) return controls[index];

        // GetPath reads PlayerPrefs and builds its key; FindControl parses the resulting path.
        // Doing both for every IsPressed/WasPressed call generated garbage every driving frame.
        controls[index] = InputSystem.FindControl(GetPath(action, gamepad)) as ButtonControl;
        resolved[index] = true;
        return controls[index];
    }

    static void ClearButtonCache()
    {
        Array.Clear(_keyboardButtons, 0, _keyboardButtons.Length);
        Array.Clear(_gamepadButtons, 0, _gamepadButtons.Length);
        Array.Clear(_keyboardButtonResolved, 0, _keyboardButtonResolved.Length);
        Array.Clear(_gamepadButtonResolved, 0, _gamepadButtonResolved.Length);
    }

    static bool PhysicalIsPressed(GameBindingAction action)
        => (FindButton(action, false)?.isPressed ?? false)
        || (FindButton(action, true)?.isPressed ?? false);

    static bool IsSuppressed(GameBindingAction action)
    {
        if (!_suppressedUntilReleased.Contains(action)) return false;
        if (PhysicalIsPressed(action)) return true;

        _suppressedUntilReleased.Remove(action);
        return false;
    }

    public static bool IsPressed(GameBindingAction action)
        => !IsSuppressed(action) && PhysicalIsPressed(action);

    public static bool WasPressed(GameBindingAction action)
        => !IsSuppressed(action)
        && ((FindButton(action, false)?.wasPressedThisFrame ?? false)
         || (FindButton(action, true)?.wasPressedThisFrame ?? false));

    /// Prevents one physical press from both leaving an interactable and immediately entering it
    /// again when its controller and PlayerInteraction run in the opposite order that frame.
    public static void SuppressUntilReleased(GameBindingAction action)
        => _suppressedUntilReleased.Add(action);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetRuntimeState()
    {
        _suppressedUntilReleased.Clear();
        ClearButtonCache();
    }

    public static void BeginRebind(GameBindingAction action, bool gamepad,
                                   Action<bool, string> completed)
    {
        CancelRebind();
        _rebindSubscription = InputSystem.onAnyButtonPress.Call(control =>
        {
            if (control == null) return;
            bool correctDevice = gamepad ? control.device is Gamepad
                                         : control.device is Keyboard or Mouse;
            if (!correctDevice) return;

            if (control.device is Keyboard && control.name == "escape")
            {
                CancelRebind();
                completed?.Invoke(false, null);
                return;
            }

            string duplicate = FindDuplicate(control.path, action, gamepad);
            if (duplicate != null)
            {
                CancelRebind();
                completed?.Invoke(false, $"Already used by {duplicate}");
                return;
            }

            PlayerPrefs.SetString(Prefix + action + (gamepad ? ".Gamepad" : ".Keyboard"), control.path);
            PlayerPrefs.Save();
            ClearButtonCache();
            CancelRebind();
            ApplyToAllPlayerInputs();
            completed?.Invoke(true, null);
        });
    }

    static string FindDuplicate(string path, GameBindingAction except, bool gamepad)
    {
        foreach (var d in Definitions)
            if (d.Action != except && string.Equals(GetPath(d.Action, gamepad), path,
                    StringComparison.OrdinalIgnoreCase))
                return d.Label;
        return null;
    }

    public static void CancelRebind()
    {
        _rebindSubscription?.Dispose();
        _rebindSubscription = null;
    }

    public static void ResetToDefaults()
    {
        CancelRebind();
        foreach (var d in Definitions)
        {
            PlayerPrefs.DeleteKey(Prefix + d.Action + ".Keyboard");
            PlayerPrefs.DeleteKey(Prefix + d.Action + ".Gamepad");
        }
        PlayerPrefs.Save();
        ClearButtonCache();
        ApplyToAllPlayerInputs();
    }

    public static void ApplyToPlayerInput(PlayerInput input)
    {
        if (input?.actions == null) return;
        ApplyAction(input.actions.FindAction("Jump"), GameBindingAction.Jump);
        ApplyAction(input.actions.FindAction("Sprint"), GameBindingAction.Sprint);
    }

    static void ApplyAction(InputAction action, GameBindingAction binding)
    {
        if (action == null) return;
        for (int i = 0; i < action.bindings.Count; i++)
        {
            var b = action.bindings[i];
            if (b.isComposite || b.isPartOfComposite) continue;
            if (b.path.Contains("Keyboard")) action.ApplyBindingOverride(i, GetPath(binding, false));
            else if (b.path.Contains("Gamepad")) action.ApplyBindingOverride(i, GetPath(binding, true));
        }
    }

    public static void ApplyToAllPlayerInputs()
    {
        foreach (var input in UnityEngine.Object.FindObjectsByType<PlayerInput>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
            ApplyToPlayerInput(input);
    }
}
