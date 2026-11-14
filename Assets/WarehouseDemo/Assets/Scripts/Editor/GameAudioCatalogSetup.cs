#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Creates/refreshes the data-driven catalog after audio import. Uses only public Unity editor
/// APIs and deliberately does not create or edit an AudioMixer asset.
/// </summary>
public static class GameAudioCatalogSetup
{
    const string CueFolder = "Assets/Resources/Audio/Cues";
    const string CatalogPath = "Assets/Resources/Audio/GameAudioCatalog.asset";
    const string MixerPath = "Assets/Resources/Audio/WarehouseAudioMixer.mixer";

    readonly struct Definition
    {
        public readonly GameAudioEvent Event;
        public readonly bool Loop;
        public readonly float Spatial;
        public readonly float VolumeMin;
        public readonly float VolumeMax;
        public readonly float PitchMin;
        public readonly float PitchMax;
        public readonly float Cooldown;
        public readonly float MinimumIntensity;
        public readonly float FullIntensity;
        public readonly string[] Keywords;
        public readonly string[] FallbackPaths;

        public Definition(GameAudioEvent audioEvent, bool loop, float spatial,
            float volumeMin, float volumeMax, float pitchMin, float pitchMax, float cooldown,
            float minimumIntensity, float fullIntensity, string[] keywords,
            params string[] fallbackPaths)
        {
            Event = audioEvent;
            Loop = loop;
            Spatial = spatial;
            VolumeMin = volumeMin;
            VolumeMax = volumeMax;
            PitchMin = pitchMin;
            PitchMax = pitchMax;
            Cooldown = cooldown;
            MinimumIntensity = minimumIntensity;
            FullIntensity = fullIntensity;
            Keywords = keywords;
            FallbackPaths = fallbackPaths;
        }
    }

