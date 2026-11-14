using Unity.Netcode;
using UnityEngine;

/// Shows another player's injuries on the puppet everyone else looks at.
///
/// Everything about being hurt runs on the machine of the player it happened to: InjurySystem, the
/// panels coming off, the hobble, the crouch. Left there, a team-mate with an arm torn off was seen
/// by the rest of the shift as an intact robot — and worse than invisible, because the animator
/// parameters *are* bridged, so a man hopping on one leg was seen calmly walking at the speed the
/// hop was feeding the tree. The picture was not missing; it was wrong.
///
/// Three things arrive, by three different roads, and which road each takes is not arbitrary:
///
///   • **The injury mask** — a NetworkVariable, because it is state. Somebody joining mid-shift has
///     to see the arm that came off ten minutes ago, and a one-shot RPC could never tell them.
///     Repainted with BotDamageVisual, which works here unchanged: the co-op prefab carries the
///     same separable `A_`/`B_` panels as the body the player wears. The mask also decides whether
///     his severed limb is still lying on this machine's floor: a repair is nothing but a part
///     leaving the mask, so clearing the copy from here needs no second message and covers every
///     road out of an injury, including a late joiner who never saw the limb dropped.
///
///   • **The hobble** — also a NetworkVariable, and separate from the mask even though it could be
///     derived from it, because the puppet reacts to it completely differently: the mask repaints a
///     body, this swaps its locomotion. Uses InjuredLocomotion.Swap, the same code the local player
///     runs, so the two sides cannot drift into showing different things.
///
///   • **The crouch** — an animator bool, because one road for those already exists and works:
///     PlayerAnimatorBridge copies parameters onto the puppet and NetworkAnimator replicates them.
///     A layer weight is not a parameter and cannot travel, so the weight is derived here — and so
///     is the grounding, with RepairCrouch.Grounding, the same measurement the owner runs. The
///     crouch clips are not grounded (RepairCrouch says by how much, in centimetres), so a puppet
///     given only the weight plays them raw and stands visibly off the floor for the whole repair.
///
/// Remote-only. The owner's own body is the scene rig, which the local components drive directly,
/// and running this on top of it would be two things painting one robot.
[DisallowMultipleComponent]
public class RemoteInjuryVisual : MonoBehaviour
{
    /// Two bits per part, so the mask carries severity and not just "broken".
    ///
    /// Packed rather than sent as a list because it is rewritten on every change and read by every
    /// peer: one int replicates in a single field, and the parts are a fixed, small set.
    public const int BitsPerPart = 2;

    public enum State { Fine = 0, Dented = 1, Severed = 2 }

    /// Three bits per part for the graft mask: a role plus one, so zero can mean "its own paint".
    public const int GraftBitsPerPart = 3;

    public static readonly BodyPart[] Parts =
    {
        BodyPart.Head, BodyPart.Torso,
        BodyPart.LeftArm, BodyPart.RightArm,
        BodyPart.LeftLeg, BodyPart.RightLeg,
    };

    /// Packs one part's state into a mask.
    public static int With(int mask, BodyPart part, State state)
    {
        int slot = System.Array.IndexOf(Parts, part);
        if (slot < 0) return mask;

        int shift = slot * BitsPerPart;
        return (mask & ~(0b11 << shift)) | (((int)state & 0b11) << shift);
    }

    /// Reads one part's state out of a mask.
    public static State Read(int mask, BodyPart part)
    {
        int slot = System.Array.IndexOf(Parts, part);
        if (slot < 0) return State.Fine;

        return (State)((mask >> (slot * BitsPerPart)) & 0b11);
    }

    // ── The graft mask ────────────────────────────────────────────────────────
    //
    // Which parts of this body were fitted from somebody else's, and whose livery they wear. A
    // second mask rather than more bits in the first, because the two answer different questions
    // and change at different times: a part can be grafted and then broken again, and a late
    // joiner needs both answers independently.
    //
    // The role rather than the player: liveries are per-role assets every peer already has, so
    // three bits repaint an arm identically everywhere without sending a material or a name.

