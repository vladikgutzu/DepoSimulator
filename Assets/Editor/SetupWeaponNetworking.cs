using Unity.Netcode;
using UnityEditor;
using UnityEngine;

/// Puts the weapon manager into the scene, next to the ones that already arbitrate boxes, pallets
/// and ladders.
///
/// It is a plain in-scene NetworkObject like they are: nothing spawns it, every peer has it from
/// scene load, and NGO matches the copies up by GlobalObjectIdHash.
public static class SetupWeaponNetworking
{
    const string ObjectName = "NetworkWeaponManager";

    [MenuItem("Tools/Warehouse/Setup Weapon Networking")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var existing = Object.FindFirstObjectByType<NetworkWeaponManager>(FindObjectsInactive.Include);
        if (existing != null)
            return $"{ObjectName} already in the scene ({existing.name})";

        var go = new GameObject(ObjectName);
        Undo.RegisterCreatedObjectUndo(go, "Setup weapon networking");

        // Beside its neighbours in the hierarchy rather than at the root end, so the managers stay
        // together and one missing one is obvious at a glance.
        var neighbour = Object.FindFirstObjectByType<NetworkPhysBoxManager>(FindObjectsInactive.Include);
        if (neighbour != null) go.transform.SetSiblingIndex(neighbour.transform.GetSiblingIndex() + 1);

        go.AddComponent<NetworkObject>();
        go.AddComponent<NetworkWeaponManager>();

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);

        return $"{ObjectName} added to the scene — save the scene to keep it";
    }
}
