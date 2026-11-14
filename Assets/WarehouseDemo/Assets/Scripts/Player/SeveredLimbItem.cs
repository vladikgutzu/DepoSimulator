using System.Collections.Generic;
using UnityEngine;

/// The arm or leg that came off: picked up off the floor, carried in the other hand, and refitted
/// once the player also has a toolkit.
///
/// It is the player's own limb, not a prop that stands for one — BotDamageVisual.BuildSeveredLimb
/// copies the actual meshes out of the body at the moment of the cut. So the thing on the floor is
/// recognisably *his* arm, in his materials, in the pose it was in when it went, and he walks the
/// warehouse holding it.
///
/// This is what turned the repair from a button into an errand. It used to be mended by holding E
/// while facing nothing in particular, anywhere in the building. Now there are two things to find
/// and they are in different places: the limb, which is where you were standing when you lost it,
/// and the toolkit. Carrying the limb visibly is the part that makes the trip read — a man walking
/// to a toolbox with his own arm in his hand is telling the rest of the shift what happened without
/// a word of UI.
///
/// Which hand: the left, if he still has one, so the right stays free for a weapon. If the left is
/// the arm he lost, the right takes it instead.
///
/// **Any left arm fits any robot.** These are machines, and a limb on the floor is a spare part
/// rather than a personal effect: whoever is missing that part can carry it to a toolkit and bolt
/// it on. Two players who both lost a left arm can simply swap — the first to find one uses it, and
/// the other goes looking for the one still lying about. The only thing that does not transfer is
/// the paint: the limb keeps the livery of the body it was cut from, so a violet Dispatcher wearing
/// a Picker's arm walks around visibly wearing a Picker's arm. That is the point rather than a
/// side-effect — it is how the shift can see who patched himself up out of whom.
[RequireComponent(typeof(Collider))]
public class SeveredLimbItem : MonoBehaviour, IHoldInteractable, ICanInteractCheck
{
    /// Everything currently on the floor or in a hand — mine and every team-mate's, because a
    /// session puts a copy of everybody's limb on everybody's machine (see
    /// NetworkPlayer.DropLimbClientRpc). Which is why nearly everything below is asked "whose".
    static readonly List<SeveredLimbItem> Dropped = new();

