using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Adds BoxCollider to every BoxPhysicsInteractable that has no collider.
// Run multiple times — uses EditorPrefs offset to process in batches of 3000.
public static class FixBoxColliders
{
    const int BatchSize = 3000;
    const string OffsetKey = "FixBoxColOffset";

    [MenuItem("Tools/Warehouse Demo/Fix Box Colliders (batch)")]
    public static void Execute()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int batchStart = EditorPrefs.GetInt(OffsetKey, 0);
        var allBpi = Object.FindObjectsOfType<BoxPhysicsInteractable>(true);
        int end  = Mathf.Min(batchStart + BatchSize, allBpi.Length);

        int fixed_ = 0, skipped = 0, noRend = 0;

        for (int i = batchStart; i < end; i++)
        {
            var go = allBpi[i].gameObject;

            // Root-only check — fastest possible, avoids hierarchy traversal
            if (go.GetComponent<Collider>() != null) { skipped++; continue; }

            var rend = go.GetComponent<MeshRenderer>();
            if (rend == null) { noRend++; continue; }

            AddBoxCollider(go, rend.bounds);
            fixed_++;
        }

        // Advance (or reset) batch pointer
        int nextOffset = end >= allBpi.Length ? 0 : end;
        EditorPrefs.SetInt(OffsetKey, nextOffset);

        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"[FixBoxColliders] Batch [{batchStart}..{end-1}] of {allBpi.Length}  " +
                  $"fixed={fixed_}  skipped={skipped}  noRend={noRend}  " +
                  $"nextBatch={nextOffset}  time={sw.Elapsed.TotalSeconds:F1}s  " +
                  $"{(nextOffset == 0 ? "ALL DONE — save scene now" : $"{allBpi.Length - nextOffset} remaining")}");
    }

    static void AddBoxCollider(GameObject go, Bounds worldBounds)
    {
        Vector3 center    = go.transform.InverseTransformPoint(worldBounds.center);
        Vector3 ls        = go.transform.lossyScale;
        Vector3 worldSize = worldBounds.size;
        Vector3 size = new Vector3(
            ls.x != 0f ? worldSize.x / Mathf.Abs(ls.x) : worldSize.x,
            ls.y != 0f ? worldSize.y / Mathf.Abs(ls.y) : worldSize.y,
            ls.z != 0f ? worldSize.z / Mathf.Abs(ls.z) : worldSize.z
        );
        var col    = go.AddComponent<BoxCollider>();
        col.center = center;
        col.size   = size;
    }
}
