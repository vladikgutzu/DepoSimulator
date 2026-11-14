using UnityEngine;

/// The radio charger on the wall (charger_GRP_01), turned into a repair job while
/// RadioInterferenceEvent is running — the same way a power box becomes one.
///
/// It used to be a blue cube conjured beside the bench, which read as a debug primitive because
/// that is what it was. The warehouse already has chargers modelled into the wall, so the job goes
/// on one of those: the player walks to a thing that looks like radio equipment.
/// Only Dispatcher can fix it in multiplayer; any player in solo mode.
/// Requires the toolkit and a hold of RepairHoldDuration seconds.
// No RequireComponent here on purpose: Collider is abstract, so Unity cannot satisfy it — it
// refuses to add this component at all and hands back null. The charger has no collider of its
// own, so Spawn adds a BoxCollider itself, before the component that needs it.
public class RadioRepairPanel : MonoBehaviour, IHoldInteractable, ICanInteractCheck
{
    static readonly System.Collections.Generic.List<RadioRepairPanel> Live = new();

    public static RadioRepairPanel FindNearest(Vector3 position, float radius)
    {
        float best = radius * radius;
        RadioRepairPanel result = null;
        foreach (var panel in Live)
        {
            if (panel == null) continue;
            float distance = Vector3.SqrMagnitude(panel.transform.position - position);
            if (distance > best) continue;
            best = distance;
            result = panel;
        }
        return result;
    }
    public const float RepairHoldDuration = 5f;

    public static RadioRepairPanel Current { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetStatics()
    {
        Current = null;
        Live.Clear();
    }

    void OnEnable()
    {
        Current = this;
        if (!Live.Contains(this)) Live.Add(this);
    }

    void OnDisable()
    {
        if (Current == this) Current = null;
        Live.Remove(this);
    }

    public bool  RequiresHold => GameEventManager.IsRadioInterfering;
    public float HoldDuration => RepairHoldDuration;

    public string InteractPrompt
    {
        get
        {
            if (!GameEventManager.IsRadioInterfering) return "";
            if (!ToolkitItem.IsHeld) return "Find the toolkit first";

            // Says why it cannot be used, rather than offering a hold that will not start.
            if (!CanInteract(null)) return "The Dispatcher has to fix the radio";

            return $"{InputHints.EK} Hold to repair radio ({RepairHoldDuration:0}s)";
        }
    }

    public bool CanInteract(GameObject interactor)
    {
        if (!GameEventManager.IsRadioInterfering) return false;
        if (!ToolkitItem.IsHeld) return false;

        // In multiplayer the radio is the Dispatcher's job — but asked of NetworkPlayer.Local, not
        // of the object that pressed E.
        //
        // That object is the scene rig, and the rig has no NetworkPlayer: the component lives on
        // the puppet the network spawns beside it. So this used to be null on every machine, the
        // check came out false for everybody, and PlayerInteraction refuses to start a hold it has
        // been told cannot succeed. Walk up, hold E, nothing happens, no message — for the
        // Dispatcher too, which is what made it look like the old toolkit rule was still in force.
        if (Unity.Netcode.NetworkManager.Singleton?.IsListening == true)
        {
            // Nobody playing Dispatcher? Then the radio is everyone's job — the alternative is a
            // shift that can never get its comms back.
            if (!RoleTaskManager.TeamHasRole(PlayerRoleType.Dispatcher)) return true;

            return NetworkPlayer.Local != null
                && NetworkPlayer.Local.RoleType == PlayerRoleType.Dispatcher;
        }

        // Solo: any player can fix it
        return true;
    }

    public void Interact(GameObject interactor)
    {
        // PlayerInteraction only calls this once the hold completes, but re-check —
        // role/toolkit state could have changed mid-hold (e.g. toolkit consumed elsewhere).
        if (!CanInteract(interactor)) return;

        if (Unity.Netcode.NetworkManager.Singleton?.IsListening == true)
            CoopGameManager.Instance?.FixRadioServerRpc();
        else
            RadioInterferenceEvent.Active?.Fix();
    }

    /// The charger this job is on, and what had to be added to it to make it one — all of it taken
    /// back when the radio is fixed, so the charger goes back to being scenery.
    RepairGlow  _glow;
    BoxCollider _addedCollider;

    /// Tried in order. The first name is the one to use if the scene ever gets an object called
    /// exactly that — renaming a charger is then all it takes to move the job onto it. The second
    /// is what the warehouse actually ships with today.
    static readonly string[] ChargerNames = { "radioCharger_GRP_01", "charger_GRP_01" };

    /// Puts the job on a charger. Returns the charger, or null when the scene has none.
    public static GameObject Spawn()
    {
        var charger = PickCharger();
        if (charger == null)
        {
            Debug.LogWarning($"[RadioRepairPanel] none of [{string.Join(", ", ChargerNames)}] in " +
                             "the scene — the radio cannot be repaired this shift.");
            return null;
        }

        if (charger.GetComponent<RadioRepairPanel>() != null) return charger.gameObject;

        // The chargers carry no collider, and PlayerInteraction finds things by raycast. One sized
        // to what is actually drawn is added for the length of the job and taken away with it —
        // and before the component, which is useless without something to aim at.
        var added = charger.GetComponent<Collider>() == null ? AddCollider(charger) : null;

        var panel = charger.gameObject.AddComponent<RadioRepairPanel>();
        panel._addedCollider = added;
        panel._glow          = RepairGlow.On(charger.gameObject);
        return charger.gameObject;
    }

    /// Everyone picks the same charger without being told which: the scene is the same on every
    /// machine, so the first one in a deterministic order is the same object everywhere.
    static Transform PickCharger()
    {
        var all = FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        foreach (var wanted in ChargerNames)
        {
            var found = new System.Collections.Generic.List<Transform>();
            foreach (var t in all)
                if (t.name == wanted) found.Add(t);

            if (found.Count == 0) continue;

            SceneNetIds.SortDeterministically(found);
            return found[0];
        }

        return null;
    }

    static BoxCollider AddCollider(Transform charger)
    {
        var col = charger.gameObject.AddComponent<BoxCollider>();

        var rends = charger.GetComponentsInChildren<Renderer>(false);
        if (rends.Length == 0) return col;

        var bounds = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) bounds.Encapsulate(rends[i].bounds);

        col.center = charger.InverseTransformPoint(bounds.center);
        col.size   = charger.InverseTransformVector(bounds.size);

        // A negative extent from a mirrored transform is a collider Unity refuses to build.
        col.size = new Vector3(Mathf.Abs(col.size.x), Mathf.Abs(col.size.y), Mathf.Abs(col.size.z));
        return col;
    }

    /// Back to scenery: the job's collider and glow go, the charger stays where it always was.
    void OnDestroy()
    {
        _glow?.Off();
        if (_addedCollider != null) Destroy(_addedCollider);
    }
}
