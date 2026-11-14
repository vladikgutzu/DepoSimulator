using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Reflection;

public static class SetCarryDistance
{
    // Global carry settings (all boxes)
    const float HoldDistance = 0.15f;
    const float HandHeight   = 0.85f;

    // Per-prefab carry rotation overrides (key = name substring)
    static readonly (string nameContains, Vector3 rotation)[] RotationOverrides =
    {
        ("box_wide_flat", new Vector3(-100f, 0f, 0f)),
    };

    [MenuItem("Tools/Warehouse Demo/Apply Carry Settings")]
    public static void Execute()
    {
        var all = Object.FindObjectsByType<BoxPhysicsInteractable>(FindObjectsInactive.Include);
        var t   = typeof(BoxPhysicsInteractable);
        var hdF = t.GetField("_holdDistance",  BindingFlags.NonPublic | BindingFlags.Instance);
        var hhF = t.GetField("_handHeight",    BindingFlags.NonPublic | BindingFlags.Instance);
        var crF = t.GetField("_carryRotation", BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var bpi in all)
        {
            hdF?.SetValue(bpi, HoldDistance);
            hhF?.SetValue(bpi, HandHeight);

            // Find matching rotation override by name
            var rot = Vector3.zero;
            foreach (var (nameContains, rotation) in RotationOverrides)
                if (bpi.name.ToLower().Contains(nameContains.ToLower()))
                    rot = rotation;

            crF?.SetValue(bpi, rot);
            EditorUtility.SetDirty(bpi);
        }

        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log($"[CarryDist] Updated {all.Length} boxes.");
    }
}
