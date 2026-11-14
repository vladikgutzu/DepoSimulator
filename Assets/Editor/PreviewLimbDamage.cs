using System.Linq;
using UnityEditor;
using UnityEngine;

/// Breaks every limb on a body in the open scene so the wreckage can be tuned by eye.
///
/// The pack ships its torn chunks with no snapping points — its own readme says to put one in the
/// bone and rotate it by hand until it sticks outwards — so the four numbers on BotDamageVisual
/// (_debrisAlong, _debrisOut, _debrisScale, _debrisTurn) are the recorded result of somebody
/// looking at it. This is what lets that somebody look at it: run it, select the body, and drag the
/// sliders with the scene view open.
///
/// Run again to put everything back.
public static class PreviewLimbDamage
{
    const string BodyPath = "Assets/Destructible_Humanoid_Robot/Prefabs/HumanBot_Skinned_A.prefab";
    const string StandIn  = "__LimbDamagePreview";

    [MenuItem("Tools/Warehouse/Preview Limb Damage")]
    public static void Run() => Debug.Log(Execute());

    [MenuItem("Tools/Warehouse/Preview Limb Damage — Clear")]
    public static void Clear()
    {
        var stand = GameObject.Find(StandIn);
        if (stand != null) { Object.DestroyImmediate(stand); Debug.Log("[LimbDamage] preview removed."); return; }

        foreach (var v in Object.FindObjectsByType<BotDamageVisual>(FindObjectsInactive.Include,
                                                                    FindObjectsSortMode.None))
            v.Clear();

        Debug.Log("[LimbDamage] panels restored.");
    }

    public static string Execute()
    {
        // A body already in the scene is the honest thing to look at; a stand-in is the fallback
        // when there is no player in it yet.
        var target = Object.FindObjectsByType<Animator>(FindObjectsInactive.Include,
                                                        FindObjectsSortMode.None)
            .Select(a => a.gameObject)
            .FirstOrDefault(go => go.GetComponentsInChildren<Renderer>(true)
                                    .Any(r => r.name == "B_Head_Geom"));

        bool madeStandIn = false;

        if (target == null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPath);
            if (prefab == null) return $"no body in the scene and no prefab at {BodyPath}";

            target = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            target.name = StandIn;
            target.transform.position = SceneView.lastActiveSceneView != null
                ? SceneView.lastActiveSceneView.pivot
                : new Vector3(0f, 1f, 0f);

            madeStandIn = true;
        }

        var visual = BotDamageVisual.Ensure(target);
        if (!visual.Supported)
            return $"{target.name} has no separable panels — this is one of the pack's " +
                   "single-mesh variants, and nothing can be taken off it.";

        visual.Clear();

        foreach (BodyPart part in System.Enum.GetValues(typeof(BodyPart)))
            if (part != BodyPart.None && part != BodyPart.Torso)
                visual.SetBroken(part, true);

        Selection.activeGameObject = target;
        SceneView.RepaintAll();

        return $"every limb broken on {target.name}" +
               (madeStandIn ? " (stand-in created — use the Clear menu item to remove it)" : "") +
               ".\nSelect it and tune BotDamageVisual: _debrisAlong, _debrisOut, _debrisScale, " +
               "_debrisTurn. Re-run this to apply changes.";
    }
}
