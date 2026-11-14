using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Unity.Netcode;
using Unity.Netcode.Components;

/// Tools → Warehouse Demo → Setup Thief System
///
/// One-shot wiring for the thief/melee mechanic:
///   1. adds MeleeCombat to the player in the open scene,
///   2. builds Assets/Prefabs/Thief.prefab from a Zombie Slaughter body (given the zombie
///      animator controller, a NavMeshAgent + ThiefController),
///   3. assigns that prefab to GameEventManager,
///   4. drops a ThiefSpawnPoints holder in the scene for hand-authored doors.
///
/// Everything it does has a runtime fallback, so re-running it is safe and skipping it only
/// costs you the nice model — ThiefEvent still runs with a primitive stand-in.
public static class SetupThiefSystem
{
    // The raider is a zombie. Guy 2 is used because its avatar is the one Unity's humanoid
    // mapper accepts outright (see ZombieAssetSetup) — the girl bodies never map.
    const string SourcePrefabPath = "Assets/Zombie Slaughter/Prefabs/Zombie Guy 2A/Zombie Guy 2A.prefab";
    const string ThiefPrefabPath  = "Assets/Prefabs/Thief.prefab";
    const string MaterialFolder   = "Assets/Zombie Slaughter/Materials";

    [MenuItem("Tools/Warehouse Demo/Setup Thief System")]
    static void Run() => Execute(interactive: true);

    /// The whole setup, minus the modal dialog when interactive is false — that is the form
    /// automation (and anything driving the editor over MCP) has to use: a DisplayDialog with
    /// nobody at the keyboard blocks the editor until somebody clicks it.
    public static string Execute(bool interactive)
    {
        var report = new List<string>();

        if (!AddMeleeToPlayer(report, interactive))
            return "ABORTED: " + string.Join("\n", report);

        // The zombie body and its clips have to be humanoid before a prefab built on them is
        // worth anything, and that setup is idempotent — so just run it.
        report.Add(ZombieAssetSetup.Execute());

        var prefab = BuildThiefPrefab(report);
        AssignToEventManager(prefab, report);
        RegisterNetworkPrefab(prefab, report);
        EnsureSpawnPointsHolder(report);

        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        string body = string.Join("\n", report);
        Debug.Log("[Thief] Setup complete:\n" + body);
        if (interactive)
        {
            EditorUtility.DisplayDialog("Thief system", body +
                "\n\nTest it now: select the GameEventManager object and use its context " +
                "menu -> Debug / Fire Thief.", "OK");
        }

        return body;
    }

    // ── 1. Player ─────────────────────────────────────────────────────────────

    static bool AddMeleeToPlayer(List<string> report, bool interactive)
    {
        GameObject player = null;

        var tpc = Object.FindFirstObjectByType<StarterAssets.ThirdPersonController>();
        if (tpc != null) player = tpc.gameObject;

        if (player == null)
        {
            var fpc = Object.FindFirstObjectByType<FirstPersonController>();
            if (fpc != null) player = fpc.gameObject;
        }

        if (player == null) player = GameObject.FindWithTag("Player");

        if (player == null)
        {
            const string msg = "No player found in the open scene (looked for " +
                "ThirdPersonController, FirstPersonController, then the \"Player\" tag). " +
                "Open the game scene first.";

            report.Add(msg);
            if (interactive) EditorUtility.DisplayDialog("Thief system", msg, "OK");
            else Debug.LogError("[Thief] " + msg);
            return false;
        }

        if (player.GetComponent<MeleeCombat>() == null)
        {
            Undo.AddComponent<MeleeCombat>(player);
            report.Add($"• MeleeCombat added to '{player.name}' (left click swings).");
        }
        else
        {
            report.Add($"• MeleeCombat already on '{player.name}'.");
        }

        return true;
    }

    // ── 2. Thief prefab ───────────────────────────────────────────────────────

