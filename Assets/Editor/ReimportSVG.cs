using UnityEditor;

public class ReimportSVG
{
    public static void Execute()
    {
        string[] paths = {
            "Assets/WarehouseDemo/Assets/UI/Icons/scanner.svg",
            "Assets/WarehouseDemo/Assets/UI/Icons/forklift.svg",
            "Assets/WarehouseDemo/Assets/UI/Icons/inventory.svg",
            "Assets/WarehouseDemo/Assets/UI/Icons/clipboard.svg",
            "Assets/WarehouseDemo/Assets/UI/Icons/radio.svg",
            "Assets/WarehouseDemo/Assets/UI/Icons/menu.svg",
            "Assets/WarehouseDemo/Assets/UI/Icons/settings.svg",
            "Assets/WarehouseDemo/Assets/UI/Icons/check.svg",
            "Assets/WarehouseDemo/Assets/UI/Icons/info.svg",
            "Assets/WarehouseDemo/Assets/UI/Icons/triangle-alert.svg",
        };
        foreach (var p in paths)
            AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);

        BuildSceneUI.Execute();
        UnityEngine.Debug.Log("[ReimportSVG] Done.");
    }
}
