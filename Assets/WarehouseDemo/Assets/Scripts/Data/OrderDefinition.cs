using UnityEngine;

[CreateAssetMenu(menuName = "Warehouse/Order Definition", fileName = "New Order")]
public class OrderDefinition : ScriptableObject
{
    [Tooltip("Unchecked orders are excluded from GameManager's catalog even though Resources.LoadAll " +
             "still finds them regardless of which folder they sit in (it recurses into subfolders — " +
             "a naming convention like '_disabled' does not actually exclude an asset).")]
    public bool IsEnabled = true;

    public string OrderName;
    public string ClientName;
    [TextArea] public string Description;
    public PartRequirement[] RequiredParts;
    public int   Reward = 100;
    [Tooltip("Seconds to complete the order. 0 = no deadline.")]
    public float TimeLimitSeconds = 0f;

    [Header("Assembly Ghost")]
    [Tooltip("If set, BuildGhost uses this prefab and maps each part via GhostChildPaths instead of Resources/AssemblyReferences/{name}.")]
    public GameObject SharedGhostPrefab;

    [System.Serializable]
    public struct PartRequirement
    {
        public PartDefinition Part;
        public int Count;
        [Tooltip("Assembly stage (0 = first). Parts in same stage can be placed in any order.\nNext stage unlocks only after the current one is fully assembled.")]
        [Range(0, 4)] public int AssemblyStage;
    }
}
