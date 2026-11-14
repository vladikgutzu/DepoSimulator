using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

public static class EnableIKAndAddHandIK
{
    const string MainControllerPath =
        "Assets/StarterAssets/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";
    const string SittingControllerPath =
        "Assets/Animations/SittingController.controller";

    [MenuItem("Tools/Warehouse Demo/Enable IK Pass + Add PlayerHandIK")]
    public static void Execute()
    {
        // ── 1. Enable IK Pass on the player's main controller ─────────────────
        EnableIKOnController(MainControllerPath);

        // ── 2. Enable IK Pass on the sitting controller (used in forklift) ────
        // Without this, hand/foot IK has no effect while the player is seated.
        EnableIKOnController(SittingControllerPath);

        // ── 3. Add PlayerHandIK to Player in scene ────────────────────────────
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) { Debug.LogError("[IK] Player не найден! Убедитесь что тег 'Player' назначен."); return; }

        if (player.GetComponent<PlayerHandIK>() == null)
        {
            Undo.AddComponent<PlayerHandIK>(player);
            EditorUtility.SetDirty(player);
            Debug.Log("[IK] PlayerHandIK добавлен на Player.");
        }
        else
        {
            Debug.Log("[IK] PlayerHandIK уже есть на Player.");
        }

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[IK] ✓ Готово! IK Pass включён на обоих контроллерах. " +
                  "Назначьте IK targets в ReachTruckController через Setup Reach Truck.");
    }

    static void EnableIKOnController(string path)
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        if (controller == null)
        {
            Debug.LogWarning($"[IK] Controller не найден: {path}");
            return;
        }

        var layers = controller.layers;
        bool changed = false;
        for (int i = 0; i < layers.Length; i++)
        {
            if (!layers[i].iKPass)
            {
                layers[i].iKPass = true;
                changed = true;
            }
        }

        if (changed)
        {
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"[IK] IK Pass включён: {path}");
        }
        else
        {
            Debug.Log($"[IK] IK Pass уже был включён: {path}");
        }
    }
}
