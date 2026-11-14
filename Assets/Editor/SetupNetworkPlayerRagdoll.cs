using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Rebuilds the ragdoll on the co-op player prefab after a body swap.
///
/// Swapping the model left the old rig where it was: twelve rigidbodies and eleven joints on a
/// skeleton that is now deactivated, and RemoteRagdollVisual._hipBone pointing into it. Since
/// that component collects bones with "include inactive", a remote player going down would have
/// thrown the invisible retired body around while the visible one stood still.
///
/// The joint chain itself comes from SetupPlayerRagdoll.BuildRigOn, so the networked twin falls
/// over with the same masses and limits as the local player rather than a second set that drifts.
public static class SetupNetworkPlayerRagdoll
{
    const string PrefabPath = "Assets/WarehouseDemo/Assets/Prefabs/NetworkPlayer.prefab";

    [MenuItem("Tools/Warehouse/Setup Network Player Ragdoll")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder();
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);

        try
        {
            var anim = root.GetComponentInChildren<Animator>(true);
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman)
                return "the prefab has no humanoid Animator";

            // Every rig, not just the one on the bones about to be used: the retired skeleton is
            // deliberately kept in the hierarchy, and leaving its rigidbodies there would give
            // RemoteRagdollVisual two skeletons to throw around.
            int stripped = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                foreach (var c in t.GetComponents<CharacterJoint>()) { Object.DestroyImmediate(c); stripped++; }
                foreach (var c in t.GetComponents<Rigidbody>())      { Object.DestroyImmediate(c); stripped++; }
                foreach (var c in t.GetComponents<Collider>())       { Object.DestroyImmediate(c); stripped++; }
            }
            report.AppendLine($"• stripped {stripped} components from the previous rig(s)");

            var bones = SetupPlayerRagdoll.BuildRigOn(anim.gameObject, anim);
            if (bones == null) return report + "bone collection failed - see the console";

            report.AppendLine($"• {bones.Count} bones rigged on '{anim.gameObject.name}'");

            // Point the visual at the new hips, or it reads the fall off a bone that no longer
            // moves anything.
            var visual = root.GetComponentInChildren<RemoteRagdollVisual>(true);
            if (visual != null)
            {
                var so = new SerializedObject(visual);
                so.FindProperty("_hipBone").objectReferenceValue = bones["Hips"];
                so.ApplyModifiedPropertiesWithoutUndo();
                report.AppendLine($"• RemoteRagdollVisual._hipBone -> {bones["Hips"].name}");
            }
            else report.AppendLine("• no RemoteRagdollVisual on the prefab");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);

            // ── Check what actually landed on disk ───────────────────────────
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            int bodies = saved.GetComponentsInChildren<Rigidbody>(true).Length;
            int joints = saved.GetComponentsInChildren<CharacterJoint>(true).Length;

            var savedVisual = saved.GetComponentInChildren<RemoteRagdollVisual>(true);
            var hip = savedVisual != null
                ? new SerializedObject(savedVisual).FindProperty("_hipBone").objectReferenceValue
                : null;

            report.AppendLine($"• saved: {bodies} rigidbodies, {joints} joints, " +
                              $"hip={(hip != null ? hip.name : "NULL")}");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        return report.ToString();
    }
}
