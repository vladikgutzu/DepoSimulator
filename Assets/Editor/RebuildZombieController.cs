using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// Rebuilds only the zombie animator controller.
///
/// Setup Zombie Assets does this too, but it re-imports six meshes and thirty-four animation FBXs
/// on the way, which is minutes of work to change one blend tree. The controller build is already
/// its own step inside that tool, so this just calls it.
public static class RebuildZombieController
{
    [MenuItem("Tools/Warehouse Demo/Rebuild Zombie Controller")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new List<string>();

        typeof(ZombieAssetSetup)
            .GetMethod("BuildController", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { report });

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(ZombieAssetSetup.ControllerPath);
        if (ac == null) return "controller missing after the rebuild — " + string.Join("; ", report);

        // What the movement trees ended up with, since that is the whole point of the rebuild.
        var trees = new List<string>();
        foreach (var st in ac.layers[0].stateMachine.states)
        {
            if (st.state.motion is not BlendTree bt) continue;

            var parts = new List<string>();
            foreach (var child in bt.children)
                parts.Add($"{(child.motion != null ? child.motion.name : "null")}@{child.threshold:0.00}");

            trees.Add(st.state.name + ": " + string.Join("  ", parts));
        }

        return string.Join("\n", trees) + "\n" + string.Join("\n", report);
    }
}
