using System.Collections.Generic;
using UnityEngine;

/// The warehouse scene has thousands of BoxPhysicsInteractable instances (mostly idle stock
/// sitting on shelves) — letting Unity dispatch Update/LateUpdate to every single one of them,
/// every frame, on every connected client, is real dispatch overhead for objects that are doing
/// nothing 99.9% of the time. Only a handful of boxes are ever actually grabbed, following a
/// pallet/cart, or being remote-smoothed at once; BoxPhysicsInteractable registers/unregisters
/// itself here exactly when it enters/leaves one of those states, and this is the only thing that
/// still runs a real Update/LateUpdate — over that small active set, not all of them.
///
/// OnCollisionEnter (ragdoll-on-impact) is untouched by this — it stays a normal per-instance
/// Unity message on BoxPhysicsInteractable itself, so idle boxes can still be hit/knocked over.
/// Auto-creates itself (no manual scene setup required, so it can't be silently missing).
[DefaultExecutionOrder(100)]
public class BoxPhysicsTickDriver : MonoBehaviour
{
    static BoxPhysicsTickDriver s_instance;
    static readonly HashSet<BoxPhysicsInteractable> s_active = new();

    // Reused snapshot buffer — registering/unregistering a box from within its own TickUpdate/
    // TickLateUpdate (e.g. Release() mid-tick) would otherwise mutate s_active while this class
    // is iterating it.
    static BoxPhysicsInteractable[] s_snapshot = new BoxPhysicsInteractable[16];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureExists()
    {
        if (s_instance != null) return;
        var go = new GameObject("BoxPhysicsTickDriver");
        Object.DontDestroyOnLoad(go);
        s_instance = go.AddComponent<BoxPhysicsTickDriver>();
    }

    public static void Register(BoxPhysicsInteractable box)   => s_active.Add(box);
    public static void Unregister(BoxPhysicsInteractable box) => s_active.Remove(box);

    static int Snapshot()
    {
        if (s_snapshot.Length < s_active.Count)
            s_snapshot = new BoxPhysicsInteractable[Mathf.NextPowerOfTwo(s_active.Count)];
        s_active.CopyTo(s_snapshot);
        return s_active.Count;
    }

    void Update()
    {
        int count = Snapshot();
        for (int i = 0; i < count; i++)
            s_snapshot[i]?.TickUpdate();
    }

    void LateUpdate()
    {
        int count = Snapshot();
        for (int i = 0; i < count; i++)
            s_snapshot[i]?.TickLateUpdate();
    }
}
