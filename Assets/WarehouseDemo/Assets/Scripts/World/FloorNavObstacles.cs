using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// Makes the raiders walk around pallets and boxes standing on the floor instead of through them.
///
/// The raiders are NavMeshAgents, and an agent knows nothing about colliders: it goes wherever the
/// baked mesh says the floor is free. Nothing that moves is baked — the bake only reads the
/// navMeshObjects layer — so every pallet the forklift can carry, and every box knocked off one,
/// was open floor as far as a raider could tell. Static props are covered by obstacles saved in the
/// scene (Tools/Warehouse/Add Zombie Nav Obstacles); these cannot be, because they move.
///
/// So each gets a carving NavMeshObstacle, switched on only while it stands on the floor and is
/// still:
///   • a pallet — one obstacle for the whole stack, the pallet's footprint and the height of what
///     is on it. The boxes on its deck are then left alone;
///   • a loose box lying directly on the floor.
/// Thousands of boxes and pallets sit on racks, well above anything an agent walks on; an obstacle
/// there would carve nothing and still cost a rebuild every time the stack was disturbed.
///
/// Server (or single player) only: the agents only run there.
public class FloorNavObstacles : MonoBehaviour
{
    /// Standing on the floor: the underside this close to it. A box on a pallet's deck sits a
    /// pallet's height up (0.15) and is covered by the pallet's obstacle.
    const float FloorHeight = 0.1f;

    /// Moving slower than this counts as standing still. Something carried on the forks or
    /// sliding across the floor is avoided by the agents' own steering, not carved every frame.
    const float StillSpeed = 0.15f;

    /// How tall a pallet's obstacle is. Only has to reach the height an agent stands at.
    const float PalletObstacleHeight = 1.2f;

    const float Interval     = 0.5f;   // how often a slice of the list is looked at
    const float RefreshEvery = 10f;    // how often the scene is searched for new ones

    struct Entry
    {
        public Component Owner;        // the box or the pallet
        public Collider  Footprint;    // what decides whether it is on the floor
        public bool      IsPallet;
    }

