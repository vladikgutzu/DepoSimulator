using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public class SetupGhostVisualizer
{
    public static void Execute()
    {
        // ── Material ──────────────────────────────────────────────
        var ghostMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/utility/M_shdr_alpha.mat");
        if (ghostMat == null) { Debug.LogError("[GhostSetup] M_shdr_alpha.mat not found"); return; }

        // ── Orders ────────────────────────────────────────────────
        OrderDefinition driveOrder = null, frameOrder = null, armsOrder = null;
        foreach (var guid in AssetDatabase.FindAssets("t:OrderDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var order = AssetDatabase.LoadAssetAtPath<OrderDefinition>(path);
            if (order == null) continue;
            if (order.name == "order_moto_drive") driveOrder = order;
            else if (order.name == "order_moto_frame") frameOrder = order;
            else if (order.name == "order_moto_arms")  armsOrder  = order;
        }
        Debug.Log($"[GhostSetup] Orders found — drive:{driveOrder != null} frame:{frameOrder != null} arms:{armsOrder != null}");

        // ── Find moto_bot_forassembly ─────────────────────────────
        var immersive = GameObject.Find("ImmersiveTraining");
        if (immersive == null) { Debug.LogError("[GhostSetup] ImmersiveTraining not found in scene"); return; }
        var motoBot = immersive.transform.Find("moto_bot_forassembly")?.gameObject;
        if (motoBot == null) { Debug.LogError("[GhostSetup] moto_bot_forassembly not found"); return; }

        // ── Find / create GameplayRoot parent ──────────────────────
        var gameplayRoot = GameObject.Find("GameplayRoot");
        Transform parent = gameplayRoot != null ? gameplayRoot.transform : null;

        // ── Remove previous setup if re-running ───────────────────
        var existing = GameObject.Find("AssemblyGhostVisualizer");
        if (existing != null) Object.DestroyImmediate(existing);
        var existingRefs = GameObject.Find("AssemblyReferences");
        if (existingRefs != null) Object.DestroyImmediate(existingRefs);

        // ── Part names per order (match moto_bot_forassembly children) ─
        string[] driveParts = { "motor", "cables", "cable_housing" };
        string[] frameParts = { "body_main", "body_front", "frame_cover", "bridge" };
        string[] armsParts  = { "arm_chassis", "chassis_support", "claw",
                                "inner_shaft", "outer_shaft", "gliders_left", "gliders_right" };

        // ── Create reference groups (invisible, contain duplicated meshes) ─
        var refRoot = new GameObject("AssemblyReferences");
        if (parent != null) refRoot.transform.SetParent(parent, false);

        var refDrive = CreateRefGroup("Ref_moto_drive", motoBot, driveParts, refRoot.transform);
        var refFrame = CreateRefGroup("Ref_moto_frame", motoBot, frameParts, refRoot.transform);
        var refArms  = CreateRefGroup("Ref_moto_arms",  motoBot, armsParts,  refRoot.transform);

        // Disable — only ghost copies will be visible at runtime
        refDrive.SetActive(false);
        refFrame.SetActive(false);
        refArms.SetActive(false);

        // ── Create AssemblyGhostVisualizer GO ─────────────────────
        var vizGO = new GameObject("AssemblyGhostVisualizer");
        if (parent != null) vizGO.transform.SetParent(parent, false);
        var viz = vizGO.AddComponent<AssemblyGhostVisualizer>();

        // ── Assign all fields via SerializedObject (handles struct array correctly) ─
        var so = new SerializedObject(viz);
        so.FindProperty("_ghostMaterial").objectReferenceValue = ghostMat;

        var entriesProp = so.FindProperty("_entries");
        entriesProp.arraySize = 3;
        AssignEntry(entriesProp.GetArrayElementAtIndex(0), driveOrder, refDrive);
        AssignEntry(entriesProp.GetArrayElementAtIndex(1), frameOrder, refFrame);
        AssignEntry(entriesProp.GetArrayElementAtIndex(2), armsOrder,  refArms);
        so.ApplyModifiedProperties();

        // ── Save scene ────────────────────────────────────────────
        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log("[GhostSetup] Done! AssemblyGhostVisualizer created and configured.");
    }

    static void AssignEntry(SerializedProperty entry, OrderDefinition order, GameObject refGO)
    {
        entry.FindPropertyRelative("Order").objectReferenceValue = order;
        entry.FindPropertyRelative("ReferenceAssembly").objectReferenceValue = refGO;
    }

    // Duplicates relevant children of motoBot into a new parent GO.
    // worldPositionStays = true → ghosts appear at the real assembled world positions.
    static GameObject CreateRefGroup(string groupName, GameObject motoBot, string[] childNames, Transform parent)
    {
        var group = new GameObject(groupName);
        group.transform.SetParent(parent, false);

        foreach (var childName in childNames)
        {
            var child = motoBot.transform.Find(childName);
            if (child == null)
            {
                Debug.LogWarning($"[GhostSetup] '{childName}' not found in {motoBot.name} — skipped");
                continue;
            }
            var copy = Object.Instantiate(child.gameObject, parent: group.transform, worldPositionStays: true);
            copy.name = childName;
        }

        return group;
    }
}
