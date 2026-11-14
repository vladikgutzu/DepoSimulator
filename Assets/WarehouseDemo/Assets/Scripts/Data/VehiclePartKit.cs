using UnityEngine;

/// Breaks a purchasable vehicle down into the parts its delivery pallet must carry.
/// One box spawns per entry in Parts — a vehicle whose FBX is a single mesh should
/// list exactly one part; a vehicle modeled as separate child meshes should list one
/// PartDefinition per meaningful chunk (frame, wheels, handle, etc).
[CreateAssetMenu(menuName = "Warehouse/Vehicle Part Kit", fileName = "New Vehicle Part Kit")]
public class VehiclePartKit : ScriptableObject
{
    public PartDefinition[] Parts;

    [Tooltip("Seconds between purchase and the delivery pallet appearing at a gate.")]
    public float DeliveryDelaySeconds = 20f;
}