    /// The limb in the player's hand right now, if any. One at a time: he has one spare hand.
    public static SeveredLimbItem Carried { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetStatics() { Dropped.Clear(); Carried = null; }

    BodyPart  _part;
    Collider  _collider;
    Rigidbody _body;

    /// The role whose livery this limb is painted in — the body it was cut from, not the body it
    /// ends up on. Replicated as a role rather than as a material because the liveries are per-role
    /// assets every peer already has, so three bits repaint the arm identically everywhere.
    ///
    /// A limb cut off a body that was *already* wearing a graft carries that graft's livery on:
    /// the arm stays the colour it has been since the first time it changed hands.
    PlayerRoleType _livery = PlayerRoleType.None;
    uint _networkVersion;

    /// Whose arm this is, or null for my own.
    ///
    /// A limb is not a NetworkObject — it is built out of the body it was cut from, at the moment
    /// of the cut, so there is no prefab to spawn and every machine makes its own copy. That works,
    /// but it leaves every copy looking identical to every other, and the list above holds all of
    /// them at once. Without a name on each one, two players losing a left arm is a coin toss over
    /// which of the two `For` hands back — and every message about a limb (picked up, put down,
    /// used up) has to name which of the two it is about.
    ///
    /// It is no longer a permission. Anybody missing that part may take it; see `Fits`. What the
    /// name still decides is whose livery the limb wears, whose copy the network messages move,
    /// and what the prompt says it is.
    ///
    /// Null rather than "the local NetworkPlayer" on purpose: solo has no NetworkPlayer at all, and
    /// mine has to mean mine in both.
    NetworkPlayer _owner;

    /// Recorded rather than derived from `_owner == null`, and that is not belt and braces.
    /// A NetworkPlayer that has been destroyed — the player left, the session ended — compares
    /// equal to null through Unity's overload, so a departed team-mate's arm would quietly
    /// re-label itself as mine, become interactable, and offer to be bolted onto my body.
    bool _mine = true;

    /// The connection this was cut off, frozen at the moment of the cut.
    ///
    /// The address every message about this limb uses, and deliberately a number rather than the
    /// NetworkPlayer above: that object dies when its player leaves, and a limb addressed by a dead
    /// object is one nothing can ever say anything about again. The number outlives him.
    ulong _ownerId;

    /// True once the body it came off is no longer in the session — the player left, or respawned
    /// into a fresh one.
    ///
    /// It stops being his arm and becomes a spare part: nobody's, still perfectly good, still
    /// fitting anybody missing that side. Part of the address rather than a flag on the side, so a
    /// player who reconnects and loses the same arm again cannot be confused with the one he left
    /// behind — the old one is orphaned, the new one is not, and the two never answer to the same
    /// message.
    bool _orphan;

    /// Whose limb this is, or null when it is the local player's own — or when nobody's, see
    /// `_orphan`.
    public NetworkPlayer Owner => _owner;

    /// True when the body this came off has left the session. It is a spare part now.
    public bool IsOrphan => _orphan;

    /// The connection this was cut off. Every network message about a limb is addressed by this,
    /// plus the part, plus whether it has been orphaned.
    public ulong OwnerId => _ownerId;

    /// The local player's connection, or zero in solo, where there is none and none is needed.
    static ulong LocalId =>
        NetworkPlayer.Local != null ? NetworkPlayer.Local.OwnerClientId : 0UL;

    /// True for the limb the local player actually lost, as opposed to one cut off a team-mate.
    public bool IsMine => _mine;

    /// Which limb this is. Read by the repair so it mends the part the player actually went and
    /// fetched, rather than whichever injury happens to sort worst.
    public BodyPart Part => _part;

    /// The livery this limb is painted in. Goes onto the body that refits it.
    public PlayerRoleType Livery => _livery;
    public uint NetworkVersion => _networkVersion;

    /// True when the local player is missing exactly this part, and so can use this limb.
    ///
    /// This is the whole permission check now, and it deliberately says nothing about whose limb
    /// it is. What used to be here — mine and only mine — is what made a team-mate's arm scenery.
    public bool Fits
    {
        get
        {
            var injuries = InjurySystem.Instance;
            return injuries != null && injuries.IsSevered(_part);
        }
    }

    /// True while this one is in a hand rather than on the floor.
    public bool InHand => Carried == this;

    /// Drops a built limb on the floor as a findable thing.
    ///
    /// Thrown rather than placed. A limb that appears exactly where it was cut is standing inside
    /// the player, and the first thing the character controller does is shove it somewhere
    /// arbitrary; a small push and a fall settle it a step or two away, which reads as it having
    /// come off and landed.
    public static SeveredLimbItem Attach(GameObject limb, BodyPart part, NetworkPlayer owner = null,
                                         PlayerRoleType livery = PlayerRoleType.None)
    {
        if (limb == null) return null;

        var bounds = Measure(limb);

        var box = limb.AddComponent<BoxCollider>();
        box.center = bounds.center;
        // A hand-sized collider is a hard thing to put a crosshair on across a warehouse floor, and
        // this is meant to be picked up, not hunted with a magnifying glass.
        box.size = Vector3.Max(bounds.size, new Vector3(0.25f, 0.25f, 0.25f));

        var body = limb.AddComponent<Rigidbody>();
        body.mass                   = 4f;
        body.linearDamping          = 0.4f;
        body.angularDamping         = 1.5f;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        Vector3 away = Random.insideUnitSphere;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = Vector3.forward;

        body.linearVelocity  = away.normalized * 2.2f + Vector3.up * 1.4f;
        body.angularVelocity = Random.insideUnitSphere * 4f;

        var item = limb.AddComponent<SeveredLimbItem>();
        item._part     = part;
        item._body     = body;
        item._collider = box;
        item._owner    = owner;
        item._mine     = owner == null;
        item._ownerId  = owner != null ? owner.OwnerClientId : LocalId;
        item._livery   = livery;

        Dropped.Add(item);
        return item;
    }

    /// The local player's own limb for this part, wherever it is.
    public static SeveredLimbItem For(BodyPart part) => Find(LocalId, part, orphan: false);

    /// One limb, by the address every machine agrees on.
    ///
    /// The connection it was cut off, the part, and whether its owner has since gone. All three
    /// earn their place: two players can lose a left arm, and one player can lose a left arm, leave
    /// it behind on a respawn, and lose the replacement later.
    public static SeveredLimbItem Find(ulong ownerId, BodyPart part, bool orphan)
    {
        foreach (var item in Dropped)
            if (item != null && item._part == part && item._orphan == orphan
                && item._ownerId == ownerId)
                return item;

        return null;
    }

    /// Applies the canonical state held by CoopGameManager. Safe to call repeatedly: it creates a
    /// visual only when this peer does not have one yet, then reconciles identity and presentation.
    public static void ApplyNetworkState(NetworkLimbState state)
    {
        var part = (BodyPart)state.Part;
        var item = Find(state.OwnerId, part, state.Orphan);

        // Orphaning changes one component of the address. Reuse the existing visual rather than
        // spawning a second copy when the owner's disconnect update arrives.
        if (item == null)
        {
            foreach (var candidate in Dropped)
            {
                if (candidate == null || candidate._ownerId != state.OwnerId ||
                    candidate._part != part) continue;
                item = candidate;
                break;
            }
        }

        NetworkPlayer owner = PlayerByClient(state.OwnerId);
        if (item == null)
        {
            // Prefer the original body, then the same livery, then any robot rig. Late join after
            // the owner left still needs a mesh to represent the persistent spare part.
            NetworkPlayer source = owner;
            if (source == null)
            {
                foreach (var player in NetworkPlayer.All)
                    if (player != null && (int)player.RoleType == state.Livery) { source = player; break; }
            }
            if (source == null)
            {
                foreach (var player in NetworkPlayer.All)
                    if (player != null) { source = player; break; }
            }
            if (source == null) return; // CoopGameManager retries after player visuals spawn.

            var visual = source.transform.Find("CharacterVisual");
            var body = visual != null ? visual.gameObject : null;
            var limb = BotDamageVisual.Ensure(body)?.BuildSeveredLimb(part);
            if (limb == null) return;

            item = Attach(limb, part, owner, (PlayerRoleType)state.Livery);
            if (item == null) return;
        }

        item._ownerId = state.OwnerId;
        item._part = part;
        item._owner = state.Orphan ? null : owner;
        item._orphan = state.Orphan;
        item._mine = !state.Orphan && state.OwnerId == LocalId;
        item._livery = (PlayerRoleType)state.Livery;
        item._networkVersion = state.Version;

        if (state.InHand)
        {
            var local = NetworkPlayer.Local;
            // The local gameplay body uses its own animator, not the hidden CharacterVisual puppet.
            // Usually TakeInHand already placed this exact object before publishing the intent,
            // but reconciliation must also repair a missed/late grant. The old fallback attached
            // it to the local hidden puppet, leaving the visible arm metres behind the player while
            // still following him.
            if (local != null && local.OwnerClientId == state.HolderId)
            {
                if (Carried != item)
                {
                    if (Carried != null)
                    {
                        MeleeCombat.Instance?.UnequipLimb(Carried);
                        Carried.Release();
                    }
                    Carried = item;
                }
                if (MeleeCombat.Instance == null || !MeleeCombat.Instance.EquipLimb(item))
                    Debug.LogWarning("[SeveredLimbItem] Canonical carry reached the owner before " +
                                     "an active local humanoid hand was available; keeping the " +
                                     "item for the next reconciliation pass instead of attaching " +
                                     "it to the hidden NetworkPlayer puppet.");
                return;
            }

            if (Carried == item) Carried = null;

            var holder = PlayerByClient(state.HolderId);
            if (holder == null) return;
            var visual = holder.transform.Find("CharacterVisual");
            var animator = visual != null ? visual.GetComponent<Animator>() : null;
            item.ShowInHand(animator,
                holder.Severed(BodyPart.LeftArm), holder.Severed(BodyPart.RightArm));
            return;
        }

        item.ShowOnGround(state.Position, state.Rotation, networkAuthoritative: true);
    }

    public static void RemoveNetworkState(ulong ownerId, BodyPart part, bool orphan)
    {
        var item = Find(ownerId, part, orphan);
        if (item != null) Destroy(item.gameObject);
    }

    static NetworkPlayer PlayerByClient(ulong clientId)
    {
        foreach (var player in NetworkPlayer.All)
            if (player != null && player.OwnerClientId == clientId) return player;
        return null;
    }

    /// Lets go of whatever is in hand — for a respawn, where the fist is about to stop existing.
    ///
    /// Nothing is destroyed, and that is the change. A limb used to be wiped when its owner
    /// respawned or left, on the reasoning that it was a repair for an injury that no longer
    /// existed. Under the shared-parts rule it is nothing of the sort: it is a left arm, and
    /// somebody on this shift is going to need one. It stays on the floor and becomes anybody's.
    public static void DropCarried()
    {
        if (Carried != null) Carried.PutDown();
    }

    /// Turns one player's limbs into spare parts, because the body they came off has gone.
    ///
    /// Matched on the reference rather than on the id, because this runs from the puppet's
    /// OnDestroy and a NetworkBehaviour being torn down is a bad thing to ask questions of. The id
    /// each limb already carries is left exactly as it was: it is still the address, it is simply
    /// an address for somebody who is no longer here.
    public static void OrphanFor(NetworkPlayer owner)
    {
        if (owner == null) return;

        foreach (var item in Dropped)
            if (item != null && !item._orphan && ReferenceEquals(item._owner, owner))
                item.Orphan();
    }

    void Orphan()
    {
        _orphan = true;
        _mine   = false;
        _owner  = null;
    }

    void OnDestroy()
    {
        Dropped.Remove(this);
        if (Carried == this)
        {
            MeleeCombat.Instance?.UnequipLimb(this);
            Carried = null;
        }
    }

    // ── Carrying ──────────────────────────────────────────────────────────────

    /// Puts the limb in the local player's hand, and tells everyone else it went there.
    /// The placement itself is Grip, below.
    public void TakeInHand(GameObject player)
    {
        var combat = MeleeCombat.Instance;
        if (combat == null || !combat.EquipLimb(this))
        {
            PublishCarry(false, transform.position);
            HUDController.Instance?.AddNotification(
                "Weapon slot occupied", "Drop the current weapon before taking the limb.",
                HUDController.NotifType.Warning);
            return;
        }

        Carried = this;

        // And on every other machine, where the same limb is lying on the floor as a copy of its
        // own. Carrying it visibly is the whole reason this is an errand rather than a button —
        // "a man walking to a toolbox with his own arm in his hand is telling the rest of the shift
        // what happened without a word of UI", says the top of this file, about a picture that
        // until now only he could see.
        PublishCarry(true, transform.position);

        string what = IsMine
            ? $"Your {BotDamageVisual.Label(_part)} is in hand"
            : $"{OwnerName}'s {BotDamageVisual.Label(_part)} is in hand — it will fit";

        HUDController.Instance?.AddNotification(
            IsMine ? "Limb recovered" : "Spare part",
            $"{what} — hold {InputHints.EK} to refit it",
            HUDController.NotifType.Success);
    }

    /// Lets go of it, wherever he is standing.
    public void PutDown()
    {
        MeleeCombat.Instance?.UnequipLimb(this);
        if (Carried == this) Carried = null;

        Release();

        PublishCarry(false, transform.position);
    }

    /// Used up by a repair: off the floor, out of every hand, on every machine.
    ///
    /// Addressed to the limb's owner rather than to the player consuming it, because that is the
    /// object every peer files its copy under — and the two are no longer the same player.
    public void Consume()
    {
        MeleeCombat.Instance?.UnequipLimb(this);
        if (Carried == this) Carried = null;
        var me = NetworkPlayer.Local;

        // Sent on my own object and addressed by the limb, which is the only shape that works for
        // one whose owner has left: there is no object of his left to send anything on.
        if (me != null && me.IsSpawned) me.ConsumeLimbServerRpc((int)_part, _ownerId, _orphan);

        Destroy(gameObject);
    }

    /// Whoever it was cut off, for a prompt or a notification.
    public string OwnerName =>
        IsMine         ? "your"
      : _orphan        ? "a spare"
      : _owner != null ? _owner.PlayerName
      :                  "someone";

    /// Tells the rest of the session where this limb went. Silent in solo, and silent in the
    /// window between OnNetworkDespawn and the object actually going away, where calling a
    /// ServerRpc throws.
    ///
    /// Sent on the *owner's* object and stamped with the holder, which are now two different
    /// players. Every peer keeps its copies filed under whoever they were cut off, so the owner is
    /// what says which arm moved; the holder is what says whose fist to put it in.
    void PublishCarry(bool inHand, Vector3 position)
    {
        var me = NetworkPlayer.Local;
        if (me == null || !me.IsSpawned) return;

        me.CarryLimbServerRpc((int)_part, inHand, position, me.OwnerClientId, _ownerId, _orphan);
    }

    // ── The same two moves, for a limb that belongs to somebody else ──────────
    //
    // No HUD, no `Carried`, no network: this machine is watching, not deciding. The placement
    // itself is shared with the owner's copy, so the arm sits in the puppet's fist exactly the way
    // it sits in his own.

    /// Puts a team-mate's limb into his puppet's hand.
    public void ShowInHand(Animator animator, bool leftGone, bool rightGone)
    {
        Transform hand = null;
        if (animator != null && animator.isHuman)
        {
            // Standard weapons use the right-hand slot. If that arm is the missing one, its bone
            // still moves but has no visible hand, so the limb must fall back to the surviving slot.
            if (!rightGone)     hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            else if (!leftGone) hand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        }
        if (hand == null) return;
        if (transform.parent == hand) return;

        Grip(hand);
    }

    /// Puts a team-mate's limb back on the floor where he dropped it.
    public void ShowOnGround(Vector3 position)
    {
        if (Carried == this) Carried = null;
        Release();
        transform.position = position;
    }

    public void ShowOnGround(Vector3 position, Quaternion rotation, bool networkAuthoritative)
    {
        if (Carried == this) Carried = null;
        Release();
        transform.SetPositionAndRotation(position, rotation);

        if (!networkAuthoritative || _body == null) return;

        // A limb state is created at the instant of severing, while the copied mesh is still at
        // shoulder/hip height. Freezing that initial position produced the permanently floating
        // arms seen by clients. Only freeze a canonical ground state when there is real static
        // support below it; otherwise keep gravity enabled and let it fall.
        if (!TryPlaceOnStaticSupport())
        {
            _body.isKinematic = false;
            _body.useGravity = true;
            _body.detectCollisions = true;
            _body.WakeUp();
            return;
        }

        _body.isKinematic = true;
        _body.useGravity = false;
        _body.detectCollisions = true;
        _body.linearVelocity = Vector3.zero;
        _body.angularVelocity = Vector3.zero;
    }

    // ── Shared mechanics ──────────────────────────────────────────────────────

    /// Parented to the bone and left alone, the same way MeleeCombat holds a weapon and for the
    /// same reason it gave up doing it any other way: the arms are animated, so anything that
    /// tries to follow the hand rather than *be* the hand slides through it the moment he moves.
    void Grip(Transform hand)
    {
        if (_body != null) { _body.isKinematic = true; _body.detectCollisions = false; }
        if (_collider != null) _collider.enabled = false;

        // Preserve the detached mesh's world size. Robot hand bones inherit the imported rig's
        // sub-unit scale; parenting with local scale 1 made the copied limb use that scale and
        // invalidated the bounds-based grip offset.
        Vector3 worldScale = transform.lossyScale;
        var bounds = Measure(gameObject);
        transform.SetParent(hand, false);
        transform.localScale = DivideScale(worldScale, hand.lossyScale);

        // Along the limb the hand belongs to, so a spare leg is carried like a club rather than
        // sticking out sideways from the fist. The rig runs its bones towards their children, so
        // asking the skeleton is more honest than writing an axis down — see BotDamageVisual, which
        // learned that the arms run along bone-local -X and the legs along -Y.
        Vector3 along  = CarryDirection(hand);
        Vector3 axis   = LongestAxis(bounds);

        transform.localRotation = Quaternion.FromToRotation(axis, along);

        // Held at its middle, a hand's width out of the fist. Without cancelling the rotated centre
        // the limb hangs off by however far its geometry happens to sit from its own origin, which
        // for a leg is most of a shin.
        transform.localPosition = along * 0.06f - transform.localRotation * bounds.center;

        SetLayerRecursive(gameObject, hand.gameObject.layer);
    }

    /// Used by MeleeCombat so the limb follows the same right-hand weapon slot as every authored
    /// weapon while retaining its own object identity and repair metadata.
    public void PlaceInWeaponSlot(Transform hand)
    {
        if (hand != null) Grip(hand);
    }

    bool TryPlaceOnStaticSupport()
    {
        if (_collider == null) return false;

        bool wasEnabled = _collider.enabled;
        _collider.enabled = false; // never raycast from inside the limb's own collider
        Physics.SyncTransforms();

        Vector3 origin = transform.position + Vector3.up * 0.25f;
        RaycastHit[] hits = Physics.RaycastAll(
            origin, Vector3.down, 8f, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);

        _collider.enabled = wasEnabled;
        if (hits == null || hits.Length == 0) return false;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            if (hit.collider == null || hit.normal.y < 0.45f) continue;
            // Dynamic boxes, vehicles and characters are not permanent support. If one is below
            // the limb it must remain a real Rigidbody and fall naturally with that object.
            if (hit.rigidbody != null) continue;

            Physics.SyncTransforms();
            float lift = hit.point.y - _collider.bounds.min.y + 0.01f;
            transform.position += Vector3.up * lift;
            if (_body != null) _body.position = transform.position;
            return true;
        }

        return false;
    }

