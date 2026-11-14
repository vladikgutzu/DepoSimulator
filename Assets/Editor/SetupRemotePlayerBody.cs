using UnityEditor;
using UnityEngine;

/// Puts RemotePlayerBody on the co-op player prefab, so a team-mate is something you can actually
/// hit — and something you cannot walk through.
///
/// A tool rather than a hand edit, for the reason the other Setup* scripts give: a prefab has to be
/// changed through a contents scope, and this project has already lost a serialized array to a
/// careless one. It reports what it found and what it changed instead of assuming.
///
/// It goes on the **root**, not on CharacterVisual. The capsule stands where the player stands, and
/// the root is the transform ClientNetworkTransform drives; hung on the visual it would be carried
/// off by RemoteRagdollVisual's ground-clamp corrections during every fall.
public static class SetupRemotePlayerBody
{
    const string PrefabPath = "Assets/WarehouseDemo/Assets/Prefabs/NetworkPlayer.prefab";

    [MenuItem("Tools/Warehouse/Setup Remote Player Body")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new System.Text.StringBuilder("[Setup Remote Player Body]\n");

        using var scope = new PrefabUtility.EditPrefabContentsScope(PrefabPath);
        var root = scope.prefabContentsRoot;

        // What was there before, so the log says plainly whether this was the missing piece.
        var hadCollider = root.GetComponent<Collider>();
        var hadBody     = root.GetComponent<Rigidbody>();

        report.AppendLine($"• root collider before: {(hadCollider == null ? "none" : hadCollider.GetType().Name)}");
        report.AppendLine($"• root rigidbody before: {(hadBody == null ? "none" : "present")}");

        var visual  = root.transform.Find("CharacterVisual");
        var ragdoll = visual != null ? visual.GetComponent<RemoteRagdollVisual>() : null;

        report.AppendLine($"• CharacterVisual: {(visual == null ? "MISSING" : "found")}" +
                          $", RemoteRagdollVisual: {(ragdoll == null ? "MISSING" : "found")}");

        // Bone colliders are not a body. They spend the whole shift with detectCollisions off and
        // only wake for the length of a fall — which is exactly why a crate went through everyone.
        int bones = visual != null ? visual.GetComponentsInChildren<Collider>(true).Length : 0;
        report.AppendLine($"• ragdoll bone colliders under the visual: {bones} " +
                          "(inactive except during a fall — not a standing body)");

        if (root.GetComponent<RemotePlayerBody>() == null)
        {
            root.AddComponent<RemotePlayerBody>();
            report.AppendLine("• RemotePlayerBody added to the root");
        }
        else report.AppendLine("• RemotePlayerBody already there");

        report.AppendLine("• the capsule and its kinematic rigidbody are built at runtime in " +
                          "Awake, and stay disabled on the owner's own copy");

        return report.ToString();
    }
}
