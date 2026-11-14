using System.Collections.Generic;
using UnityEngine;

/// Tags a BoxPhysicsInteractable-enabled scene box as containing a specific order part.
/// Pickup, carry, hand-off between players, and placement at the assembly bench are all
/// handled by BoxPhysicsInteractable + AssemblyBench's carried-box path — this component
/// only carries the data (which part, whether it's currently needed), mirroring how
/// PackedOrderBox tags a shipping-bound box without owning any of its own interaction logic.
[DisallowMultipleComponent]
public class BoxWithPart : MonoBehaviour
{
    static readonly HashSet<BoxWithPart> s_all = new();
    public static IReadOnlyCollection<BoxWithPart> All => s_all;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetRegistry() => s_all.Clear();

    void OnEnable()  => s_all.Add(this);
    void OnDisable() => s_all.Remove(this);

    void OnDestroy()
    {
        // A required crate can be destroyed by the fall-through watchdog or another world event.
        // Ask the spawner to replace it on the next frame. Normal installation is safe: by then
        // the bench count has already advanced and IsPartNeeded returns false.
        if (IsRequired && Part != null)
            PartSpawner.Instance?.RequestReplacement(Part);
    }

    public PartDefinition Part       { get; private set; }
    public bool           IsRequired { get; private set; }

    public void Setup(PartDefinition part, bool required)
    {
        Part       = part;
        IsRequired = required;
    }
}
