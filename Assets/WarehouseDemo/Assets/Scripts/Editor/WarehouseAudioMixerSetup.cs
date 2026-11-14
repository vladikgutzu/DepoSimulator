#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

/// Creates the project mixer through Unity's own mixer controller, then leaves all normal runtime
/// code on the public AudioMixer API. This is a one-time editor setup operation; no mixer YAML is
/// hand-authored or modified outside Unity.
public static class WarehouseAudioMixerSetup
{
    public const string MixerPath = "Assets/Resources/Audio/WarehouseAudioMixer.mixer";
    static readonly string[] GroupNames =
    {
        "Music", "Ambience", "SFX", "UI", "Foley", "Vehicles"
    };

    [MenuItem("Tools/Warehouse Demo/Audio/Create or Repair Mixer")]
    public static void CreateOrRepair()
    {
        EnsureFolder("Assets/Resources/Audio");
        AudioMixer existing = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
        if (existing != null && HasRequiredGroups(existing))
        {
            Debug.Log("[WarehouseAudioMixerSetup] Mixer already contains all required groups.");
            return;
        }

        if (existing != null)
        {
            Debug.LogError("[WarehouseAudioMixerSetup] Existing mixer is incomplete. Repair it " +
                           "from the Audio Mixer window rather than replacing a referenced asset.");
            return;
        }

        Assembly editorAssembly = typeof(Editor).Assembly;
        Type controllerType = editorAssembly.GetType("UnityEditor.Audio.AudioMixerController");
        Type groupType = editorAssembly.GetType("UnityEditor.Audio.AudioMixerGroupController");
        Type exposedType = editorAssembly.GetType("UnityEditor.Audio.ExposedAudioParameter");
        if (controllerType == null || groupType == null || exposedType == null)
            throw new InvalidOperationException("Unity Audio Mixer editor API was not found.");

        MethodInfo create = controllerType.GetMethod("CreateMixerControllerAtPath",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo createGroup = controllerType.GetMethod("CreateNewGroup",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        PropertyInfo masterProperty = controllerType.GetProperty("masterGroup",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        PropertyInfo childrenProperty = groupType.GetProperty("children",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        PropertyInfo exposedProperty = controllerType.GetProperty("exposedParameters",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        object controller = create?.Invoke(null, new object[] { MixerPath });
        if (controller == null || createGroup == null || masterProperty == null ||
            childrenProperty == null || exposedProperty == null)
            throw new InvalidOperationException("Unity Audio Mixer editor API shape is unsupported.");

        object master = masterProperty.GetValue(controller);
        Array groups = Array.CreateInstance(groupType, GroupNames.Length);
        Array exposed = Array.CreateInstance(exposedType, GroupNames.Length + 1);

        SetExposed(exposed, 0, exposedType, master, groupType, "MasterVolume");
        for (int i = 0; i < GroupNames.Length; i++)
        {
            object group = createGroup.Invoke(controller, new object[] { GroupNames[i], true });
            groups.SetValue(group, i);
            SetExposed(exposed, i + 1, exposedType, group, groupType,
                GroupNames[i] + "Volume");
        }

        childrenProperty.SetValue(master, groups);
        exposedProperty.SetValue(controller, exposed);
        EditorUtility.SetDirty((UnityEngine.Object)controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[WarehouseAudioMixerSetup] Created mixer and exposed game-audio buses at " +
                  MixerPath);
    }

    static void SetExposed(Array array, int index, Type exposedType, object group,
        Type groupType, string parameterName)
    {
        MethodInfo getGuid = groupType.GetMethod("GetGUIDForVolume",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object value = Activator.CreateInstance(exposedType);
        exposedType.GetField("guid", BindingFlags.Instance | BindingFlags.Public |
                                     BindingFlags.NonPublic)?.SetValue(value, getGuid.Invoke(group, null));
        exposedType.GetField("name", BindingFlags.Instance | BindingFlags.Public |
                                     BindingFlags.NonPublic)?.SetValue(value, parameterName);
        array.SetValue(value, index);
    }

    static bool HasRequiredGroups(AudioMixer mixer)
    {
        for (int i = 0; i < GroupNames.Length; i++)
            if (mixer.FindMatchingGroups(GroupNames[i]).Length == 0) return false;
        return true;
    }

    static void EnsureFolder(string folder)
    {
        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}
#endif
