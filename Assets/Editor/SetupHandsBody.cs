using System.Linq;
using UnityEditor;
using UnityEngine;

/// Makes the detachable-limb body usable as a player body.
///
/// `HumanBot_Skinned_Hands` is the variant worth having: measured, it carries **33 separate mesh
/// renderers** — the inner chassis (`A_*`) as well as the outer plating (`B_*`), each parented to
/// its own bone. `HumanBot_Skinned_A` has the chassis welded into one skinned mesh, so on that body
/// an injury can only strip a panel and reveal what is underneath. On this one a limb can be taken
/// off outright.
///
/// It arrives as **Legacy with no avatar at all**, which is why it cannot simply be worn: without a
/// humanoid avatar the animator has nothing to retarget through and the body does not animate. Same
/// class of trap as the Mixamo clip in Setup Injured Clip, and just as silent.
///
/// The avatar is copied from HumanBot_Skinned_A rather than built fresh. The two share a skeleton
/// bone for bone, and copying guarantees the mapping is identical — a rebuilt avatar can differ in
/// small ways, and every animation in the game is authored against the first one.
public static class SetupHandsBody
{
    public const string HandsPath = "Assets/Destructible_Humanoid_Robot/Models/HumanBot_Skinned_Hands.fbx";
    public const string SourcePath = "Assets/Destructible_Humanoid_Robot/Models/HumanBot_Skinned_A.fbx";

    [MenuItem("Tools/Warehouse/Setup Detachable Body")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var importer = AssetImporter.GetAtPath(HandsPath) as ModelImporter;
        if (importer == null) return $"no model importer at {HandsPath}";

        var source = AssetDatabase.LoadAllAssetsAtPath(SourcePath).OfType<Avatar>().FirstOrDefault();
        if (source == null) return $"no avatar in {SourcePath} — that body is the mapping this copies";

        if (importer.animationType == ModelImporterAnimationType.Human &&
            importer.sourceAvatar == source)
            return $"{HandsPath} already set up (Humanoid, avatar copied from {source.name}).";

        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup   = ModelImporterAvatarSetup.CopyFromOther;
        importer.sourceAvatar  = source;

        importer.SaveAndReimport();

        // Verified rather than assumed: a copied avatar that does not fit reports itself invalid,
        // and an invalid avatar animates nothing while looking perfectly fine in the project view.
        var made = AssetDatabase.LoadAllAssetsAtPath(HandsPath).OfType<Avatar>().FirstOrDefault();

        string state = made == null
            ? "no avatar was produced — the skeletons may not match after all"
            : made.isValid ? "avatar valid" : "avatar INVALID";

        var model = AssetDatabase.LoadMainAssetAtPath(HandsPath) as GameObject;
        int parts = model != null
            ? model.GetComponentsInChildren<Renderer>(true).Count(r => r.name.StartsWith("A_") ||
                                                                       r.name.StartsWith("B_"))
            : 0;

        return $"{HandsPath}:\n• Legacy -> Humanoid, avatar copied from {source.name}\n" +
               $"• {state}\n• {parts} detachable parts on the skeleton";
    }
}
