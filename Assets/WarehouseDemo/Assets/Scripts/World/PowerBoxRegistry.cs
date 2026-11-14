using System.Collections.Generic;
using UnityEngine;

/// Finds every power box on the warehouse poles — "powerBox_06" and "powerBox_07", which hang in
/// pairs on the same pole and count as independent boxes. PowerFailureEvent picks 3 of them at
/// random as repair targets each time it fires. The list is sorted by world position (not
/// scene traversal order) so server and clients pick the exact same boxes from the same indices.
public static class PowerBoxRegistry
{
    public static List<GameObject> GetAll()
    {
        var result = new List<GameObject>();
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            if (go.name == "powerBox_06" || go.name == "powerBox_07")
                result.Add(go);

        result.Sort((a, b) =>
        {
            int cmp = a.transform.position.x.CompareTo(b.transform.position.x);
            return cmp != 0 ? cmp : a.transform.position.z.CompareTo(b.transform.position.z);
        });
        return result;
    }

    /// Picks up to `count` distinct random indices into GetAll()'s result, without
    /// repeats. Same algorithm on server and client given the same Random state won't
    /// matter here — indices are always transmitted explicitly via network payload.
    public static List<int> PickRandomIndices(int poolSize, int count)
    {
        var pool = new List<int>(poolSize);
        for (int i = 0; i < poolSize; i++) pool.Add(i);

        var picked = new List<int>(Mathf.Min(count, poolSize));
        while (picked.Count < count && pool.Count > 0)
        {
            int r = Random.Range(0, pool.Count);
            picked.Add(pool[r]);
            pool.RemoveAt(r);
        }
        return picked;
    }
}
