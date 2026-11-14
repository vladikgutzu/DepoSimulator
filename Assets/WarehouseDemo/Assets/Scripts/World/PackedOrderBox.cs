using UnityEngine;

/// Marks a BoxPhysicsInteractable as a packed order ready for delivery.
/// ShippingZone checks for this component on the carried box to accept delivery.
[DisallowMultipleComponent]
public class PackedOrderBox : MonoBehaviour
{
    public OrderDefinition Order { get; set; }
    public string OrderName { get; private set; }

    public void BindOrder(OrderDefinition order, string orderName)
    {
        Order = order;
        OrderName = !string.IsNullOrEmpty(orderName) ? orderName : order?.OrderName;
    }

    public bool MatchesOrder(OrderDefinition activeOrder) =>
        activeOrder != null && !string.IsNullOrEmpty(OrderName) &&
        string.Equals(OrderName, activeOrder.OrderName, System.StringComparison.Ordinal);
}
