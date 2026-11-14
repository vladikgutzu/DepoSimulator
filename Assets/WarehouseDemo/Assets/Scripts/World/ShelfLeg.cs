using UnityEngine;

/// Marker component — place on the leg/upright child GameObjects of a PhysicsShelf.
/// Only collisions with these objects trigger TipOver; hits to shelves, boards, or
/// pallets above are ignored.
public class ShelfLeg : MonoBehaviour { }
