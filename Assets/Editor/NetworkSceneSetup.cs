using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

/// Run via Tools → Warehouse Demo → Setup Network Scene
/// Adds NetworkManager, NetworkServices, CoopGameManager, CoopHUDExtension,
/// and creates the NetworkPlayer prefab registered with NetworkManager.
public class NetworkSceneSetup
{
    [MenuItem("Tools/Warehouse Demo/Setup Network Scene")]
    public static void Execute()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

        // ── 1. NetworkManager ───────────────────────────────────────────────────
        NetworkManager nm;
        var existingNM = GameObject.Find("NetworkManager");
        if (existingNM == null)
        {
            var nmGO = new GameObject("NetworkManager");
            nm = nmGO.AddComponent<NetworkManager>();
            nmGO.AddComponent<UnityTransport>();
            Debug.Log("[NetSetup] Created NetworkManager + UnityTransport");
        }
        else
        {
            nm = existingNM.GetComponent<NetworkManager>();
            Debug.Log("[NetSetup] NetworkManager already exists, skipping creation.");
        }

        // ── 2. NetworkServices (UGS singletons) ─────────────────────────────────
        if (GameObject.Find("NetworkServices") == null)
        {
            var svcGO = new GameObject("NetworkServices");
            svcGO.AddComponent<NetworkBootstrapper>();
            svcGO.AddComponent<NetworkLobbyManager>();
            svcGO.AddComponent<VivoxManager>();
            svcGO.AddComponent<RoleTaskManager>();
            Debug.Log("[NetSetup] Created NetworkServices GO with all manager components");
        }
        else
        {
            Debug.Log("[NetSetup] NetworkServices already exists, skipping.");
        }

        // ── 3. CoopGameManager (scene NetworkBehaviour) ─────────────────────────
        if (GameObject.Find("CoopGameManager") == null)
        {
            var cgGO = new GameObject("CoopGameManager");
            cgGO.AddComponent<NetworkObject>();
            cgGO.AddComponent<CoopGameManager>();
            Debug.Log("[NetSetup] Created CoopGameManager with NetworkObject");
        }
        else
        {
            Debug.Log("[NetSetup] CoopGameManager already exists, skipping.");
        }

        // ── 4. Network gameplay managers ────────────────────────────────────────
        EnsureNetworkManager("NetworkBoxManager",      typeof(NetworkBoxManager));
        EnsureNetworkManager("NetworkPhysBoxManager",  typeof(NetworkPhysBoxManager));
        EnsureNetworkManager("NetworkPalletManager",   typeof(NetworkPalletManager));
        EnsureNetworkManager("NetworkLadderManager",   typeof(NetworkLadderManager));

        // ── 5. CoopHUDExtension on HUD_UIDocument ──────────────────────────────
        var hudGO = GameObject.Find("HUD_UIDocument");
        if (hudGO != null)
        {
            if (hudGO.GetComponent<CoopHUDExtension>() == null)
            {
                hudGO.AddComponent<CoopHUDExtension>();
                Debug.Log("[NetSetup] Added CoopHUDExtension to HUD_UIDocument");
            }
            else
            {
                Debug.Log("[NetSetup] CoopHUDExtension already on HUD_UIDocument, skipping.");
            }
        }
        else
        {
            Debug.LogWarning("[NetSetup] HUD_UIDocument not found — CoopHUDExtension NOT added.");
        }

        // ── 6. NetworkPlayer prefab ──────────────────────────────────────────────
        const string prefabDir  = "Assets/WarehouseDemo/Assets/Prefabs";
        const string prefabPath = prefabDir + "/NetworkPlayer.prefab";

        if (!System.IO.Directory.Exists(prefabDir))
            System.IO.Directory.CreateDirectory(prefabDir);

        GameObject savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (savedPrefab == null)
        {
            var tmp = new GameObject("NetworkPlayer");
            tmp.AddComponent<NetworkObject>();
            tmp.AddComponent<NetworkPlayer>();

            savedPrefab = PrefabUtility.SaveAsPrefabAsset(tmp, prefabPath);
            Object.DestroyImmediate(tmp);
            AssetDatabase.Refresh();
            Debug.Log($"[NetSetup] Created NetworkPlayer prefab at {prefabPath}");
        }
        else
        {
            Debug.Log("[NetSetup] NetworkPlayer prefab already exists, skipping creation.");
        }

        // ── 7. Register prefab with NetworkManager ──────────────────────────────
        if (nm != null && savedPrefab != null)
        {
            bool alreadyRegistered = false;
            foreach (var entry in nm.NetworkConfig.Prefabs.Prefabs)
            {
                if (entry.Prefab == savedPrefab) { alreadyRegistered = true; break; }
            }

            if (!alreadyRegistered)
            {
                nm.NetworkConfig.Prefabs.Add(new NetworkPrefab { Prefab = savedPrefab });
                EditorUtility.SetDirty(nm);
                Debug.Log("[NetSetup] Registered NetworkPlayer prefab with NetworkManager");
            }
            else
            {
                Debug.Log("[NetSetup] NetworkPlayer prefab already registered.");
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[NetSetup] Scene saved. Network setup complete.");
    }

    static void EnsureNetworkManager(string goName, System.Type componentType)
    {
        if (GameObject.Find(goName) != null)
        {
            Debug.Log($"[NetSetup] {goName} already exists, skipping.");
            return;
        }
        var go = new GameObject(goName);
        go.AddComponent<NetworkObject>();
        go.AddComponent(componentType);
        Debug.Log($"[NetSetup] Created {goName} with NetworkObject + {componentType.Name}");
    }
}