    static Vector3 DivideScale(Vector3 world, Vector3 parent)
    {
        const float epsilon = 0.0001f;
        return new Vector3(
            Mathf.Abs(parent.x) > epsilon ? world.x / parent.x : world.x,
            Mathf.Abs(parent.y) > epsilon ? world.y / parent.y : world.y,
            Mathf.Abs(parent.z) > epsilon ? world.z / parent.z : world.z);
    }

    void Release()
    {
        transform.SetParent(null, true);

        if (_collider != null) _collider.enabled = true;
        if (_body != null)
        {
            _body.isKinematic      = false;
            _body.detectCollisions = true;
            _body.linearVelocity   = Vector3.zero;
            _body.angularVelocity  = Vector3.zero;
        }

        SetLayerRecursive(gameObject, 0);
    }

    /// The hand that can hold it: the left by preference, so the right stays free for a weapon.
    ///
    /// Null when both arms are gone, which the pair rule in InjurySystem.Admit now prevents. See
    /// RequiresHold for why the case is still handled.
    ///
    /// Which arms are gone is passed in rather than read off InjurySystem, because on a machine
    /// that is only watching, InjurySystem is the *local* player's — and asking it which of a
    /// team-mate's arms is missing gives a confident answer about the wrong body.
    static Transform FreeHand(Animator animator, bool leftGone, bool rightGone)
    {
        if (animator == null || !animator.isHuman) return null;

        if (!leftGone)  return animator.GetBoneTransform(HumanBodyBones.LeftHand);
        if (!rightGone) return animator.GetBoneTransform(HumanBodyBones.RightHand);

        return null;
    }

