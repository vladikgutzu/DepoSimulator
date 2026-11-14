using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// Tools → Warehouse Demo → Export Zombie Voice Clips
///
/// Renders ZombieVoice's synthesized growls, roars, impacts and death groan to
/// Assets/Audio/Zombie/*.wav so they can be auditioned in the inspector — and, if they pass,
/// dropped into the clip slots on the Thief prefab (or replaced with real recordings).
///
/// Exporting is optional: with the slots left empty the same sounds are generated at runtime.
public static class ExportZombieVoiceClips
{
    const string OutputDir = "Assets/Audio/Zombie";

    [MenuItem("Tools/Warehouse Demo/Export Zombie Voice Clips")]
    static void Run()
    {
        string report = Execute();
        EditorUtility.DisplayDialog("Zombie voice", report, "OK");
    }

    public static string Execute()
    {
        Directory.CreateDirectory(OutputDir);

        var clips = new List<AudioClip>();
        clips.AddRange(ZombieVoice.SynthGrowls());
        clips.AddRange(ZombieVoice.SynthAttacks());
        clips.AddRange(ZombieVoice.SynthHits());
        clips.Add(ZombieVoice.SynthDeath());

        int written = 0;
        foreach (var clip in clips)
        {
            if (clip == null) continue;
            File.WriteAllBytes($"{OutputDir}/{clip.name}.wav", EncodeWav(clip));
            written++;
        }

        AssetDatabase.Refresh();

        string body = $"• {written} clips written to {OutputDir}.";
        Debug.Log("[Zombie] Voice export:\n" + body);
        return body;
    }

    /// Minimal 16-bit PCM WAV: 44-byte header, then the samples.
    static byte[] EncodeWav(AudioClip clip)
    {
        var samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        using var stream = new MemoryStream();
        using var w      = new BinaryWriter(stream);

        int byteRate   = clip.frequency * clip.channels * 2;
        int dataLength = samples.Length * 2;

        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataLength);
        w.Write(new[] { 'W', 'A', 'V', 'E' });

        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                                    // PCM chunk size
        w.Write((short)1);                              // PCM, uncompressed
        w.Write((short)clip.channels);
        w.Write(clip.frequency);
        w.Write(byteRate);
        w.Write((short)(clip.channels * 2));            // block align
        w.Write((short)16);                             // bits per sample

        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataLength);

        foreach (var s in samples)
            w.Write((short)(Mathf.Clamp(s, -1f, 1f) * short.MaxValue));

        w.Flush();
        return stream.ToArray();
    }
}
