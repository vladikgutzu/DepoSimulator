using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Tools > Warehouse Demo > Setup Vehicle Camera
// Ensures CameraModeSwitcher is present on the Player.
// No Cinemachine cameras needed — vehicles use TPSCameraController vehicle mode.
public static class SetupVehicleCamera
{
    [MenuItem("Tools/Warehouse Demo/Setup Vehicle Camera")]
    public static void Execute()
    {
        var player = GameObject.Find("Player");
        if (player == null) { Debug.LogError("[VehicleCam] Player not found"); return; }

        var switcher = player.GetComponent<CameraModeSwitcher>();
        if (switcher == null)
        {
            switcher = Undo.AddComponent<CameraModeSwitcher>(player);
            Debug.Log("[VehicleCam] CameraModeSwitcher added to Player.");
        }
        else
        {
            Debug.Log("[VehicleCam] CameraModeSwitcher already present on Player.");
        }

        EditorUtility.SetDirty(player);
        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        EditorUtility.DisplayDialog("Setup Vehicle Camera",
            "Готово!\n\nCameraModeSwitcher присутствует на Player.\n" +
            "При посадке ReachTruckController переназначает PlayerCameraRoot\n" +
            "на CameraAnchor погрузчика — Cinemachine3rdPersonFollow\n" +
            "автоматически показывает вид сзади.", "OK");
    }
}
