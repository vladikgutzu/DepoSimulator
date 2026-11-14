using UnityEngine;

/// Where the weapon carry socket sits on the body.
///
/// An asset rather than a value on the scene object, because the socket is tuned in play mode —
/// weapon in hand, watching the hands — and everything in a scene is thrown away when play stops.
/// The socket transform still exists and is still what the weapon hangs from; this is just the
/// place its pose is kept between sessions.
///
/// One asset for the whole player, not one per weapon: the socket is a spot on the chest, and how
/// a particular weapon hangs off it is that weapon's own CarryOffset / CarryRotation.
[CreateAssetMenu(menuName = "Warehouse/Weapon Carry Socket", fileName = "weapon_carry_socket")]
public class WeaponCarrySocketSettings : ScriptableObject
{
    public const string ResourcePath = "Catalog/Weapons/weapon_carry_socket";

    [Tooltip("Local position on the chest bone.")]
    public Vector3 LocalPosition = new Vector3(0.18f, 0.02f, 0.10f);

    [Tooltip("Local rotation on the chest bone, in degrees.")]
    public Vector3 LocalRotation;

    static WeaponCarrySocketSettings _cached;
    static bool _looked;

    /// The settings asset, or null when nobody has made one — in which case whatever the socket
    /// transform is set to in the scene stands.
    public static WeaponCarrySocketSettings Load()
    {
        if (_looked) return _cached;
        _looked = true;
        _cached = Resources.Load<WeaponCarrySocketSettings>(ResourcePath);
        return _cached;
    }

    // Statics survive a play session with domain reload turned off, so a re-imported or newly
    // created asset would otherwise stay invisible until the editor restarted.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Reset() { _cached = null; _looked = false; }
}
