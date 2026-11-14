using System.Linq;
using UnityEditor;
using UnityEngine;

/// Makes a body that was placed in the scene by hand into the player's body.
///
/// The usual route instantiates the model and works out its scale, and that measurement has been
/// wrong in three different ways on this pack (see SwapPlayerModel.MeasureHeight). A body dropped
/// into the scene and eyeballed sidesteps the question entirely: whatever size it looks, that is
/// the size it keeps.
///
/// So this takes the object as it stands — scale included — parents it under the Player, gives it
/// the name the body switcher expects, and then hands over to PlayerBody so the avatar, the
/// ragdoll, the wardrobe and the network puppet are all rebuilt against it. Nothing about the
/// switcher changes; it simply finds the body already there.
public static class AdoptSceneBody
{
    const string SourceName = "HumanBot_Skinned_Hands";

    [MenuItem("Tools/Warehouse/Adopt Scene Body (HumanBot hands)")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player") ?? GameObject.Find("Player");
        if (player == null) return "no GameObject tagged 'Player' in the open scene";

        var source = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                          FindObjectsSortMode.None)
            .FirstOrDefault(t => t.name == SourceName && !t.IsChildOf(player.transform));

        if (source == null)
            return $"no loose '{SourceName}' in the scene. Drag the model in, size it by eye, " +
                   "then run this.";

        var report = new System.Text.StringBuilder();

        string height = Describe(source);
        report.AppendLine($"• adopting {SourceName} — {height}");

        string nodeName = "Body__humanbot_hands";

        // The old node goes. Kept nodes are the switcher's business, but two bodies under the same
        // name is not something it can sort out, and the avatar would have two skeletons to choose
        // between — which is exactly the bug that put the player in a T-pose.
        var existing = player.transform.Find(nodeName);
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing.gameObject);
            report.AppendLine($"• removed the previous {nodeName}");
        }

        Undo.SetTransformParent(source, player.transform, "Adopt scene body");

        source.name             = nodeName;
        source.localPosition    = Vector3.zero;
        source.localRotation    = Quaternion.identity;
        // localScale is deliberately left as it was found: the whole point is that the size was
        // settled by eye rather than by a measurement that keeps getting it wrong.

        source.SetAsFirstSibling();
        source.gameObject.SetActive(true);

        report.AppendLine($"• parented to Player as {nodeName}, scale kept at {source.localScale}");
        report.AppendLine();

        // Everything else — avatar, binding, ragdoll, skins, the puppet other players see — is the
        // switcher's job, and it will find this body already in place.
        report.AppendLine(PlayerBody.Wear(PlayerBody.HumanBotHands));

        return report.ToString();
    }

    static string Describe(Transform body)
    {
        var head = body.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Head");
        var foot = body.GetComponentsInChildren<Transform>(true)
                       .Where(t => t.name.EndsWith("Foot") || t.name.EndsWith("ToeBase"))
                       .OrderBy(t => t.position.y).FirstOrDefault();

        return head != null && foot != null
            ? $"{head.position.y - foot.position.y:F2} m head to foot, scale {body.localScale.x:F3}"
            : $"scale {body.localScale.x:F3}";
    }
}
