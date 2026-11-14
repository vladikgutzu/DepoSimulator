using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// Spawned at the player's death position. Teammates hold [E] for 3 seconds to rescue.
/// Forklift operators can also rescue by lifting the shelf (handled via ReachTruckController).
///
/// Two flavors, distinguished by which field is set:
/// - Local marker (_deathManager set): shown on the victim's own client, via Spawn(). Only
///   used for the solo self-view / bookkeeping — CanInteract blocks the victim rescuing themself.
/// - Remote marker (_ownerClientId set): shown on every OTHER client, via SpawnRemote(), driven
///   by NetworkPlayer.IsDeadNet. Rescuing one routes through NetworkPlayer.RequestRescueServerRpc
///   since the victim's actual PlayerDeathManager only exists on the victim's own client.
[RequireComponent(typeof(Collider))]
public class CrushedPlayerMarker : MonoBehaviour, IInteractable, ICanInteractCheck
{
    public static CrushedPlayerMarker Current { get; private set; }

    static readonly Dictionary<ulong, CrushedPlayerMarker> _remoteMarkers = new();

    /// All markers currently visible on this client — local self-marker (if any) plus every
    /// remote teammate marker. Used by ReachTruckController to find a rescuable marker in range.
    public static IEnumerable<CrushedPlayerMarker> AllMarkers
    {
        get
        {
            if (Current != null) yield return Current;
            foreach (var m in _remoteMarkers.Values)
                if (m != null) yield return m;
        }
    }

    const float RescueDuration = 3f;
    const float MaxRescueRange = 2.5f;

    PlayerDeathManager _deathManager;
    ulong?             _ownerClientId;
    GameObject         _rescuer;
    float              _rescueProgress;

    public ulong? OwnerClientId => _ownerClientId;

    public string InteractPrompt
    {
        get
        {
            if (_rescuer != null)
            {
                int pct = Mathf.RoundToInt(_rescueProgress / RescueDuration * 100f);
                return $"Pulling out... {pct}%";
            }
            return $"Pull out from under the shelf  {InputHints.E}  (hold 3 sec)";
        }
    }

    public bool CanInteract(GameObject interactor)
    {
        // Remote markers only ever exist on OTHER clients (see NetworkPlayer.OnDeadStateChanged,
        // which skips spawning one on the victim's own client) — always rescuable.
        if (_ownerClientId.HasValue) return true;

        if (_deathManager == null) return false;
        // In multiplayer only other players can rescue (not yourself)
        bool isMP = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;
        if (!isMP) return true;
        // Don't rescue yourself
        return interactor != _deathManager.gameObject;
    }

    public void Interact(GameObject interactor)
    {
        if (_rescuer != null) return;   // already being rescued
        _rescuer        = interactor;
        _rescueProgress = 0f;

        if (_ownerClientId.HasValue)
            NetworkPlayer.Local?.BeginRescueServerRpc(_ownerClientId.Value);
    }

    void Update()
    {
        if (_rescuer == null) return;

        // Cancel if rescuer walked away
        float dist = Vector3.Distance(_rescuer.transform.position, transform.position);
        if (dist > MaxRescueRange) { CancelRescue(); return; }

        // Cancel if E released
        var kb = Keyboard.current;
        var gp = Gamepad.current;
        bool held = GameInputBindings.IsPressed(GameBindingAction.Interact);
        if (!held) { CancelRescue(); return; }

        _rescueProgress += Time.deltaTime;
        if (_rescueProgress >= RescueDuration)
            CompleteRescue();
    }

    void CancelRescue()
    {
        if (_ownerClientId.HasValue)
            NetworkPlayer.Local?.CancelRescueServerRpc();
        _rescuer        = null;
        _rescueProgress = 0f;
    }

    void CompleteRescue()
    {
        if (_deathManager != null)
        {
            _deathManager.RescueByTeammate(_rescuer.transform.position);
            // DestroyMarker is called inside PlayerDeathManager.Respawn()
        }
        else if (_ownerClientId.HasValue)
        {
            // Route through the network — this client has no direct reference to the
            // victim's PlayerDeathManager, only their own NetworkPlayer.
            NetworkPlayer.Local?.RequestRescueServerRpc(_ownerClientId.Value, false);
        }
    }

    void OnDestroy()
    {
        if (Current == this) Current = null;
        if (_ownerClientId.HasValue
            && _remoteMarkers.TryGetValue(_ownerClientId.Value, out var m) && m == this)
            _remoteMarkers.Remove(_ownerClientId.Value);
    }

    // ── Spawn / destroy ───────────────────────────────────────────────────────

    public static void Spawn(Vector3 position, PlayerDeathManager deathManager)
    {
        DestroyMarker();
        var marker = CreateMarker(position);
        marker._deathManager = deathManager;
        Current = marker;
    }

    /// Shown on every client OTHER than the victim — driven by NetworkPlayer.IsDeadNet.
    public static void SpawnRemote(Vector3 position, ulong ownerClientId)
    {
        if (_remoteMarkers.TryGetValue(ownerClientId, out var existing) && existing != null) return;
        var marker = CreateMarker(position);
        marker._ownerClientId = ownerClientId;
        _remoteMarkers[ownerClientId] = marker;
    }

    public static void DestroyMarkerFor(ulong ownerClientId)
    {
        if (_remoteMarkers.TryGetValue(ownerClientId, out var marker) && marker != null)
            Destroy(marker.gameObject);
        _remoteMarkers.Remove(ownerClientId);
    }

    public static void DestroyMarker()
    {
        if (Current != null)
        {
            Destroy(Current.gameObject);
            Current = null;
        }
    }

    static CrushedPlayerMarker CreateMarker(Vector3 position)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "CrushedPlayerMarker";
        go.transform.position = position;
        go.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);

        // Red marker — clearly visible
        var rend = go.GetComponent<Renderer>();
        if (rend != null)
        {
            var mat = new Material(rend.sharedMaterial);
            mat.color = new Color(0.85f, 0.1f, 0.1f);
            mat.SetColor("_BaseColor", new Color(0.85f, 0.1f, 0.1f));
            mat.SetColor("_EmissionColor", new Color(0.4f, 0.0f, 0.0f));
            mat.EnableKeyword("_EMISSION");
            rend.material = mat;
        }

        return go.AddComponent<CrushedPlayerMarker>();
    }
}
