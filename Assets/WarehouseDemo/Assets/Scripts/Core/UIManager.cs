public static class UIManager
{
    public static bool IsAnyPanelOpen { get; private set; }

    // Static bool persists across scene reloads — reset it before each scene loads.
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetOnLoad() => IsAnyPanelOpen = false;

    public static void OpenPanel()
    {
        IsAnyPanelOpen = true;
        UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.None;
        UnityEngine.Cursor.visible = true;
    }

    public static void ClosePanel()
    {
        IsAnyPanelOpen = false;
        UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.Locked;
        UnityEngine.Cursor.visible = false;
    }
}
