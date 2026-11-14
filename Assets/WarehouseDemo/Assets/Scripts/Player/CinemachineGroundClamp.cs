using Unity.Cinemachine;
using UnityEngine;

/// Cinemachine extension: keeps the camera from sinking below the floor.
///
/// The active player rig (Starter Assets ThirdPersonController + Cinemachine3rdPersonFollow on
/// "PlayerFollowCamera") swings the whole shoulder/arm rig by the FULL rotation of
/// CinemachineCameraTarget (yaw AND pitch). ThirdPersonController.TopClamp is set to 89° so
/// players can look almost straight up at high shelves — but Cinemachine3rdPersonFollow has no
/// built-in floor awareness, only wall/obstacle collision (CameraCollisionFilter). At steep
/// up-look angles the shoulder pivot itself swings below the floor, and since both the pivot and
/// the resulting camera point end up on the same (wrong) side of the floor collider, the built-in
/// collision avoidance never registers a hit — the camera clips straight through.
///
/// This extension runs after Body (Cinemachine3rdPersonFollow) computes its position each frame
/// and pushes the camera back up if it dropped below _groundReference's height + _minHeight,
/// without touching rotation — the player can still tilt the view up to see the top shelf, only
/// the camera's world position is kept above ground.
[ExecuteAlways]
public class CinemachineGroundClamp : CinemachineExtension
{
    [Tooltip("Transform whose Y position represents the floor under the player (e.g. the player " +
             "root, NOT the camera-follow target which already sits above head height).")]
    [SerializeField] Transform _groundReference;

    [Tooltip("Minimum height the camera must stay above _groundReference's Y position.")]
    [SerializeField] float _minHeight = 0.15f;

    protected override void PostPipelineStageCallback(
        CinemachineVirtualCameraBase vcam,
        CinemachineCore.Stage stage,
        ref CameraState state,
        float deltaTime)
    {
        if (stage != CinemachineCore.Stage.Body) return;

        Transform ground = _groundReference != null ? _groundReference : vcam.Follow;
        if (ground == null) return;

        float minY = ground.position.y + _minHeight;
        float currentY = state.GetCorrectedPosition().y;
        if (currentY < minY)
            state.PositionCorrection += new Vector3(0f, minY - currentY, 0f);
    }
}
