using UnityEngine;

// Manages the local player's role and pushes modifiers to relevant systems.
// Add to the Player GameObject alongside FirstPersonController.
public class RoleController : MonoBehaviour
{
    public static RoleController Instance { get; private set; }

    [SerializeField] private PlayerRole _startRole;

    public PlayerRole CurrentRole { get; private set; }
    public event System.Action<PlayerRole> OnRoleChanged;

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        if (_startRole != null) SetRole(_startRole);
    }

    public void SetRole(PlayerRole role)
    {
        CurrentRole = role;
        if (role == null) return;

        OnRoleChanged?.Invoke(role);
    }

    // Read by FirstPersonController each frame — no direct coupling needed.
    public float GetSpeedMultiplier() =>
        CurrentRole != null ? CurrentRole.SpeedMultiplier : 1f;

    // Read by AssemblyBench if Assembler role bonus is needed in future.
    public float GetAssemblySpeedMultiplier() =>
        CurrentRole != null ? CurrentRole.AssemblySpeedMultiplier : 1f;
}
