using UnityEngine;

/// The materials a player wears for one role — or, with Solo ticked, the ones worn when nobody
/// has a role at all.
///
/// One asset per look. The body is a single skinned mesh with two submeshes, so Materials is
/// ordered the same way the renderer expects them: element 0 is the armour, element 1 the
/// underlayer. A set with the wrong count is ignored rather than applied half-way, since a body
/// wearing one role's plating over another's frame is worse than one that never changed.
[CreateAssetMenu(menuName = "Warehouse/Player Skin", fileName = "New Player Skin")]
public class PlayerSkinSet : ScriptableObject
{
    [Tooltip("Worn in single player, where there are no roles to tell apart. Exactly one set " +
             "should have this ticked; Role is ignored when it is.")]
    public bool Solo;

    [Tooltip("Which role wears this. Ignored on the solo set.")]
    public PlayerRoleType Role = PlayerRoleType.None;

    [Tooltip("In renderer order, one per submesh.")]
    public Material[] Materials;
}
