using UnityEngine;

[CreateAssetMenu(menuName = "Warehouse/Part Definition", fileName = "New Part")]
public class PartDefinition : ScriptableObject
{
    public string PartName;
    public string PartID;
    [TextArea] public string Description;
    public GameObject VisualPrefab;           // actual 3D moto_part prefab
    public GameObject GhostPrefab;            // transparent ghost for assembly slots
    public Color      DebugColor = Color.gray;
    public bool       IsHeavy;

    [Header("Shared Ghost (multi-part prefabs)")]
    [Tooltip("Child paths within OrderDefinition.SharedGhostPrefab whose renderers belong to this part. Leave empty for legacy PartID-named-child lookup.")]
    public string[] GhostChildPaths;
}