    static GameObject BuildThiefPrefab(List<string> report)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefabPath);
        if (source == null)
        {
            report.Add($"• Thief prefab SKIPPED — {SourcePrefabPath} not found. " +
                       "ThiefEvent will use its capsule fallback.");
            return null;
        }

        var temp = (GameObject)PrefabUtility.InstantiatePrefab(source);
        PrefabUtility.UnpackPrefabInstance(temp, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        temp.name = "Thief";

        StripPlayerComponents(temp);
        ApplyZombieAnimator(temp, report);

        var agent = temp.GetComponent<NavMeshAgent>();
        if (agent == null) agent = temp.AddComponent<NavMeshAgent>();
        agent.radius                 = 0.35f;
        agent.height                 = 1.8f;
        agent.baseOffset             = 0f;
        agent.angularSpeed           = 480f;
        agent.acceleration           = 12f;
        agent.obstacleAvoidanceType  = ObstacleAvoidanceType.LowQualityObstacleAvoidance;

        if (temp.GetComponent<ThiefController>() == null) temp.AddComponent<ThiefController>();

        AssignBodyVariants(temp, report);

        // On the prefab so its clip slots are visible in the inspector; ThiefController adds
        // one at runtime anyway, and with no clips assigned it synthesizes the sounds.
        if (temp.GetComponent<ZombieVoice>() == null) temp.AddComponent<ZombieVoice>();

        AddNetworking(temp);

        // A capsule so melee raycasts and box collisions have something to hit. The thief is
        // not pushed by physics — the agent owns his position — so no Rigidbody.
        var capsule = temp.GetComponent<CapsuleCollider>();
        if (capsule == null) capsule = temp.AddComponent<CapsuleCollider>();
        capsule.center = new Vector3(0f, 0.9f, 0f);
        capsule.height = 1.8f;
        capsule.radius = 0.32f;

        Directory.CreateDirectory(Path.GetDirectoryName(ThiefPrefabPath));
        var saved = PrefabUtility.SaveAsPrefabAsset(temp, ThiefPrefabPath);
        Object.DestroyImmediate(temp);

        report.Add($"• Thief prefab written to {ThiefPrefabPath}.");
        return saved;
    }

    /// Hands the raider the five skins of its own mesh family. Within a family the pack varies
    /// only the material — A through E share one mesh and one skeleton — so a raid can be mixed
    /// without a second prefab, a second avatar or a second NGO registration. Crossing families
    /// (Guy 1 vs 2 vs 3) would mean different skeletons and is deliberately not attempted here.
    static void AssignBodyVariants(GameObject root, List<string> report)
    {
        var thief = root.GetComponent<ThiefController>();
        if (thief == null) return;

        // ".../Zombie Guy 2A/Zombie Guy 2A.prefab" -> "Zombie Guy 2"
        string bodyName = Path.GetFileNameWithoutExtension(SourcePrefabPath);
        string family   = bodyName.Substring(0, bodyName.Length - 1);

        var mats = new List<Material>();
        foreach (char v in "ABCDE")
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/{family}{v}.mat");
            if (mat != null) mats.Add(mat);
        }

        if (mats.Count == 0)
        {
            report.Add($"• No \"{family}x\" materials in {MaterialFolder} — every raider looks the same.");
            return;
        }

        var so  = new SerializedObject(thief);
        var arr = so.FindProperty("_bodyVariants");
        arr.arraySize = mats.Count;
        for (int i = 0; i < mats.Count; i++)
            arr.GetArrayElementAtIndex(i).objectReferenceValue = mats[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        report.Add($"• Body skins: {mats.Count} ({family}A-{family}{(char)('A' + mats.Count - 1)}).");
    }

    // Removes everything that makes this a player, keeping only what makes it a walking body.
    // MonoBehaviours go first: built-ins like CharacterController cannot be destroyed while a
    // [RequireComponent] script still depends on them.
    static void StripPlayerComponents(GameObject root)
    {
        foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null) Object.DestroyImmediate(mb);

        foreach (var comp in root.GetComponentsInChildren<Component>(true))
        {
            if (comp == null) continue;
            if (comp is Transform) continue;
            if (comp is Animator) continue;
            if (comp is SkinnedMeshRenderer || comp is MeshRenderer || comp is MeshFilter) continue;

            Object.DestroyImmediate(comp);
        }
    }

    // Hooks the zombie controller onto whatever Animator came with the body. The avatar is
    // already on the model prefab, so only the controller has to be supplied.
    static void ApplyZombieAnimator(GameObject root, List<string> report)
    {
        var animator = root.GetComponentInChildren<Animator>(true);
        if (animator == null)
        {
            report.Add("• No Animator on the zombie body — the raider will not animate.");
            return;
        }

        var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
            ZombieAssetSetup.ControllerPath);

        if (controller == null)
        {
            report.Add($"• {ZombieAssetSetup.ControllerPath} missing — run Setup Zombie Assets.");
            return;
        }

        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion           = false;
        animator.updateMode                = AnimatorUpdateMode.Normal;
        animator.cullingMode               = AnimatorCullingMode.CullUpdateTransforms;

        report.Add("• Zombie animator controller applied.");
    }

    // Multiplayer: the raider is a server-spawned NetworkObject whose body is replicated by a
    // server-authoritative NetworkTransform. NetworkThief carries the rest (role, weapon, loot,
    // damage RPCs). All of it is inert in single player.
    static void AddNetworking(GameObject root)
    {
        if (root.GetComponent<NetworkObject>() == null) root.AddComponent<NetworkObject>();
        if (root.GetComponent<NetworkThief>()  == null) root.AddComponent<NetworkThief>();

        var netTransform = root.GetComponent<NetworkTransform>();
        if (netTransform == null) netTransform = root.AddComponent<NetworkTransform>();

        // Server writes, everyone reads — the FSM lives on the server and nowhere else.
        netTransform.AuthorityMode = NetworkTransform.AuthorityModes.Server;
        netTransform.InLocalSpace  = false;
        netTransform.Interpolate   = true;

        // He never changes size, and his pitch/roll only matter for the tip-over, which every
        // client replays from its own broadcast — so only position and yaw go over the wire.
        netTransform.SyncScaleX = netTransform.SyncScaleY = netTransform.SyncScaleZ = false;
        netTransform.SyncRotAngleX = netTransform.SyncRotAngleZ = false;
    }

    /// A runtime-spawned NetworkObject has to be in the NetworkManager's prefab list or NGO
    /// refuses to spawn it. The list may live in a shared NetworkPrefabsList asset or inline
    /// on the manager, so handle both.
    static void RegisterNetworkPrefab(GameObject prefab, List<string> report)
    {
        if (prefab == null) return;

        var manager = Object.FindFirstObjectByType<NetworkManager>();
        if (manager == null)
        {
            report.Add("• No NetworkManager in this scene — thief prefab not registered for co-op.");
            return;
        }

        var config = manager.NetworkConfig;
        if (config == null)
        {
            report.Add("• NetworkManager has no NetworkConfig — thief prefab not registered.");
            return;
        }

        var lists = config.Prefabs.NetworkPrefabsLists;
        if (lists != null && lists.Count > 0 && lists[0] != null)
        {
            // The default list auto-populates itself from every prefab that carries a
            // NetworkObject, and it does so on asset import — i.e. possibly after this runs.
            // Adding a second entry for the same prefab makes NGO log a duplicate
            // GlobalObjectIdHash error and drop registrations, so count first, then dedupe.
            int found = CountEntries(lists[0], prefab);

            if (found == 0)
            {
                lists[0].Add(new NetworkPrefab { Prefab = prefab });
                report.Add($"• Thief prefab added to network prefab list '{lists[0].name}'.");
            }
            else if (found > 1)
            {
                int removed = RemoveDuplicates(lists[0], prefab);
                report.Add($"• Removed {removed} duplicate thief entries from '{lists[0].name}'.");
            }
            else
            {
                report.Add($"• Thief prefab already listed in '{lists[0].name}'.");
            }

            EditorUtility.SetDirty(lists[0]);
            AssetDatabase.SaveAssetIfDirty(lists[0]);
        }
        else if (!config.Prefabs.Contains(prefab))
        {
            config.Prefabs.Add(new NetworkPrefab { Prefab = prefab });
            report.Add("• Thief prefab added to NetworkManager's inline prefab list.");
        }

        EditorUtility.SetDirty(manager);
    }

    static int CountEntries(NetworkPrefabsList list, GameObject prefab)
    {
        int found = 0;
        foreach (var entry in list.PrefabList)
            if (entry != null && entry.Prefab == prefab) found++;
        return found;
    }

    static int RemoveDuplicates(NetworkPrefabsList list, GameObject prefab)
    {
        // PrefabList is read-only from the outside; the asset exposes Add/Remove instead.
        var extra   = new List<NetworkPrefab>();
        bool keptOne = false;

        foreach (var entry in list.PrefabList)
        {
            if (entry == null || entry.Prefab != prefab) continue;
            if (!keptOne) { keptOne = true; continue; }
            extra.Add(entry);
        }

        foreach (var entry in extra) list.Remove(entry);
        return extra.Count;
    }

    // ── 3. GameEventManager ───────────────────────────────────────────────────

    static void AssignToEventManager(GameObject prefab, List<string> report)
    {
        var gem = Object.FindFirstObjectByType<GameEventManager>();
        if (gem == null)
        {
            report.Add("• GameEventManager not in this scene — assign the thief prefab by hand.");
            return;
        }

        if (prefab == null)
        {
            report.Add("• No prefab to assign to GameEventManager.");
            return;
        }

        var so   = new SerializedObject(gem);
        var prop = so.FindProperty("_thiefPrefab");
        if (prop == null)
        {
            report.Add("• GameEventManager has no _thiefPrefab field — recompile and re-run.");
            return;
        }

        prop.objectReferenceValue = prefab;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(gem);

        report.Add("• Thief prefab assigned to GameEventManager.");
    }

    // ── 4. Spawn points ───────────────────────────────────────────────────────

    static void EnsureSpawnPointsHolder(List<string> report)
    {
        if (Object.FindFirstObjectByType<ThiefSpawnPoints>() != null)
        {
            report.Add("• ThiefSpawnPoints already in the scene.");
            return;
        }

        var go = new GameObject("ThiefSpawnPoints");
        go.AddComponent<ThiefSpawnPoints>();
        Undo.RegisterCreatedObjectUndo(go, "Create ThiefSpawnPoints");

        report.Add("• ThiefSpawnPoints created — leave its lists empty to auto-pick navmesh " +
                   "positions around the warehouse edge, or drop door transforms in.");
    }
}