    /// Packs one part's donor role into a graft mask.
    public static int WithGraft(int mask, BodyPart part, PlayerRoleType donor)
    {
        int slot = System.Array.IndexOf(Parts, part);
        if (slot < 0) return mask;

        int shift = slot * GraftBitsPerPart;
        int value = ((int)donor + 1) & 0b111;

        return (mask & ~(0b111 << shift)) | (value << shift);
    }

    /// Reads one part's donor role. False when the part is wearing its own paint.
    public static bool ReadGraft(int mask, BodyPart part, out PlayerRoleType donor)
    {
        donor = PlayerRoleType.None;

        int slot = System.Array.IndexOf(Parts, part);
        if (slot < 0) return false;

        int value = (mask >> (slot * GraftBitsPerPart)) & 0b111;
        if (value == 0) return false;

        donor = (PlayerRoleType)(value - 1);
        return true;
    }

    // ── Instance ──────────────────────────────────────────────────────────────

    [Tooltip("Seconds to fade the repair crouch in and out. Matches RepairCrouch's own fade, so " +
             "the movement looks the same from either side of the session.")]
    [SerializeField] float _crouchFade = 0.18f;

    static readonly int CrouchHash = Animator.StringToHash(RepairCrouch.CrouchParam);
    static readonly int DownHash   = Animator.StringToHash(RepairCrouch.DownState);

    NetworkPlayer           _player;
    Animator                _animator;
    BotDamageVisual         _visual;
    InjuredLocomotion.Swap  _hobble;
    RepairCrouch.Grounding  _ground;
    RemoteRagdollVisual     _ragdoll;

    int   _shown  = -1;         // the mask currently painted; -1 so the first update always runs
    int   _grafted = -1;        // the graft mask currently worn, same reasoning
    float _crouchWeight;
    int   _crouchLayer = -1;

    /// His limbs stay, and stop being his.
    ///
    /// They used to go with him, on the reasoning that an arm belonging to a player who has left is
    /// unpickable and unrepairable and would lie there for the rest of the shift. Both halves of
    /// that stopped being true the moment any left arm began fitting any robot: what he leaves
    /// behind is a spare part, and deleting it takes a working limb out of a warehouse where
    /// somebody is probably hopping around missing one.
    ///
    /// Orphaned rather than reassigned, because it needs an address that outlives him — see
    /// SeveredLimbItem.Orphan.
    void OnDestroy()
    {
        if (_player != null) SeveredLimbItem.OrphanFor(_player);
    }

    void Awake()
    {
        _player   = GetComponentInParent<NetworkPlayer>();
        _animator = GetComponentInChildren<Animator>();

        if (_animator == null) return;

        _visual  = BotDamageVisual.Ensure(_animator.gameObject);
        _hobble  = new InjuredLocomotion.Swap(_animator);
        _ragdoll = GetComponent<RemoteRagdollVisual>();

        // The root, not this transform: the correction is applied to the body hanging off the root,
        // and here that body is the object this component is on. Same shape as the player's own rig,
        // where the root is PlayerArmature and the body is the model under it.
        if (_player != null) _ground = new RepairCrouch.Grounding(_player.transform, _animator);

        for (int i = 0; i < _animator.layerCount; i++)
            if (_animator.GetLayerName(i) == RepairCrouch.LayerName) { _crouchLayer = i; break; }
    }

    void LateUpdate()
    {
        if (_player == null || _animator == null) return;

        // The owner already has all of this on his own rig. Checked every frame rather than once in
        // Awake because ownership is not known until the object spawns, which is later.
        if (_player.IsOwner) return;

        // Before the panels, so a part that comes back on this frame comes back on already
        // wearing the right colour instead of flashing the wearer's own for a frame.
        Graft(_player.GraftMask.Value);

        Paint(_player.InjuryMask.Value);
        _hobble?.Apply(_player.IsHobbling.Value);
        Crouch(_animator.GetBool(CrouchHash));

        // After the crouch has set the weight, and last of all, because the feet can only be
        // measured once the pose is built. Stood down for a fall: the ragdoll writes this same
        // localPosition to keep the knees out of the concrete, and two corrections on one transform
        // is one too many.
        if (_ragdoll == null || !_ragdoll.IsRagdoll) _ground?.Apply(_crouchWeight);
    }

