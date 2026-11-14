using UnityEngine;
using UnityEditor;
using System.Reflection;

// Adds BoxPhysicsInteractable + BoxHandAnchors to every box_grp inside
// pallet_box_pack_prefab so the player can grab individual boxes off the pallet.
// Run via execute_script or Tools > Warehouse Demo > Setup Pallet Box Pack.
public static class SetupPalletBoxPack
{
    const string PalletPath          = "Assets/Prefabs/env/pallet_box_pack_prefab.prefab";
    const float  BoxMass             = 18f;   // kg per individual box
    const float  GripHeightFraction  = 0.30f; // hands at 30% from bottom

    [MenuItem("Tools/Warehouse Demo/Setup Pallet Box Pack")]
    public static void Execute()
    {
        var root = PrefabUtility.LoadPrefabContents(PalletPath);
        try
        {
            // Clear ALL old Rigidbodies first — AddRigidbodyToBoxes may have added them
            // to wrong objects (whole pallet, wrap mesh, etc.)
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(rb);

            int count = 0;
            SetupBoxGroups(root.transform, ref count);

            PrefabUtility.SaveAsPrefabAsset(root, PalletPath);
            Debug.Log($"[PalletBox] Done. Configured {count} box_grp objects.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // ── Recursively find every box_grp and configure it ──────────────────────

    static void SetupBoxGroups(Transform t, ref int count)
    {
        for (int i = 0; i < t.childCount; i++)
        {
            var child = t.GetChild(i);
            // box_grp, box_grp1 … box_grp7
            if (child.name == "box_grp" || child.name.StartsWith("box_grp"))
                ConfigureBox(child.gameObject, ref count);
            else
                SetupBoxGroups(child, ref count);
        }
    }

    static void ConfigureBox(GameObject go, ref int count)
    {
        // ── Remove stray child Rigidbodies ───────────────────────────────────
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
            if (rb.gameObject != go) Object.DestroyImmediate(rb);

        // ── Collider — add a BoxCollider on the root if no colliders exist ───
        if (go.GetComponentsInChildren<Collider>(true).Length == 0)
        {
            var rends3 = go.GetComponentsInChildren<Renderer>(true);
            Bounds bFall = rends3.Length > 0 ? rends3[0].bounds
                : new Bounds(go.transform.position + Vector3.up * 0.345f, Vector3.one * 0.69f);
            foreach (var r in rends3) bFall.Encapsulate(r.bounds);
            var bc    = go.AddComponent<BoxCollider>();
            bc.center = go.transform.InverseTransformPoint(bFall.center);
            bc.size   = bFall.size;
        }

        // ── BoxPhysicsInteractable first — [RequireComponent] auto-adds Rigidbody ─
        if (go.GetComponent<BoxPhysicsInteractable>() == null)
            go.AddComponent<BoxPhysicsInteractable>();

        // ── Configure the Rigidbody that was auto-added ──────────────────────
        var rb2 = go.GetComponent<Rigidbody>();
        if (rb2 != null)
        {
            rb2.mass                   = BoxMass;
            rb2.isKinematic            = true;
            rb2.interpolation          = RigidbodyInterpolation.Interpolate;
            rb2.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb2.useGravity             = false;
        }

        // ── BoxHandAnchors + IK targets ──────────────────────────────────────
        var anchors = go.GetComponent<BoxHandAnchors>() ?? go.AddComponent<BoxHandAnchors>();

        // World-space bounds from all child renderers
        var rends2 = go.GetComponentsInChildren<Renderer>(true);
        Bounds b = rends2.Length > 0 ? rends2[0].bounds : new Bounds(go.transform.position + Vector3.up * 0.345f, Vector3.one * 0.69f);
        foreach (var r in rends2) b.Encapsulate(r.bounds);

        // Convert to box_grp local space for hand target positions
        var localMin    = go.transform.InverseTransformPoint(b.min);
        var localMax    = go.transform.InverseTransformPoint(b.max);
        var localCenter = go.transform.InverseTransformPoint(b.center);
        float localH    = localMax.y - localMin.y;
        float gripY     = localMin.y + localH * GripHeightFraction;

        var rightPos = new Vector3(localMax.x, gripY, localCenter.z);
        var leftPos  = new Vector3(localMin.x, gripY, localCenter.z);
        var rightRot = Quaternion.Euler(0f,  90f, 0f);
        var leftRot  = Quaternion.Euler(0f, -90f, 0f);

        var rightT = EnsureChild(go, "HandTarget_R", rightPos, rightRot);
        var leftT  = EnsureChild(go, "HandTarget_L", leftPos,  leftRot);

        // Write to private fields via reflection
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var aType = typeof(BoxHandAnchors);
        aType.GetField("_rightPos",        flags)?.SetValue(anchors, rightPos);
        aType.GetField("_rightRot",        flags)?.SetValue(anchors, rightRot);
        aType.GetField("_leftPos",         flags)?.SetValue(anchors, leftPos);
        aType.GetField("_leftRot",         flags)?.SetValue(anchors, leftRot);
        aType.GetField("_rightHandTarget", flags)?.SetValue(anchors, rightT);
        aType.GetField("_leftHandTarget",  flags)?.SetValue(anchors, leftT);

        go.tag = "BoxInteract";
        count++;
    }

    // Creates or updates a child GameObject with the given LOCAL position/rotation
    static Transform EnsureChild(GameObject parent, string childName,
                                  Vector3 localPos, Quaternion localRot)
    {
        var existing = parent.transform.Find(childName);
        if (existing != null)
        {
            existing.localPosition = localPos;
            existing.localRotation = localRot;
            return existing;
        }
        var go = new GameObject(childName);
        go.transform.SetParent(parent.transform, worldPositionStays: false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        return go.transform;
    }
}
