using UnityEngine;
using UnityEngine.UIElements;

public static class ControlsBindingTable
{
    public static VisualElement Build()
    {
        var root = new VisualElement();
        root.AddToClassList("binding-table");

        var status = new Label();
        status.AddToClassList("binding-status");

        AddRow(root, "ACTION", "KEYBOARD / MOUSE", "GAMEPAD", true, null, null);
        AddRow(root, "Move", "W / A / S / D", "Left Stick", false, null, null);
        AddRow(root, "Look", "Mouse", "Right Stick", false, null, null);

        foreach (var definition in GameInputBindings.Definitions)
        {
            var keyboard = MakeBindingButton(definition.Action, false, status);
            var gamepad  = MakeBindingButton(definition.Action, true, status);
            AddRow(root, definition.Label, null, null, false, keyboard, gamepad);
        }

        root.Add(status);
        var reset = new Button(() =>
        {
            GameInputBindings.ResetToDefaults();
            var rebuilt = Build();
            root.parent?.Insert(root.parent.IndexOf(root), rebuilt);
            root.RemoveFromHierarchy();
        }) { text = "RESET TO DEFAULTS" };
        reset.AddToClassList("binding-reset");
        root.Add(reset);
        return root;
    }

    static Button MakeBindingButton(GameBindingAction action, bool gamepad, Label status)
    {
        var button = new Button { text = GameInputBindings.GetDisplayName(action, gamepad) };
        button.AddToClassList("binding-cell-button");
        button.clicked += () =>
        {
            status.text = gamepad ? "Press a gamepad button…  (Esc cancels)"
                                  : "Press a keyboard or mouse button…  (Esc cancels)";
            button.text = "WAITING…";
            GameInputBindings.BeginRebind(action, gamepad, (changed, error) =>
            {
                button.text = GameInputBindings.GetDisplayName(action, gamepad);
                status.text = changed ? "Binding saved" : error ?? "Rebinding cancelled";
            });
        };
        return button;
    }

    static void AddRow(VisualElement root, string action, string keyboard, string gamepad,
                       bool header, Button keyboardButton, Button gamepadButton)
    {
        var row = new VisualElement();
        row.AddToClassList(header ? "binding-row-header" : "binding-row");
        var actionLabel = new Label(action);
        actionLabel.AddToClassList("binding-action");
        row.Add(actionLabel);

        if (keyboardButton != null) row.Add(keyboardButton);
        else { var label = new Label(keyboard); label.AddToClassList("binding-cell"); row.Add(label); }
        if (gamepadButton != null) row.Add(gamepadButton);
        else { var label = new Label(gamepad); label.AddToClassList("binding-cell"); row.Add(label); }
        root.Add(row);
    }
}
