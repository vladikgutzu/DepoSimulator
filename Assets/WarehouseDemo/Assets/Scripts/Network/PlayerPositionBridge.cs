using Unity.Netcode;
using UnityEngine;

/// Place on the scene FPS player GameObject.
/// Copies local player's position/yaw to their NetworkPlayer object each frame
/// so ClientNetworkTransform can replicate it to all other clients.
[RequireComponent(typeof(CharacterController))]
public class PlayerPositionBridge : MonoBehaviour
{
    void LateUpdate()
    {
        var net = NetworkPlayer.Local;
        if (net == null || !net.IsOwner) return;

        net.transform.position = transform.position;
        // Only sync yaw — the character model shouldn't tilt with camera pitch
        net.transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
    }
}
