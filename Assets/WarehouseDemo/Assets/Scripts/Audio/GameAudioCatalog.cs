using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "GameAudioCatalog", menuName = "Warehouse/Audio/Game Audio Catalog")]
public sealed class GameAudioCatalog : ScriptableObject
{
    [SerializeField] AudioCue[] _cues;
    [SerializeField, Range(4, 64)] int _oneShotVoiceCount = 20;

    Dictionary<GameAudioEvent, AudioCue> _lookup;

    public int OneShotVoiceCount => _oneShotVoiceCount;

    public bool TryGet(GameAudioEvent audioEvent, out AudioCue cue)
    {
        EnsureLookup();
        return _lookup.TryGetValue(audioEvent, out cue) && cue != null && cue.HasClips;
    }

    void EnsureLookup()
    {
        if (_lookup != null) return;
        _lookup = new Dictionary<GameAudioEvent, AudioCue>(_cues?.Length ?? 0);
        if (_cues == null) return;

        for (int i = 0; i < _cues.Length; i++)
        {
            AudioCue cue = _cues[i];
            if (cue == null || cue.Event == GameAudioEvent.None) continue;
            _lookup[cue.Event] = cue;
        }
    }

#if UNITY_EDITOR
    public void EditorSetCues(AudioCue[] cues)
    {
        _cues = cues;
        _lookup = null;
    }
#endif

    void OnValidate() => _lookup = null;
}
