using UnityEngine;

/// Implemented by every warehouse mini-event.
/// Apply starts the effect; Remove cleans it up.
/// When the event ends (duration elapsed or condition met) it must call
/// GameEventManager.Instance?.MarkEventDone(Name) so the slot reopens.
public interface IGameEvent
{
    string Name        { get; }
    string Description { get; }

    /// Called by GameEventManager to start the effect.
    /// host is the GameEventManager MonoBehaviour — use it for StartCoroutine.
    void Apply(MonoBehaviour host);

    /// Called to cancel/cleanup early (e.g. on scene unload).
    void Remove();

    /// Called on the server to encode any random choices (positions, names, IDs) that must
    /// be identical on all clients. Return empty string if no extra data is needed.
    string GetNetworkPayload(UnityEngine.MonoBehaviour host) => string.Empty;
}
