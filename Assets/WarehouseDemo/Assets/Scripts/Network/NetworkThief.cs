using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// The networking half of a raider. ThiefController stays the brain and runs on the server
/// only; this component carries everything the other clients need to see the same fight.
///
/// Split of responsibilities:
///   • position/rotation — NetworkTransform on the prefab (server-authoritative),
///   • what he is (role, weapon, loot) — NetworkVariables, so a late joiner gets the truth,
///   • what just happened (a blow landing, going down, a call-out) — ClientRpcs, because a
///     one-shot event replayed to a late joiner would be noise, not information.
///
/// Everything here is inert in single player: the components exist, OnNetworkSpawn never
/// fires, and ThiefController talks to the local HUD and the local player directly.
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(ThiefController))]
public class NetworkThief : NetworkBehaviour
{
    // ── Replicated identity ───────────────────────────────────────────────────

    readonly NetworkVariable<int> _role = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    readonly NetworkVariable<int> _weapon = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// Which body skin he wears. The server rolls it once so a raid is not five identical
    /// zombies; replicated because every client has to see the same one.
    readonly NetworkVariable<int> _body = new(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// PartID of the part he is carrying, empty when his hands are empty.
    readonly NetworkVariable<FixedString64Bytes> _lootPartId = new(
        new FixedString64Bytes(""),
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// Which locomotion clip he is on, 0 shamble … 1 run. The server picks it from the pace he is
    /// set to keep; a client has no agent to work that out from.
    readonly NetworkVariable<float> _gait = new(
        1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    ThiefController _thief;

    void Awake() => _thief = GetComponent<ThiefController>();

    public override void OnNetworkSpawn()
    {
        _role.OnValueChanged       += OnRoleChanged;
        _weapon.OnValueChanged     += OnWeaponChanged;
        _body.OnValueChanged       += OnBodyChanged;
        _lootPartId.OnValueChanged += OnLootChanged;
        _gait.OnValueChanged       += OnGaitChanged;

        if (IsServer) return;

        _thief.ApplyRemoteGait(_gait.Value);

        // A client that spawned in mid-raid gets the current values, not just future changes.
        _thief.ApplyRemoteRole((ThiefRole)_role.Value);
        _thief.ApplyRemoteWeapon((MeleeWeaponKind)_weapon.Value);
        _thief.ApplyRemoteBody(_body.Value);
        _thief.ApplyRemoteLoot(_lootPartId.Value.ToString());
    }

    public override void OnNetworkDespawn()
    {
        _role.OnValueChanged       -= OnRoleChanged;
        _weapon.OnValueChanged     -= OnWeaponChanged;
        _body.OnValueChanged       -= OnBodyChanged;
        _lootPartId.OnValueChanged -= OnLootChanged;
        _gait.OnValueChanged       -= OnGaitChanged;
    }

    void OnGaitChanged(float _, float now) => _thief.ApplyRemoteGait(now);

    void OnRoleChanged(int _, int now)   => _thief.ApplyRemoteRole((ThiefRole)now);
    void OnWeaponChanged(int _, int now) => _thief.ApplyRemoteWeapon((MeleeWeaponKind)now);
    void OnBodyChanged(int _, int now)   => _thief.ApplyRemoteBody(now);

    void OnLootChanged(FixedString64Bytes _, FixedString64Bytes now)
        => _thief.ApplyRemoteLoot(now.ToString());

    // ── Server-side setters (called by ThiefController) ───────────────────────

    public void PublishRole(ThiefRole role)
    {
        if (IsServer) _role.Value = (int)role;
    }

    public void PublishWeapon(MeleeWeaponKind weapon)
    {
        if (IsServer) _weapon.Value = (int)weapon;
    }

    public void PublishBody(int variant)
    {
        if (IsServer) _body.Value = variant;
    }

    /// Only sent when it has actually moved, so a raider holding one pace costs nothing on the wire.
    public void PublishGait(float gait)
    {
        if (IsServer && IsSpawned && Mathf.Abs(_gait.Value - gait) > 0.05f) _gait.Value = gait;
    }

    public void PublishLoot(string partId)
    {
        if (IsServer) _lootPartId.Value = new FixedString64Bytes(partId ?? "");
    }

    // ── Melee: a client swung at him ──────────────────────────────────────────

    /// Sent by MeleeCombat on whichever client threw the punch. No damage, direction or heavy
    /// flag is accepted from the caller: NetworkPlayer resolves those from its server-approved
    /// swing token and server-owned WeaponKind.
    [ServerRpc(RequireOwnership = false)]
    public void RequestHitServerRpc(uint swingSequence, ServerRpcParams rpcParams = default)
    {
        if (!IsServer || _thief == null || _thief.Current == ThiefController.State.Down) return;

        ulong senderId = rpcParams.Receive.SenderClientId;
        NetworkPlayer attacker = FindNetworkPlayer(senderId);
        if (attacker == null) return;
        if (!attacker.TryConsumeServerMeleeHit(
                swingSequence, NetworkObjectId, transform.position,
                out int damage, out Vector3 direction, out bool heavy)) return;

        _thief.TakeHit(damage, direction, heavy);
    }

    static NetworkPlayer FindNetworkPlayer(ulong clientId)
    {
        foreach (var p in NetworkPlayer.All)
            if (p != null && p.OwnerClientId == clientId) return p;
        return null;
    }

    // ── Broadcasts (server → everyone) ────────────────────────────────────────

    /// One call-out, shown on every client's HUD. Runs on the host too — the server does not
    /// raise the notification locally, it goes through here so nobody gets it twice.
    public void Announce(string title, string sub, HUDController.NotifType type)
    {
        if (!IsSpawned || !IsServer)
        {
            HUDController.Instance?.AddNotification(title, sub, type);
            return;
        }

        AnnounceClientRpc(title, sub, (int)type);
    }

    [ClientRpc]
    void AnnounceClientRpc(string title, string sub, int type)
        => HUDController.Instance?.AddNotification(title, sub, (HUDController.NotifType)type);

    /// He is on the floor: everyone plays the same fall and stops colliding with him.
    public void BroadcastDown(Vector3 fallDirection, bool heavy)
    {
        if (IsServer && IsSpawned) GoDownClientRpc(fallDirection, heavy);
    }

    [ClientRpc]
    void GoDownClientRpc(Vector3 fallDirection, bool heavy)
    {
        if (IsServer) return;   // the server already ran the real GoDown
        _thief.ApplyRemoteDown(fallDirection, heavy);
    }

    /// A box left the pallet and now rides his shoulder. Clients drive the same follow the
    /// cart uses, so no NetworkObject re-parenting is involved.
    public void BroadcastCarry(ulong boxNetId, bool carrying)
    {
        if (IsServer && IsSpawned) CarryBoxClientRpc(boxNetId, carrying);
    }

    [ClientRpc]
    void CarryBoxClientRpc(ulong boxNetId, bool carrying)
    {
        if (IsServer) return;
        _thief.ApplyRemoteCarry(boxNetId, carrying);
    }

    /// A swing, so a fight looks like a fight on every screen and not only the host's.
    public void BroadcastSwing(int variant)
    {
        if (IsServer && IsSpawned) SwingClientRpc(variant);
    }

    [ClientRpc]
    void SwingClientRpc(int variant)
    {
        if (IsServer) return;
        _thief.PlayAttack(variant);
    }

    /// One of his blows landing on a player — the thud, so it is not a host-only sound.
    public void BroadcastImpact()
    {
        if (IsServer && IsSpawned) ImpactClientRpc();
    }

    [ClientRpc]
    void ImpactClientRpc()
    {
        if (IsServer) return;
        _thief.PlayImpact();
    }

    /// He just took one — everyone sees the stagger.
    public void BroadcastDamage(Vector3 fromDirection)
    {
        if (IsServer && IsSpawned) DamageClientRpc(fromDirection);
    }

    [ClientRpc]
    void DamageClientRpc(Vector3 fromDirection)
    {
        if (IsServer) return;
        _thief.PlayDamage(fromDirection);
    }

    /// His bat hits the floor as a pickup on every client at the same spot.
    public void BroadcastWeaponDrop(int kind, Vector3 position)
    {
        if (IsServer && IsSpawned) DropWeaponClientRpc(kind, position);
    }

    [ClientRpc]
    void DropWeaponClientRpc(int kind, Vector3 position)
    {
        if (IsServer) return;
        MeleeWeaponItem.Spawn((MeleeWeaponKind)kind, position);
    }
}
