using UnityEngine;
using UnityEditor;

public static class SideViewCamera
{
    public static void Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) return;

        var sv = SceneView.lastActiveSceneView;
        if (sv == null) return;

        // Position scene camera to the right side of the player, slightly elevated
        var pos = player.transform.position + new Vector3(3f, 1.5f, 0f);
        sv.LookAt(player.transform.position + Vector3.up * 1.2f, Quaternion.Euler(15f, -90f, 0f), 4f);
        sv.Repaint();
    }
}