    readonly List<Entry> _entries = new();
    readonly Dictionary<Component, NavMeshObstacle> _obstacles = new();
    float _nextTick, _nextRefresh;
    int _cursor;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        // Every load, not once per process: NGO reloads the scene under a joining client, and the
        // menu hands over to the warehouse by loading it.
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode _)
    {
        if (FindAnyObjectByType<FloorNavObstacles>() != null) return;
        if (FindAnyObjectByType<BoxPhysicsInteractable>() == null) return;   // not a warehouse

        var go = new GameObject("~FloorNavObstacles");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<FloorNavObstacles>();
    }

    static bool RunsAgents
    {
        get
        {
            var nm = NetworkManager.Singleton;
            return nm == null || !nm.IsListening || nm.IsServer;
        }
    }

    void Update()
    {
        if (Time.time < _nextTick) return;
        _nextTick = Time.time + Interval;

        if (!RunsAgents) return;

        if (Time.time >= _nextRefresh)
        {
            _nextRefresh = Time.time + RefreshEvery;
            Collect();
        }

        // A tenth of the list per tick: everything every five seconds, and anything that has just
        // come to rest within half a second on average.
        int count = Mathf.Max(1, _entries.Count / 10);
        for (int i = 0; i < count && _entries.Count > 0; i++)
        {
            _cursor = (_cursor + 1) % _entries.Count;
            Refresh(_entries[_cursor]);
        }
    }

    void Collect()
    {
        _entries.Clear();

        foreach (var pallet in FindObjectsByType<PalletBehavior>(FindObjectsSortMode.None))
        {
            var deck = DeckCollider(pallet);
            if (deck != null) _entries.Add(new Entry { Owner = pallet, Footprint = deck, IsPallet = true });
        }

        foreach (var box in FindObjectsByType<BoxPhysicsInteractable>(FindObjectsSortMode.None))
        {
            var col = box.GetComponent<Collider>();
            if (col != null) _entries.Add(new Entry { Owner = box, Footprint = col });
        }
    }

    /// The pallet's own deck — the one collider under it that is not a box.
    static Collider DeckCollider(PalletBehavior pallet)
    {
        foreach (var col in pallet.GetComponentsInChildren<Collider>())
        {
            if (col.isTrigger) continue;
            if (col.GetComponentInParent<BoxPhysicsInteractable>() != null) continue;
            return col;
        }
        return null;
    }

    void Refresh(Entry e)
    {
        if (e.Owner == null) return;

        bool want = StandsOnFloor(e);
        _obstacles.TryGetValue(e.Owner, out var obstacle);

        if (!want)
        {
            if (obstacle != null && obstacle.enabled) obstacle.enabled = false;
            return;
        }

        if (obstacle == null)
        {
            // A scene-authored obstacle, or one left by a previous runtime installer during an
            // additive reload, may already be present even though this instance's dictionary has
            // not seen it yet. Adding a second NavMeshObstacle can return null; dereferencing that
            // result caused a repeating exception every update tick.
            obstacle = e.Owner.GetComponent<NavMeshObstacle>();
            if (obstacle == null && e.Owner != null)
                obstacle = e.Owner.gameObject.AddComponent<NavMeshObstacle>();
            if (obstacle == null) return;

            obstacle.shape               = NavMeshObstacleShape.Box;
            obstacle.carving             = true;
            obstacle.carveOnlyStationary = true;
            Fit(e, obstacle);
            _obstacles[e.Owner] = obstacle;
        }

        if (!obstacle.enabled) obstacle.enabled = true;
    }

    static bool StandsOnFloor(Entry e)
    {
        var col = e.Footprint;
        if (col == null || !col.enabled) return false;
        if (col.bounds.min.y > FloorHeight) return false;

        if (!e.IsPallet && BoxPhysicsInteractable.GrabbedBox == (BoxPhysicsInteractable)e.Owner) return false;

        // Carried: parented to a raider, a player or a forklift.
        var parent = e.Owner.transform.parent;
        if (parent != null && (parent.GetComponentInParent<ThiefController>() != null
                            || parent.GetComponentInParent<CharacterController>() != null
                            || parent.GetComponentInParent<ReachTruckController>() != null))
            return false;

        var rb = e.Owner.GetComponent<Rigidbody>();
        return rb == null || rb.isKinematic || rb.linearVelocity.sqrMagnitude < StillSpeed * StillSpeed;
    }

    /// Box shape in the owner's own space: the collider for a box, the deck's footprint raised to
    /// the height of a stack for a pallet.
    static void Fit(Entry e, NavMeshObstacle obstacle)
    {
        var t = e.Owner.transform;

        var bc = e.Footprint as BoxCollider;
        if (!e.IsPallet && bc != null && bc.transform == t)
        {
            obstacle.center = bc.center;
            obstacle.size   = bc.size;
            return;
        }

        // World-aligned bounds into the owner's space. Pallets stand upright, so this is exact
        // enough for a footprint.
        var b = e.Footprint.bounds;
        var s = t.lossyScale;
        var size = new Vector3(b.size.x / Mathf.Max(0.001f, Mathf.Abs(s.x)),
                               b.size.y / Mathf.Max(0.001f, Mathf.Abs(s.y)),
                               b.size.z / Mathf.Max(0.001f, Mathf.Abs(s.z)));
        var center = t.InverseTransformPoint(b.center);

        if (e.IsPallet)
        {
            // Swap to the pallet's own axes: its footprint is x/z in its space whichever way it
            // is turned about Y.
            var local = Quaternion.Inverse(t.rotation) * new Vector3(b.size.x, 0f, b.size.z);
            size = new Vector3(Mathf.Abs(local.x), PalletObstacleHeight, Mathf.Abs(local.z));
            // Footprint of a turned pallet read off world-aligned bounds is a little generous;
            // the deck is almost square, so that is a few centimetres at most.
            center = new Vector3(center.x, center.y - b.extents.y / Mathf.Max(0.001f, Mathf.Abs(s.y))
                                                    + PalletObstacleHeight * 0.5f, center.z);
        }

        obstacle.center = center;
        obstacle.size   = size;
    }
}