    /// Repaints the body, and clears away limbs that are back on it. Cheap to call every frame —
    /// it does nothing until the mask changes.
    void Paint(int mask)
    {
        if (_visual == null || mask == _shown) return;

        _shown = mask;

        foreach (var part in Parts)
        {
            var state = Read(mask, part);

            if (state == State.Fine) _visual.SetBroken(part, false);
            else _visual.SetBroken(part, true, state == State.Severed
                                                  ? BotDamageVisual.Severity.Severed
                                                  : BotDamageVisual.Severity.Dented);

            // Deliberately nothing here about clearing his limb away.
            //
            // This used to destroy the copy on the transition out of Severed, on the reasoning that
            // a repair is a part leaving the mask and the arm on the floor must therefore be the
            // one he just used. That stopped being true the moment any left arm began fitting any
            // robot: he can walk in whole having bolted on somebody else's, and his own arm is then
            // still a good spare — possibly one already in a third player's fist, which this would
            // have deleted out of it.
            //
            // A limb now leaves the world for exactly one reason, and it says so out loud:
            // NetworkPlayer.ConsumeLimb, when somebody bolts it on. Nothing else deletes one — not
            // a repair, not a respawn, not its owner leaving the session. See InjurySystem.Mend.
        }
    }

    /// Puts the borrowed paint on a puppet — the arm this player refitted out of somebody else.
    ///
    /// Nothing here decides anything: the wearer's machine worked out whose livery each part is in
    /// and packed it into the mask. This unpacks it and hands it to the same BotDamageVisual the
    /// owner's own body uses, so the beige arm on the violet robot is beige on every screen,
    /// including one that connected after the swap happened.
    void Graft(int mask)
    {
        if (_visual == null || mask == _grafted) return;
        _grafted = mask;

        foreach (var part in Parts)
        {
            if (ReadGraft(mask, part, out var donor)) _visual.SetGraft(part, donor);
            else                                      _visual.ClearGraft(part);
        }
    }

    /// Drives the crouch layer, which is the one thing here that is a fade rather than a state.
    void Crouch(bool on)
    {
        if (_crouchLayer < 0) return;

        float step = _crouchFade > 0.001f ? Time.deltaTime / _crouchFade : 1f;

        // Taken while he is still upright — before the weight leaves zero — so it is a reading of
        // this body standing, which is the whole reference the correction is measured against.
        if (on && _crouchWeight <= 0f) _ground?.Capture();

        _crouchWeight = Mathf.MoveTowards(_crouchWeight, on ? 1f : 0f, step);

        _animator.SetLayerWeight(_crouchLayer, _crouchWeight);

        var current = _animator.GetCurrentAnimatorStateInfo(_crouchLayer);

        // Started from the top when the crouch begins, and parked on the empty state when it ends
        // so a stale pose is not sitting at weight zero waiting to be faded back in halfway
        // through. The stand-up is deliberately not mirrored: the owner's crouch bool goes false
        // when he starts standing, and fading the layer out over the same fifth of a second gives
        // the same result without a second parameter to keep in step.
        if (on && current.shortNameHash != DownHash)
            _animator.Play($"{RepairCrouch.LayerName}.{RepairCrouch.DownState}", _crouchLayer, 0f);
        else if (!on && _crouchWeight <= 0.001f && current.shortNameHash == DownHash)
        {
            _animator.Play($"{RepairCrouch.LayerName}.{RepairCrouch.IdleState}", _crouchLayer, 0f);

            // Let go with the pose rather than at the end of the repair, so the correction fades to
            // zero along with the thing that needed it instead of being dropped in one frame.
            _ground?.Release();
        }
    }
}
