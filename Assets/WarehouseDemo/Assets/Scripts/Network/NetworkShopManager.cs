using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Server-authoritative vehicle shop: validates funds against the team's shared score, deducts,
/// and spawns the purchased vehicle at its designated spawn point. Purchased vehicles are
/// team-shared (not owner-locked) like every other interactable in the game — anyone can use
/// them once bought. Single-player (no NetworkManager listening) spends + spawns locally instead.
public class NetworkShopManager : NetworkBehaviour
{
    public static NetworkShopManager Instance { get; private set; }

    /// Raised only after the authoritative purchase path has resolved.  UI must listen to this
    /// instead of assuming that sending a ServerRpc means the purchase succeeded.
    public static event System.Action<VehicleType, bool, bool> OnPurchaseResolved;

    public enum VehicleType
    {
        Cart,
        PalletJack,
        Forklift,
        HandScanner,
        RepairKit,
        EmptyPallets,
        OrderInsurance,
        ExtendedPowerCore,
    }

    [System.Serializable]
    public struct ShopEntry
    {
        public VehicleType Type;
        public string      DisplayName;
        public GameObject  Prefab;
        public Transform   SpawnPoint;
        public int         Price;
        [Tooltip("If assigned, purchase does not spawn the vehicle immediately — VehicleDeliveryManager "
               + "spawns a parts pallet at a random gate after a delay instead. Leave null for instant spawn.")]
        public VehiclePartKit PartKit;
    }

    [SerializeField] ShopEntry[] _entries;

