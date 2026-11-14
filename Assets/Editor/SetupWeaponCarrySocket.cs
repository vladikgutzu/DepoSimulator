using UnityEditor;
using UnityEngine;

/// Puts the weapon carry socket into the scene as an object you can drag.
///
/// MeleeCombat makes one at runtime if it has to, but a runtime object dies with the play session,
/// so the position can never be kept. Made here instead it is part of the scene: drag it in the
/// Scene view, and every weapon hangs where you left it.
///
/// It sits under the chest, not the hand: while a weapon is CARRIED the socket holds it and the
/// hands are pulled onto its grip points by IK — drag a grip point, and the hand follows, which is
/// how the cart and the ladder have always behaved. The fist only takes the weapon for the length
/// of a blow.
public static class SetupWeaponCarrySocket
{
    const string SocketName = "WeaponCarrySocket";

    [MenuItem("Tools/Warehouse/Setup Weapon Carry Socket")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) return "no GameObject tagged 'Player' in the open scene";

        var combat = player.GetComponent<MeleeCombat>();
        if (combat == null) return "the player has no MeleeCombat";

        var anim = player.GetComponent<Animator>();
        if (anim == null || !anim.isHuman) return "the player has no humanoid animator";

        var chest = anim.GetBoneTransform(HumanBodyBones.UpperChest)
                 ?? anim.GetBoneTransform(HumanBodyBones.Chest)
                 ?? anim.GetBoneTransform(HumanBodyBones.Spine);
        if (chest == null) return "the avatar maps no chest or spine bone to hang the socket on";

        var so   = new SerializedObject(combat);
        var prop = so.FindProperty("_carrySocket");

        // Already set up and still alive: leave it exactly where it was put.
        if (prop.objectReferenceValue is Transform kept && kept != null)
            return $"socket already assigned: {Path(kept)} — drag it in the Scene view to move the weapon";

        var existing = chest.Find(SocketName);
        Transform socket;

        if (existing != null) socket = existing;
        else
        {
            var go = new GameObject(SocketName);
            Undo.RegisterCreatedObjectUndo(go, "Weapon carry socket");
            socket = go.transform;
            socket.SetParent(chest, false);

            // Roughly at the right hip's height on the near side, angled across the body — a
            // starting point to drag from, not a considered pose.
            socket.localPosition = so.FindProperty("_carrySocketOffset").vector3Value;
            socket.localRotation = Quaternion.identity;
        }

        prop.objectReferenceValue = socket;
        so.ApplyModifiedProperties();

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(player.scene);

        return $"socket {(existing != null ? "found" : "created")} at {Path(socket)} and assigned to MeleeCombat\n" +
               "  drag it to place the weapon; drag GripPoint / OffHandPoint on the weapon to place the hands";
    }

    static string Path(Transform t)
    {
        var s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }
}
