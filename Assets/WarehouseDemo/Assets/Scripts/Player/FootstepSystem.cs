using UnityEngine;

// Attach to the Player root alongside FirstPersonController.
// Plays footstep sounds based on movement state and floor surface (PhysicsMaterial name).
// Uses a 3D AudioSource so sounds are spatially correct for other players in co-op.
[RequireComponent(typeof(AudioSource))]
public class FootstepSystem : MonoBehaviour
{
    [System.Serializable]
    public struct SurfaceAudio
    {
        public string     PhysicsMaterialName; // exact name, case-insensitive
        public AudioClip[] Clips;
    }

    [Header("Step Intervals (seconds)")]
    [SerializeField] private float _walkInterval   = 0.50f;
    [SerializeField] private float _sprintInterval = 0.30f;
    [SerializeField] private float _crouchInterval = 0.70f;

    [Header("Volume")]
    [SerializeField] private float _walkVolume   = 0.40f;
    [SerializeField] private float _sprintVolume = 0.70f;
    [SerializeField] private float _crouchVolume = 0.20f;

    [Header("Surface Audio")]
    [SerializeField] private SurfaceAudio[] _surfaces;
    [SerializeField] private AudioClip[]    _defaultClips;

    [Header("Ground Detection")]
    [SerializeField] private LayerMask _groundMask = ~0;

    private AudioSource           _audio;
    private FirstPersonController _fpc;
    private float                 _stepTimer;
    private string[]              _surfaceLower; // pre-lowercased surface names

    private void Awake()
    {
        _audio = GetComponent<AudioSource>();
        _fpc   = GetComponent<FirstPersonController>();

        _audio.playOnAwake  = false;
        _audio.spatialBlend = 1f;
        _audio.rolloffMode  = AudioRolloffMode.Linear;
        _audio.maxDistance  = 15f;

        _surfaceLower = new string[_surfaces?.Length ?? 0];
        for (int i = 0; i < _surfaceLower.Length; i++)
            _surfaceLower[i] = _surfaces[i].PhysicsMaterialName?.ToLowerInvariant() ?? "";
    }

    private void Update()
    {
        if (_fpc == null || !_fpc.IsMoving || UIManager.IsAnyPanelOpen) return;

        float interval = _fpc.IsSprinting ? _sprintInterval
                       : _fpc.IsCrouching ? _crouchInterval
                                          : _walkInterval;
        _stepTimer += Time.deltaTime;
        if (_stepTimer < interval) return;

        _stepTimer = 0f;
        PlayStep();
    }

    private void PlayStep()
    {
        var clips = GetSurfaceClips();
        if (clips == null || clips.Length == 0) return;

        float vol = _fpc.IsSprinting ? _sprintVolume
                  : _fpc.IsCrouching ? _crouchVolume
                                     : _walkVolume;

        _audio.PlayOneShot(clips[Random.Range(0, clips.Length)], vol);
    }

    private AudioClip[] GetSurfaceClips()
    {
        if (Physics.Raycast(transform.position + Vector3.up * 0.1f,
                            Vector3.down, out var hit, 0.4f, _groundMask,
                            QueryTriggerInteraction.Ignore))
        {
            var mat = hit.collider.sharedMaterial;
            if (mat != null)
            {
                string matName = mat.name.ToLowerInvariant();
                for (int i = 0; i < _surfaceLower.Length; i++)
                    if (_surfaceLower[i] == matName) return _surfaces[i].Clips;
            }
        }
        return _defaultClips;
    }
}
