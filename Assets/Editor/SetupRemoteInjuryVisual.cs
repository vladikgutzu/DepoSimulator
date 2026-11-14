using UnityEditor;
using UnityEngine;

/// Puts RemoteInjuryVisual on the co-op player prefab, so team-mates can see each other's injuries.
///
/// A tool rather than a line in the prefab, for the reason SwapNetworkPlayerModel gives at length:
/// a prefab has to be edited through a contents scope, and that scope has bitten this project
/// before — rebuilding Thief.prefab the same way silently emptied a serialized material array. So
/// this reports what it found and what it changed rather than assuming.
///
/// It goes on **CharacterVisual**, not on the prefab root: that is where the body and the animator
/// live, and the component walks up to the NetworkPlayer for the replicated state.
public static class SetupRemoteInjuryVisual
{
    const string PrefabPath = "Assets/WarehouseDemo/Assets/Prefabs/NetworkPlayer.prefab";

    [MenuItem("Tools/Warehouse/Setup Remote Injury Visual")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new System.Text.StringBuilder("[Setup Remote Injury Visual]\n");

        using var scope = new PrefabUtility.EditPrefabContentsScope(PrefabPath);
        var root = scope.prefabContentsRoot;

        var visual = root.transform.Find("CharacterVisual");
        if (visual == null) return report + "• NetworkPlayer has no CharacterVisual child";

        var anim = visual.GetComponentInChildren<Animator>(true);
        report.AppendLine($"• body on '{visual.name}', animator " +
                          $"{(anim == null ? "MISSING" : anim.gameObject.name)}" +
                          $"{(anim != null ? $" (humanoid={anim.avatar != null && anim.avatar.isHuman})" : "")}");

        if (anim == null) return report.ToString();

        // The panels the injury visual takes off. Reported rather than assumed: on a body variant
        // welded from one mesh there is nothing to remove, and the component would run silently
        // doing nothing at all.
        int panels = 0;
        foreach (var r in visual.GetComponentsInChildren<Renderer>(true))
            if (r.name.StartsWith("A_") || r.name.StartsWith("B_")) panels++;

        report.AppendLine($"• separable panels found: {panels}" +
                          (panels == 0 ? "  ← injuries will not show on this body" : ""));

        var existing = visual.GetComponent<RemoteInjuryVisual>();
        if (existing == null)
        {
            visual.gameObject.AddComponent<RemoteInjuryVisual>();
            report.AppendLine("• RemoteInjuryVisual added to CharacterVisual");
        }
        else report.AppendLine("• RemoteInjuryVisual already there");

        report.AppendLine($"• crouch layer on the controller: " +
                          $"{HasLayer(anim, RepairCrouch.LayerName)}");

        return report.ToString();
    }

    static bool HasLayer(Animator anim, string name)
    {
        var ac = anim.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
        if (ac == null) return false;

        foreach (var l in ac.layers) if (l.name == name) return true;
        return false;
    }
}
