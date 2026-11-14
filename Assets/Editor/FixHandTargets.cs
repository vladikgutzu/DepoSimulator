using UnityEngine;
using UnityEditor;

// Repositions HandTarget_R and HandTarget_L on box prefabs so hands
// land on the box SURFACE, not inside it.
public static class FixHandTargets
{
    struct BoxConfig
    {
        public string path;
        public Vector3 rightPos; // right hand local pos
        public Vector3 leftPos;  // left hand local pos
        public Vector3 rightRot; // euler
        public Vector3 leftRot;
    }

    static readonly BoxConfig[] Configs =
    {
        new() {
            path     = "Assets/Prefabs/env/box_med_prefab.prefab",
            rightPos = new Vector3( 0.30f, 0.12f, 0f),
            leftPos  = new Vector3(-0.30f, 0.12f, 0f),
            rightRot = new Vector3(0f,  90f, 0f),
            leftRot  = new Vector3(0f, 270f, 0f),
        },
        new() {
            path     = "Assets/Prefabs/env/box_large_prefab.prefab",
            rightPos = new Vector3( 0.40f, 0.18f, 0f),
            leftPos  = new Vector3(-0.40f, 0.18f, 0f),
            rightRot = new Vector3(0f,  90f, 0f),
            leftRot  = new Vector3(0f, 270f, 0f),
        },
        new() {
            path     = "Assets/Prefabs/env/box_long_flat_prefab.prefab",
            rightPos = new Vector3( 0.42f, 0.10f, 0f),
            leftPos  = new Vector3(-0.42f, 0.10f, 0f),
            rightRot = new Vector3(0f,  90f, 0f),
            leftRot  = new Vector3(0f, 270f, 0f),
        },
        new() {
            path     = "Assets/Prefabs/env/box_wide_flat_prefab.prefab",
            rightPos = new Vector3( 0.60f, 0.10f, 0f),
            leftPos  = new Vector3(-0.60f, 0.10f, 0f),
            rightRot = new Vector3(0f,  90f, 0f),
            leftRot  = new Vector3(0f, 270f, 0f),
        },
    };

    [MenuItem("Tools/Warehouse Demo/Fix Hand Targets")]
    public static void Execute()
    {
        foreach (var cfg in Configs)
        {
            using var scope = new PrefabUtility.EditPrefabContentsScope(cfg.path);
            var root = scope.prefabContentsRoot;

            var r = root.transform.Find("HandTarget_R");
            var l = root.transform.Find("HandTarget_L");

            if (r == null || l == null)
            {
                Debug.LogWarning($"[FixHands] HandTarget missing in {cfg.path}");
                continue;
            }

            r.localPosition = cfg.rightPos;
            r.localRotation = Quaternion.Euler(cfg.rightRot);
            l.localPosition = cfg.leftPos;
            l.localRotation = Quaternion.Euler(cfg.leftRot);

            Debug.Log($"[FixHands] {root.name}: R={cfg.rightPos} L={cfg.leftPos}");
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[FixHands] Done.");
    }
}
