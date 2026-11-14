using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// Deterministic network ids for scene-placed objects that have no NetworkObject of their own —
/// ladders, pallets, and the boxes stacked on a pallet.
///
/// A box that carries its own NetworkObject does not use this: NGO's NetworkObjectId already
/// means the same object on every peer and is the right answer. But pallet_box_pack_prefab has
/// one NetworkObject on its root and 24 grabbable boxes underneath it, and Ladder.prefab and
/// pallet_boxes_prefab have none at all, so until they gain one this derives the same guarantee
/// from scene data. See BoxPhysicsInteractable.AssignSceneNetworkIds.
///
/// The rule: order every instance by its scene-authored world position, break ties on hierarchy
/// path, and use the resulting index as the id. Both inputs come from the scene asset, which is
/// byte-identical on every peer, so each client independently derives the same numbering without
/// exchanging anything. That is exactly the property the previous scheme lacked — it numbered by
/// Awake() order, which Unity does not define, so the same id could name different objects on the
/// host and on a client.
///
/// Ordering must be computed before anything moves. Callers run it from a
/// RuntimeInitializeOnLoadMethod(AfterSceneLoad) pass, which lands after every Awake but before
/// the first FixedUpdate — so positions are still the authored ones, not whatever independent
/// physics has since drifted them to.
public static class SceneNetIds
{
    // Root sibling order changes when unrelated network/persistent roots are created.
    // Evaluate only on first registration, before the scene box moves.
    public static string BoxHierarchyPath(Transform t)
    {
        if (t.parent != null)
            return BoxHierarchyPath(t.parent) + "/" + t.name.Length + ":" + t.name + "#" + t.GetSiblingIndex();
        var p = t.position;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}|{1}:{2}@{3},{4},{5}", t.gameObject.scene.path, t.name.Length, t.name,
            Quantize(p.x), Quantize(p.y), Quantize(p.z));
    }

    /// Runs a numbering pass now, and again after every scene load.
    ///
    /// [RuntimeInitializeOnLoadMethod] fires once per process, not once per scene, so a pass
    /// hung on it alone numbers whatever was open at startup and never runs again. That is not
    /// a corner case here: NGO scene management is on, so a joining client loads the server's
    /// scene during synchronisation -- destroying every component numbered a moment earlier and
    /// replacing it with an unnumbered one. From then on the client resolved every id to
    /// nothing, which is exactly why a box in a teammate's hands stayed sitting on its shelf
    /// while its carrier saw it in their arms.
    ///
    /// Passes must therefore be idempotent: each one clears its own registry before renumbering,
    /// so a re-run replaces the previous numbering rather than appending to it.
    public static void RunEveryLoad(Action pass)
    {
        if (pass == null) return;
        if (!s_passes.Contains(pass)) s_passes.Add(pass);

        // Named handler, not a lambda, so the -= actually matches: with domain reload turned
        // off in Enter Play Mode Options these statics survive between sessions and a lambda
        // would stack one more subscription per play.
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;

        pass();
    }

    static readonly List<Action> s_passes = new();

    static void OnSceneLoaded(Scene _, LoadSceneMode __)
    {
        for (int i = 0; i < s_passes.Count; i++) s_passes[i]?.Invoke();
    }

    public static void SortDeterministically<T>(List<T> items) where T : Component
    {
        items.Sort(Compare);
    }

    static int Compare<T>(T a, T b) where T : Component
    {
        bool aNull = a == null, bNull = b == null;
        if (aNull || bNull) return aNull == bNull ? 0 : aNull ? 1 : -1;

        Vector3 pa = a.transform.position, pb = b.transform.position;
        int c = Quantize(pa.x).CompareTo(Quantize(pb.x)); if (c != 0) return c;
        c     = Quantize(pa.z).CompareTo(Quantize(pb.z)); if (c != 0) return c;
        c     = Quantize(pa.y).CompareTo(Quantize(pb.y)); if (c != 0) return c;

        // Two props authored at the same spot still need a stable order between them.
        return string.CompareOrdinal(HierarchyPath(a.transform), HierarchyPath(b.transform));
    }

    // Centimetre granularity: coarser than any float noise between two processes deserializing
    // the same scene, finer than the gap between two distinct props.
    static long Quantize(float v) => (long)Mathf.Round(v * 100f);

    /// The scene path of a transform: the same string on every peer, since every part of it comes
    /// from the scene asset. Public because a numbering pass hashes it into an id — see
    /// BoxPhysicsInteractable.AssignSceneNetworkIds.
    ///
    /// Each step carries its sibling index, and that is not decoration. **Names are not unique.**
    /// Unity happily allows a dozen siblings all called the same thing, and this scene uses that:
    /// under one pallet pack sit two dozen boxes named box_grp1, at
    /// `env/shelf_forklift_GRP_01_prefab/pallet_box_pack_prefab (3)/pallet_box_pack/box_Level_01/`.
    /// A path of names alone made every one of them the same string — so as an identity it named a
    /// crowd, not a box, and as a tie-break below it broke nothing.
    ///
    /// The index is safe to hash because child order is authored in the scene file, so both peers
    /// deserialize it identically. It is only read while numbering, which happens at scene load —
    /// before anything reparents a box — and a box that already has an id is never renumbered.
    public static string HierarchyPath(Transform t)
    {
        var sb = new System.Text.StringBuilder(64);
        Append(sb, t);
        return sb.ToString();
    }

    static void Append(System.Text.StringBuilder sb, Transform t)
    {
        if (t.parent != null)
        {
            Append(sb, t.parent);
            sb.Append('/');
        }

        sb.Append(t.name).Append('#').Append(t.GetSiblingIndex());
    }
}