    static readonly Definition[] Definitions =
    {
        Ui(GameAudioEvent.Pickup, "cardboard box open", "box pickup",
            "Assets/Audio/Pixabay/box_pickup_182560.mp3"),
        Ui(GameAudioEvent.PlacePart, "assembly part place", "part place latch",
            "Assets/Audio/Actions/sfx_assembly_part_place_01.wav"),
        Ui(GameAudioEvent.ReturnPart, "placing cardboard box", "box place",
            "Assets/Audio/Pixabay/box_place_453025.mp3"),
        Ui(GameAudioEvent.OrderAccepted, "order accepted", "new notification",
            "Assets/Audio/Pixabay/ui_success_132473.mp3"),
        Ui(GameAudioEvent.OrderComplete, "success notification", "order complete",
            "Assets/Audio/Pixabay/ui_success_132473.mp3",
            "Assets/Audio/microwave-end-sound-98680.wav"),
        Ui(GameAudioEvent.OrderExpired, "incorrect buzzer", "wrong",
            "Assets/Audio/Pixabay/ui_error_147336.mp3"),
        Ui(GameAudioEvent.DemoComplete, "success notification", "order complete",
            "Assets/Audio/Pixabay/ui_success_132473.mp3",
            "Assets/Audio/microwave-end-sound-98680.wav"),
        Ui(GameAudioEvent.Error, "incorrect buzzer", "wrong",
            "Assets/Audio/Pixabay/ui_error_147336.mp3"),
        Ui(GameAudioEvent.Ping, "barcode", "scanner", "ping",
            "Assets/Audio/lamp-key-sfx-312914.wav"),
        World(GameAudioEvent.BoxDrop, 0.1f, 0f, 0f,
            "dropping cardboard box", "cardboard box drop",
            "Assets/Audio/Pixabay/box_drop_light_453026.mp3"),
        World(GameAudioEvent.BoxRelease, 0.08f, 0f, 0f,
            "placing cardboard box", "box place",
            "Assets/Audio/Pixabay/box_place_453025.mp3"),
        World(GameAudioEvent.BoxThrow, 0.08f, 0f, 0f,
            "cardboard box slides", "box throw",
            "Assets/Audio/Pixabay/box_slide_31817.mp3"),
        World(GameAudioEvent.BoxImpact, 0.6f, 0.2f, 6f,
            "dropping cardboard box", "cardboard box drop", "box impact",
            "Assets/Audio/Pixabay/box_drop_light_453026.mp3"),
        World(GameAudioEvent.BoxSlide, 0.2f, 0.5f, 4f,
            "cardboard box slides", "cardboard rustling",
            "Assets/Audio/Pixabay/box_slide_31817.mp3"),
        World(GameAudioEvent.PalletImpact, 0.12f, 0.8f, 7f,
            "wood impact", "wooden thud", "wood crash",
            "Assets/Audio/Pixabay/pallet_wood_thud_6244.mp3"),
        World(GameAudioEvent.ForkliftStart, 0.1f, 0f, 0f, "forklift starting",
            "Assets/Audio/Pixabay/forklift_start_36156.mp3"),
        Loop(GameAudioEvent.ForkliftIdleLoop, 0.75f, 0.9f, 1.02f,
            "forklift movement", "forklift idle", "Assets/Audio/driving.wav"),
        Loop(GameAudioEvent.ForkliftDriveLoop, 0.8f, 0.85f, 1.12f,
            "forklift movement", "forklift", "Assets/Audio/driving2.wav",
            "Assets/Audio/driving3.wav"),
        Loop(GameAudioEvent.ForkliftHydraulicLoop, 0.85f, 0.95f, 1.05f,
            "hydraulics", "hydraulic", "Assets/Audio/electric-motor-whir-77588.wav"),
        Loop(GameAudioEvent.ForkliftReverseLoop, 0.85f, 1f, 1f,
            "truck reversing beep", "fork truck beep",
            "Assets/Audio/Pixabay/vehicle_reverse_beep_359231.mp3"),
        World(GameAudioEvent.ForkliftStop, 0.1f, 0f, 0f, "forklift stop", "shutdown",
            "Assets/Audio/Pixabay/forklift_stop_89775.mp3"),
        World(GameAudioEvent.ForkliftImpact, 0.15f, 0.8f, 8f,
            "vehicle impact", "metal impact",
            "Assets/Audio/Pixabay/robot_metal_impact_30254.mp3"),
        World(GameAudioEvent.ForkliftHorn, 0.15f, 0f, 0f, "forklift horn", "truck horn",
            "Assets/Audio/Pixabay/forklift_horn_153263.mp3"),
        World(GameAudioEvent.AssemblyTool, 0.08f, 0f, 0f, "drill", "ratchet",
            "Assets/Audio/Pixabay/repair_ratchet_33787.mp3",
            "Assets/Audio/drill_clip_01.wav"),
        World(GameAudioEvent.AssemblyTape, 0.08f, 0f, 0f, "packing tape taping", "packing tape",
            "Assets/Audio/Pixabay/packing_tape_31068.mp3"),
        new Definition(GameAudioEvent.MeleeSwing, false, 0f, 0.18f, 0.25f,
            1.25f, 1.35f, 0.08f, 0f, 0f,
            new[] { "dramatic whoosh swing", "weapon swing" },
            new[] { "Assets/Audio/Pixabay/melee_swing_metal_pipe_trimmed.wav" }),
        World(GameAudioEvent.MetalImpact, 0.08f, 0.4f, 6f, "metal impact",
            "Assets/Audio/Pixabay/robot_metal_impact_30254.mp3"),
        World(GameAudioEvent.RobotFall, 0.2f, 0.5f, 8f, "metallic fall", "robot fall",
            "Assets/Audio/Pixabay/robot_metal_impact_30254.mp3"),
        World(GameAudioEvent.LimbPickup, 0.1f, 0f, 0f, "robot limb pickup",
            "Assets/Audio/Actions/sfx_robot_limb_pickup_01.wav"),
        World(GameAudioEvent.LimbInstall, 0.18f, 0f, 0f, "robot limb install",
            "Assets/Audio/Actions/sfx_robot_limb_install_01.wav"),
        Ui(GameAudioEvent.PowerOutage, "power outage", "shutdown",
            "Assets/Audio/Pixabay/event_power_outage_451574.mp3"),
        Ui(GameAudioEvent.PowerRestore, "power restore", "success notification",
            "Assets/Audio/Pixabay/ui_success_132473.mp3"),
        Ui(GameAudioEvent.ShortCircuit, "short circuit",
            "Assets/Audio/Pixabay/event_short_circuit_154595.mp3"),
        Ui(GameAudioEvent.LobbyJoin, "new notification", "ui click",
            "Assets/Audio/Pixabay/ui_success_132473.mp3"),
        Ui(GameAudioEvent.LobbyLeave, "ui click", "wrong",
            "Assets/Audio/Pixabay/ui_error_147336.mp3"),
        Ui(GameAudioEvent.LobbyRoleSelected, "ui click", "game interface",
            "Assets/Audio/lamp-key-sfx-312914.wav"),
        Ui(GameAudioEvent.PurchaseSuccess, "purchase success", "cash register kaching",
            "Assets/Audio/Pixabay/purchase_success_kaching_376867.mp3"),
        UiFixed(GameAudioEvent.UiButtonClick, 0.9f, "ui button click", "game interface",
            "Assets/Audio/lamp-key-sfx-312914.wav"),
        UiMedium(GameAudioEvent.InteractConfirm, "interaction confirm", "switch click",
            "Assets/Audio/lamp-key-sfx-312914.wav"),
        Loop(GameAudioEvent.ConveyorLoop, 1f, 0.96f, 1.04f,
            "conveyor loop", "factory conveyor", "machine loop",
            "Assets/Audio/Pixabay/conveyor_belt_584731.mp3"),
        World(GameAudioEvent.PlayerFootstepWalk, 0.08f, 0f, 0f,
            "robot footstep", "metal footstep", "Player Footstep"),
        WorldLoud(GameAudioEvent.PlayerFootstepRun, 0.08f,
            "robot footstep", "metal footstep", "Player Footstep"),
        WorldLoud(GameAudioEvent.PlayerJump, 0.15f,
            "robot jump", "servo jump", "Player Jump"),
        World(GameAudioEvent.PlayerLand, 0.15f, 0f, 0f,
            "robot land", "metal landing", "Player Land",
            "Assets/StarterAssets/ThirdPersonController/Character/Sfx/Player_Land.wav"),
        new Definition(GameAudioEvent.RobotPunchImpact, false, 1f,
            0.58f, 0.75f, 1.06f, 1.16f, 0.09f, 0f, 0f,
            new[] { "robot punch impact", "metal impact" },
            "Assets/Audio/Pixabay/robot_metal_impact_30254.mp3"),
        new Definition(GameAudioEvent.RobotDamageImpact, false, 1f,
            0.82f, 1f, 0.82f, 0.96f, 0.12f, 0f, 0f,
            new[] { "robot damage impact", "metal impact" },
            "Assets/Audio/Pixabay/robot_metal_impact_30254.mp3"),
    };

