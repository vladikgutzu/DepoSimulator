using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// Tools → Warehouse Demo → Setup Scissor Lift FBX
///
/// Builds a fully animated ScissorLiftFBX from the imported GLB/FBX model:
///  - Unpacks the model prefab so meshes can be reorganised
///  - Reparents the Basket mesh under Platform so it moves with the player
///  - Creates X-beam ScissorArms visual that stretches as platform rises
///  - Wires all ScissorLift component references automatically
///  - Deactivates the old primitive ScissorLift
[InitializeOnLoad]
public static class SetupScissorLiftFBX
{
    [MenuItem("Tools/Warehouse Demo/Setup Scissor Lift FBX")]
    public static void Run()
    {
        // ── 1. Locate primitive ScissorLift ────────────────────────────────────
        var primitive   = GameObject.Find("ScissorLift");
        var spawnPos    = primitive != null ? primitive.transform.position
                                           : new Vector3(3.892f, 0f, 4.058f);
        var spawnRot    = primitive != null ? primitive.transform.rotation
                                           : Quaternion.identity;

        // ── 2. Locate FBX prefab ───────────────────────────────────────────────
        var fbxPrefab = FindFbxPrefab();
        if (fbxPrefab == null)
        {
            EditorUtility.DisplayDialog("Setup Scissor Lift",
                "Cannot find scissor_lift prefab.\n" +
                "Expected in Assets/Prefabs/env/ or Assets/Fbx/.", "OK");
            return;
        }

        // ── 3. Remove previous FBX setup if re-running ────────────────────────
        var old = GameObject.Find("ScissorLiftFBX");
        if (old != null)
        {
            if (!EditorUtility.DisplayDialog("Setup Scissor Lift",
                    "'ScissorLiftFBX' already exists — recreate it?", "Yes", "Cancel"))
                return;
            Undo.DestroyObjectImmediate(old);
        }

        // ── 4. Create root wrapper ─────────────────────────────────────────────
        var root = new GameObject("ScissorLiftFBX");
        Undo.RegisterCreatedObjectUndo(root, "Create ScissorLiftFBX");
        root.transform.position = spawnPos;
        root.transform.rotation = spawnRot;

        // ── 5. Instantiate & unpack FBX visual ────────────────────────────────
        var visual = (GameObject)PrefabUtility.InstantiatePrefab(fbxPrefab);
        if (visual == null) visual = Object.Instantiate(fbxPrefab);
        Undo.RegisterCreatedObjectUndo(visual, "Instantiate FBX visual");
        visual.name = "Visual";
        visual.transform.SetParent(root.transform, false);
        visual.transform.localPosition = Vector3.zero;

        // Fully unpack so we can freely reparent child meshes
        PrefabUtility.UnpackPrefabInstance(
            visual, PrefabUnpackMode.Completely,
            InteractionMode.AutomatedAction);

        // ── 6. Find basket mesh & measure its world height ─────────────────────
        Transform basketNode = FindDescendantContains(visual.transform, "Basket")
                            ?? FindDescendantContains(visual.transform, "basket")
                            ?? FindDescendantContains(visual.transform, "platform")
                            ?? FindDescendantContains(visual.transform, "Platform");

        float platformBaseHeight = MeasureBasketHeight(basketNode, visual, spawnPos.y);
        Debug.Log($"[SetupScissorLift] platformBaseHeight = {platformBaseHeight:F3} m");

        // ── 7. Create Platform GO ──────────────────────────────────────────────
        var platform = new GameObject("Platform");
        Undo.RegisterCreatedObjectUndo(platform, "Create Platform");
        platform.transform.SetParent(root.transform, false);
        platform.transform.localPosition = new Vector3(0f, platformBaseHeight, 0f);

        // ── 8. Reparent basket under Platform ─────────────────────────────────
        if (basketNode != null)
        {
            Undo.SetTransformParent(basketNode, platform.transform, "Reparent basket");
            // Keep world position — local will be (~0, 0, 0) since platform is
            // already positioned at basket world height
        }
        else
        {
            Debug.LogWarning("[SetupScissorLift] Basket node not found — " +
                             "platform will be invisible. Player will still stand correctly.");
        }

        // ── 9. Create ExitPoint ───────────────────────────────────────────────
        var exitPoint = new GameObject("ExitPoint");
        Undo.RegisterCreatedObjectUndo(exitPoint, "Create ExitPoint");
        exitPoint.transform.SetParent(platform.transform, false);
        exitPoint.transform.localPosition = new Vector3(1.4f, 0f, 0f);

        // ── 10. Create ScissorArms visual ──────────────────────────────────────
        var arms = BuildScissorArms(root.transform, platformBaseHeight);

        // ── 11. BoxCollider on root ────────────────────────────────────────────
        var col    = Undo.AddComponent<BoxCollider>(root);
        col.size   = new Vector3(1.6f, 0.5f, 2.2f);
        col.center = new Vector3(0f, 0.25f, 0f);

        // ── 12. ScissorLift component, wire all references ─────────────────────
        var sc = Undo.AddComponent<ScissorLift>(root);
        var so = new SerializedObject(sc);
        so.FindProperty("_platform").objectReferenceValue          = platform.transform;
        so.FindProperty("_exitPoint").objectReferenceValue         = exitPoint.transform;
        so.FindProperty("_scissorMesh").objectReferenceValue       = arms.transform;
        so.FindProperty("_platformBaseHeight").floatValue          = platformBaseHeight;
        so.FindProperty("_maxHeight").floatValue                   = 5f;
        so.FindProperty("_liftSpeed").floatValue                   = 2f;
        so.FindProperty("_driveSpeed").floatValue                  = 4f;
        so.FindProperty("_turnSpeed").floatValue                   = 80f;
        so.ApplyModifiedProperties();

        // ── 13. Deactivate primitive ───────────────────────────────────────────
        if (primitive != null)
        {
            Undo.RecordObject(primitive, "Deactivate primitive ScissorLift");
            primitive.SetActive(false);
        }

        // ── 14. Finish ─────────────────────────────────────────────────────────
        Selection.activeGameObject = root;
        EditorUtility.SetDirty(root);
        EditorSceneManager.MarkSceneDirty(root.scene);

        Debug.Log("[SetupScissorLift] ✓ Done!");
        EditorUtility.DisplayDialog("Setup Scissor Lift",
            $"✓ ScissorLiftFBX готов!\n\n" +
            $"Высота платформы (опущена): {platformBaseHeight:F2} м\n" +
            $"Максимальный подъём: 5 м\n\n" +
            (basketNode != null
                ? "Корзина перенесена под Platform — анимация работает."
                : "⚠ Корзина не найдена — проверь иерархию вручную.") +
            "\n\nСохрани сцену (Ctrl+S).", "OK");
    }

