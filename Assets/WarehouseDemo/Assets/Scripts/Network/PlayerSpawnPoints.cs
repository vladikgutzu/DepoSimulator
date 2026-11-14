using UnityEngine;

/// Scene-placed registry of where each connecting client's Player rig should start. Without this,
/// every client shares the single scene-placed "Player" GameObject's fixed starting transform, so
/// all players spawn stacked on top of each other. NetworkPlayer.OnNetworkSpawn() looks this up
/// for the local owner and repositions the Player rig before any position sync starts.
public class PlayerSpawnPoints : MonoBehaviour
{
    public static PlayerSpawnPoints Instance { get; private set; }

    [SerializeField] Transform[] _points;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    public Transform GetPointFor(ulong clientId)
    {
        if (_points == null || _points.Length == 0) return null;
        return _points[(int)(clientId % (ulong)_points.Length)];
    }
}
