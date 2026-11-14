using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;

public class WireMinimapRefs
{
    public static void Execute()
    {
        var gmGO = GameObject.Find("GameManager");
        if (gmGO == null) { Debug.LogError("[WireMinimapRefs] GameManager not found"); return; }

        var mm = gmGO.GetComponent<MinimapController>();
        if (mm == null) { Debug.LogError("[WireMinimapRefs] MinimapController not found on GameManager"); return; }

        // Find HUD_UIDocument (may be inactive)
        var allRoots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        GameObject hudGO = null;
        foreach (var root in allRoots)
        {
            if (root.name == "HUD_UIDocument") { hudGO = root; break; }
            var found = root.transform.Find("HUD_UIDocument");
            if (found != null) { hudGO = found.gameObject; break; }
        }

        if (hudGO != null)
        {
            var doc = hudGO.GetComponent<UnityEngine.UIElements.UIDocument>();
            var so  = new SerializedObject(mm);
            so.FindProperty("_hudDocument").objectReferenceValue = doc;
            so.ApplyModifiedProperties();
            Debug.Log("[WireMinimapRefs] Wired HUD_UIDocument -> MinimapController._hudDocument");
        }
        else
        {
            Debug.LogWarning("[WireMinimapRefs] HUD_UIDocument not found in scene");
        }

        // Wire Player transform
        var playerGO = GameObject.Find("Player");
        if (playerGO != null)
        {
            var so2 = new SerializedObject(mm);
            so2.FindProperty("_playerTransform").objectReferenceValue = playerGO.transform;
            so2.ApplyModifiedProperties();
            Debug.Log("[WireMinimapRefs] Wired Player -> MinimapController._playerTransform");
        }

        EditorUtility.SetDirty(mm);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log("[WireMinimapRefs] Done.");
    }
}