    [InitializeOnLoadMethod]
    static void ScheduleRefreshAfterDomainReload()
    {
        EditorApplication.delayCall -= RefreshWhenEditorReady;
        EditorApplication.delayCall += RefreshWhenEditorReady;
    }

    static void RefreshWhenEditorReady()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += RefreshWhenEditorReady;
            return;
        }

        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            return;
        }

        BuildOrRefresh();
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.delayCall += RefreshWhenEditorReady;
    }

    [MenuItem("Tools/Warehouse Demo/Audio/Build or Refresh Catalog")]
    public static void BuildOrRefresh()
    {
        EnsureFolder(CueFolder);
        var allClips = LoadProjectClips();
        var cues = new AudioCue[Definitions.Length];
        AudioMixer mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);

        for (int i = 0; i < Definitions.Length; i++)
        {
            Definition definition = Definitions[i];
            string cuePath = $"{CueFolder}/{definition.Event}.asset";
            AudioCue cue = AssetDatabase.LoadAssetAtPath<AudioCue>(cuePath);
            if (cue == null)
            {
                cue = ScriptableObject.CreateInstance<AudioCue>();
                AssetDatabase.CreateAsset(cue, cuePath);
            }

            AudioClip[] selected = FindClips(definition, allClips);
            cue.EditorConfigure(definition.Event, selected, definition.Loop,
                definition.Spatial, definition.VolumeMin, definition.VolumeMax,
                definition.PitchMin, definition.PitchMax, definition.Cooldown,
                definition.MinimumIntensity, definition.FullIntensity,
                FindOutput(mixer, OutputGroupFor(definition.Event)));
            EditorUtility.SetDirty(cue);
            cues[i] = cue;
        }

        GameAudioCatalog catalog = AssetDatabase.LoadAssetAtPath<GameAudioCatalog>(CatalogPath);
        if (catalog == null)
        {
            catalog = ScriptableObject.CreateInstance<GameAudioCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
        }
        catalog.EditorSetCues(cues);
        EditorUtility.SetDirty(catalog);

        // Assign the catalog to AudioManagers in currently open scenes without reflection or
        // serialized YAML editing. Resources.Load remains the safe fallback for unopened scenes.
        foreach (AudioManager manager in UnityEngine.Object.FindObjectsByType<AudioManager>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var serialized = new SerializedObject(manager);
            SerializedProperty property = serialized.FindProperty("_catalog");
            if (property == null) continue;
            property.objectReferenceValue = catalog;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(manager);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[GameAudioCatalogSetup] Refreshed {cues.Length} cues; " +
                  $"{CountAssigned(cues)} have at least one clip.");
    }

    internal static void RefreshAfterAudioImport() => BuildOrRefresh();

    static string OutputGroupFor(GameAudioEvent audioEvent)
    {
        switch (audioEvent)
        {
            case GameAudioEvent.UiButtonClick:
            case GameAudioEvent.InteractConfirm:
            case GameAudioEvent.PurchaseSuccess:
            case GameAudioEvent.OrderAccepted:
            case GameAudioEvent.OrderComplete:
            case GameAudioEvent.OrderExpired:
            case GameAudioEvent.DemoComplete:
            case GameAudioEvent.Error:
            case GameAudioEvent.Ping:
            case GameAudioEvent.LobbyJoin:
            case GameAudioEvent.LobbyLeave:
            case GameAudioEvent.LobbyRoleSelected:
                return "UI";
            case GameAudioEvent.ForkliftStart:
            case GameAudioEvent.ForkliftIdleLoop:
            case GameAudioEvent.ForkliftDriveLoop:
            case GameAudioEvent.ForkliftHydraulicLoop:
            case GameAudioEvent.ForkliftReverseLoop:
            case GameAudioEvent.ForkliftStop:
            case GameAudioEvent.ForkliftImpact:
            case GameAudioEvent.ForkliftHorn:
                return "Vehicles";
            case GameAudioEvent.ConveyorLoop:
                return "Ambience";
            case GameAudioEvent.PlayerFootstepWalk:
            case GameAudioEvent.PlayerFootstepRun:
            case GameAudioEvent.PlayerJump:
            case GameAudioEvent.PlayerLand:
                return "Foley";
            default:
                return "SFX";
        }
    }

    static AudioMixerGroup FindOutput(AudioMixer mixer, string groupName)
    {
        if (mixer == null) return null;
        AudioMixerGroup[] groups = mixer.FindMatchingGroups(groupName);
        return groups != null && groups.Length > 0 ? groups[0] : null;
    }

    static Definition Ui(GameAudioEvent audioEvent, params string[] keywords) =>
        new(audioEvent, false, 0f, 0.85f, 1f, 0.98f, 1.02f, 0.05f,
            0f, 0f, keywords, ExtractPaths(keywords));

    static Definition UiQuiet(GameAudioEvent audioEvent, params string[] keywords) =>
        new(audioEvent, false, 0f, 0.28f, 0.4f, 0.98f, 1.02f, 0.04f,
            0f, 0f, RemovePaths(keywords), ExtractPaths(keywords));

    static Definition UiMedium(GameAudioEvent audioEvent, params string[] keywords) =>
        new(audioEvent, false, 0f, 0.60f, 0.75f, 0.98f, 1.02f, 0.04f,
            0f, 0f, RemovePaths(keywords), ExtractPaths(keywords));

    static Definition UiFixed(GameAudioEvent audioEvent, float volume, params string[] keywords) =>
        new(audioEvent, false, 0f, volume, volume, 0.98f, 1.02f, 0.04f,
            0f, 0f, RemovePaths(keywords), ExtractPaths(keywords));

    static Definition World(GameAudioEvent audioEvent, float cooldown,
        float minimumIntensity, float fullIntensity, params string[] keywords) =>
        new(audioEvent, false, 1f, 0.78f, 1f, 0.94f, 1.06f, cooldown,
            minimumIntensity, fullIntensity, RemovePaths(keywords), ExtractPaths(keywords));

    static Definition WorldLoud(GameAudioEvent audioEvent, float cooldown, params string[] keywords) =>
        new(audioEvent, false, 1f, 1f, 1f, 0.94f, 1.06f, cooldown,
            0f, 0f, RemovePaths(keywords), ExtractPaths(keywords));

    static Definition Loop(GameAudioEvent audioEvent, float spatial, float pitchMin,
        float pitchMax, params string[] keywords) =>
        new(audioEvent, true, spatial, 0.15f, 0.8f, pitchMin, pitchMax, 0f,
            0f, 0f, RemovePaths(keywords), ExtractPaths(keywords));

    static string[] RemovePaths(string[] values)
    {
        var result = new List<string>(values.Length);
        for (int i = 0; i < values.Length; i++)
            if (!values[i].StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                result.Add(values[i]);
        return result.ToArray();
    }

    static string[] ExtractPaths(string[] values)
    {
        var result = new List<string>(values.Length);
        for (int i = 0; i < values.Length; i++)
            if (values[i].StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                result.Add(values[i]);
        return result.ToArray();
    }

    static List<AudioClip> LoadProjectClips()
    {
        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Audio" });
        var result = new List<AudioClip>(guids.Length);
        for (int i = 0; i < guids.Length; i++)
        {
            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(
                AssetDatabase.GUIDToAssetPath(guids[i]));
            if (clip != null) result.Add(clip);
        }
        return result;
    }

    static AudioClip[] FindClips(Definition definition, List<AudioClip> allClips)
    {
        const int MaxVariations = 4;
        var result = new List<AudioClip>(MaxVariations);
        AddFallbacks(definition.FallbackPaths, result);

        for (int keywordIndex = 0; keywordIndex < definition.Keywords.Length; keywordIndex++)
        {
            string keyword = Normalize(definition.Keywords[keywordIndex]);
            if (string.IsNullOrEmpty(keyword)) continue;
            for (int clipIndex = 0; clipIndex < allClips.Count; clipIndex++)
            {
                AudioClip clip = allClips[clipIndex];
                if (result.Contains(clip) || !Normalize(clip.name).Contains(keyword)) continue;
                result.Add(clip);
                if (result.Count >= MaxVariations) return result.ToArray();
            }
        }
        return result.ToArray();
    }

    static void AddFallbacks(string[] paths, List<AudioClip> result)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(paths[i]);
            if (clip != null && !result.Contains(clip)) result.Add(clip);
        }
    }

    static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ').Trim();
    }

    static int CountAssigned(AudioCue[] cues)
    {
        int count = 0;
        for (int i = 0; i < cues.Length; i++)
            if (cues[i] != null && cues[i].HasClips) count++;
        return count;
    }

    static void EnsureFolder(string folder)
    {
        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}

public sealed class GameAudioCatalogPostprocessor : AssetPostprocessor
{
    static bool _refreshScheduled;

    static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
        string[] movedAssets, string[] movedFromAssetPaths)
    {
        if (_refreshScheduled) return;
        for (int i = 0; i < importedAssets.Length; i++)
        {
            string path = importedAssets[i];
            bool isManagedAudio =
                path.StartsWith("Assets/Audio/Pixabay/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("Assets/Audio/Actions/", StringComparison.OrdinalIgnoreCase);
            if (!isManagedAudio
                || AssetDatabase.GetMainAssetTypeAtPath(path) != typeof(AudioClip))
                continue;

            _refreshScheduled = true;
            EditorApplication.delayCall += Refresh;
            return;
        }
    }

    static void Refresh()
    {
        _refreshScheduled = false;
        GameAudioCatalogSetup.RefreshAfterAudioImport();
    }
}
#endif
