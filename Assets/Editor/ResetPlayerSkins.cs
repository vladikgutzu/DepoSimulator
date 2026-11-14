using UnityEditor;
using UnityEngine;

/// Wipes the generated wardrobe so it can be rebuilt. Separate from the generator, which is
/// deliberately non-destructive - anything hand-tuned there survives a re-run, and that is worth
/// keeping even when a bad batch has to be cleared.
public static class ResetPlayerSkins
{
    const string Folder = "Assets/Resources/Catalog/PlayerSkins";

    [MenuItem("Tools/Warehouse/Reset Player Skins")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        if (!AssetDatabase.IsValidFolder(Folder)) return Folder + " does not exist";

        bool ok = AssetDatabase.DeleteAsset(Folder);
        AssetDatabase.Refresh();

        return ok ? "deleted " + Folder : "could not delete " + Folder;
    }
}
