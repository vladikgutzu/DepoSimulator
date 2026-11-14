using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// Attach to a shelf root. When TipOver() is called the shelf animates falling
/// in the hit direction and ragdolls nearby players.
/// No Rigidbody needed — uses coroutine-driven RotateAround so MeshColliders stay valid.
/// Multiplayer: initial TipOver is relayed via CoopGameManager ServerRpc so all clients see it.
public class PhysicsShelf : MonoBehaviour
{
    [SerializeField] float _ragdollRadius = 3.5f;

    bool _tipped;
    public bool IsTipped => _tipped;

    // ── Network registry ──────────────────────────────────────────────────────
    // All clients load the same scene → same shelves → sort by position for deterministic IDs.

    public  static readonly Dictionary<int, PhysicsShelf> Registry = new();
    int _netId = -1;
    public int NetId => _netId;

    void Start()
    {
        if (Registry.Count == 0)
            BuildRegistry();
    }

    void OnDestroy()
    {
        if (_netId >= 0) Registry.Remove(_netId);
    }

    static void BuildRegistry()
    {
        var all = Object.FindObjectsByType<PhysicsShelf>(FindObjectsSortMode.None);
        System.Array.Sort(all, (a, b) =>
        {
            var pa = a.transform.position;
            var pb = b.transform.position;
            int cx = pa.x.CompareTo(pb.x);
            if (cx != 0) return cx;
            int cz = pa.z.CompareTo(pb.z);
            if (cz != 0) return cz;
            return pa.y.CompareTo(pb.y);
        });
        for (int i = 0; i < all.Length; i++)
        {
            all[i]._netId = i;
            Registry[i]   = all[i];
        }
    }

    // ── Entry point called by ReachTruckController ────────────────────────────

    // In multiplayer the initial hit is relayed via server so all clients execute TipOverLocal.
    // Domino calls (fromDomino=true) run locally on each client — same result everywhere.
    public void TipOver(Vector3 hitDirection, bool fromDomino = false)
    {
        bool isMP = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;
        if (isMP && !fromDomino)
        {
            CoopGameManager.Instance?.ShelfTipOverServerRpc(_netId, hitDirection);
            return;
        }
        TipOverLocal(hitDirection, fromDomino);
    }

    /// Server-side proof that the sender's currently occupied reach truck is physically at this
    /// shelf. Bounds are expanded by a small contact tolerance because PhysX normally prevents two
    /// solid colliders from penetrating; a valid collision often ends with their surfaces merely
    /// touching. The client-proposed direction is intentionally ignored — the server derives the
    /// fall away from the vehicle.
    internal bool ServerTryValidateVehicleContact(Transform vehicleRoot, out Vector3 fallDirection)
    {
        fallDirection = Vector3.zero;
        var network = Unity.Netcode.NetworkManager.Singleton;
        if (_tipped || vehicleRoot == null || network == null || !network.IsServer)
            return false;

        Collider[] vehicleColliders = vehicleRoot.GetComponentsInChildren<Collider>(false);
        bool hasVehicleBounds = false;
        Bounds vehicleBounds = default;
        foreach (var collider in vehicleColliders)
        {
            if (collider == null || !collider.enabled || collider.isTrigger) continue;
            if (!hasVehicleBounds)
            {
                vehicleBounds = collider.bounds;
                hasVehicleBounds = true;
            }
            else
            {
                vehicleBounds.Encapsulate(collider.bounds);
            }
        }

        if (!hasVehicleBounds) return false;

        Bounds shelfBounds = GetShelfBounds();
        shelfBounds.Expand(0.35f);
        if (!shelfBounds.Intersects(vehicleBounds)) return false;

        fallDirection = shelfBounds.center - vehicleBounds.center;
        fallDirection.y = 0f;
        if (fallDirection.sqrMagnitude < 0.01f)
            fallDirection = transform.forward;
        else
            fallDirection.Normalize();

        return true;
    }

