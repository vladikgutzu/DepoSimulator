using UnityEngine;
using UnityEditor;
using System.Text;

public static class FindPlayerBones
{
    public static void Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) { Debug.LogError("Player not found!"); return; }

        var sb = new StringBuilder();
        foreach (var t in player.GetComponentsInChildren<Transform>(true))
        {
            var name = t.name.ToLower();
            if (name.Contains("hand") || name.Contains("wrist") || name.Contains("palm"))
                sb.AppendLine($"  {t.name}  (path: {GetPath(t, player.transform)})");
        }
        Debug.Log($"[Bones] Hand bones:\n{sb}");
    }

    static string GetPath(Transform t, Transform root)
    {
        var parts = new System.Collections.Generic.List<string>();
        var cur = t;
        while (cur != root && cur != null) { parts.Insert(0, cur.name); cur = cur.parent; }
        return string.Join("/", parts);
    }
}
