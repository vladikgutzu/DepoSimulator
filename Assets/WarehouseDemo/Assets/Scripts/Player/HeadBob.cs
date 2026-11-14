using UnityEngine;

// Attach to the Camera child of the Player.
// Handles all camera localPosition animation: head bob while moving + crouch Y offset.
// CameraShake (LateUpdate) runs after this and applies rotation-only shake on top.
[RequireComponent(typeof(Camera))]
public class HeadBob : MonoBehaviour
{
    [Header("Walk Bob")]
    [SerializeField] private float _walkBobSpeed  = 14f;
    [SerializeField] private float _walkBobAmount = 0.05f;

    [Header("Sprint Bob")]
    [SerializeField] private float _sprintBobSpeed  = 18f;
    [SerializeField] private float _sprintBobAmount = 0.10f;

    [Header("Crouch Bob")]
    [SerializeField] private float _crouchBobSpeed  = 10f;
    [SerializeField] private float _crouchBobAmount = 0.025f;

    [Header("Crouch Camera Offset")]
    [Tooltip("Extra local-Y applied when crouching (negative = lower camera)")]
    [SerializeField] private float _crouchCamOffset = -0.5f;

    [Header("Smoothing")]
    [SerializeField] private float _returnSpeed = 8f;

    private FirstPersonController _fpc;
    private Vector3 _baseLocalPos;
    private float   _bobTimer;

    private void Awake()
    {
        _fpc = GetComponentInParent<FirstPersonController>();
        _baseLocalPos = transform.localPosition;
    }

    private void Update()
    {
        if (_fpc == null) return;

        float crouchOffset = _fpc.IsCrouching ? _crouchCamOffset : 0f;

        Vector3 target;
        if (_fpc.IsMoving)
        {
            float speed  = _fpc.IsSprinting ? _sprintBobSpeed
                         : _fpc.IsCrouching ? _crouchBobSpeed
                                            : _walkBobSpeed;
            float amount = _fpc.IsSprinting ? _sprintBobAmount
                         : _fpc.IsCrouching ? _crouchBobAmount
                                            : _walkBobAmount;

            _bobTimer += Time.deltaTime * speed;

            float bobY = Mathf.Sin(_bobTimer)        * amount;
            float bobX = Mathf.Sin(_bobTimer * 0.5f) * amount * 0.5f;

            target = _baseLocalPos + new Vector3(bobX, crouchOffset + bobY, 0f);
        }
        else
        {
            _bobTimer = 0f;
            target = _baseLocalPos + new Vector3(0f, crouchOffset, 0f);
        }

        transform.localPosition = Vector3.Lerp(
            transform.localPosition, target, Time.deltaTime * _returnSpeed);
    }
}
