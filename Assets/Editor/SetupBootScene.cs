using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine;

/// Builds the boot scene and puts it first in the build list.
///
/// First is the whole point: Unity loads whatever sits at index 0 before any game code runs, so
/// that scene is the one that cannot have a loading screen over it. Making it nearly empty is what
/// lets the warehouse be loaded the ordinary way, asynchronously, with a screen in front of it.
///
/// Re-runnable — it rewrites the scene and the build list rather than appending a second copy.
public static class SetupBootScene
{
    const string ScenePath     = "Assets/Scenes/Boot.unity";
    const string WarehousePath = "Assets/urp_scene_with_training 1.unity";

    [MenuItem("Tools/Warehouse/Setup Boot Scene")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        if (!File.Exists(WarehousePath))
            return $"cannot find the warehouse scene at {WarehousePath} — build list left alone";

        // Saving over a path that is itself open in the editor does nothing, and says nothing about
        // it either: the tool reported success on every re-run while the file on disk stayed at its
        // first version. Getting the boot scene out of the editor first is what makes this
        // re-runnable rather than write-once.
        if (SceneManager.GetSceneByPath(ScenePath).isLoaded)
            EditorSceneManager.OpenScene(WarehousePath, OpenSceneMode.Single);

        int    roots = BuildScene();
        string order = SetBuildOrder();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return $"{ScenePath} written, {roots} root objects.\n{order}";
    }

    static int BuildScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

        // A camera only so the frame is cleared to the same black the loading screen paints, and
        // so Unity does not warn about a scene with nothing rendering. The screen itself is a UI
        // Toolkit panel and does not need one.
        var camGo = new GameObject("Camera");
        var cam   = camGo.AddComponent<Camera>();
        cam.clearFlags      = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(12f / 255f, 12f / 255f, 14f / 255f, 1f);
        cam.cullingMask     = 0;      // nothing to draw — this scene is empty by design
        cam.depth           = -100;

        // Without one, Unity logs "There are no audio listeners in the scene" every frame the boot
        // scene is up — over a hundred lines before the warehouse takes over. Only an info message,
        // but the log is worth keeping readable.
        camGo.AddComponent<AudioListener>();

        var loaderGo = new GameObject("BootLoader");
        loaderGo.AddComponent<BootLoader>();

        // new GameObject() drops into the ACTIVE scene, and a scene opened additively is not the
        // active one — so without these two moves the objects stay behind in whatever was open and
        // an empty boot scene gets saved over the top.
        SceneManager.MoveGameObjectToScene(camGo,    scene);
        SceneManager.MoveGameObjectToScene(loaderGo, scene);

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);

        int objects = scene.rootCount;
        EditorSceneManager.CloseScene(scene, removeScene: true);
        return objects;
    }

    /// Boot first, the warehouse right after it, everything else left where it was and disabled as
    /// it already was.
    static string SetBuildOrder()
    {
        var others = EditorBuildSettings.scenes
            .Where(s => s.path != ScenePath && s.path != WarehousePath)
            .ToList();

        var ordered = new List<EditorBuildSettingsScene>
        {
            new EditorBuildSettingsScene(ScenePath,     true),
            new EditorBuildSettingsScene(WarehousePath, true),
        };
        ordered.AddRange(others);

        EditorBuildSettings.scenes = ordered.ToArray();

        return "Build list:\n" + string.Join("\n", ordered.Select(
            (s, i) => $"  {i}  {(s.enabled ? "on " : "off")}  {s.path}"));
    }
}
