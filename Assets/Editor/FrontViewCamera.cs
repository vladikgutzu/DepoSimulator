using UnityEngine;
using UnityEditor;

public static class FrontViewCamera
{
    public static void Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) return;
        var sv = SceneView.lastActiveSceneView;
        if (sv == null) return;
        sv.LookAt(player.transform.position + Vector3.up * 1.1f,
                  Quaternion.Euler(10f, 180f, 0f), 3.5f);
        sv.Repaint();
    }
    // Dummy GameObject path so capture_scene_object frames this object
    public static string TargetPath => "Warehouse/Props/box_large_prefab";
}
