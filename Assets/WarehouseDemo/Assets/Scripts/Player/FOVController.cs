using UnityEngine;

// Attach to the Camera child of the Player.
// Smoothly transitions fieldOfView based on FirstPersonController movement state.
[RequireComponent(typeof(Camera))]
public class FOVController : MonoBehaviour
{
    [SerializeField] private float _defaultFOV = 60f;
    [SerializeField] private float _sprintFOV  = 70f;
    [SerializeField] private float _crouchFOV  = 55f;
    [SerializeField] private float _lerpSpeed  = 8f;

    private Camera                _cam;
    private FirstPersonController _fpc;

    private void Awake()
    {
        _cam = GetComponent<Camera>();
        _fpc = GetComponentInParent<FirstPersonController>();
        _cam.fieldOfView = _defaultFOV;
    }

    private void Update()
    {
        if (_fpc == null) return;

        float target = _fpc.IsSprinting ? _sprintFOV
                     : _fpc.IsCrouching ? _crouchFOV
                                        : _defaultFOV;

        _cam.fieldOfView = Mathf.Lerp(_cam.fieldOfView, target, Time.deltaTime * _lerpSpeed);
    }
}