    // ==========================================================================
    //  ScissorArms builder
    // ==========================================================================
    // Creates an X-shaped scissor mechanism visual that stretches as the
    // platform rises.  The container GO is at root level, positioned and scaled
    // each frame by ScissorLift.ApplyHeight().
    //
    //  Container (ScissorArms) — pivot at y=0, scale.y driven by height
    //    ├── Beam_Front  — two cubes crossing on the XY plane
    //    ├── Beam_Back
    //    ├── Strut_L     — vertical side support (thin)
    //    └── Strut_R
    private static GameObject BuildScissorArms(Transform root, float baseHeight)
    {
        var arms = new GameObject("ScissorArms");
        Undo.RegisterCreatedObjectUndo(arms, "Create ScissorArms");
        arms.transform.SetParent(root, false);
        // Initial position/scale — Awake() will correct on first frame
        arms.transform.localPosition = new Vector3(0f, baseHeight * 0.5f, 0f);
        arms.transform.localScale    = new Vector3(1f, baseHeight, 1f);

        // Material — yellow steel
        var mat = FindOrCreateMaterial();

        // ── Two crossing X-beams (front face, visible from side) ───────────────
        CreateBeam(arms.transform, "Beam_FL", mat,
            scale:    new Vector3(0.08f, 1f, 0.08f),
            rotation: Quaternion.Euler(0f, 0f,  32f),
            offset:   new Vector3(-0.12f, 0f, -0.55f));

        CreateBeam(arms.transform, "Beam_FR", mat,
            scale:    new Vector3(0.08f, 1f, 0.08f),
            rotation: Quaternion.Euler(0f, 0f, -32f),
            offset:   new Vector3( 0.12f, 0f, -0.55f));

        CreateBeam(arms.transform, "Beam_BL", mat,
            scale:    new Vector3(0.08f, 1f, 0.08f),
            rotation: Quaternion.Euler(0f, 0f,  32f),
            offset:   new Vector3(-0.12f, 0f,  0.55f));

        CreateBeam(arms.transform, "Beam_BR", mat,
            scale:    new Vector3(0.08f, 1f, 0.08f),
            rotation: Quaternion.Euler(0f, 0f, -32f),
            offset:   new Vector3( 0.12f, 0f,  0.55f));

        // ── Thin vertical struts on the sides ─────────────────────────────────
        CreateBeam(arms.transform, "Strut_L", mat,
            scale:    new Vector3(0.05f, 1f, 0.05f),
            rotation: Quaternion.identity,
            offset:   new Vector3(-0.7f, 0f, 0f));

        CreateBeam(arms.transform, "Strut_R", mat,
            scale:    new Vector3(0.05f, 1f, 0.05f),
            rotation: Quaternion.identity,
            offset:   new Vector3( 0.7f, 0f, 0f));

        return arms;
    }

