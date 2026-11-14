// Interface for tools that can be equipped from the HUD action bar (slots 1-5).
// Implement on MonoBehaviour components that represent an equippable tool (scanner,
// plansheet, radio, forklift controller, etc.).
//
// HUDController calls OnEquip/OnUnequip when the player cycles slots.
// OnPrimaryUse is called when the player presses the interact key while the tool is active.
public interface IEquippableTool
{
    /// Display name shown in the HUD slot tooltip.
    string ToolName { get; }

    /// Called when this tool becomes the active slot.
    void OnEquip();

    /// Called when the player switches away or closes the active slot.
    void OnUnequip();

    /// Called on E-press (or primary input) while the tool is equipped.
    void OnPrimaryUse();
}