    // Called directly in solo, and via ClientRpc in multiplayer.
    internal void TipOverLocal(Vector3 hitDirection, bool fromDomino)
    {
        if (_tipped) return;
        _tipped = true;

        hitDirection.y = 0f;
        if (hitDirection.sqrMagnitude < 0.01f) hitDirection = transform.forward;
        hitDirection.Normalize();

        RagdollPlayers();

        if (!fromDomino)
        {
            HUDController.Instance?.AddNotification(
                "SHELF COLLAPSED!",
                "The forklift knocked over a shelf — watch out!",
                HUDController.NotifType.Danger);
        }

        GameEventManager.TriggerShake(fromDomino ? 2f : 3.5f);
        ShelfCrashSound.Instance?.Play(transform.position);

        StartCoroutine(FallRoutine(hitDirection));
    }

    // ── Fall animation ────────────────────────────────────────────────────────

    IEnumerator FallRoutine(Vector3 hitDir)
    {
        // Pivot at the base edge of the shelf in the hit direction.
        Bounds bounds = GetShelfBounds();
        Vector3 pivot = bounds.center;
        pivot.y        = bounds.min.y;
        pivot          += hitDir * bounds.extents.magnitude * 0.5f;

        // Rotation axis is perpendicular to fall direction (horizontal)
        Vector3 fallAxis = Vector3.Cross(Vector3.up, hitDir).normalized;

        float totalAngle = 90f;
        float rotated    = 0f;

        int frameCount = 0;

        while (rotated < totalAngle)
        {
            // Ease-in: slow start (overcoming inertia), fast finish (gravity acceleration)
            float t = rotated / totalAngle;
            float speed = Mathf.Lerp(15f, 120f, t * t);   // degrees per second

            float step = speed * Time.deltaTime;
            step     = Mathf.Min(step, totalAngle - rotated);
            rotated += step;

            transform.RotateAround(pivot, fallAxis, step);

            // Every 3 frames after 20° — check if shelf is sweeping through a player.
            if (rotated > 20f && ++frameCount % 3 == 0)
                CheckCrush();

            yield return null;
        }

        // Final crush check after shelf has fully landed.
        CheckCrush();
        RagdollPlayers();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void CheckCrush()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
        {
            if (!nm.IsServer) return;

            Bounds authoritativeBounds = GetShelfBounds();
            authoritativeBounds.Expand(0.25f);
            foreach (var player in NetworkPlayer.All)
            {
                if (player == null || player.IsDeadNet.Value) continue;
                if (authoritativeBounds.Contains(player.transform.position))
                    player.ServerApplyDeath(player.transform.position);
            }
            return;
        }

        var dm = PlayerDeathManager.Instance;
        if (dm == null || dm.IsDead) return;

        Bounds b = GetShelfBounds();
        b.Expand(0.25f);   // slight forgiveness margin

        if (b.Contains(dm.PlayerTransform.position))
            dm.Die(dm.PlayerTransform.position);
    }

    void RagdollPlayers()
    {
        Vector3 center = GetShelfBounds().center;

        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
        {
            if (!nm.IsServer) return;

            foreach (var player in NetworkPlayer.All)
            {
                if (player == null || player.IsDeadNet.Value || player.IsRagdolledNet.Value)
                    continue;

                float dist = Vector3.Distance(player.transform.position, center);
                if (dist > _ragdollRadius) continue;

                Vector3 impulse = player.transform.position - center;
                impulse.y = 0f;
                if (impulse.sqrMagnitude < 0.01f) impulse = player.transform.forward;
                impulse = impulse.normalized;
                impulse.y = 0.3f;
                player.ServerApplyRagdoll(impulse * 16f, player.transform.position);
            }
            return;
        }

        foreach (var ragdoll in Object.FindObjectsByType<RagdollController>(FindObjectsSortMode.None))
        {
            if (ragdoll.IsRagdoll) continue;
            // Skip dead players — they already have a death state
            var dm = ragdoll.GetComponent<PlayerDeathManager>()
                  ?? ragdoll.GetComponentInParent<PlayerDeathManager>();
            if (dm != null && dm.IsDead) continue;

            float dist = Vector3.Distance(ragdoll.transform.position, center);
            if (dist > _ragdollRadius) continue;

            Vector3 impulse = (ragdoll.transform.position - center).normalized;
            impulse.y = 0.3f;
            ragdoll.TriggerRagdoll(impulse * 16f, ragdoll.transform.position);
        }
    }

    Bounds GetShelfBounds()
    {
        var renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return new Bounds(transform.position, Vector3.one * 2f);

        var b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);
        return b;
    }
}
