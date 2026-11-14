using UnityEngine;

public enum PlayerRoleType
{
    None,
    Picker,           // Fast, highlights needed parts
    Assembler,        // Fast assembly, two benches
    ForkLiftOperator, // Operates the forklift, moves pallets
    Dispatcher,       // Warehouse map, pings, manages orders
}

[CreateAssetMenu(menuName = "Warehouse/Player Role", fileName = "New Role")]
public class PlayerRole : ScriptableObject
{
    public PlayerRoleType RoleType    = PlayerRoleType.None;
    public string         RoleName;
    [TextArea]
    public string         Description;
    public Color          RoleColor   = Color.white;

    [Header("Movement")]
    [Range(0.5f, 2f)]  public float SpeedMultiplier         = 1f;

    [Header("Picker — part highlight")]
    [Range(0f, 30f)]   public float PartHighlightRange       = 0f;

    [Header("Assembler — bench speed")]
    [Range(1f, 3f)]    public float AssemblySpeedMultiplier  = 1f;

    [Header("Forklift")]
    public bool HasForkLiftAccess = false;
}