    private static void CreateBeam(Transform parent, string name, Material mat,
                                    Vector3 scale, Quaternion rotation, Vector3 offset)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(go, "Create beam " + name);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localScale    = scale;
        go.transform.localRotation = rotation;
        go.transform.localPosition = offset;

        // Remove collider — only the root BoxCollider matters
        var col = go.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col);

        if (mat != null)
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    private static Material FindOrCreateMaterial()
    {
        // Try to find existing ScissorLift_Yellow material
        var guids = AssetDatabase.FindAssets("ScissorLift_Yellow t:Material",
            new[] { "Assets" });
        if (guids.Length > 0)
            return AssetDatabase.LoadAssetAtPath<Material>(
                AssetDatabase.GUIDToAssetPath(guids[0]));

        // Create a simple yellow material
        const string path = "Assets/WarehouseDemo/Assets/Materials/ScissorArms.mat";
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.color = new Color(0.95f, 0.72f, 0.05f); // yellow
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // ==========================================================================
    //  Helpers
    // ==========================================================================

    private static GameObject FindFbxPrefab()
    {
        string[] searchFolders = { "Assets/Prefabs/env", "Assets/Fbx", "Assets" };
        foreach (var folder in searchFolders)
        {
            var guids = AssetDatabase.FindAssets("scissor_lift t:Prefab",
                new[] { folder });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var go   = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null) return go;
            }
            // Also try GLB as direct GameObject asset
            guids = AssetDatabase.FindAssets("scissor_lift t:GameObject",
                new[] { folder });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".cs"))
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (go != null) return go;
                }
            }
        }
        return null;
    }

    private static Transform FindDescendantContains(Transform root, string substr)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t != root && t.name.Contains(substr))
                return t;
        return null;
    }

    private static float MeasureBasketHeight(Transform basketNode,
                                              GameObject visual, float rootWorldY)
    {
        if (basketNode != null)
        {
            // Use the bottom of the basket bounds
            var renderers = basketNode.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 0)
            {
                var b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                float h = b.min.y - rootWorldY;
                return Mathf.Max(0.1f, h);
            }
            return Mathf.Max(0.1f, basketNode.position.y - rootWorldY);
        }

        // Fallback: use 75% of total model height
        var allRenderers = visual.GetComponentsInChildren<Renderer>(true);
        if (allRenderers.Length > 0)
        {
            var b = allRenderers[0].bounds;
            foreach (var r in allRenderers) b.Encapsulate(r.bounds);
            return Mathf.Max(0.1f, b.size.y * 0.65f);
        }
        return 1.2f; // default fallback
    }

    [MenuItem("Tools/Warehouse Demo/Setup Scissor Lift FBX", true)]
    private static bool Validate() => !Application.isPlaying;
}
