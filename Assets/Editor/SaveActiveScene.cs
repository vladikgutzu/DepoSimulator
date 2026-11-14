using UnityEditor;
using UnityEditor.SceneManagement;
public class SaveActiveScene
{
    [MenuItem("Tools/Warehouse Demo/Save Scene")]
    public static void Execute() => EditorSceneManager.SaveOpenScenes();
}
