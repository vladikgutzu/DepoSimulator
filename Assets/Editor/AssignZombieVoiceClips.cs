using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// Puts the recorded zombie clips onto the raider prefab's ZombieVoice.
///
/// ZombieVoice synthesises everything it needs when nothing is assigned, which is why the raiders
/// have had a voice all along — but the prefab's clip arrays were empty, so every one of them was
/// using the fallback and the recordings in Assets/Audio/Zombie were doing nothing.
///
/// Worth knowing when reading that folder: the eight .wav files in it are not recordings. They are
/// what Tools/Warehouse Demo/Export Zombie Voice Clips wrote out of the synthesiser — same names as
/// the synth builders use — so assigning them would swap the fallback for a copy of itself. Only
/// the .mp3 files are real, and those are what this assigns.
///
/// Re-runnable: it rewrites the arrays from what is on disk.
public static class AssignZombieVoiceClips
{
    const string PrefabPath = "Assets/Prefabs/Thief.prefab";
    const string ClipFolder = "Assets/Audio/Zombie";

    static readonly string[] Growls =
    {
        "zombie_growling_1.mp3",
        "zombie_growling_2.mp3",
        "zombie_growling_3.mp3",
    };

    static readonly string[] Hits =
    {
        "hit_1.mp3",
        "hit_2.mp3",
    };

    [MenuItem("Tools/Warehouse/Assign Zombie Voice Clips")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) return $"no prefab at {PrefabPath}";

        var voice = prefab.GetComponentInChildren<ZombieVoice>(true);
        if (voice == null) return $"{PrefabPath} has no ZombieVoice";

        var report = new List<string>();

        var so = new SerializedObject(voice);
        int growls = Fill(so, "_growls", Growls, report);
        int hits   = Fill(so, "_hits",   Hits,   report);
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(prefab);
        AssetDatabase.SaveAssets();

        report.Add("");
        report.Add($"{growls} growl(s) and {hits} hit(s) on {PrefabPath}.");
        report.Add("_attacks and _death left empty on purpose — nothing recorded for them yet, so " +
                   "they keep using the synthesised roar and groan.");

        return string.Join("\n", report);
    }

    static int Fill(SerializedObject so, string field, string[] fileNames, List<string> report)
    {
        var prop = so.FindProperty(field);
        if (prop == null) { report.Add($"• {field}: no such field"); return 0; }

        var clips = new List<AudioClip>();

        foreach (string name in fileNames)
        {
            string path = $"{ClipFolder}/{name}";
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);

            if (clip == null) { report.Add($"• {path}: MISSING"); continue; }

            TuneImport(path, report);
            clips.Add(clip);
        }

        prop.arraySize = clips.Count;
        for (int i = 0; i < clips.Count; i++)
            prop.GetArrayElementAtIndex(i).objectReferenceValue = clips[i];

        report.Add($"• {field}: {string.Join(", ", clips.Select(c => c.name))}");
        return clips.Count;
    }

    /// Mono, and decompressed up front.
    ///
    /// Mono is the one that matters. ZombieVoice plays these on a source with spatialBlend 1 and
    /// the comment "positional: you hear where he is" — but a stereo clip carries its own left and
    /// right, and Unity will not pan what is already panned. Every one of these recordings imported
    /// as two channels, so the whole point of a 3D zombie voice was quietly lost. forceToMono folds
    /// them down at import and hands the panning back to the engine.
    ///
    /// Decompress-on-load because they are short and they fire on something the player is watching.
    /// Compressed-in-memory mp3 pays a decode on the first play of each clip, which is exactly the
    /// moment the sound needed to be already there.
    static void TuneImport(string path, List<string> report)
    {
        var importer = AssetImporter.GetAtPath(path) as AudioImporter;
        if (importer == null) return;

        var settings = importer.defaultSampleSettings;

        bool wantsMono   = !importer.forceToMono;
        bool wantsLoaded = settings.loadType != AudioClipLoadType.DecompressOnLoad
                        || !settings.preloadAudioData;

        if (!wantsMono && !wantsLoaded) return;

        importer.forceToMono = true;

        // preloadAudioData moved onto the per-platform sample settings; the importer-level property
        // is obsolete and does not compile here.
        settings.loadType         = AudioClipLoadType.DecompressOnLoad;
        settings.preloadAudioData = true;
        importer.defaultSampleSettings = settings;

        importer.SaveAndReimport();

        var did = new List<string>();
        if (wantsMono)   did.Add("forced to mono");
        if (wantsLoaded) did.Add("decompress on load");
        report.Add($"    ({System.IO.Path.GetFileName(path)}: {string.Join(", ", did)})");
    }
}
