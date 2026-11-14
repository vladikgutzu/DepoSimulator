using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

/// Puts a starting set of weapon pickups into the scene, one inside each gate raiders break in
/// through.
///
/// A starting point, not a decree — drag them wherever they belong. The reason they begin at the
/// breach gates is that this is the one place the game already promises a weapon will be wanted:
/// GateBreachEvent spends twenty seconds hammering on a shutter precisely so somebody can go and
/// arm themselves, and that promise is only real if there is something within reach of where the
/// noise is coming from.
///
/// Kinds are dealt round-robin so no two neighbouring gates hold the same thing, which is what
/// makes the choice of which gate to run to worth anything.
///
/// Re-runnable: it clears the ones it placed before and lays them out again. Anything you moved or
/// added by hand is left alone — it only owns objects still parented under its own container.
public static class PlaceWeaponPickups
{
    const string PickupFolder  = "Assets/Resources/Catalog/Weapons/Pickups";
    const string ContainerName = "WeaponPickups";

    /// How far inside the doorway a pickup sits: past the threshold, not in it.
    const float InsideOffset = 2.2f;

    [MenuItem("Tools/Warehouse/Place Weapon Pickups At Gates")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var gates = Object.FindObjectsByType<GateAnimator>(FindObjectsInactive.Include,
                                                           FindObjectsSortMode.None)
                          .Where(g => g.IsZombieSpawn && g.FloorAnchor != null)
                          .OrderBy(g => g.name, System.StringComparer.Ordinal)
                          .ToList();

        if (gates.Count == 0)
            return "no gate is marked for zombie spawns — nothing placed";

        var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { PickupFolder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .OrderBy(p => p, System.StringComparer.Ordinal)
            .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
            .Where(p => p != null)
            .ToList();

        if (prefabs.Count == 0)
            return $"no pickup prefabs in {PickupFolder} — run Build Weapon Pickups first";

        // Only what this tool owns is cleared, so hand placement survives a re-run.
        var container = GameObject.Find(ContainerName);
        if (container != null) Object.DestroyImmediate(container);
        container = new GameObject(ContainerName);
        Undo.RegisterCreatedObjectUndo(container, "Place weapon pickups");

        var report = new List<string>();

        for (int i = 0; i < gates.Count; i++)
        {
            var gate = gates[i];

            // The gate's own idea of which way the warehouse is — the doorway faces different
            // local axes on different gates, so this cannot be read off a transform.
            if (!gate.TryGetSides(out Vector3 inside, out Vector3 outside)) continue;

            Vector3 inward = (inside - outside).normalized;
            Vector3 spot   = gate.FloorAnchor.position + inward * InsideOffset;

            // On the floor an agent can reach, or it is a weapon behind a crate.
            if (NavMesh.SamplePosition(spot, out var hit, 4f, NavMesh.AllAreas)) spot = hit.position;

            var prefab = prefabs[i % prefabs.Count];
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, container.transform);

            go.transform.position = spot + Vector3.up * 0.12f;
            go.transform.rotation = Quaternion.Euler(90f, Random.Range(0f, 360f), 0f);
            go.name = $"{prefab.name}_{gate.name}";

            report.Add($"• {prefab.name} inside {gate.name}");
        }

        var scene = gates[0].gameObject.scene;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        return string.Join("\n", report) +
               $"\n\n{report.Count} pickup(s) under \"{ContainerName}\", scene saved. Move them " +
               "wherever they belong — while the scene holds any, the shift-start scatter stays off.";
    }
}
