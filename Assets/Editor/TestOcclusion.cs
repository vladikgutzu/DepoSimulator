using UnityEngine;
using UnityEditor;
using System.Reflection;

public static class TestOcclusion
{
    public static void Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) { Debug.LogError("[TestOcclusion] Player not found"); return; }

        // ── Fix OcclusionMask to include navMeshObjects (layer 6) + Default (0) ──
        var fader = player.GetComponent<CameraOcclusionFader>();
        if (fader != null)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            // Layer 0 (Default) + Layer 6 (navMeshObjects)
            LayerMask mask = LayerMask.GetMask("Default", "navMeshObjects");
            typeof(CameraOcclusionFader)
                .GetField("_occlusionMask", flags)
                ?.SetValue(fader, mask);
            Debug.Log($"[TestOcclusion] OcclusionMask updated: Default + navMeshObjects (int={mask.value})");
        }

        // ── Move player BETWEEN the two main shelf rows ──────────────────────
        // shelfLarge_GRP_01_prefab center X ≈ -8.87, Z ≈ -8.38
        // Put player in the aisle (Z slightly in front of the shelf face)
        var shelfPos = new Vector3(-8.87f, 0f, -6.0f); // aisle in front of shelf
        player.transform.position = shelfPos;
        player.transform.rotation = Quaternion.Euler(0f, 90f, 0f); // facing shelf

        Debug.Log($"[TestOcclusion] Player placed in shelf aisle at {shelfPos}");

        // ── Scene View: camera from BEHIND player, pointing through the shelf ─
        var sv = SceneView.lastActiveSceneView;
        if (sv != null)
        {
            // Camera behind the player (at higher Z), looking toward the shelf (-Z direction)
            var pivot = player.transform.position + Vector3.up * 1.0f;
            sv.LookAt(pivot, Quaternion.Euler(10f, 90f, 0f), 4f);
            sv.Repaint();
        }
    }
}
