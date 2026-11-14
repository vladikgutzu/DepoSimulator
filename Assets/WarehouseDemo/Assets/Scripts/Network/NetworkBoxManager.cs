using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// Server-authoritative manager for networked part-box assignments — syncs which scene box
/// contains which part across all clients, so every client tags the same box with the same
/// BoxWithPart data. Pickup, carry, hand-off between players, and placement are handled
/// entirely by BoxPhysicsInteractable + NetworkPhysBoxManager (BoxWithPart is a pure data tag
/// added on top of that, same as PackedOrderBox for shipping boxes) — this manager owns only
/// the assignment mapping, not any interaction/networking around carrying.
public class NetworkBoxManager : NetworkBehaviour
{
    public static NetworkBoxManager Instance { get; private set; }

    NetworkList<BoxPickupState> _assignments;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;

        _assignments = new NetworkList<BoxPickupState>(
            new List<BoxPickupState>(),
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

        _assignments.OnListChanged += OnAssignmentsChanged;

        // Apply any assignments already in the list (late-join clients)
        if (!IsServer)
            foreach (var a in _assignments)
                ApplyAssignmentLocally(a);

        if (IsServer)
        {
            var gm = GameManager.Instance;
            if (gm != null)
            {
                gm.OnOrderCompleted += OnOrderDone;
                gm.OnOrderExpired   += OnOrderDone;
            }
        }
    }

    public override void OnNetworkDespawn()
    {
        _assignments.OnListChanged -= OnAssignmentsChanged;

        if (IsServer)
        {
            var gm = GameManager.Instance;
            if (gm != null)
            {
                gm.OnOrderCompleted -= OnOrderDone;
                gm.OnOrderExpired   -= OnOrderDone;
            }
        }

        if (Instance == this) Instance = null;
    }

    // ── Called by server-side PartSpawner after it assigns boxes ────────────────

    public void SyncAssignments()
    {
        if (!IsServer) return;
        _assignments.Clear();
        foreach (var bwp in BoxWithPart.All)
        {
            if (bwp == null || bwp.Part == null) continue;
            var phys = bwp.GetComponent<BoxPhysicsInteractable>();
            if (phys == null) continue;

            // PartSpawner network-spawns every pooled box before assigning (EnsureNetworkSpawned),
            // so NetBoxId is a real NetworkObjectId here and means the same box on every peer.
            ulong netId = phys.NetBoxId;
            if (netId == 0UL)
            {
                Debug.LogWarning($"[NetworkBoxManager] '{bwp.name}' has no spawned NetworkObject — " +
                                  "cannot sync its part assignment to clients.");
                continue;
            }

            _assignments.Add(new BoxPickupState
            {
                NetBoxId = netId,
                PartId   = new FixedString64Bytes(bwp.Part.PartID)
            });
        }
        Debug.Log($"[NetworkBoxManager] Synced {_assignments.Count} box assignments to clients.");
    }

    void OnOrderDone(OrderDefinition _) => ClearAssignments();

    void ClearAssignments()
    {
        if (!IsServer) return;
        _assignments.Clear();
        ClearClientBoxesClientRpc();
    }

    // ── Client: react to list changes ───────────────────────────────────────────

    void OnAssignmentsChanged(NetworkListEvent<BoxPickupState> evt)
    {
        if (IsServer) return; // Server applied directly via PartSpawner
        if (evt.Type == NetworkListEvent<BoxPickupState>.EventType.Add)
            ApplyAssignmentLocally(evt.Value);
    }

    // NGO does not promise that a box's spawn message is processed before the NetworkList
    // update naming it, so a miss here is "not yet", not "never". Retry for a short window
    // before giving up, otherwise a lost race would silently leave the box partless again --
    // the exact failure mode the position-key lookup used to have.
    const float LookupRetryWindow = 3f;

    System.Collections.IEnumerator RetryAssignment(BoxPickupState state)
    {
        for (float t = 0f; t < LookupRetryWindow; t += Time.deltaTime)
        {
            yield return null;
            if (FindBoxByNetId(state.NetBoxId) == null) continue;
            ApplyAssignmentLocally(state);
            yield break;
        }
        Debug.LogWarning($"[NetworkBoxManager] NetworkObject {state.NetBoxId} never spawned on " +
                          $"this client — part '{state.PartId}' has no box here.");
    }

