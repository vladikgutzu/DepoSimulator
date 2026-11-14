using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Lays one of every weapon in a row in front of the player, so a grip can be checked by walking
/// down the line and pressing E, instead of hunting the six pickups GameEventManager scatters
/// across the warehouse at the start of a shift.
///
/// Everything lands under one parent, TEMP_WeaponTestRow — delete that object and the row is gone.
/// A second run replaces the previous row rather than laying a second one beside it.
public static class LayOutWeaponsForTesting
{
    const string RowName = "TEMP_WeaponTestRow";
    const float  Ahead   = 2.5f;   // metres in front of the player the row starts
    const float  Spacing = 1.2f;   // between weapons, wide enough to aim at one at a time

    [MenuItem("Tools/Warehouse/Lay Out Weapons For Testing")]
    public static void Run() => Debug.Log(Execute());

    [MenuItem("Tools/Warehouse/Clear Weapon Test Row")]
    public static void Clear()
    {
        var row = GameObject.Find(RowName);
        if (row == null) { Debug.Log("no weapon test row in the scene"); return; }

        Object.DestroyImmediate(row);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("weapon test row removed");
    }

    public static string Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) return "no GameObject tagged 'Player' in the open scene";

        var defs = Resources.LoadAll<MeleeWeaponDefinition>("Catalog/Weapons")
                            .Where(d => d != null && d.Kind != MeleeWeaponKind.Fists)
                            .OrderBy(d => d.Kind)
                            .ToArray();
        if (defs.Length == 0) return "no weapon definitions under Resources/Catalog/Weapons";

        var old = GameObject.Find(RowName);
        if (old != null) Object.DestroyImmediate(old);

        var row = new GameObject(RowName);
        Undo.RegisterCreatedObjectUndo(row, "Lay out weapons");

        // Across his line of sight rather than along it, so the whole row is visible at once and
        // the crosshair picks out one weapon at a time instead of three behind each other.
        Vector3 forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        Vector3 centre = player.transform.position + forward * Ahead;
        float   start  = -(defs.Length - 1) * 0.5f * Spacing;

        var report = new StringBuilder($"laid out {defs.Length} weapons in front of the player\n");

        for (int i = 0; i < defs.Length; i++)
        {
            Vector3 pos = centre + right * (start + i * Spacing);
            pos.y = FloorUnder(pos, player.transform.position.y) + 0.15f;

            var go = MeleeWeaponItem.Spawn(defs[i].Kind, pos);
            go.transform.SetParent(row.transform, worldPositionStays: true);

            report.AppendLine($"  {defs[i].Kind,-18} {(defs[i].Grip == MeleeSwingArm.GripKind.TwoHanded ? "two-handed" : "one-handed")}");
        }

        EditorSceneManager.MarkSceneDirty(player.scene);

        report.AppendLine("\nPress Play and walk the line — E takes one, G puts it down. "
                        + "Tools/Warehouse/Clear Weapon Test Row removes the lot.");
        return report.ToString();
    }

    /// The floor under a spot, ignoring anything the player is standing on top of — a weapon half
    /// sunk into a pallet cannot be picked up, and one hovering above one looks broken.
    static float FloorUnder(Vector3 pos, float fallbackY)
    {
        var hits = Physics.RaycastAll(pos + Vector3.up * 3f, Vector3.down, 8f,
                                      ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var hit in hits)
        {
            if (hit.collider.GetComponentInParent<MeleeWeaponItem>() != null) continue;
            return hit.point.y;
        }
        return fallbackY;
    }
}
