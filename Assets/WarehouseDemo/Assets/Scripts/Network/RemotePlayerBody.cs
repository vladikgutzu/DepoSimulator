using Unity.Netcode;
using UnityEngine;

/// Gives the puppet other players look at a body that things can actually hit.
///
/// It had none. The NetworkPlayer root carries no collider at all, and the only colliders anywhere
/// on the prefab are the ragdoll bones under CharacterVisual — which RemoteRagdollVisual switches
/// off in Awake (`detectCollisions = false`) and only turns on for the length of a fall. So a
/// team-mate was a hologram: a crate thrown square into his chest went through him and landed on
/// the floor behind, and because nothing collided, nothing could report the hit either.
///
/// That second half is the one worth spelling out, because it is why this cannot be fixed at the
/// throwing end alone. BoxPhysicsInteractable resolves a hit on a player by looking for a
/// RagdollController on whatever it struck, and a RagdollController only ever exists on the local
/// scene rig — the machine of the player it belongs to. The puppet has none and must not have one:
/// only the owner may knock his own body down. So the capsule here exists to make the collision
/// happen and to name whose it is; what to do about it is decided on the server and delivered to
/// the owner (see BoxPhysicsInteractable.HitRemotePlayer).
///
/// Kinematic, because the root's position belongs to ClientNetworkTransform and a simulated body
/// would fight it every frame. A kinematic rigidbody still generates collision callbacks on the
/// dynamic side of a contact, which is the side the box is on, and still blocks a CharacterController
/// — so team-mates stop walking through one another as a side effect.
///
/// Off on the owner's own copy: his real body is the scene rig standing in the same place, and two
/// capsules in one spot shove each other apart.
[DisallowMultipleComponent]
public class RemotePlayerBody : MonoBehaviour
{
    [Tooltip("Matches the scene rig's CharacterController, so a box thrown at a team-mate connects " +
             "where it would connect on your own body.")]
    [SerializeField] float _height = 1.8f;
    [SerializeField] float _radius = 0.28f;

    [Tooltip("Solid: team-mates cannot walk through each other, and a thrown box bounces off. " +
             "Unticked makes the capsule a trigger — the box still registers the hit and still " +
             "knocks him down, but passes through the body rather than off it.")]
    [SerializeField] bool _solid = true;

    NetworkPlayer       _player;
    RemoteRagdollVisual _ragdoll;
    CapsuleCollider     _capsule;
    Rigidbody           _body;

    /// Which player this body belongs to — read by whatever hit it.
    public NetworkPlayer Player => _player;

    void Awake()
    {
        _player = GetComponent<NetworkPlayer>();

        var visual = transform.Find("CharacterVisual");
        if (visual != null) _ragdoll = visual.GetComponent<RemoteRagdollVisual>();

        _body = GetComponent<Rigidbody>();
        if (_body == null) _body = gameObject.AddComponent<Rigidbody>();

        _body.isKinematic    = true;
        _body.useGravity     = false;
        _body.interpolation  = RigidbodyInterpolation.None;

        // The capsule is teleported by ClientNetworkTransform rather than swept, and a thrown crate
        // is released Discrete. Speculative contacts on this side are what stop a fast box passing
        // clean through a team-mate between two physics steps — which is the very bug this
        // component exists to close, and it would be a poor joke to reintroduce it as tunnelling.
        _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        _capsule = GetComponent<CapsuleCollider>();
        if (_capsule == null) _capsule = gameObject.AddComponent<CapsuleCollider>();

        _capsule.height    = _height;
        _capsule.radius    = _radius;
        _capsule.center    = new Vector3(0f, _height * 0.5f, 0f);
        _capsule.isTrigger = !_solid;

        // Nothing to collide with until we know whose body this is; Update turns it on.
        _capsule.enabled = false;
    }

    void Update()
    {
        // Ownership is not known until the object spawns, which is later than Awake — so this is
        // asked every frame rather than answered once.
        bool wanted = _player != null
                   && _player.IsSpawned
                   && !_player.IsOwner
                   // While he is a heap of bones on the floor, the bones are the body. A capsule
                   // standing upright through them is a second, invisible, upright team-mate that
                   // boxes bounce off and nobody can see.
                   && (_ragdoll == null || !_ragdoll.IsRagdoll);

        if (_capsule.enabled != wanted) _capsule.enabled = wanted;
    }
}
