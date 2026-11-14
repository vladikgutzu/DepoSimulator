using System.Collections.Generic;
using UnityEngine;

/// A blunt weapon lying on the warehouse floor. What it is called, how it hits and how it is
/// held all come from its MeleeWeaponDefinition asset; this only handles the pickup itself. Walk up, press [E], and it replaces your
/// bare hands for the rest of the shift. The raiders are zombies and bring no weapons, so
/// these are laid out at the start of the shift by GameEventManager (or placed by hand in a
/// scene); ThiefController still drops one if a raid is hand-authored to carry it.
///
/// Built to the same shape as ToolkitItem: assignable prefabs with a primitive fallback, so
/// the mechanic works in a scene where no art has been hooked up yet.
[RequireComponent(typeof(Collider))]
public class MeleeWeaponItem : MonoBehaviour, IInteractable, ICanInteractCheck
{
    [SerializeField] MeleeWeaponKind _kind = MeleeWeaponKind.Bat;

    public MeleeWeaponKind Kind => _kind;

    // ── Network identity ──────────────────────────────────────────────────────
    //
    // Pickups are not network-spawned: every peer has the same ones, and drops are broadcast. So
    // the id only has to be a number every machine agrees on. Same trick the boxes and pallets use.
    //
    // There are two sources of pickups and they are numbered differently:
    //
    //  • Placed in the scene by hand. Every peer loads the same scene, so they are numbered by a
    //    pass that sorts them by position — identical everywhere without a word over the wire.
    //    This is the normal case: where a crowbar lies is a level decision.
    //
    //  • Dropped during play. The server names those (NetworkWeaponManager starts at 1,000,000,
    //    clear of anything the scene will reach) and broadcasts the id with the drop.
    public int NetWeaponId { get; private set; }

    /// Scene pickups start here. Room below for anything numbered before them, room above for the
    /// server's drops.
    const int SceneIdBase = 1000;

