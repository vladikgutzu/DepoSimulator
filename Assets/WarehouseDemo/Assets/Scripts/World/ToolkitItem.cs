using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Spawnable toolkit for the PowerFailureEvent.
/// Pick up → find the malfunctioning power box → hold [E] to repair.
[RequireComponent(typeof(Collider))]
public class ToolkitItem : MonoBehaviour, IInteractable, ICanInteractCheck
{
    /// Repair incidents that explicitly teach the toolkit loop must spawn and require the kit.
    /// FireAlarmEvent remains the deliberate exception: its targets are configured with
    /// requireToolkit=false and can be repaired immediately.
    public static readonly bool Required = true;

    static int _charges;
    static readonly List<ToolkitItem> _worldPickups = new();
    public static int LocalCharges => _charges;
    public static int Charges
    {
        get
        {
            bool multiplayer = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
            return multiplayer && NetworkShopManager.Instance != null
                ? NetworkShopManager.Instance.RepairKitCharges
                : _charges;
        }
    }
    public static bool IsHeld
    {
        get => !Required || Charges > 0;
        private set
        {
            if (value && _charges == 0) _charges = 1;
            else if (!value) _charges = 0;
        }
    }

    /// Assign via GameEventManager._toolkitPrefab — set in Awake so Spawn() uses the model.
    public static GameObject KitPrefab { get; set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetStatics() { _charges = 0; _worldPickups.Clear(); KitPrefab = null; }

    void OnEnable()
    {
        if (!_worldPickups.Contains(this)) _worldPickups.Add(this);
    }

    void OnDisable() => _worldPickups.Remove(this);

    public string InteractPrompt => $"Take toolkit {InputHints.E}";
    public bool CanInteract(GameObject interactor) => !IsHeld;

    public void Interact(GameObject interactor)
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkShopManager.Instance?.RequestPickupRepairKitServerRpc(transform.position);
            return;
        }

        IsHeld = true;
        Destroy(gameObject);

        // What the kit is for depends on what is wrong with you. A player who has just walked
        // across the warehouse carrying his own arm does not need to be told to find a power box.
        var injuries = InjurySystem.Instance;
        bool hurt = injuries != null && injuries.IsInjured;

        var carried = SeveredLimbItem.Carried;

        string what = hurt && carried != null
            // Whose limb, because it may well not be his — see SeveredLimbItem, where any left arm
            // fits any robot missing one — and the colour of the arm is about to say so.
            ? $"Hold {InputHints.E} to fit {carried.OwnerName} " +
              $"{BotDamageVisual.Label(carried.Part)}"
            : hurt
                ? $"Hold {InputHints.E} to repair yourself, or find the power box"
                : $"Find the power box and hold {InputHints.E} for " +
                  $"{PowerBoxRepairTarget.RepairHoldDuration:0} sec to repair";

        HUDController.Instance?.AddNotification("Toolkit picked up", what,
            HUDController.NotifType.Success);
    }

    public static void GrantKit(int count = 1)
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            if (NetworkManager.Singleton.IsServer)
                NetworkShopManager.Instance?.ServerGrantRepairKit(count);
            return;
        }
        _charges += Mathf.Max(1, count);
    }

    public static void GrantLocalKit(int count = 1) => _charges += Mathf.Max(1, count);

    public static bool ConsumeLocalKit()
    {
        if (_charges <= 0) return false;
        _charges--;
        return true;
    }

    /// Called when a toolkit is used or the event ends without repair.
    public static void ConsumeKit()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkShopManager.Instance?.ConsumeRepairKitFromAuthoritativeEvent();
            return;
        }
        ConsumeLocalKit();
    }

    public static ToolkitItem FindWorldPickupNear(Vector3 position, float radius)
    {
        float best = radius * radius;
        ToolkitItem result = null;
        foreach (var pickup in _worldPickups)
        {
            if (pickup == null) continue;
            float distance = Vector3.SqrMagnitude(pickup.transform.position - position);
            if (distance > best) continue;
            best = distance;
            result = pickup;
        }
        return result;
    }

    public static void ConsumeWorldPickupAt(Vector3 position)
    {
        var pickup = FindWorldPickupNear(position, 1f);
        if (pickup != null) Destroy(pickup.gameObject);
    }

    /// Returns a candidate spawn position without spawning — used by PowerFailureEvent to
    /// pick a position on the server and pass it to all clients via network payload.
    public static Vector3 PickSpawnPosition() => ChooseSpawnPosition();

    /// Spawns a toolkit on open floor (not overlapping other objects). If atPos is provided
    /// the toolkit appears there; otherwise a random valid floor position is chosen.
    public static GameObject Spawn(Vector3? atPos = null)
    {
        if (!Required) return null;
        Vector3 pos = atPos ?? ChooseSpawnPosition();
        GameObject go;

        if (KitPrefab != null)
        {
            go = Object.Instantiate(KitPrefab, pos, Quaternion.identity);
            go.name = "ToolkitItem";
            // Ensure there's a collider so IInteractable works.
            if (go.GetComponentInChildren<Collider>() == null)
                go.AddComponent<BoxCollider>();
        }
        else
        {
            // Fallback: yellow cube so the game works without the prefab assigned.
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "ToolkitItem";
            go.transform.position   = pos;
            go.transform.localScale = new Vector3(0.22f, 0.22f, 0.22f);
            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                rend.material.color = new Color(1f, 0.75f, 0f);
                rend.material.SetColor("_BaseColor", new Color(1f, 0.75f, 0f));
            }
        }

        go.AddComponent<ToolkitItem>();
        return go;
    }

    // Samples random points across the warehouse floor, raycasting down and rejecting
    // spots that land on top of shelves/boxes or overlap existing solid colliders — so
    // the toolkit always spawns in open floor space, not embedded in props.
    static Vector3 ChooseSpawnPosition()
    {
        const int   attempts  = 25;
        const float floorMaxY = 0.5f; // above this the raycast hit a shelf/box top, not the floor

        for (int i = 0; i < attempts; i++)
        {
            // Over the whole floor, not the corner of it the old typed-in rectangle covered.
            Vector3 origin = WarehouseBounds.RandomPoint(6f);

            if (!Physics.Raycast(origin, Vector3.down, out var hit, 12f, ~0, QueryTriggerInteraction.Ignore))
                continue;
            if (hit.point.y > floorMaxY)
                continue;

            Vector3 candidate = hit.point + Vector3.up * 0.15f;
            bool blocked = false;
            foreach (var col in Physics.OverlapSphere(candidate, 0.35f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (col == hit.collider) continue; // the floor itself — expected, not a blocker
                blocked = true;
                break;
            }
            if (blocked) continue;

            return candidate;
        }

        // Absolute fallback: open aisle near the warehouse center.
        return new Vector3(0f, 0.2f, -10f);
    }
}
