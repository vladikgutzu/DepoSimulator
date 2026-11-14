using UnityEditor;
public class RefreshDB
{
    public static void Execute() => AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
}