    /// Which way "out of the fist" points, in the hand bone's own space.
    static Vector3 CarryDirection(Transform hand)
    {
        for (int i = 0; i < hand.childCount; i++)
        {
            var child = hand.GetChild(i);
            if (child.GetComponent<Renderer>() != null) continue;   // plating, not skeleton

            Vector3 local = hand.InverseTransformPoint(child.position);
            if (local.sqrMagnitude > 1e-6f) return local.normalized;
        }

        // No finger bones to ask — the rig convention, written down in BotDamageVisual.
        return Vector3.left;
    }

    static Vector3 LongestAxis(Bounds bounds)
    {
        var size = bounds.size;

        if (size.x >= size.y && size.x >= size.z) return Vector3.right;
        return size.y >= size.z ? Vector3.up : Vector3.forward;
    }

    static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
    }

    /// The limb's extent **in its own space**, which is the space a BoxCollider is described in.
    ///
    /// Built from the corners rather than by converting a world size, because a world size is three
    /// numbers with no orientation attached: pushing one through InverseTransformVector on a limb
    /// lying at an angle — and they all lie at an angle — mixes the axes together and can come back
    /// negative. Corners survive any rotation.
    static Bounds Measure(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one * 0.3f);

        var local = new Bounds();
        bool started = false;

        foreach (var renderer in renderers)
        {
            var world = renderer.bounds;

            for (int corner = 0; corner < 8; corner++)
            {
                var point = root.transform.InverseTransformPoint(new Vector3(
                    (corner & 1) == 0 ? world.min.x : world.max.x,
                    (corner & 2) == 0 ? world.min.y : world.max.y,
                    (corner & 4) == 0 ? world.min.z : world.max.z));

                if (started) local.Encapsulate(point);
                else         { local = new Bounds(point, Vector3.zero); started = true; }
            }
        }

        return local;
    }

    // ── Interaction ───────────────────────────────────────────────────────────
    //
    // Two interactions on one object, chosen by whether he has a hand to carry it with. Normally
    // it is a press: pick it up and take it to a toolkit. With no hand at all it becomes a hold and
    // is refitted where it lies.
    //
    // That second path is a backstop and is not reachable today: InjurySystem.Admit refuses to take
    // both of a pair, precisely so a player can never be left with two arms on the floor and
    // nothing to pick them up with. It is kept because what it guards is a dead end rather than a
    // glitch — if that rule is ever relaxed, this is what stops the relaxation being unplayable.

    public bool  RequiresHold => Fits && NoHandLeft();
    public float HoldDuration => SelfRepairTarget.RepairHoldDuration;

    static bool NoHandLeft()
    {
        var injuries = InjurySystem.Instance;
        return injuries != null
            && injuries.IsSevered(BodyPart.LeftArm)
            && injuries.IsSevered(BodyPart.RightArm);
    }

    /// "Your" or "Sasha's" — every prompt about a limb has to say whose it was, because that is
    /// the only thing telling the player what colour his arm is about to become.
    string Whose =>
        IsMine  ? "your"
      : _orphan ? "a spare"
      : $"{(_owner != null ? _owner.PlayerName : "someone")}'s";

    public string InteractPrompt
    {
        get
        {
            string label = BotDamageVisual.Label(_part);
            string whose = Whose;

            // Nothing to do with it. Named anyway rather than left silent: an unlabelled arm on the
            // floor is a thing you try to pick up once per shift, forever — and the name is also
            // the answer to "why is that one violet".
            if (!Fits) return $"{Cap(whose)} {label}";

            if (RequiresHold)
            {
                if (!ToolkitItem.IsHeld) return $"{Cap(whose)} {label} — you need the toolkit to fit it";
                return $"{InputHints.EK} Hold to fit {whose} {label} ({HoldDuration:0}s)";
            }

            if (!WeaponSlotAvailable) return $"{Cap(whose)} {label} — drop your weapon first";

            return $"Pick up {whose} {label} {InputHints.E}";
        }
    }

    static string Cap(string word) =>
        string.IsNullOrEmpty(word) ? word : char.ToUpperInvariant(word[0]) + word.Substring(1);

    static bool WeaponSlotAvailable => MeleeCombat.Instance == null ||
                                       MeleeCombat.Instance.CurrentWeapon == MeleeWeaponKind.Fists;

    public bool CanInteract(GameObject interactor) =>
        Fits && !InHand && (RequiresHold ? ToolkitItem.IsHeld : WeaponSlotAvailable);

    public void Interact(GameObject interactor)
    {
        if (!Fits) return;

        // Solo, or before the session is up: nobody to race, so it happens on the spot.
        var me = NetworkPlayer.Local;
        if (me == null || !me.IsSpawned) { Use(interactor); return; }

        // In a session it is claimed through the server first. Two robots missing the same arm and
        // one arm on the floor between them is a race, and every machine used to win it: each took
        // its own copy and each got an arm out of the one part. Nothing happens here until the
        // server says which of them reached it.
        //
        // Both paths ask, the pick-up and the fit-where-it-lies. The second is unreachable while
        // InjurySystem.Admit refuses to take both of a pair — but its whole reason for existing is
        // that the rule might one day be relaxed, and a backstop that is wrong when it fires is not
        // a backstop.
        _claimant = interactor;
        me.RequestLimbServerRpc((int)_part, _ownerId, _orphan);
    }

    /// The server says this one is mine. Called on the winner only.
    public void ClaimGranted()
    {
        var claimant = _claimant;
        _claimant = null;

        if (claimant == null) return;

        // A round trip is long enough for the reason to have gone: he may have been patched up out
        // of another arm while this one was in the post. Handed straight back rather than held —
        // the claim would otherwise sit on a limb nobody is carrying.
        if (!Fits) { PublishCarry(false, transform.position); return; }

        Use(claimant);
    }

    public void ClaimRejected()
    {
        _claimant = null;
    }

    /// Picking it up — or, with no hand left to pick it up with, fitting it where it lies. The hold
    /// has already run by the time either happens.
    void Use(GameObject interactor)
    {
        if (RequiresHold)
        {
            var injuries = InjurySystem.Instance;
            if (injuries == null || !injuries.Repair(_part, this)) return;

            AudioManager.Instance?.PlayPing();
            return;
        }

        TakeInHand(interactor);
    }

    /// Who asked, remembered across the round trip so the grant knows whose fist to fill.
    GameObject _claimant;
}
