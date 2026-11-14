using UnityEngine;
using UnityEditor;
using System.Reflection;

// Patches the 4 box prefabs and sets per-type hand grip coordinates.
// Run via execute_script or Tools > Warehouse Demo > Setup Box Prefab Anchors.
public static class SetupBoxPrefabAnchors
{
    // ── Per-type grip presets ────────────────────────────────────────────────
    // All positions are in box LOCAL space.
    //   X: half-width of box (hands grip left/right sides)
    //   Y: 30% from box bottom — natural "under and side" grip height
    //   Z: 0 (centred depth)
    // Rotations: right palm faces -X (inward), left palm faces +X (inward).
    // Tweak these values per your rig, then re-run the script.
    struct BoxPreset
    {
        public string path;
        public float  mass;
        public Vector3    rightPos;
        public Quaternion rightRot;
        public Vector3    leftPos;
        public Quaternion leftRot;
    }

    static readonly BoxPreset[] Presets =
    {
        new BoxPreset
        {
            path     = "Assets/Prefabs/env/box_large_prefab.prefab",
            mass     = 20f,
            // 0.66 × 0.67 × 0.66 — cubic box
            rightPos = new Vector3( 0.327f, 0.202f, 0f),
            rightRot = Quaternion.Euler(0f,  90f, 0f),
            leftPos  = new Vector3(-0.329f, 0.202f, 0f),
            leftRot  = Quaternion.Euler(0f, -90f, 0f),
        },
        new BoxPreset
        {
            path     = "Assets/Prefabs/env/box_long_flat_prefab.prefab",
            mass     = 10f,
            // 0.70 × 0.34 × 0.34 — wide, low box  →  hands at full width
            rightPos = new Vector3( 0.350f, 0.103f, 0f),
            rightRot = Quaternion.Euler(0f,  90f, 0f),
            leftPos  = new Vector3(-0.350f, 0.103f, 0f),
            leftRot  = Quaternion.Euler(0f, -90f, 0f),
        },
        new BoxPreset
        {
            path     = "Assets/Prefabs/env/box_med_prefab.prefab",
            mass     = 5f,
            // 0.34 × 0.34 × 0.34 — small cube  →  hands closer together
            rightPos = new Vector3( 0.172f, 0.103f, 0f),
            rightRot = Quaternion.Euler(0f,  90f, 0f),
            leftPos  = new Vector3(-0.172f, 0.103f, 0f),
            leftRot  = Quaternion.Euler(0f, -90f, 0f),
        },
        new BoxPreset
        {
            path     = "Assets/Prefabs/env/box_wide_flat_prefab.prefab",
            mass     = 15f,
            // 0.66 × 0.34 × 0.67 — wide flat box
            rightPos = new Vector3( 0.330f, 0.103f, 0f),
            rightRot = Quaternion.Euler(0f,  90f, 0f),
            leftPos  = new Vector3(-0.330f, 0.103f, 0f),
            leftRot  = Quaternion.Euler(0f, -90f, 0f),
        },
    };

    [MenuItem("Tools/Warehouse Demo/Setup Box Prefab Anchors")]
    public static void Execute()
    {
        foreach (var p in Presets)
            ProcessPrefab(p);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[BoxAnchors] All 4 box prefabs patched with per-type grip presets.");
    }

    static void ProcessPrefab(BoxPreset preset)
    {
        var root = PrefabUtility.LoadPrefabContents(preset.path);
        try
        {
            // ── Remove child Rigidbodies FIRST ──────────────────────────────
            // Child Rigidbodies (on decals, label meshes, sub-objects) don't follow
            // the parent when using transform.SetPositionAndRotation and cause visible
            // drift. Only the root needs a Rigidbody.
            foreach (var childRb in root.GetComponentsInChildren<Rigidbody>(true))
                if (childRb.gameObject != root)
                    Object.DestroyImmediate(childRb);

            // ── Root Rigidbody ───────────────────────────────────────────────
            var rb = root.GetComponent<Rigidbody>() ?? root.AddComponent<Rigidbody>();
            rb.mass                   = preset.mass;
            rb.isKinematic            = true;
            rb.interpolation          = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.useGravity             = false;

            // ── Collider (add only if none exist in hierarchy) ───────────────
            if (root.GetComponentsInChildren<Collider>(true).Length == 0)
                root.AddComponent<BoxCollider>();

            // ── BoxPhysicsInteractable ───────────────────────────────────────
            if (root.GetComponent<BoxPhysicsInteractable>() == null)
                root.AddComponent<BoxPhysicsInteractable>();

            // ── BoxHandAnchors ───────────────────────────────────────────────
            var anchors = root.GetComponent<BoxHandAnchors>()
                          ?? root.AddComponent<BoxHandAnchors>();

            // ── IK target children ───────────────────────────────────────────
            var rightT = EnsureChild(root, "HandTarget_R", preset.rightPos, preset.rightRot);
            var leftT  = EnsureChild(root, "HandTarget_L", preset.leftPos,  preset.leftRot);

            // ── Write all fields via reflection ──────────────────────────────
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var t     = typeof(BoxHandAnchors);
            t.GetField("_rightPos",        flags)?.SetValue(anchors, preset.rightPos);
            t.GetField("_rightRot",        flags)?.SetValue(anchors, preset.rightRot);
            t.GetField("_leftPos",         flags)?.SetValue(anchors, preset.leftPos);
            t.GetField("_leftRot",         flags)?.SetValue(anchors, preset.leftRot);
            t.GetField("_rightHandTarget", flags)?.SetValue(anchors, rightT);
            t.GetField("_leftHandTarget",  flags)?.SetValue(anchors, leftT);

            root.tag = "BoxInteract";

            PrefabUtility.SaveAsPrefabAsset(root, preset.path);
            Debug.Log($"[BoxAnchors] {System.IO.Path.GetFileNameWithoutExtension(preset.path)}: " +
                      $"R={preset.rightPos}  L={preset.leftPos}  mass={preset.mass}kg");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static Transform EnsureChild(GameObject parent, string childName,
                                  Vector3 worldPos, Quaternion worldRot)
    {
        var existing = parent.transform.Find(childName);
        if (existing != null)
        {
            existing.position = worldPos;
            existing.rotation = worldRot;
            return existing;
        }
        var go = new GameObject(childName);
        go.transform.position = worldPos;
        go.transform.rotation = worldRot;
        go.transform.SetParent(parent.transform, worldPositionStays: true);
        return go.transform;
    }
}