    readonly List<ShopEntry> _catalog = new();
    readonly NetworkVariable<int> _soldOutMask = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<int> _repairKitChargesNet = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<int> _insuranceChargesNet = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<double> _scannerActiveUntilNet = new(
        0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<int> _powerCoreStockNet = new(
        4, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    int _localSoldOutMask;
    int _confirmedSoldOutMask;
    int _insuranceCharges;
    float _scannerActiveUntil;
    int _localPowerCoreStock = 4;
    readonly Dictionary<ulong, double> _nextPurchaseAt = new();
    readonly Dictionary<ulong, double> _nextToolkitPickupAt = new();

    const double PurchaseCooldownSeconds = 0.35d;
    const double ToolkitPickupCooldownSeconds = 0.35d;
    const float ShopUseDistance = 5f;

    public IReadOnlyList<ShopEntry> Entries => _catalog;
    public int InsuranceCharges => IsNetworkSession() ? _insuranceChargesNet.Value : _insuranceCharges;
    public int RepairKitCharges => IsNetworkSession() ? _repairKitChargesNet.Value : ToolkitItem.LocalCharges;
    public bool ScannerActive => IsNetworkSession()
        ? NetworkManager.ServerTime.Time < _scannerActiveUntilNet.Value
        : Time.unscaledTime < _scannerActiveUntil;
    public float ScannerSecondsRemaining => IsNetworkSession()
        ? Mathf.Max(0f, (float)(_scannerActiveUntilNet.Value - NetworkManager.ServerTime.Time))
        : Mathf.Max(0f, _scannerActiveUntil - Time.unscaledTime);
    public int PowerCoreStock => IsNetworkSession() ? _powerCoreStockNet.Value : _localPowerCoreStock;
    public const int PowerCoreInitialStock = 4;

    public static bool IsLimitedStock(VehicleType type) => type is
        VehicleType.Cart or VehicleType.PalletJack or VehicleType.Forklift;

    public bool IsOutOfStock(VehicleType type)
    {
        if (type == VehicleType.ExtendedPowerCore) return PowerCoreStock <= 0;
        if (!IsLimitedStock(type)) return false;
        // The purchase-confirmation RPC can arrive one network tick before the NetworkVariable
        // delta.  Keep the confirmed bit locally too so the card cannot briefly return to 1/1.
        int mask = IsNetworkSession()
            ? _soldOutMask.Value | _confirmedSoldOutMask
            : _localSoldOutMask;
        return (mask & StockBit(type)) != 0;
    }

    public bool RequiresDelivery(VehicleType type) =>
        TryGetEntry(type, out var entry) && EntryRequiresDelivery(entry);

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        BuildCatalog();
    }

    public override void OnNetworkSpawn()
    {
        _scannerActiveUntilNet.OnValueChanged += OnScannerExpiryChanged;
        PartHighlighter.RefreshAll();
    }

    public override void OnNetworkDespawn()
    {
        _scannerActiveUntilNet.OnValueChanged -= OnScannerExpiryChanged;
        _nextPurchaseAt.Clear();
        _nextToolkitPickupAt.Clear();
    }

    void OnScannerExpiryChanged(double _, double __) => PartHighlighter.RefreshAll();

    void BuildCatalog()
    {
        _catalog.Clear();
        if (_entries != null) _catalog.AddRange(_entries);

        AddRuntimeEntry(VehicleType.HandScanner,    "Hand Scanner",    100);
        AddRuntimeEntry(VehicleType.RepairKit,      "Repair Kit",      250);
        AddRuntimeEntry(VehicleType.EmptyPallets,   "Empty Pallets",   120);
        AddRuntimeEntry(VehicleType.OrderInsurance, "Order Insurance", 200);
        AddRuntimeEntry(VehicleType.ExtendedPowerCore, "Extended Power Core", 500);
    }

    void AddRuntimeEntry(VehicleType type, string displayName, int price)
    {
        foreach (var entry in _catalog)
            if (entry.Type == type) return;

        _catalog.Add(new ShopEntry
        {
            Type = type,
            DisplayName = displayName,
            Price = price,
        });
    }

    public bool TryGetEntry(VehicleType type, out ShopEntry entry)
    {
        foreach (var e in _catalog)
        {
            if (e.Type != type) continue;
            entry = e;
            return true;
        }
        entry = default;
        return false;
    }

    /// Single-player path: spend + spawn locally, no networking involved.
    public bool BuyLocal(VehicleType type)
    {
        if (!TryGetEntry(type, out var entry)) return false;
        if (IsOutOfStock(type))
        {
            ResolvePurchaseLocal(entry, false, "Out of stock", "This vehicle was already purchased by the team");
            return false;
        }
        if (GameManager.Instance == null || !GameManager.Instance.TrySpend(entry.Price))
        {
            ResolvePurchaseLocal(entry, false, "Insufficient funds", $"Not enough money to buy: {entry.DisplayName}");
            return false;
        }

        MarkOutOfStock(type);
        float newCapacity = ApplyPurchase(entry, null);
        if (type == VehicleType.ExtendedPowerCore) DecrementPowerCoreStock();
        if (type == VehicleType.ExtendedPowerCore)
        {
            NotifyPurchase("Extended Power Core installed",
                $"Your battery capacity increased to {newCapacity:0}% · stock {PowerCoreStock}/{PowerCoreInitialStock}");
            OnPurchaseResolved?.Invoke(type, true, false);
            return true;
        }
        ResolvePurchaseLocal(entry, true);
        return true;
    }

    [ServerRpc(RequireOwnership = false)]
    public void BuyVehicleServerRpc(int vehicleTypeIndex, ServerRpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        var player = FindPlayer(senderId);
        if (player == null || player.IsDeadNet.Value || player.IsRagdolledNet.Value ||
            !IsNearOrderBoard(player))
            return;

        double now = NetworkManager.ServerTime.Time;
        if (_nextPurchaseAt.TryGetValue(senderId, out double nextAllowed) && now < nextAllowed)
            return;
        _nextPurchaseAt[senderId] = now + PurchaseCooldownSeconds;

        var type = (VehicleType)vehicleTypeIndex;
        if (!TryGetEntry(type, out var entry)) return;

        if (IsOutOfStock(type))
        {
            SendPurchaseRejectedClientRpc(
                (int)type, "Out of stock", "This vehicle was already purchased by the team",
                SingleClient(senderId));
            return;
        }

        if (GameManager.Instance == null || !GameManager.Instance.TrySpend(entry.Price))
        {
            SendPurchaseRejectedClientRpc(
                (int)type, "Insufficient funds", $"Not enough money to buy: {entry.DisplayName}",
                SingleClient(senderId));
            return;
        }

        MarkOutOfStock(type);
        float newCapacity = ApplyPurchase(entry, player);
        if (type == VehicleType.ExtendedPowerCore) DecrementPowerCoreStock();
        PurchaseAcceptedClientRpc((int)type, entry.DisplayName, EntryRequiresDelivery(entry),
            IsLimitedStock(type), player.PlayerName, newCapacity, PowerCoreStock);
    }

    float ApplyPurchase(ShopEntry entry, NetworkPlayer buyer)
    {
        switch (entry.Type)
        {
            case VehicleType.HandScanner:
                if (IsNetworkSession())
                {
                    double now = NetworkManager.ServerTime.Time;
                    _scannerActiveUntilNet.Value = System.Math.Max(
                    _scannerActiveUntilNet.Value, now) + 45d;
                    PartHighlighter.RefreshAll();
                }
                else ActivateScannerLocal(45f);
                break;

            case VehicleType.RepairKit:
                if (IsNetworkSession()) _repairKitChargesNet.Value++;
                else GrantRepairKitLocal();
                break;

            case VehicleType.EmptyPallets:
                VehicleDeliveryManager.Instance?.DeliverEmptyPalletBundle(3);
                break;

            case VehicleType.OrderInsurance:
                if (IsNetworkSession()) _insuranceChargesNet.Value++;
                else _insuranceCharges++;
                break;

            case VehicleType.ExtendedPowerCore:
                if (IsNetworkSession()) return buyer != null
                    ? buyer.AddBatteryCapacityServer(25f)
                    : 100f;
                var battery = RobotBatteryController.EnsureLocal();
                return battery != null ? battery.IncreaseCapacity(25f) : 100f;

            default:
                if (EntryRequiresDelivery(entry))
                    VehicleDeliveryManager.Instance?.QueueDelivery(entry);
                else
                    SpawnVehicle(entry);
                break;
        }
        return 0f;
    }

    void DecrementPowerCoreStock()
    {
        if (IsNetworkSession()) _powerCoreStockNet.Value = Mathf.Max(0, _powerCoreStockNet.Value - 1);
        else _localPowerCoreStock = Mathf.Max(0, _localPowerCoreStock - 1);
    }

    public bool TryConsumeOrderInsurance()
    {
        if (IsNetworkSession())
        {
            if (!IsServer || _insuranceChargesNet.Value <= 0) return false;
            _insuranceChargesNet.Value--;
            NotifyInsuranceUsedClientRpc();
            return true;
        }

        if (_insuranceCharges <= 0) return false;
        _insuranceCharges--;
        NotifyPurchase("Insurance used", "Order combo was protected");
        return true;
    }

    public bool ServerHasRepairKit => !IsNetworkSession() || _repairKitChargesNet.Value > 0;

    /// Server-only atomic consumption used by validated repair transactions.
    public bool TryConsumeRepairKitServer()
    {
        if (!IsNetworkSession()) return ToolkitItem.ConsumeLocalKit();
        if (!IsServer || _repairKitChargesNet.Value <= 0) return false;
        _repairKitChargesNet.Value--;
        return true;
    }

    public void ServerGrantRepairKit(int count = 1)
    {
        if (IsNetworkSession())
        {
            if (IsServer) _repairKitChargesNet.Value += Mathf.Max(1, count);
            return;
        }
        ToolkitItem.GrantLocalKit(count);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestPickupRepairKitServerRpc(Vector3 claimedPosition,
        ServerRpcParams rpcParams = default)
    {
        var pickup = ToolkitItem.FindWorldPickupNear(claimedPosition, 1f);
        ulong senderId = rpcParams.Receive.SenderClientId;
        var player = FindPlayer(senderId);
        if (pickup == null || player == null || player.IsDeadNet.Value || player.IsRagdolledNet.Value ||
            Vector3.SqrMagnitude(player.transform.position - pickup.transform.position) > 9f)
            return;

        double now = NetworkManager.ServerTime.Time;
        if (_nextToolkitPickupAt.TryGetValue(senderId, out double nextAllowed) && now < nextAllowed)
            return;
        _nextToolkitPickupAt[senderId] = now + ToolkitPickupCooldownSeconds;

        Vector3 acceptedPosition = pickup.transform.position;
        _repairKitChargesNet.Value++;
        Destroy(pickup.gameObject);
        ConsumeWorldToolkitClientRpc(acceptedPosition);
        ToolkitPickupConfirmedClientRpc(SingleClient(senderId));
    }

    [ClientRpc]
    void ConsumeWorldToolkitClientRpc(Vector3 position)
    {
        if (IsServer) return;
        ToolkitItem.ConsumeWorldPickupAt(position);
    }

    [ClientRpc]
    void ToolkitPickupConfirmedClientRpc(ClientRpcParams _ = default) =>
        HUDController.Instance?.AddNotification(
            "Toolkit picked up", "Repair charge added to team inventory",
            HUDController.NotifType.Success);

    static NetworkPlayer FindPlayer(ulong clientId)
    {
        foreach (var player in NetworkPlayer.All)
            if (player.OwnerClientId == clientId) return player;
        return null;
    }

    static bool IsNearOrderBoard(NetworkPlayer player)
    {
        if (player == null) return false;
        foreach (var board in Object.FindObjectsByType<OrderBoard>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (board != null && (player.transform.position - board.transform.position).sqrMagnitude <=
                ShopUseDistance * ShopUseDistance)
                return true;
        return false;
    }

    /// Event completion owns consumption for multi-step jobs such as a power failure.
    public void ConsumeRepairKitFromAuthoritativeEvent()
    {
        if (!IsNetworkSession())
        {
            ToolkitItem.ConsumeLocalKit();
            return;
        }
        if (IsServer && _repairKitChargesNet.Value > 0) _repairKitChargesNet.Value--;
    }

    void ActivateScannerLocal(float duration)
    {
        _scannerActiveUntil = Mathf.Max(_scannerActiveUntil, Time.unscaledTime) + duration;
        PartHighlighter.RefreshAll();
    }

    void GrantRepairKitLocal()
    {
        ToolkitItem.GrantLocalKit();
    }

    void NotifyPurchase(string title, string body) =>
        HUDController.Instance?.AddNotification(title, body, HUDController.NotifType.Success);

    bool IsNetworkSession() => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    [ClientRpc]
    void NotifyInsuranceUsedClientRpc() =>
        NotifyPurchase("Insurance used", "Order combo was protected");

    static bool EntryRequiresDelivery(ShopEntry entry) =>
        entry.PartKit != null && entry.PartKit.Parts != null && entry.PartKit.Parts.Length > 0;

    static int StockBit(VehicleType type) => 1 << (int)type;

    void MarkOutOfStock(VehicleType type)
    {
        if (!IsLimitedStock(type)) return;
        int bit = StockBit(type);
        if (IsNetworkSession()) _soldOutMask.Value |= bit;
        else                    _localSoldOutMask |= bit;
    }

    void ResolvePurchaseLocal(ShopEntry entry, bool success, string failureTitle = null, string failureBody = null)
    {
        bool delivery = EntryRequiresDelivery(entry);
        if (success)
        {
            string body = PurchaseConfirmationBody(entry.Type, delivery, IsLimitedStock(entry.Type));
            NotifyPurchase($"{entry.DisplayName} purchased", body);
        }
        else
        {
            HUDController.Instance?.AddNotification(
                failureTitle, failureBody, HUDController.NotifType.Warning);
        }

        OnPurchaseResolved?.Invoke(entry.Type, success, delivery);
    }

    [ClientRpc]
    void PurchaseAcceptedClientRpc(int vehicleTypeIndex, string displayName, bool delivery, bool limitedStock,
        string buyerName, float newCapacity, int powerCoreStock)
    {
        var type = (VehicleType)vehicleTypeIndex;
        if (limitedStock) _confirmedSoldOutMask |= StockBit(type);
        if (type == VehicleType.ExtendedPowerCore)
            NotifyPurchase("Extended Power Core installed",
                $"{buyerName} installed Extended Power Core — capacity increased to {newCapacity:0}% · stock {powerCoreStock}/{PowerCoreInitialStock}");
        else
            NotifyPurchase($"{displayName} purchased", PurchaseConfirmationBody(type, delivery, limitedStock));
        OnPurchaseResolved?.Invoke(type, true, delivery);
    }

    string PurchaseConfirmationBody(VehicleType type, bool delivery, bool limitedStock) => type switch
    {
        VehicleType.HandScanner    => "Team scan active for 45 seconds · item remains in stock",
        VehicleType.RepairKit      => $"Repair kits available: {RepairKitCharges} · item remains in stock",
        VehicleType.EmptyPallets   => "3 empty pallets delivered to a warehouse gate · item remains in stock",
        VehicleType.OrderInsurance => $"Protected deadlines: {InsuranceCharges} · item remains in stock",
        _ when delivery            => "Out of stock · wait for the parts delivery",
        _ when limitedStock        => "Out of stock · available at the equipment bay",
        _                          => "Purchase applied · item remains in stock",
    };

    [ClientRpc]
    void SendPurchaseRejectedClientRpc(
        int vehicleTypeIndex, string title, string body, ClientRpcParams rpcParams = default)
    {
        HUDController.Instance?.AddNotification(title, body, HUDController.NotifType.Warning);
        OnPurchaseResolved?.Invoke((VehicleType)vehicleTypeIndex, false, false);
    }

    /// Spawns the purchased vehicle immediately at its designated spawn point. Called directly
    /// for instant-buy entries, or by VehicleDeliveryManager once every delivery box has been
    /// collected for entries with a PartKit.
    public void SpawnVehicle(ShopEntry entry)
    {
        if (entry.Prefab == null) return;

        // Persistent delivery state is consumed by every peer, but only the server may create
        // the actual shared vehicle. This also makes repeated client-side reconciliation safe.
        if (IsNetworkSession() && !IsServer) return;

        Vector3    pos = entry.SpawnPoint != null ? entry.SpawnPoint.position : transform.position;
        Quaternion rot = entry.SpawnPoint != null ? entry.SpawnPoint.rotation : Quaternion.identity;
        var go = Instantiate(entry.Prefab, pos, rot);

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            go.GetComponent<NetworkObject>()?.Spawn();
    }

    static ClientRpcParams SingleClient(ulong clientId) => new()
    {
        Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
    };
}
