using UnityEngine;
using UnityEditor;

public static class GrabAndShoot
{
    public static void Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) { Debug.LogError("[GrabAndShoot] Player not found"); return; }

        var box = GameObject.Find("Warehouse/Props/box_large_prefab");
        if (box == null) { Debug.LogError("[GrabAndShoot] Box not found"); return; }

        var grabField = typeof(BoxPhysicsInteractable)
            .GetField("_grabbed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (grabField?.GetValue(null) != null)
            box.GetComponent<IInteractable>()?.Interact(player);

        var boxPos   = box.transform.position;
        var standPos = new Vector3(boxPos.x, boxPos.y, boxPos.z - 1.2f);
        player.transform.position = standPos;
        player.transform.rotation = Quaternion.identity;
        box.GetComponent<IInteractable>()?.Interact(player);

        // Camera: diagonally from front-right, elevated — shows player chest + box + both arms
        var sv = SceneView.lastActiveSceneView;
        if (sv != null)
        {
            var lookAt = new Vector3(boxPos.x + 0.1f, 1.15f, standPos.z + 0.4f);
            sv.LookAt(lookAt, Quaternion.Euler(18f, 130f, 0f), 2.0f);
            sv.Repaint();
        }
        Debug.Log("[GrabAndShoot] Done.");
    }
}
