using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;

/// Adds the PlayerArmature visual model to the NetworkPlayer prefab
/// so other players appear as the same character.
/// Run via: Tools → Warehouse Demo → Setup NetworkPlayer Avatar
public static class NetworkPlayerSetupTool
{
    const string NetworkPlayerPrefabPath =
        "Assets/WarehouseDemo/Assets/Prefabs/NetworkPlayer.prefab";

    const string PlayerArmaturePath =
        "Assets/StarterAssets/ThirdPersonController/Prefabs/PlayerArmature.prefab";

    [MenuItem("Tools/Warehouse Demo/Setup NetworkPlayer Avatar")]
    public static void SetupNetworkPlayerAvatar()
    {
        var npPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkPlayerPrefabPath);
        if (npPrefab == null)
        {
            Debug.LogError($"[Setup] NetworkPlayer prefab not found at: {NetworkPlayerPrefabPath}");
            return;
        }

        var armaturePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerArmaturePath);
        if (armaturePrefab == null)
        {
            Debug.LogError($"[Setup] PlayerArmature prefab not found at: {PlayerArmaturePath}");
            return;
        }

        using var editScope = new PrefabUtility.EditPrefabContentsScope(NetworkPlayerPrefabPath);
        var root = editScope.prefabContentsRoot;

        // Remove existing CharacterVisual if re-running the tool
        var existing = root.transform.Find("CharacterVisual");
        if (existing != null)
        {
            Object.DestroyImmediate(existing.gameObject);
            Debug.Log("[Setup] Removed old CharacterVisual, rebuilding...");
        }

        // Instantiate PlayerArmature as a child then UNPACK so component removal works.
        // PrefabUtility.InstantiatePrefab inside EditPrefabContentsScope creates a nested
        // prefab instance; Unity doesn't allow component removal on nested prefab instances
        // (only property overrides). Unpacking converts it to a plain GameObject.
        var visual = (GameObject)PrefabUtility.InstantiatePrefab(armaturePrefab, root.transform);
        PrefabUtility.UnpackPrefabInstance(visual, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        visual.name = "CharacterVisual";
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale    = Vector3.one;

        // Remove gameplay components — order matters: RequireComponent dependents first.
        // ThirdPersonController has [RequireComponent(CharacterController)] so CC must
        // be removed AFTER TPC; otherwise Unity silently blocks the removal.
        RemoveIfPresent(visual, "ThirdPersonController");
        RemoveIfPresent(visual, "StarterAssetsInputs");
        RemoveIfPresent(visual, "BasicRigidBodyPush");
        RemoveIfPresent<UnityEngine.InputSystem.PlayerInput>(visual);
        RemoveIfPresent<AudioSource>(visual);
        RemoveIfPresent<CharacterController>(visual);  // last — no longer required

        // ThirdPersonController was the receiver for the OnFootstep/OnLand animation events baked
        // into the locomotion clips it shares with the real player — without it those events log
        // "no receiver" on every step, which is expensive (full stack trace per LogError) and was
        // measurably tanking framerate on clients observing another player run.
        if (visual.GetComponent<RemoteAnimationEventSink>() == null)
            visual.AddComponent<RemoteAnimationEventSink>();

        // Remove camera root child (local player uses scene camera, not this one)
        var camRoot = visual.transform.Find("PlayerCameraRoot");
        if (camRoot != null) Object.DestroyImmediate(camRoot.gameObject);

        // Add NetworkAnimator and point it at CharacterVisual's Animator
        var existingNetAnim = root.GetComponent<NetworkAnimator>();
        if (existingNetAnim != null) Object.DestroyImmediate(existingNetAnim);

        var netAnim = root.AddComponent<NetworkAnimator>();
        // Set authority to Owner so each client drives their own animation state.
        // Property is internal-enum; set via SerializedObject to avoid compile-time dependency on enum name.
        var netAnimSo = new SerializedObject(netAnim);
        var authProp = netAnimSo.FindProperty("AuthorityMode");
        if (authProp != null) { authProp.enumValueIndex = 1; netAnimSo.ApplyModifiedPropertiesWithoutUndo(); } // 0=Server,1=Owner
        var visualAnimator = visual.GetComponent<Animator>();
        if (visualAnimator != null)
        {
            netAnim.Animator = visualAnimator;
            Debug.Log("[Setup] NetworkAnimator added (Owner authority) and linked to CharacterVisual Animator.");
        }
        else
        {
            Debug.LogWarning("[Setup] No Animator found on CharacterVisual root.");
        }

        // Attach ModelWatcher for diagnostics (remove before shipping)
        var watcher = visual.GetComponent<ModelWatcher>();
        if (watcher == null) visual.AddComponent<ModelWatcher>();

        // Wire up _characterModel field on NetworkPlayer
        var np = root.GetComponent<NetworkPlayer>();
        if (np != null)
        {
            var so = new SerializedObject(np);
            var prop = so.FindProperty("_characterModel");
            if (prop != null)
            {
                prop.objectReferenceValue = visual;
                so.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log("[Setup] _characterModel linked to CharacterVisual.");
            }
            else
            {
                Debug.LogWarning("[Setup] _characterModel field not found on NetworkPlayer. Assign it manually.");
            }
        }

        Debug.Log("[Setup] NetworkPlayer avatar setup complete. CharacterVisual + NetworkAnimator added.");
    }

    [MenuItem("Tools/Warehouse Demo/Assign NetworkPlayer Prefab")]
    public static void AssignPlayerPrefab()
    {
        var nm = UnityEngine.Object.FindFirstObjectByType<Unity.Netcode.NetworkManager>();
        if (nm == null)
        {
            Debug.LogError("[Setup] NetworkManager not found in scene.");
            return;
        }

        var npPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkPlayerPrefabPath);
        if (npPrefab == null)
        {
            Debug.LogError($"[Setup] NetworkPlayer prefab not found at: {NetworkPlayerPrefabPath}");
            return;
        }

        var so = new SerializedObject(nm);
        var prop = so.FindProperty("NetworkConfig.PlayerPrefab");
        if (prop == null)
        {
            // Try alternative path used in some NGO versions
            prop = so.FindProperty("m_NetworkConfig");
            if (prop != null) prop = prop.FindPropertyRelative("PlayerPrefab");
        }

        if (prop != null)
        {
            prop.objectReferenceValue = npPrefab;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(nm);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(nm.gameObject.scene);
            Debug.Log("[Setup] PlayerPrefab assigned to NetworkManager successfully.");
        }
        else
        {
            Debug.LogWarning("[Setup] Could not find PlayerPrefab property via SerializedObject. Assigning directly.");
            nm.NetworkConfig.PlayerPrefab = npPrefab;
            EditorUtility.SetDirty(nm);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(nm.gameObject.scene);
            Debug.Log("[Setup] PlayerPrefab assigned directly to NetworkManager.");
        }
    }

    [MenuItem("Tools/Warehouse Demo/Diag CharacterVisual Children")]
    public static void DiagCharacterVisualChildren()
    {
        var npPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkPlayerPrefabPath);
        if (npPrefab == null) { Debug.LogError("[Diag] NetworkPlayer prefab not found."); return; }

        var visual = npPrefab.transform.Find("CharacterVisual");
        if (visual == null) { Debug.LogError("[Diag] CharacterVisual not found."); return; }

        foreach (Transform t in visual.GetComponentsInChildren<Transform>(true))
        {
            var comps = string.Join(", ", System.Array.ConvertAll(t.GetComponents<Component>(), c => c?.GetType().Name ?? "null"));
            var renderers = t.GetComponents<Renderer>();
            string rendInfo = renderers.Length > 0 ? $" | Renderer enabled={renderers[0].enabled}" : "";
            Debug.Log($"[Diag] {t.name} layer={t.gameObject.layer}({LayerMask.LayerToName(t.gameObject.layer)}) active={t.gameObject.activeSelf}{rendInfo} | {comps}");
        }
    }

    [MenuItem("Tools/Warehouse Demo/Diag Camera Layers")]
    public static void DiagCameraLayers()
    {
        var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
        foreach (var cam in cameras)
        {
            int mask = cam.cullingMask;
            bool hasLayer8 = (mask & (1 << 8)) != 0;
            Debug.Log($"[CamDiag] Camera '{cam.name}' cullingMask={mask} | Layer8(Outline) included={hasLayer8}");
        }

        // Also log NetworkPlayer prefab CharacterVisual layer
        var npPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkPlayerPrefabPath);
        if (npPrefab != null)
        {
            var visual = npPrefab.transform.Find("CharacterVisual");
            if (visual != null)
                Debug.Log($"[CamDiag] CharacterVisual layer={visual.gameObject.layer} ({LayerMask.LayerToName(visual.gameObject.layer)})");
        }
    }

    static void RemoveIfPresent<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c != null) Object.DestroyImmediate(c);
    }

    static void RemoveIfPresent(GameObject go, string typeName)
    {
        foreach (var c in go.GetComponents<MonoBehaviour>())
        {
            if (c != null && c.GetType().Name == typeName)
            {
                Object.DestroyImmediate(c);
                return;
            }
        }
    }
}
