using UnityEngine;
using UnityEngine.SceneManagement;

public static class StartSinglePlayer
{
    public static void Run()
    {
        MainMenuController.AutoStart = true;
        SceneManager.LoadScene(0);
        Debug.Log("[Test] AutoStart set — reloading scene 0 as single player");
    }
}
