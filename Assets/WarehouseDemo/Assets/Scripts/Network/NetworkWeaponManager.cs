using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Server-authoritative arbitration for who picks a weapon up, and who is seen holding it.
///
/// Pickups themselves are never network-spawned — every peer lays out the same weapons from the
/// same seed, and every later one is broadcast — so this deals in the shared id each pickup gets
/// (MeleeWeaponItem.NetWeaponId) rather than in NetworkObjectIds. Same shape as
/// NetworkPhysBoxManager, which arbitrates boxes the same way and for the same reason: two players
/// reaching for one bat must not end up with a bat each.
///
/// What is actually held lives on NetworkPlayer.WeaponKind, so a late joiner sees the shift's
/// weapons in the right hands without anyone re-broadcasting anything.
public class NetworkWeaponManager : NetworkBehaviour
{
    public static NetworkWeaponManager Instance { get; private set; }

    // Replicated rather than server-only, for the late joiner: he lays out the shift's weapons
    // from the same seed as everyone else, which means he lays out the ones already picked up too.
    // Reading the list on connect is what clears them off his floor.
    NetworkList<int> _taken;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        _taken = new NetworkList<int>(
            new List<int>(),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn()
    {
        // Claimed here, not only in Awake. Awake runs once per scene load, this is cleared in
        // OnNetworkDespawn, and the co-op flow deliberately reuses the scene between rooms — so
        // from the second room onwards the static stayed null on the machine that hosted the first
        // one, and every `Instance?.` call through it did nothing at all. That reads in-game as
        // "nobody can pick anything up", because a grab is exactly such a call.
        if (Instance == null) Instance = this;

        // Everything claimed before we arrived. Deferred a frame because the seeded layout is
        // spawned from GameEventManager's own start, which may not have run yet on this peer.
        if (!IsServer) StartCoroutine(ClearTakenWhenLaidOut());
    }

    System.Collections.IEnumerator ClearTakenWhenLaidOut()
    {
        yield return null;
        yield return null;

        foreach (int id in _taken)
        {
            var item = MeleeWeaponItem.FindByNetId(id);
            if (item != null) Destroy(item.gameObject);
        }
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    /// True when weapons are being arbitrated at all. Single player takes the direct path.
    public static bool IsNetworked =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && Instance != null;

    // ── Pickup ────────────────────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void RequestPickupServerRpc(int weaponId, ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var item = MeleeWeaponItem.FindByNetId(weaponId);
        var player = FindNetworkPlayer(sender);
        if (weaponId < 0 || item == null || player == null || _taken.Contains(weaponId)) return;
        if (player.IsDeadNet.Value || player.IsRagdolledNet.Value
            || player.WeaponKind.Value != (int)MeleeWeaponKind.Fists) return;
        if ((item.transform.position - player.transform.position).sqrMagnitude > 3.5f * 3.5f)
            return;
        if (!System.Enum.IsDefined(typeof(MeleeWeaponKind), (int)item.Kind)
            || item.Kind == MeleeWeaponKind.Fists) return;

        // Commit only after every validation. This is the atomic point that prevents two clients
        // taking the same pickup in the same frame.
        _taken.Add(weaponId);
        var kind = item.Kind;

        // What he is holding is state, not an event: a client joining later reads it off the
        // player rather than needing the pickup replayed.
        SetHeldWeapon(sender, kind);

        ConfirmPickupClientRpc(weaponId, (int)kind, sender);
    }

    [ClientRpc]
    void ConfirmPickupClientRpc(int weaponId, int kind, ulong picker)
    {
        var item = MeleeWeaponItem.FindByNetId(weaponId);
        if (item != null) Destroy(item.gameObject);

        // The picker equips it locally; everyone else sees it appear on his puppet through
        // NetworkPlayer.WeaponKind, which the server has already set.
        if (NetworkManager.Singleton.LocalClientId != picker) return;

        var combat = MeleeCombat.Instance;
        if (combat != null)
            combat.EquipWeapon((MeleeWeaponKind)kind, MeleeWeaponItem.BuildVisual((MeleeWeaponKind)kind));
    }

    // ── Drop ──────────────────────────────────────────────────────────────────

    /// `position`/`rotation` are where the weapon was in the dropper's hand and `velocity` the push
    /// it leaves with, so every peer lets go of it from the same place.
    [ServerRpc(RequireOwnership = false)]
    public void RequestDropServerRpc(int kind, Vector3 position, Quaternion rotation, Vector3 velocity,
                                     ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var player = FindNetworkPlayer(sender);
        if (player == null || player.IsDeadNet.Value) return;
        if (!System.Enum.IsDefined(typeof(MeleeWeaponKind), kind)
            || kind == (int)MeleeWeaponKind.Fists
            || player.WeaponKind.Value != kind) return;
        if (!Finite(position) || !Finite(rotation) || !Finite(velocity)) return;
        if ((position - player.transform.position).sqrMagnitude > 6f * 6f) return;

        if (velocity.magnitude > 12f) velocity = velocity.normalized * 12f;
        rotation = Normalize(rotation);
        SetHeldWeapon(sender, MeleeWeaponKind.Fists);

        // The server names the new pickup, or the peers would number it differently the moment
        // their spawn counters had ever diverged.
        int id = NextDropId();
        SpawnDroppedClientRpc(kind, position, rotation, velocity, id, sender);
    }

    int _nextDropId = 1_000_000;   // above anything the seeded start layout will reach
    int NextDropId() => _nextDropId++;

    [ClientRpc]
    void SpawnDroppedClientRpc(int kind, Vector3 position, Quaternion rotation, Vector3 velocity,
                               int weaponId, ulong dropper)
    {
        // The dropper lets go of the model actually in his hand. His hands are emptied here rather
        // than before the request, so a refused drop cannot leave him with nothing and no pickup
        // on the floor either.
        if (NetworkManager.Singleton.LocalClientId == dropper)
        {
            var combat = MeleeCombat.Instance;
            if (combat != null) { combat.ReleaseHeld(weaponId, velocity); return; }
        }

        // Everyone else holds a separate copy on his puppet, which goes when his WeaponKind does.
        // Theirs is rebuilt at his hand and given the same push. Where it comes to rest can differ
        // by a hand's width between machines; the pickup is found by id, so that does not matter.
        MeleeWeaponItem.SpawnReleased((MeleeWeaponKind)kind, position, rotation, velocity, weaponId,
                                      PuppetColliders(dropper));
    }

    static Collider[] PuppetColliders(ulong clientId)
    {
        foreach (var np in NetworkPlayer.All)
            if (np != null && np.OwnerClientId == clientId)
                return np.GetComponentsInChildren<Collider>(true);
        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void SetHeldWeapon(ulong clientId, MeleeWeaponKind kind)
    {
        foreach (var np in NetworkPlayer.All)
            if (np.OwnerClientId == clientId) { np.WeaponKind.Value = (int)kind; return; }
    }

    static NetworkPlayer FindNetworkPlayer(ulong clientId)
    {
        foreach (var player in NetworkPlayer.All)
            if (player != null && player.OwnerClientId == clientId) return player;
        return null;
    }

    static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    static bool Finite(Quaternion value) => Finite(value.x) && Finite(value.y)
        && Finite(value.z) && Finite(value.w)
        && value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > 0.0001f;
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    static Quaternion Normalize(Quaternion value)
    {
        float length = Mathf.Sqrt(value.x * value.x + value.y * value.y
                                  + value.z * value.z + value.w * value.w);
        return new Quaternion(value.x / length, value.y / length, value.z / length, value.w / length);
    }
}
