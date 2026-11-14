using UnityEngine;

/// Tags a box spawned by VehicleDeliveryManager as one part of a pending vehicle delivery.
/// Pure data tag — pickup, carry, and hand-off are handled entirely by BoxPhysicsInteractable
/// (the same component every other physics box uses), mirroring how BoxWithPart tags an order
/// part without owning any interaction logic of its own. AssemblyBench checks for this
/// component on a carried box and consumes it there, independent of any active order.
[DisallowMultipleComponent]
public class VehiclePartBox : MonoBehaviour
{
    public int             DeliveryId { get; private set; }
    public int             SlotIndex  { get; private set; }
    public PartDefinition  Part       { get; private set; }

    public void Setup(int deliveryId, int slotIndex, PartDefinition part)
    {
        DeliveryId = deliveryId;
        SlotIndex  = slotIndex;
        Part       = part;

        if (part.VisualPrefab != null)
        {
            var visual = Instantiate(part.VisualPrefab, transform);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            foreach (var col in visual.GetComponentsInChildren<Collider>())
                col.enabled = false;
        }
        else
        {
            var r = GetComponent<Renderer>();
            if (r != null) r.material.color = part.DebugColor;
        }
    }
}
