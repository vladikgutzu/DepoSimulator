using StarterAssets;
using UnityEngine;

/// Applies combined speed multipliers (role specialization + injuries)
/// to ThirdPersonController every frame.
/// Add to Player alongside ThirdPersonController.
[RequireComponent(typeof(ThirdPersonController))]
public class PlayerSpeedOverride : MonoBehaviour
{
    [Header("Packed Order Carry")]
    [SerializeField, Range(0.1f, 1f)] float _bulkyCarrySpeedMultiplier = 0.55f;

    ThirdPersonController _tpc;
    HopMovement           _hop;
    float _baseMoveSpeed;
    float _baseSprintSpeed;

    void Awake()
    {
        _tpc = GetComponent<ThirdPersonController>();
        // Snapshot the speeds configured in Inspector / via SetupTPSPlayer.
        _baseMoveSpeed   = _tpc.MoveSpeed;
        _baseSprintSpeed = _tpc.SprintSpeed;
    }

    void Update()
    {
        float roleMult = SpecializationPenalties.SoloMode
            ? 1f
            : RoleTaskManager.GetBonus(SpecializationPenalties.GetLocalRole()).SpeedMultiplier;

        // The hop gate rides on top of everything else. It is 1 unless a leg is missing, and while
        // one is it swings between 0 and a burst — which is what turns a smooth glide under a
        // hopping animation into actual hopping. See HopMovement.
        if (_hop == null) _hop = GetComponent<HopMovement>();

        float mult = roleMult
                   * (InjurySystem.Instance?.SpeedMultiplier ?? 1f)
                   * GameEventManager.EnvironmentSpeedMult
                   * (_hop != null ? _hop.SpeedMultiplier : 1f)
                   * (BoxPhysicsInteractable.GrabbedBox?.HasBulkyCarryDebuff == true
                       ? _bulkyCarrySpeedMultiplier
                       : 1f);

        _tpc.MoveSpeed   = _baseMoveSpeed   * mult;
        _tpc.SprintSpeed = _baseSprintSpeed * mult;
    }
}
