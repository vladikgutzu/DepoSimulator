using UnityEngine;
using UnityEditor;

public static class RunAddBoxAnchors
{
    const float GripHeightFactor = 0.35f;

    public static void Run()
    {
        int added = 0, skipped = 0;

        var allBoxes = Object.FindObjectsByType<BoxPhysicsInteractable>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (var box in allBoxes)
        {
            var go    = box.gameObject;
            var rends = go.GetComponentsInChildren<Renderer>(false);
            if (rends.Length == 0) { skipped++; continue; }

            Bounds b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);

            if (go.GetComponent<BoxHandAnchors>() == null)
                go.AddComponent<BoxHandAnchors>();

            var anchors = go.GetComponent<BoxHandAnchors>();
            var so      = new SerializedObject(anchors);

            float gripY = b.min.y + b.size.y * GripHeightFactor;

            var rightTarget = EnsureChild(go, "HandTarget_R",
                new Vector3(b.max.x, gripY, b.center.z),
                Quaternion.LookRotation(-go.transform.right, go.transform.up));
            var leftTarget = EnsureChild(go, "HandTarget_L",
                new Vector3(b.min.x, gripY, b.center.z),
                Quaternion.LookRotation(go.transform.right, go.transform.up));

            SetField(so, "_rightHandTarget", rightTarget);
            SetField(so, "_leftHandTarget",  leftTarget);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(go);
            added++;
        }

        Debug.Log($"[BoxHandAnchors] ✓ added={added}, skipped={skipped}");
    }

    static Transform EnsureChild(GameObject parent, string name, Vector3 worldPos, Quaternion worldRot)
    {
        var t = parent.transform.Find(name);
        if (t != null) return t;
        var child = new GameObject(name);
        child.transform.SetParent(parent.transform);
        child.transform.position = worldPos;
        child.transform.rotation = worldRot;
        return child.transform;
    }

    static void SetField(SerializedObject so, string fieldName, Transform value)
    {
        var prop = so.FindProperty(fieldName);
        if (prop != null) prop.objectReferenceValue = value;
    }
}
