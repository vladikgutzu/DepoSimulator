using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Repairs BoxInteract-tagged objects that lost BoxPhysicsInteractable or BoxCollider.
public static class RepairBoxInteractables
{
    [MenuItem("Tools/Warehouse Demo/Repair Box Interactables")]
    public static void Execute()
    {
        // Find ALL GameObjects with BoxInteract tag in scene
        var tagged = GameObject.FindGameObjectsWithTag("BoxInteract");
        int addedBPI = 0, addedCollider = 0, fixedRb = 0;

        foreach (var go in tagged)
        {
            bool dirty = false;

            // 1. Ensure BoxPhysicsInteractable exists
            if (go.GetComponent<BoxPhysicsInteractable>() == null)
            {
                go.AddComponent<BoxPhysicsInteractable>();
                addedBPI++;
                dirty = true;
            }

            // 2. Ensure Rigidbody exists and is kinematic
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = go.AddComponent<Rigidbody>();
                dirty = true;
            }
            if (!rb.isKinematic || rb.useGravity)
            {
                rb.isKinematic            = true;
                rb.useGravity             = false;
                rb.interpolation          = RigidbodyInterpolation.None;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                fixedRb++;
                dirty = true;
            }

            // 3. Ensure exactly one BoxCollider exists on root
            var colliders = go.GetComponents<BoxCollider>();
            if (colliders.Length == 0)
            {
                var (lCenter, lSize) = GetLocalBounds(go);
                var box    = go.AddComponent<BoxCollider>();
                box.center = lCenter;
                box.size   = lSize;
                addedCollider++;
                dirty = true;
            }
            else
            {
                // Remove extras and recalculate size with correct local bounds
                for (int i = 1; i < colliders.Length; i++)
                    Object.DestroyImmediate(colliders[i]);

                var (lCenter, lSize) = GetLocalBounds(go);
                colliders[0].center = lCenter;
                colliders[0].size   = lSize;
                dirty = true;
            }

            if (dirty) EditorUtility.SetDirty(go);
        }

        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log($"[RepairBoxes] Found {tagged.Length} BoxInteract objects. " +
                  $"Added BPI: {addedBPI}, Added Collider: {addedCollider}, Fixed Rb: {fixedRb}.");
    }

    static Vector3 InvScale(Vector3 s) =>
        new Vector3(s.x != 0 ? 1f/s.x : 1f,
                    s.y != 0 ? 1f/s.y : 1f,
                    s.z != 0 ? 1f/s.z : 1f);

    // Correct local-space bounds: transform each mesh's corner points into the
    // root object's local space. Works for any rotation without world-AABB inflation.
    static (Vector3 center, Vector3 size) GetLocalBounds(GameObject go)
    {
        var filters = go.GetComponentsInChildren<MeshFilter>(true);
        bool first = true;
        Bounds local = new Bounds();

        foreach (var mf in filters)
        {
            if (mf.sharedMesh == null) continue;

            // Matrix from mesh-local space → root-object local space
            var toRoot = go.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            var mb = mf.sharedMesh.bounds;
            var mn = mb.min; var mx = mb.max;

            // All 8 corners of the mesh bounds
            Vector3[] corners = {
                new(mn.x,mn.y,mn.z), new(mx.x,mn.y,mn.z),
                new(mn.x,mx.y,mn.z), new(mx.x,mx.y,mn.z),
                new(mn.x,mn.y,mx.z), new(mx.x,mn.y,mx.z),
                new(mn.x,mx.y,mx.z), new(mx.x,mx.y,mx.z),
            };

            foreach (var c in corners)
            {
                var lc = toRoot.MultiplyPoint3x4(c);
                if (first) { local = new Bounds(lc, Vector3.zero); first = false; }
                else local.Encapsulate(lc);
            }
        }

        if (first) return (new Vector3(0f, 0.15f, 0f), new Vector3(0.32f, 0.32f, 0.32f));
        // 95% size — leaves a small gap between stacked boxes so physics
        // doesn't produce depenetration explosions when gravity is enabled.
        return (local.center, local.size * 0.95f);
    }
}
