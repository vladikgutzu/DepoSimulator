using UnityEngine;

public class StartGameNow
{
    public static void Execute()
    {
        var mm = Object.FindAnyObjectByType<MainMenuController>();
        if (mm != null)
        {
            // Use reflection to call StartSingleplayer (it's private)
            var m = typeof(MainMenuController).GetMethod("StartSingleplayer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (m != null) { m.Invoke(mm, null); Debug.Log("[StartGameNow] Called StartSingleplayer()"); return; }
        }

        // Fallback: activate HUD + call StartGame directly
        var allRoots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        foreach (var root in allRoots)
        {
            if (root.name == "HUD_UIDocument") { root.SetActive(true); break; }
            var found = root.transform.Find("HUD_UIDocument");
            if (found != null) { found.gameObject.SetActive(true); break; }
        }

        GameManager.Instance?.StartGame();
        Debug.Log("[StartGameNow] Started game via fallback");
    }
}