    void ApplyAssignmentLocally(BoxPickupState state)
    {
        var box = FindBoxByNetId(state.NetBoxId);
        if (box == null)
        {
            StartCoroutine(RetryAssignment(state));
            return;
        }
        if (box.GetComponent<BoxWithPart>() != null) return; // already set up

        var def = PartRegistry.Get(state.PartId.ToString());
        if (def == null) return;

        var bwp = box.AddComponent<BoxWithPart>();
        bwp.Setup(def, true);

        // Same as PartSpawner.Assign() on the server: ensure BoxPhysicsInteractable exists and
        // is enabled so pickup/carry works identically to any other physics box on this client.
        // No id fixup needed any more — the component derives NetBoxId from the NetworkObject
        // it sits on, which NGO already guarantees is identical here and on the server.
        PartSpawner.EnsureBoxPhysics(box);
        var phys = box.GetComponent<BoxPhysicsInteractable>() ?? box.AddComponent<BoxPhysicsInteractable>();
        phys.enabled = true;
        phys.AdoptSceneId();   // same as the server does in PartSpawner.Assign
    }

    [ClientRpc]
    void ClearClientBoxesClientRpc()
    {
        if (IsServer) return; // server cleanup is handled by PartSpawner.ClearAssigned()
        foreach (var bwp in new List<BoxWithPart>(BoxWithPart.All))
            if (bwp != null) Object.Destroy(bwp);
    }

    /// Server: take one box's part away on every client.
    ///
    /// SyncAssignments cannot express this. It clears the list and re-adds everything, and the
    /// client handler only listens for Add — deliberately, since a full clear-and-refill would
    /// otherwise rebuild every box in the warehouse on each sync. So a box that quietly drops OUT
    /// of the list stays assigned on the clients, and they end up holding a part the server has
    /// forgotten. UrgentPartEvent needs exactly this when it takes back the crate it planted.
    public void ClearBoxAssignment(ulong netBoxId)
    {
        if (!IsServer || netBoxId == 0UL) return;
        ClearBoxAssignmentClientRpc(netBoxId);
    }

    [ClientRpc]
    void ClearBoxAssignmentClientRpc(ulong netBoxId)
    {
        if (IsServer) return;   // the server dropped its own component before broadcasting

        var box = FindBoxByNetId(netBoxId);
        var bwp = box != null ? box.GetComponent<BoxWithPart>() : null;
        if (bwp != null) Object.Destroy(bwp);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // Replaces the old position-key match. That one rounded world coordinates to the
    // centimetre and required an exact string hit, so any physics divergence between two
    // independently simulating clients — which is normal for Rigidbody stacks — silently lost
    // the assignment. NGO's spawn table is exact and needs no tolerance.
    //
    // NGO's table alone is not enough, though. A box inside a pallet pack or a box group has no
    // NetworkObject of its own; its NetBoxId is the scene-path hash BoxPhysicsInteractable assigns
    // (above 1 << 40), which NGO has never heard of. About a thousand of the pool's boxes are like
    // that, so on every client those parts were reported "never spawned" and the box stayed
    // partless: no highlight, and carrying it to the bench installed nothing.
    static GameObject FindBoxByNetId(ulong netId)
    {
        var box = BoxPhysicsInteractable.FindByNetId(netId);
        if (box != null) return box.gameObject;

        var nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null) return null;
        return nm.SpawnManager.SpawnedObjects.TryGetValue(netId, out var netObj) && netObj != null
            ? netObj.gameObject
            : null;
    }
}

public struct BoxPickupState : INetworkSerializable, System.IEquatable<BoxPickupState>
{
    public ulong              NetBoxId;   // NetworkObjectId — identical on every peer
    public FixedString64Bytes PartId;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref NetBoxId);
        serializer.SerializeValue(ref PartId);
    }

    public bool Equals(BoxPickupState other) =>
        NetBoxId == other.NetBoxId && PartId == other.PartId;

    public override bool Equals(object obj) =>
        obj is BoxPickupState other && Equals(other);

    public override int GetHashCode() =>
        NetBoxId.GetHashCode() * 397 ^ PartId.GetHashCode();
}