    static int s_nextId = SceneIdBase;
    static readonly Dictionary<int, MeleeWeaponItem> s_byId = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetIds() { s_nextId = SceneIdBase; s_byId.Clear(); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void InstallSceneIdPass() => SceneNetIds.RunEveryLoad(AssignSceneNetworkIds);

    /// Numbers the pickups the level author placed.
    ///
    /// Runs on every scene load, not once per process: NGO reloads the scene under a joining
    /// client, and a pass that only ever ran at startup would leave his pickups unnumbered — the
    /// same trap the boxes were caught by.
    ///
    /// A weapon that already has an id was handed one by the server as a drop, and is left alone.
    static void AssignSceneNetworkIds()
    {
        var scene = new List<MeleeWeaponItem>();

        foreach (var item in FindObjectsByType<MeleeWeaponItem>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (item == null || item.NetWeaponId != 0) continue;
            scene.Add(item);
        }

        // The pass lands after every Awake but before anything has moved, which is what makes the
        // position sort agree between two machines.
        SceneNetIds.SortDeterministically(scene);

        for (int i = 0; i < scene.Count; i++)
        {
            scene[i].NetWeaponId = SceneIdBase + i;
            s_byId[scene[i].NetWeaponId] = scene[i];
        }

        if (scene.Count > 0) s_nextId = Mathf.Max(s_nextId, SceneIdBase + scene.Count);
    }

    /// Whether the level already supplies pickups, so nothing needs laying out at shift start.
    public static bool AnyInScene()
    {
        foreach (var item in FindObjectsByType<MeleeWeaponItem>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (item != null) return true;

        return false;
    }

    /// Find a pickup by the id every peer agrees on. Null when it has already been taken.
    public static MeleeWeaponItem FindByNetId(int id) =>
        s_byId.TryGetValue(id, out var item) && item != null ? item : null;

    void OnDestroy()
    {
        if (s_byId.TryGetValue(NetWeaponId, out var mine) && mine == this) s_byId.Remove(NetWeaponId);
    }

    // ── Interaction ───────────────────────────────────────────────────────────

    public string InteractPrompt => $"Take {DisplayName(_kind).ToLowerInvariant()} {InputHints.E}";

    public bool CanInteract(GameObject interactor)
    {
        if (MeleeCombat.Instance == null) return false;

        // Same bar as swinging it: a bat is picked up with the hands, off the floor, by someone
        // who is standing on it rather than on a ladder rung. PlayerBusyState covers the box in
        // his arms, the ladder, the toolkit, the vehicles and being down — see MeleeCombat.
        if (PlayerBusyState.IsBusy(interactor != null ? interactor : gameObject)) return false;

        return MeleeCombat.Instance.CurrentWeapon != _kind;
    }

    public void Interact(GameObject interactor)
    {
        if (MeleeCombat.Instance == null) return;

        // Multiplayer: ask, do not take. Two players reaching for the same bat both destroyed
        // their own copy of it and walked away armed — the pickup is local, so nothing said no.
        // The server hands it to whoever asked first and tells everyone else it is gone.
        if (NetworkWeaponManager.IsNetworked)
        {
            NetworkWeaponManager.Instance.RequestPickupServerRpc(NetWeaponId);
            return;
        }

        MeleeCombat.Instance.EquipWeapon(_kind, BuildVisual(_kind));
        Destroy(gameObject);

        // The drop key is named on pickup because nothing else in the world advertises it, and a
        // player who does not know it is stuck holding the thing for the rest of the shift.
        HUDController.Instance?.AddNotification(
            $"{DisplayName(_kind)} picked up",
            $"{HintFor(_kind)}\n{InputHints.G} to put it down",
            HUDController.NotifType.Success);
    }

    // Names and hints live on the weapon asset. Falling back to the enum name keeps a pickup
    // readable even for a kind nobody has authored yet.
    static string DisplayName(MeleeWeaponKind kind)
    {
        var def = MeleeWeaponRegistry.Get(kind);
        return def != null && !string.IsNullOrWhiteSpace(def.DisplayName)
            ? def.DisplayName
            : kind.ToString();
    }

    static string HintFor(MeleeWeaponKind kind)
    {
        var def = MeleeWeaponRegistry.Get(kind);
        return def != null && !string.IsNullOrWhiteSpace(def.PickupHint)
            ? def.PickupHint
            : "Left click to swing";
    }

    // ── Spawning ──────────────────────────────────────────────────────────────

    /// Places a weapon pickup lying on the floor. With no position given, a clear patch of floor is
    /// chosen; a given position is a point on or just above the floor.
    ///
    /// It used to be stood up with a fixed Euler(90, y, 0), which points +Z at the ground. Every
    /// model hangs from its grip, so the whole shaft went through the floor — and the bat, whose
    /// shaft runs down -Y instead, stood on end. It is now laid down by its own collider: longest
    /// side horizontal, thinnest side up, bottom face on the floor.
    public static GameObject Spawn(MeleeWeaponKind kind, Vector3? atPos = null, int forcedNetId = -1)
    {
        Vector3 pos = atPos ?? ChooseFloorPosition();

        var go = BuildVisual(kind);
        go.name = $"MeleeWeapon_{kind}";
        WeaponVisualPlacer.EnsurePhysics(go);

        go.transform.rotation = LyingRotation(go, Random.Range(0f, 360f));
        go.transform.position = pos;
        RestOnFloor(go);

        // Already at rest; do not let it rattle about while the shift loads.
        var rb = go.GetComponent<Rigidbody>();
        if (rb != null) rb.Sleep();

        MakePickup(go, kind, forcedNetId);
        return go;
    }

    /// The same weapon, off the hand and into the world: it keeps its model and its pose and falls
    /// from where it was held. This is what a drop does — see MeleeCombat.ReleaseHeld.
    public static MeleeWeaponItem Release(GameObject visual, MeleeWeaponKind kind, Vector3 velocity,
                                          int forcedNetId = -1, Collider[] ignore = null)
    {
        if (visual == null) return null;

        visual.name = $"MeleeWeapon_{kind}";
        WeaponVisualPlacer.Release(visual, velocity, ignore);
        return MakePickup(visual, kind, forcedNetId);
    }

    /// A freshly built model let go of at a hand pose — what the other peers see when a teammate
    /// drops a weapon. Their copy of his puppet's weapon is a separate object, so they rebuild it
    /// where his hand was and give it the same push.
    public static MeleeWeaponItem SpawnReleased(MeleeWeaponKind kind, Vector3 position, Quaternion rotation,
                                                Vector3 velocity, int forcedNetId, Collider[] ignore = null)
    {
        var go = BuildVisual(kind);
        go.transform.SetPositionAndRotation(position, rotation);
        return Release(go, kind, velocity, forcedNetId, ignore);
    }

    /// Makes an existing model a pickup: the component, and an id every peer agrees on.
    static MeleeWeaponItem MakePickup(GameObject go, MeleeWeaponKind kind, int forcedNetId)
    {
        // The collider has to exist first — RequireComponent would otherwise hang a unit cube on
        // the root. EnsurePhysics puts a fitted one there.
        WeaponVisualPlacer.EnsurePhysics(go);

        // Not ??: a missing component is Unity's fake null in the editor, which ?? takes for real.
        var item = go.GetComponent<MeleeWeaponItem>();
        if (item == null) item = go.AddComponent<MeleeWeaponItem>();
        item._kind = kind;

        // Explicit ids let a drop be broadcast with the id it must have on every peer; without one
        // the server would be naming a pickup the clients number differently.
        item.NetWeaponId = forcedNetId >= 0 ? forcedNetId : s_nextId++;
        if (forcedNetId >= s_nextId) s_nextId = forcedNetId + 1;
        s_byId[item.NetWeaponId] = item;

        return item;
    }

    /// Rotation that lays a weapon flat: its longest side horizontal along `yaw`, its thinnest
    /// side pointing up. Read off the root collider, so it is right for any model orientation.
    static Quaternion LyingRotation(GameObject go, float yaw)
    {
        var box = go.GetComponent<BoxCollider>();
        Vector3 size = box != null ? box.size : new Vector3(0.1f, 0.1f, 1f);

        Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
        float[]   len  = { size.x, size.y, size.z };

        int longest = 0, thinnest = 0;
        for (int i = 1; i < 3; i++)
        {
            if (len[i] > len[longest])  longest  = i;
            if (len[i] < len[thinnest]) thinnest = i;
        }
        if (thinnest == longest) thinnest = (longest + 1) % 3;

        var local = Quaternion.LookRotation(axes[longest], axes[thinnest]);
        var world = Quaternion.LookRotation(Quaternion.Euler(0f, yaw, 0f) * Vector3.forward, Vector3.up);
        return world * Quaternion.Inverse(local);
    }

    /// Drops the model straight down onto whatever is under it, bottom face touching.
    static void RestOnFloor(GameObject go)
    {
        var col = go.GetComponent<Collider>();
        if (col == null) return;

        Physics.SyncTransforms();
        var b = col.bounds;

        var origin = new Vector3(b.center.x, b.max.y + 0.5f, b.center.z);
        var hits   = Physics.RaycastAll(origin, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore);

        float floorY = float.NegativeInfinity;
        foreach (var h in hits)
        {
            if (h.collider == col || h.collider.transform.IsChildOf(go.transform)) continue;
            // Only the ground counts: not the raider who dropped it (his capsule has no body), not
            // a player, not a box that will itself be knocked about.
            if (h.collider.attachedRigidbody != null) continue;
            if (h.collider.GetComponentInParent<CharacterController>() != null) continue;
            if (h.collider.GetComponentInParent<ThiefController>() != null) continue;
            if (h.point.y > floorY) floorY = h.point.y;
        }
        if (float.IsNegativeInfinity(floorY)) return;

        go.transform.position += Vector3.up * (floorY + 0.005f - b.min.y);
    }

    /// Builds just the model — used both for the floor pickup and for the copy that gets
    /// parented to the player's hand on equip.
    public static GameObject BuildVisual(MeleeWeaponKind kind)
    {
        var def    = MeleeWeaponRegistry.Get(kind);
        var prefab = def != null ? def.Prefab : null;

        if (prefab != null)
        {
            var fromPrefab = Object.Instantiate(prefab);
            fromPrefab.name = $"Weapon_{kind}";
            return fromPrefab;
        }

        // Fallback: an empty root with the shape as a child, so the root sits at the grip and
        // the haft extends forward along +Z from it. Everything downstream assumes that —
        // MeleeSwingArm's off-hand grip is an offset along this same +Z.
        var root = new GameObject($"Weapon_{kind}");

        var wood  = new Color(0.45f, 0.28f, 0.14f);
        var steel = new Color(0.62f, 0.64f, 0.68f);

        switch (kind)
        {
            case MeleeWeaponKind.RollingPin:
                AddShaft(root, 0.22f, 0.045f, wood);
                break;

            case MeleeWeaponKind.PipeWrench:
                AddShaft(root, 0.22f, 0.03f, steel);
                AddBlock(root, new Vector3(0.09f, 0.05f, 0.09f),
                         new Vector3(0f, 0f, 0.42f), Vector3.zero, steel);
                break;

            case MeleeWeaponKind.Plank:
                AddBlock(root, new Vector3(0.14f, 0.03f, 1.0f),
                         new Vector3(0f, 0f, 0.5f), Vector3.zero, wood);
                break;

            case MeleeWeaponKind.Crowbar:
                AddShaft(root, 0.42f, 0.028f, steel);
                // The bent claw, angled off the far end so it reads as a crowbar in silhouette
                // rather than as a plain rod.
                AddBlock(root, new Vector3(0.03f, 0.10f, 0.03f),
                         new Vector3(0f, 0.03f, 0.86f), new Vector3(35f, 0f, 0f), steel);
                break;

            default:   // Bat
                AddShaft(root, 0.32f, 0.055f, wood);
                break;
        }

        return root;
    }

    /// A cylinder laid along +Z. halfLength is Unity's cylinder half-height, so the shaft runs
    /// from the grip out to 2*halfLength.
    static void AddShaft(GameObject root, float halfLength, float radius, Color color)
    {
        var shape = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Object.Destroy(shape.GetComponent<Collider>());
        shape.transform.SetParent(root.transform, false);

        // Unity cylinders are 2 units tall along Y — rotate it to point forward.
        shape.transform.localScale    = new Vector3(radius, halfLength, radius);
        shape.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        shape.transform.localPosition = new Vector3(0f, 0f, halfLength);
        Tint(shape, color);
    }

    static void AddBlock(GameObject root, Vector3 size, Vector3 pos, Vector3 euler, Color color)
    {
        var shape = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.Destroy(shape.GetComponent<Collider>());
        shape.transform.SetParent(root.transform, false);

        shape.transform.localScale    = size;
        shape.transform.localPosition = pos;
        shape.transform.localRotation = Quaternion.Euler(euler);
        Tint(shape, color);
    }

    static void Tint(GameObject go, Color color)
    {
        var rend = go.GetComponent<Renderer>();
        if (rend == null) return;
        rend.material.color = color;
        rend.material.SetColor("_BaseColor", color);
    }

    // Samples the floor for a clear spot, same approach as ToolkitItem.ChooseSpawnPosition:
    // raycast down and reject anything that landed on top of a shelf or overlaps a prop.
    static Vector3 ChooseFloorPosition()
    {
        const int   attempts  = 30;
        const float floorMaxY = 0.5f;

        for (int i = 0; i < attempts; i++)
        {
            var origin = WarehouseBounds.RandomPoint(6f);

            if (!Physics.Raycast(origin, Vector3.down, out var hit, 12f, ~0, QueryTriggerInteraction.Ignore))
                continue;
            if (hit.point.y > floorMaxY) continue;

            Vector3 candidate = hit.point + Vector3.up * 0.12f;

            bool blocked = false;
            foreach (var col in Physics.OverlapSphere(candidate, 0.4f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (col == hit.collider) continue;   // the floor itself
                blocked = true;
                break;
            }
            if (blocked) continue;

            return candidate;
        }

        return new Vector3(2f, 0.2f, -8f);
    }
}
