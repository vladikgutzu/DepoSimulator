using System.Collections.Generic;
using UnityEngine;

/// Interactable facade for the four scene charging stations. Components are installed at runtime
/// so hand-authored station transforms remain the source of truth.
public sealed class ChargingStation : MonoBehaviour, IInteractable, ICanInteractCheck, INoOutlineHighlight
{
    static readonly List<ChargingStation> All = new();
    static float _nextInstallAttempt;
    public int StationIndex { get; private set; } = -1;
    public Transform ChargePoint { get; private set; }
    public string InteractPrompt => IsLocalRobotChargingHere()
        ? $"Disconnect charger  {InputHints.E}"
        : $"Use charging station  {InputHints.E}";

    public static ChargingStation ByIndex(int index) => index >= 0 && index < All.Count ? All[index] : null;

    public static ChargingStation FindNearby(Vector3 playerPosition, float radius = 3f)
    {
        if (All.Count == 0 && Time.unscaledTime >= _nextInstallAttempt)
        {
            _nextInstallAttempt = Time.unscaledTime + 1f;
            Install();
        }

        ChargingStation best = null;
        float bestDistance = radius;
        foreach (var station in All)
        {
            if (station == null || !station.isActiveAndEnabled) continue;
            float distance = Vector3.Distance(playerPosition, station.ClosestInteractionPoint(playerPosition));
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = station;
        }
        return best;
    }

    Vector3 ClosestInteractionPoint(Vector3 from)
    {
        Vector3 closest = transform.position;
        float best = float.MaxValue;
        foreach (var collider in GetComponentsInChildren<Collider>(true))
        {
            if (!collider.enabled) continue;
            Vector3 point = collider.ClosestPoint(from);
            float sqr = (point - from).sqrMagnitude;
            if (sqr < best) { best = sqr; closest = point; }
        }
        return closest;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); SortAndIndex(); }
    void OnDisable() { All.Remove(this); SortAndIndex(); }

    public void Configure()
    {
        ChargePoint = transform.Find("ChargePoint");
        if (ChargePoint == null)
            foreach (var child in GetComponentsInChildren<Transform>(true))
                if (child.name.Equals("ChargePoint", System.StringComparison.OrdinalIgnoreCase)) { ChargePoint = child; break; }

        // The collider authored on the station root is the interaction volume. ChargePoint sits
        // inside that volume, so leaving it solid makes PhysX eject the CharacterController before
        // it can dock. Only convert root colliders; child mesh colliders remain physical geometry.
        foreach (var interactionCollider in GetComponents<Collider>())
            interactionCollider.isTrigger = true;

        EnsureAimCollider();
        SortAndIndex();
    }

    void EnsureAimCollider()
    {
        // A station already has its designer-authored interaction volume. Never add a second solid
        // collider around the complete model: it would enclose ChargePoint and block docking.
        if (GetComponent<Collider>() != null) return;

        var aim = gameObject.AddComponent<BoxCollider>();
        aim.center = Vector3.zero;
        aim.size = Vector3.one;
        aim.isTrigger = true;
    }

    public bool CanInteract(GameObject interactor)
    {
        var battery = RobotBatteryController.EnsureLocal();
        if (battery == null) return false;
        if (battery.IsCharging) return battery.StationIndex == StationIndex;
        return battery.ChargePercent < 99.9f;
    }

    public void Interact(GameObject interactor)
    {
        var battery = RobotBatteryController.EnsureLocal();
        if (battery == null) return;
        if (battery.IsCharging)
        {
            if (battery.StationIndex == StationIndex) battery.StopCharging();
            return;
        }
        var chargingUi = ChargingUIController.Ensure();
        if (chargingUi != null)
        {
            chargingUi.ShowSelection(this, battery);
            return;
        }

        HUDController.Instance?.AddNotification(
            "Charging UI unavailable",
            "The HUD is still loading. Try the charging station again.",
            HUDController.NotifType.Warning);
    }

    bool IsLocalRobotChargingHere()
    {
        var battery = RobotBatteryController.EnsureLocal();
        return battery != null && battery.IsCharging && battery.StationIndex == StationIndex;
    }

    static void SortAndIndex()
    {
        All.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        for (int i = 0; i < All.Count; i++) All[i].StationIndex = i;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap() => Install();

    public static void Install()
    {
        foreach (var root in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
        {
            if (!root.name.StartsWith("ChargingStation", System.StringComparison.Ordinal)) continue;
            var station = root.GetComponent<ChargingStation>() ?? root.gameObject.AddComponent<ChargingStation>();
            station.Configure();
        }

        Transform player = PlayerFinder.Find();
        if (player != null && player.GetComponent<RobotBatteryController>() == null)
            player.gameObject.AddComponent<RobotBatteryController>();

        var hud = Object.FindAnyObjectByType<HUDController>(FindObjectsInactive.Include);
        if (hud != null && hud.GetComponent<ChargingUIController>() == null)
            hud.gameObject.AddComponent<ChargingUIController>();
    }
}
