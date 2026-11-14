using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Which body the player wears — as a switch, not a one-way change.
///
/// A body is never just a mesh here. Four things are tied to whichever skeleton is on:
/// the humanoid avatar the animator retargets through, the ragdoll rigidbodies and joints that
/// hang off the bones, the wardrobe (PlayerSkin refuses a skin whose material count does not
/// match the renderer, so role colour silently stops working when the body changes underneath
/// it), and the puppet on NetworkPlayer.prefab that everyone else in the session actually looks
/// at. Swapping the mesh alone leaves three of those pointing at the previous body, and each one
/// fails quietly rather than loudly — no colour, a ragdoll that drops an invisible corpse, and
/// teammates who still see the old character.
///
/// So the switch is one operation. Bodies are kept in the scene side by side, one node each
/// (Body__<key>), with the worn one active and the rest deactivated, which is what makes going
/// back cost a menu click instead of a re-import.
public static class PlayerBody
{
    public sealed class Def
    {
        public string Key;          // names the node under Player
        public string Label;
        public string BodyPath;     // what gets instantiated
        public string AvatarPath;   // where the humanoid Avatar lives — always the FBX

        /// Per-role liveries: skin key ("solo", "picker", "assembler", "forkliftoperator",
        /// "dispatcher") to the albedo that role's body wears. A body shipping a single texture
        /// leaves this empty and gets tinted copies instead, which is the older behaviour.
        ///
        /// A role belongs here only when the texture already READS as that role's colour from
        /// PlayerRoleColors: the minimap dot and the co-op HUD go on calling that player green
        /// whatever the body wears, and a livery that disagrees with them is worse than none.
        /// That is why the Dispatcher is absent below — the pack has no violet, so violet
        /// stays a tint.
        public (string Key, string TexturePath)[] RoleAlbedos = System.Array.Empty<(string, string)>();
    }

    public static readonly Def HumanBot = new()
    {
        Key        = "humanbot",
        Label      = "HumanBot (destructible robot)",
        BodyPath   = "Assets/Destructible_Humanoid_Robot/Prefabs/HumanBot_Skinned_A.prefab",
        AvatarPath = "Assets/Destructible_Humanoid_Robot/Models/HumanBot_Skinned_A.fbx",
        RoleAlbedos = new[]
        {
            ("solo",             Tex("White")),
            ("picker",           Tex("Green")),         // green, which is what PlayerRoleColors says
            ("assembler",        Tex("Blue")),          // blue
            ("forkliftoperator", Tex("Red")),          // picked by eye over the amber-ish camo
        },
    };

    /// The same robot, built so limbs come off.
    ///
    /// Worn instead of HumanBot_Skinned_A when injuries should remove a limb rather than open one
    /// up. Measured: this variant has 33 separate mesh renderers on the skeleton — the inner
    /// chassis as well as the plating — against 16 plus one welded skinned mesh on the other.
    ///
    /// AvatarPath points at the *other* body on purpose. This one is imported with CopyFromOther,
    /// so it carries no avatar of its own; the mapping lives in HumanBot_Skinned_A.fbx and both
    /// bodies share it bone for bone, which is what lets every animation retarget unchanged.
    /// See Tools/Warehouse/Setup Detachable Body.
    public static readonly Def HumanBotHands = new()
    {
        Key        = "humanbot_hands",
        Label      = "HumanBot (detachable limbs)",
        BodyPath   = "Assets/Destructible_Humanoid_Robot/Models/HumanBot_Skinned_Hands.fbx",
        AvatarPath = "Assets/Destructible_Humanoid_Robot/Models/HumanBot_Skinned_A.fbx",
        RoleAlbedos = HumanBot.RoleAlbedos,
    };

    static string Tex(string name) =>
        $"Assets/Destructible_Humanoid_Robot/Materials/Humanbot_Albedo_{name}.png";

    public static readonly Def XYBot = new()
    {
        Key        = "xybot",
        Label      = "XY Bot (previous body)",
        BodyPath   = SwapPlayerModel.ModelPath,
        AvatarPath = SwapPlayerModel.ModelPath,
    };

    [MenuItem("Tools/Warehouse/Player Body/HumanBot (destructible robot)", priority = 10)]
    public static void WearHumanBot() => Debug.Log(Wear(HumanBot));

    [MenuItem("Tools/Warehouse/Player Body/HumanBot (detachable limbs)", priority = 11)]
    public static void WearHumanBotHands()
    {
        Debug.Log(SetupHandsBody.Execute());
        Debug.Log(Wear(HumanBotHands));
    }

    [MenuItem("Tools/Warehouse/Player Body/XY Bot (previous body)", priority = 12)]
    public static void WearXYBot() => Debug.Log(Wear(XYBot));

    public static string Wear(Def body)
    {
        var report = new StringBuilder($"── wearing {body.Label} ──\n");

        // 1. The body the local player looks at.
        report.AppendLine(SwapPlayerModel.Execute(body.BodyPath, body.AvatarPath, body.Key));

        // 2. The wardrobe is tinted FROM the body that is on, so it has to be rebuilt after the
        //    swap, not before. Wiped rather than merged: two sets for one role would leave
        //    PlayerSkinRegistry picking a colour by load order.
        report.AppendLine("• skins: " + ResetPlayerSkins.Execute());
        report.AppendLine(CreatePlayerSkins.Execute(LoadAlbedos(body, report)));

        // 3. Ragdoll bodies and joints sit on the bones of whichever body is worn.
        SetupPlayerRagdoll.Execute();
        report.AppendLine("• local ragdoll rebuilt on the worn skeleton");

        // 4. The puppet teammates see. Skipping it makes you a robot in your own game only.
        report.AppendLine(SwapNetworkPlayerModel.Execute(body.BodyPath, body.AvatarPath));
        report.AppendLine(SetupNetworkPlayerRagdoll.Execute());

        report.AppendLine("\nSave the scene to keep this. The bodies not worn are still in the "
                        + "hierarchy under Player, deactivated — switching back is this same menu.");
        return report.ToString();
    }

    /// Resolves the body's liveries, naming any that fail to load rather than quietly dropping
    /// that role back to a tint — a missing texture would otherwise look like a design choice.
    static Dictionary<string, Texture> LoadAlbedos(Def body, StringBuilder report)
    {
        var map = new Dictionary<string, Texture>();
        foreach (var (key, path) in body.RoleAlbedos)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
            if (tex == null) { report.AppendLine($"• MISSING livery for {key}: {path}"); continue; }
            map[key] = tex;
        }
        return map;
    }
}
