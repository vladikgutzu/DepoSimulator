using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// What the player is swinging. Bare hands are always available; a bat or a knife is picked
/// up off the warehouse floor (see MeleeWeaponItem) and replaces them until the shift ends.
/// Everything a raid can be beaten off with. All blunt: no blades, so a blow reads as "get off
/// that pallet" rather than as a killing. Every one of them is something that would plausibly be
/// lying around a warehouse.
///
/// The values are written out rather than left to declaration order, because they are what gets
/// serialised into the weapon assets and sent over the wire. 2, 3 and 8 are deliberately skipped:
/// 2 and 3 belonged to Baton and Sledgehammer, dropped once the art arrived and no model matched
/// them, and 8 to the Fire Extinguisher, cut from the weapon set. Reusing those numbers would
/// quietly turn an old baton into a pipe wrench.
public enum MeleeWeaponKind
{
    Fists            = 0,
    Bat              = 1,
    Crowbar          = 4,
    PipeWrench       = 5,
    Plank            = 6,
    RollingPin       = 7,
    RobotLimb        = 9,
}

/// Left-click melee, used to drive thieves off the pallets.
///
/// There are no attack animations in the project, so the swing is procedural — but it is the
/// arm that swings, not the weapon model: MeleeSwingArm springs the right shoulder and elbow
/// through the arc in LateUpdate. That is what makes a bare-handed swing visible at all, and it
/// carries the weapon for free, since the model is parented to the hand bone.
///
/// Add to the Player GameObject (the Editor tool Tools → Warehouse Demo → Setup Thief System
/// does this for you).
public class MeleeCombat : MonoBehaviour
{
    public static MeleeCombat Instance { get; private set; }

    // ── Per-weapon stats ──────────────────────────────────────────────────────

    internal readonly struct WeaponStats
    {
        public readonly int    Damage;
        public readonly float  Reach;
        public readonly float  HalfAngle;   // degrees off the aim direction that still connects
        public readonly float  Cooldown;
        public readonly float  Windup;      // press to blow landing
        public readonly float  Trauma;
        public readonly int    MaxTargets;  // 1 duels, more sweeps the arc
        public readonly string Label;

        public WeaponStats(int damage, float reach, float halfAngle,
                           float cooldown, float windup, float trauma, string label,
                           int maxTargets = 1)
        {
            Damage     = damage;
            Reach      = reach;
            HalfAngle  = halfAngle;
            Cooldown   = cooldown;
            Trauma     = trauma;
            Label      = label;
            MaxTargets = Mathf.Max(1, maxTargets);

            // A wind-up that eats the whole cooldown would leave no time to swing through and
            // recover, so it is capped at half of it however the asset is authored.
            float fallback = cooldown * DefaultWindupFraction;
            Windup = Mathf.Clamp(windup > 0f ? windup : fallback, 0.02f, cooldown * 0.5f);
        }
    }

    // Bare hands are the one set of numbers that stays in code: there is no weapon asset to
    // hang them off, and they double as the safe answer when a definition is missing.
    /// What the wind-up used to be for everything, kept as the fallback for a weapon whose
    /// asset leaves it at 0.
    const float DefaultWindupFraction = 0.30f;

    static readonly WeaponStats Unarmed = new(1, 1.7f, 55f, 0.55f, 0f, 0.12f, "Fists");
    static readonly WeaponStats RobotLimb = new(2, 1.9f, 50f, 0.70f, 0.18f, 0.22f,
                                                 "Robot limb");

    /// Numbers come from the weapon's MeleeWeaponDefinition asset. A kind with no asset falls
    /// back to bare hands rather than to invented values — a weapon that quietly hits like
    /// something nobody authored is worse than one that obviously does nothing special.
    static WeaponStats StatsFor(MeleeWeaponKind kind)
    {
        if (kind == MeleeWeaponKind.Fists) return Unarmed;
        if (kind == MeleeWeaponKind.RobotLimb) return RobotLimb;

        var def = MeleeWeaponRegistry.Get(kind);
        if (def == null) return Unarmed;

        return new WeaponStats(def.Damage, def.Reach, def.HalfAngle,
                               def.Cooldown, def.Windup, def.Trauma, def.DisplayName,
                               def.MaxTargets);
    }

    public readonly struct NetworkWeaponStats
    {
        public readonly int Damage;
        public readonly float Reach;
        public readonly float HalfAngle;
        public readonly float Cooldown;
        public readonly float Windup;
        public readonly int MaxTargets;

        internal NetworkWeaponStats(WeaponStats stats)
        {
            Damage = stats.Damage;
            Reach = stats.Reach;
            HalfAngle = stats.HalfAngle;
            Cooldown = stats.Cooldown;
            Windup = stats.Windup;
            MaxTargets = stats.MaxTargets;
        }
    }

    public static bool TryGetNetworkStats(int rawKind, out NetworkWeaponStats result)
    {
        result = default;
        if (!System.Enum.IsDefined(typeof(MeleeWeaponKind), rawKind)) return false;
        var kind = (MeleeWeaponKind)rawKind;
        if (kind != MeleeWeaponKind.Fists && kind != MeleeWeaponKind.RobotLimb &&
            MeleeWeaponRegistry.Get(kind) == null) return false;
        result = new NetworkWeaponStats(StatsFor(kind));
        return true;
    }

    // ── Public state ──────────────────────────────────────────────────────────

    public MeleeWeaponKind CurrentWeapon { get; private set; } = MeleeWeaponKind.Fists;

    /// The model currently in hand, so an editor tool can read the grip anchors off it and write
    /// them back to the prefab. Null bare-handed.
    public GameObject WeaponVisual => _weaponVisual;
    public bool            IsSwinging    { get; private set; }
    public float FullChargeTime => Mathf.Max(0.1f, _fullChargeTime);

    // ── Internal ──────────────────────────────────────────────────────────────

    Camera        _cam;
    Animator      _animator;
    Transform     _handBone;
    GameObject    _weaponVisual;
    SeveredLimbItem _equippedLimb;

    [Header("Carry")]
    [Tooltip("Where a weapon hangs while it is being carried. Left blank, one is made under the " +
             "chest bone at runtime. Assign a scene object to be able to drag it.")]
    [SerializeField] Transform _carrySocket;

    [Tooltip("Offset from the chest bone used when the socket has to be created.")]
    [SerializeField] Vector3 _carrySocketOffset = new Vector3(0.18f, 0.02f, 0.10f);

    // True while the weapon has been handed to the fist for a blow. Carrying and swinging hold it
    // in opposite directions -- see EnterCarry.
    bool _weaponInHand;
    bool _socketPlaced;

    // The masked arms layer and its two states. Resolved once: a controller without them (a
    // vehicle's, or a project where the layer was never built) leaves the swing procedural.
    int  _armsLayer = -1;
    bool _armsChecked;

    [Tooltip("Editor only. On: the weapon is re-seated from its GripPoint every frame, so dragging " +
             "the point moves the weapon. Off: the weapon stays where you drag it, and saving writes " +
             "that pose. Off is the one you want for eyeballing a grip.")]
    [SerializeField] bool _liveGripTuning;

    [Tooltip("Length of the clip in the arms layer's attack state, in seconds. The swing is scaled " +
             "to the weapon's cooldown from it — a baton has to get through the same animation in " +
             "a third of the time a sledgehammer takes. Update it if the clip is replaced.")]
    [SerializeField] float _attackClipDuration = 2.27f;

    static readonly int CarryStateHash  = Animator.StringToHash("WeaponCarry");
    static readonly int AttackStateHash = Animator.StringToHash("WeaponAttack");
    static readonly int AttackSpeedHash = Animator.StringToHash("AttackSpeed");
    MeleeSwingArm _swingArm;
    float         _nextSwingTime;

    bool  _charging;
    float _chargeTime;
    uint  _nextNetworkSwingSequence;
    uint  _pendingNetworkSwingSequence;

    [Header("Charged blow")]
    [Tooltip("Hold attack this long for a fully charged swing: it puts a raider down whatever " +
             "his health, and throws the body instead of laying it down. The cost is standing " +
             "still for it while the rest of the raid closes in.")]
    [SerializeField] float _fullChargeTime = 1.1f;
    [Tooltip("Extra impulse the charged blow carries, on top of what the weapon already does.")]
    [SerializeField] float _chargeTrauma = 0.5f;

    void Awake()
    {
        var np = GetComponent<NetworkPlayer>();
        if (np == null || Unity.Netcode.NetworkManager.Singleton?.IsListening != true)
            Instance = this;
        _animator = FindActiveHumanoidAnimator();

        // Added here rather than only on the prefab so a Player that was set up before this
        // existed still swings — same trick ThiefController uses for ZombieVoice.
        _swingArm = GetComponent<MeleeSwingArm>();
        if (_swingArm == null) _swingArm = gameObject.AddComponent<MeleeSwingArm>();
        _swingArm.RebindAnimator(_animator);
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    public static void BindLocal(MeleeCombat combat)
    {
        if (combat != null) Instance = combat;
    }

    public static void UnbindLocal(MeleeCombat combat)
    {
        if (Instance == combat) Instance = null;
    }

    void Update()
    {
#if UNITY_EDITOR
        // Live grip tuning. The placement is normally worked out once, at equip, which makes the
        // anchors on a weapon already in hand dead weight — you drag GripPoint and nothing moves,
        // because nothing reads it again. Re-applying every frame in the editor turns them into
        // handles: drag, and the weapon slides so the anchor stays in the fist.
        //
        // Idempotent, so this is not a fight: the anchors are children of the weapon, so moving
        // the weapon moves them too and the pose it computes stops changing the moment the drag
        // does. Editor-only because it exists for authoring, not for play.
        // Off by default, and that is the point: with it on, the weapon is re-seated every frame
        // from its anchor, so dragging the WEAPON in the hierarchy does nothing — it snaps back
        // before the mouse is up. Off, the weapon stays where it is put and the grip is tuned by
        // moving the thing you are actually looking at. Turn it on to drag GripPoint instead.
        if (_liveGripTuning
            && _weaponVisual != null && _weaponVisual.GetComponent<WeaponGripAnchors>() != null)
        {
            ApplyHandPlacement();
            PublishGrip();
        }
#endif

        var mouse = Mouse.current;
        var kb    = Keyboard.current;
        var gp    = Gamepad.current;

        // Alt + left click is PingSystem's binding — don't swing out from under it.
        bool suppressed = kb != null && kb.leftAltKey.isPressed;

        bool held = !suppressed && GameInputBindings.IsPressed(GameBindingAction.Attack);

        // Anything that takes the body away mid-charge drops it: he is not winding up a blow
        // while ragdolled, in a menu, or with his arms round a box.
        if (!CanSwingNow())
        {
            CancelCharge();
            return;
        }

        if (held)
        {
            if (!_charging)
            {
                _charging = true;
                BeginNetworkCharge();
            }
            _chargeTime += Time.deltaTime;
            ShowChargePrompt();
            return;
        }

        if (!_charging) return;

        // Released. A tap is an ordinary swing; a full hold is the charged one.
        bool full = _chargeTime >= _fullChargeTime;
        uint sequence = _pendingNetworkSwingSequence;
        CancelCharge(notifyServer: false);

        StartCoroutine(Swing(full, sequence));
    }

    void BeginNetworkCharge()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) return;
        _pendingNetworkSwingSequence = ++_nextNetworkSwingSequence;
        NetworkPlayer.Local?.BeginMeleeChargeServerRpc(_pendingNetworkSwingSequence);
    }

    void CancelCharge(bool notifyServer = true)
    {
        if (!_charging) return;

        if (notifyServer && _pendingNetworkSwingSequence != 0)
            NetworkPlayer.Local?.CancelMeleeChargeServerRpc(_pendingNetworkSwingSequence);

        _charging   = false;
        _chargeTime = 0f;
        _pendingNetworkSwingSequence = 0;
        HUDController.Instance?.HideActionProgress();
    }

    /// Only shown once the hold has gone past a tap, so an ordinary click does not flash a bar
    /// on screen every time.
    void ShowChargePrompt()
    {
        const float TapGrace = 0.12f;
        if (_chargeTime < TapGrace) return;

        float pct = Mathf.Clamp01(_chargeTime / Mathf.Max(0.01f, _fullChargeTime));
        HUDController.Instance?.ShowActionProgress("ATTACK POWER", pct);
    }

    // ── Equipping ─────────────────────────────────────────────────────────────

    /// Called by MeleeWeaponItem on pickup. visual is an already-instantiated model that this
    /// component takes ownership of and parents to the right hand; pass null for no model.
    public void EquipWeapon(MeleeWeaponKind kind, GameObject visual)
    {
        if (_equippedLimb != null) _equippedLimb.PutDown();
        if (_weaponVisual != null) { ClearCarryIK(); Destroy(_weaponVisual); }

        CurrentWeapon = kind;
        _weaponVisual = visual;

        if (_weaponVisual == null) { PublishGrip(); return; }

        EnterCarry();

        // Riding the hand bone it must never collide with the world, or it would fight the
        // CharacterController every step. Its collider and body are switched off rather than
        // destroyed: the same object falls out of the hand again when it is dropped.
        WeaponVisualPlacer.Hold(_weaponVisual);

        PublishGrip();
    }

    /// Occupies the normal weapon slot with the actual networked limb object. It is deliberately
    /// not rebuilt as a MeleeWeaponItem: this exact object carries the limb identity used later by
    /// repair, drop and network reconciliation.
    public bool EquipLimb(SeveredLimbItem limb)
    {
        if (limb == null) return false;

        // Never report a successful pickup until there is a real, visible hand to receive it.
        // Falling back to the Player root put the mesh at the character origin, which is the
        // detached arm seen following the owner's waist while observers saw it in a proper hand.
        var hand = ResolveLimbHand();
        if (hand == null) return false;

        if (_equippedLimb == limb && CurrentWeapon == MeleeWeaponKind.RobotLimb)
        {
            // Reconciliation is also a repair pass. The active character body can change while
            // the persistent item survives, so reseat it even when the logical equip is unchanged.
            _weaponVisual = limb.gameObject;
            _weaponInHand = true;
            limb.PlaceInWeaponSlot(hand);
            WeaponVisualPlacer.Hold(_weaponVisual);
            PublishGrip();
            return true;
        }
        if (CurrentWeapon != MeleeWeaponKind.Fists || _weaponVisual != null) return false;

        _equippedLimb = limb;
        _weaponVisual = limb.gameObject;
        CurrentWeapon = MeleeWeaponKind.RobotLimb;

        EnterCarry();
        WeaponVisualPlacer.Hold(_weaponVisual);
        PublishGrip();
        return true;
    }

    /// Frees the weapon slot without destroying or cloning the limb. The caller decides whether
    /// that same object is dropped to the floor or consumed by a successful repair.
    public bool UnequipLimb(SeveredLimbItem limb)
    {
        if (_equippedLimb != limb) return false;

        ClearCarryIK(immediate: true);
        _equippedLimb = null;
        _weaponVisual = null;
        _weaponInHand = false;
        CurrentWeapon = MeleeWeaponKind.Fists;
        PublishGrip();
        return true;
    }

    /// Hands the swing rig what it needs to know about what is being held: one fist or two, and
    /// where along the shaft the off hand takes hold. Both come off the weapon asset, so a
    /// sledgehammer can be gripped differently from a bat without touching code.
    void PublishGrip()
    {
        if (_swingArm == null) return;

        var def    = CurrentWeapon == MeleeWeaponKind.Fists ? null
                                                            : MeleeWeaponRegistry.Get(CurrentWeapon);
        // A severed limb has no authored definition asset, but it still occupies the one-handed
        // weapon slot. Treating it as Fists made the swing alternate onto the empty/missing arm.
        var grip   = CurrentWeapon == MeleeWeaponKind.RobotLimb
            ? MeleeSwingArm.GripKind.OneHanded
            : def != null ? def.Grip : MeleeSwingArm.GripKind.Fists;
        var weapon = _weaponVisual != null ? _weaponVisual.transform : null;

        // Same precedence as the fist: an anchor on the model beats the vector on the asset. Read
        // as a point in the weapon's own space, which is what MeleeSwingArm's IK expects, so the
        // anchor can sit anywhere in the model's hierarchy rather than having to be a direct child.
        Vector3? offHand = def != null ? def.OffHandGrip : (Vector3?)null;

        var anchors = _weaponVisual != null ? _weaponVisual.GetComponent<WeaponGripAnchors>() : null;
        if (anchors != null && anchors.OffHandGrip != null && weapon != null)
            offHand = weapon.InverseTransformPoint(anchors.OffHandGrip.position);

        _swingArm.SetGrip(grip, weapon, offHand);
    }

    /// Whether the weapon in hand needs the arm that was just lost.
    ///
    /// The right arm is the one holding it, so losing that drops anything. The left only matters
    /// for a two-handed weapon — a bat needs both hands on the shaft, a crowbar does not.
    public static bool ArmIsNeeded(BodyPart part)
    {
        var combat = Instance;
        if (combat == null || combat.CurrentWeapon == MeleeWeaponKind.Fists) return false;

        if (part == BodyPart.RightArm) return true;
        if (part != BodyPart.LeftArm)  return false;

        var def = MeleeWeaponRegistry.Get(combat.CurrentWeapon);
        return def != null && def.Grip == MeleeSwingArm.GripKind.TwoHanded;
    }

    /// Puts the held weapon back on the floor as a pickup and returns to bare hands.
    ///
    /// Without this a weapon is a one-way door: picking one up occupies the hands for the rest
    /// of the shift, and the only way back to bare hands is finding a different weapon. Returns
    /// false when there was nothing to put down, so the caller can fall through to whatever else
    /// the drop key does.
    public bool DropWeapon(bool force = false)
    {
        if (CurrentWeapon == MeleeWeaponKind.Fists) return false;

        // Mid-swing the model is somewhere along an arc and the hit test has not resolved yet.
        // Let the blow finish rather than having the bat blink out of a raider's face.
        //
        // Forced when the arm holding it has just been torn off, where waiting for the swing to
        // land is exactly the wrong behaviour: the swing is not going to land.
        if (IsSwinging && !force) return false;

        var kind = CurrentWeapon;

        // A severed limb is already the persistent pickup. Sending it through NetworkWeaponManager
        // would create a second generic weapon and lose the identity required to refit it later.
        if (kind == MeleeWeaponKind.RobotLimb)
        {
            var limb = _equippedLimb;
            if (limb != null) limb.PutDown();
            else
            {
                _weaponVisual = null;
                _weaponInHand = false;
                CurrentWeapon = MeleeWeaponKind.Fists;
                PublishGrip();
            }

            AudioManager.Instance?.PlayDrop();
            HUDController.Instance?.AddNotification(
                "Limb dropped",
                $"Weapon slot free — {InputHints.E} to pick it back up",
                HUDController.NotifType.Success);
            return true;
        }

        // Multiplayer: the server names the pickup and broadcasts it, so every peer ends up with
        // one bat on the floor rather than each spawning its own. Hands are emptied when that
        // broadcast comes back (ReleaseHeld), not here. The hand pose and the push travel with the
        // request so the other peers can let go of their copy from the same place.
        if (NetworkWeaponManager.IsNetworked)
        {
            var held = _weaponVisual != null ? _weaponVisual.transform : null;
            NetworkWeaponManager.Instance.RequestDropServerRpc(
                (int)kind,
                held != null ? held.position : DropPosition(),
                held != null ? held.rotation : Quaternion.identity,
                DropVelocity());
        }
        else
        {
            ReleaseHeld(-1, DropVelocity());
        }

        AudioManager.Instance?.PlayDrop();
        HUDController.Instance?.AddNotification(
            $"{kind} dropped",
            $"Both hands free — {InputHints.E} to pick it back up",
            HUDController.NotifType.Success);

        return true;
    }

    /// Lets go of the weapon in the hand: the model comes off the bone exactly where it is and
    /// falls, and becomes the pickup. Returns to bare hands.
    ///
    /// Taken off the skeleton rather than destroyed and rebuilt on the floor, which is what a drop
    /// used to do — and the rebuilt one was placed by a fixed rotation that pushed the shaft into
    /// the ground. `netId` is the server's id for the pickup in a session, -1 alone.
    public void ReleaseHeld(int netId, Vector3 velocity)
    {
        var kind   = CurrentWeapon;
        var visual = _weaponVisual;

        // Out of our hands first, so EquipWeapon below does not destroy it. The off hand lets go
        // of the shaft when the grip is republished there.
        ClearCarryIK(immediate: true);
        _weaponVisual = null;
        _weaponInHand = false;
        EquipWeapon(MeleeWeaponKind.Fists, null);

        if (kind == MeleeWeaponKind.Fists) return;

        if (visual != null)
            MeleeWeaponItem.Release(visual, kind, velocity, netId, OwnColliders());
        else
            MeleeWeaponItem.Spawn(kind, DropPosition(), netId);
    }

    /// A drop is let go of, not thrown: his own movement plus a small push forward and up, so it
    /// clears his legs instead of landing on his feet.
    Vector3 DropVelocity()
    {
        if (_cam == null) _cam = Camera.main;

        Vector3 forward = _cam != null
            ? Vector3.ProjectOnPlane(_cam.transform.forward, Vector3.up).normalized
            : transform.forward;
        if (forward.sqrMagnitude < 0.01f) forward = transform.forward;

        var cc = GetComponentInParent<CharacterController>();
        Vector3 own = cc != null ? cc.velocity : Vector3.zero;

        return own + forward * 1.3f + Vector3.up * 1.2f;
    }

    /// Everything on this character that could touch the falling weapon: the capsule and any
    /// limb colliders.
    Collider[] OwnColliders() => transform.root.GetComponentsInChildren<Collider>(true);

    /// A clear patch of floor just in front of him, found the same way PlayerInteraction places
    /// a dropped part: cast down from above head height so the weapon lands on whatever surface
    /// is actually there rather than inside it.
    Vector3 DropPosition()
    {
        if (_cam == null) _cam = Camera.main;

        Vector3 forward = _cam != null
            ? Vector3.ProjectOnPlane(_cam.transform.forward, Vector3.up).normalized
            : transform.forward;

        if (forward.sqrMagnitude < 0.01f) forward = transform.forward;

        Vector3 origin = transform.position + forward * 1.2f + Vector3.up * 1.5f;

        return Physics.Raycast(origin, Vector3.down, out var hit, 3f)
            ? hit.point + Vector3.up * 0.08f
            : transform.position + forward * 1.2f;
    }

    /// Places the held weapon in the fist from its definition. Public and re-runnable so the
    /// numbers can be nudged in the inspector during play and seen immediately, instead of having
    /// to drop the weapon and pick it up again after every tweak.
    [ContextMenu("Debug / Reapply Grip")]
    public void ApplyHandPlacement()
    {
        if (_weaponVisual == null) return;
        if (_equippedLimb != null)
        {
            _equippedLimb.PlaceInWeaponSlot(ResolveLimbHand());
            return;
        }

        // An anchor on the model wins over the numbers on the asset: it says where the palm closes
        // and which way the shaft leaves the fist, and it can be dragged into place while looking
        // at the hand instead of being derived from three Euler angles.
        var anchors = _weaponVisual.GetComponent<WeaponGripAnchors>();
        if (anchors != null && anchors.MainGrip != null && PlaceByGrip(anchors.MainGrip)) return;

        var def = MeleeWeaponRegistry.Get(CurrentWeapon);

        _weaponVisual.transform.localPosition = def != null ? def.HandOffset
                                                            : new Vector3(0.03f, 0.06f, 0.02f);
        _weaponVisual.transform.localRotation = Quaternion.Euler(def != null ? def.HandRotation
                                                                             : Vector3.zero);
    }

    /// Moves the weapon so that its grip anchor ends up exactly at the hand bone — same position,
    /// same orientation. The weapon is already parented to the hand, so the arithmetic is in local
    /// space: put the anchor at the origin, and the model hangs off it wherever the artist left it.
    bool PlaceByGrip(Transform grip)
    {
        var weapon = _weaponVisual.transform;

        // The anchor's pose relative to the weapon root, which is fixed by how the model was
        // authored and does not change when the weapon moves.
        Quaternion gripLocalRot = Quaternion.Inverse(weapon.rotation) * grip.rotation;
        Vector3    gripLocalPos = weapon.InverseTransformPoint(grip.position);

        weapon.localRotation = Quaternion.Inverse(gripLocalRot);
        weapon.localPosition = -(weapon.localRotation * gripLocalPos);
        return true;
    }

    // ── Carrying vs swinging ──────────────────────────────────────────────────
    //
    // These are opposite relationships and confusing them is what made the grip impossible to
    // author. CARRYING: the weapon hangs on a socket at the chest and the HANDS are pulled onto
    // it by IK, so dragging a grip point moves the hand — the same way the cart and the ladder
    // have always worked. SWINGING: the weapon is handed to the fist and rides it, because a blow
    // is thrown by the arm and the weapon has to follow the arm rather than lead it.

    /// Puts the weapon in the fist and leaves it there.
    ///
    /// It used to hang on a socket at the chest with the hand pulled onto it by IK, which reads
    /// perfectly while he stands still and falls apart the moment anything animates: the solver
    /// drags the wrist toward a grip that is riding the chest, so a run cycle bends the arm around
    /// a target it can never reach and the weapon looks like it is sliding through the hand.
    ///
    /// Parented to the bone there is nothing to fight. Whatever the animation does with the arm —
    /// run, idle, the attack clip — the weapon goes with it, because it IS the hand.
    ///
    /// The socket and CarryOffset are left in place unused, for a holstered weapon on the back
    /// later; nothing reads them now.
    public void EnterCarry() => EnterHand();

    /// Puts the weapon in the fist: where it lives, both while carried and while swinging.
    void EnterHand()
    {
        if (_weaponVisual == null) return;

        var hand = _equippedLimb != null ? ResolveLimbHand() : ResolveHandBone();
        if (_equippedLimb != null)
        {
            // A limb attached to the Player root is not "in hand": it floats at the waist and
            // follows locomotion. Keep it where it is until a valid humanoid hand is available.
            if (hand == null) { _weaponInHand = false; return; }
            _equippedLimb.PlaceInWeaponSlot(hand);
        }
        else
        {
            _weaponVisual.transform.SetParent(hand != null ? hand : transform, false);
            ApplyHandPlacement();
        }

        _weaponInHand = true;

        // No IK on either wrist: the arms are animated, and a solver pulling them onto the weapon
        // they are already holding can only fight the animation.
        ClearCarryIK(immediate: true);
        if (_swingArm != null) _swingArm.SuppressOffHandGrip = true;
        PublishGrip();
    }

    /// Plays the swing animation on the arms layer, scaled to fit this weapon's cooldown.
    ///
    /// The clip is one downward attack of a fixed length; a baton swings three times as fast as a
    /// sledgehammer. Rather than one clip per weapon, its playback speed is a parameter, so the
    /// same animation covers all of them and the timing stays where the weapon's stats put it —
    /// which is also where the hit is resolved, so what you see and what lands cannot drift.
    bool PlayAttackClip(float duration)
    {
        if (!ResolveArmsLayer()) return false;
        if (duration <= 0.01f) return false;

        _animator.SetFloat(AttackSpeedHash, _attackClipDuration / duration);

        // Raised here rather than left to PlayerWeaponLayer's blend: a swing lasts under a second
        // and would be a third over before a ramp got the layer up. Coming back down is that
        // component's job, and there a blend is what stops the arms snapping back.
        _animator.SetLayerWeight(_armsLayer, 1f);
        _animator.CrossFadeInFixedTime(AttackStateHash, 0.06f, _armsLayer, 0f);
        return true;
    }

    void ReturnToCarryClip()
    {
        if (_armsLayer < 0 || _animator == null) return;
        if (!_animator.HasState(_armsLayer, CarryStateHash)) return;

        _animator.CrossFadeInFixedTime(CarryStateHash, 0.12f, _armsLayer);
    }

    bool ResolveArmsLayer()
    {
        if (!EnsureActiveHumanoidRig()) return false;
        if (_armsChecked) return _armsLayer >= 0;
        _armsChecked = true;

        if (_animator == null) return false;

        _armsLayer = _animator.GetLayerIndex(PlayerWeaponLayer.LayerName);
        if (_armsLayer >= 0 && !_animator.HasState(_armsLayer, AttackStateHash)) _armsLayer = -1;

        return _armsLayer >= 0;
    }


    void ClearCarryIK(bool immediate = false)
    {
        // Only ours: something else may have taken the hands in the meantime, and clearing then
        // would drop a box or let go of a ladder.
        if (PlayerHandIK.Instance != null && !PlayerBusyState.HandsTaken(gameObject))
            PlayerHandIK.Instance.ClearTargets(immediate);
    }

    /// The socket a carried weapon hangs from: an assigned scene object if there is one, otherwise
    /// one made under the chest bone so the mechanic works in a scene nobody has set up yet.
    Transform ResolveCarrySocket()
    {
        if (_carrySocket != null) { ApplySocketSettings(); return _carrySocket; }

        var chest = _animator != null && _animator.isHuman
            ? _animator.GetBoneTransform(HumanBodyBones.UpperChest)
              ?? _animator.GetBoneTransform(HumanBodyBones.Chest)
              ?? _animator.GetBoneTransform(HumanBodyBones.Spine)
            : null;
        if (chest == null) return null;

        var made = new GameObject("WeaponCarrySocket").transform;
        made.SetParent(chest, false);
        made.localPosition = _carrySocketOffset;
        made.localRotation = Quaternion.identity;

        _carrySocket = made;
        ApplySocketSettings();
        return _carrySocket;
    }

    /// Puts the socket where the settings asset says, once.
    ///
    /// Once, not every frame: this is the value being tuned, and re-applying it would drag the
    /// socket back out from under the mouse. Save it with Tools/Warehouse/Save Held Weapon Grip
    /// To Prefab, which writes the asset the same way it writes the grip points.
    void ApplySocketSettings()
    {
        if (_socketPlaced || _carrySocket == null) return;
        _socketPlaced = true;

        var settings = WeaponCarrySocketSettings.Load();
        if (settings == null) return;   // no asset: whatever the scene says stands

        _carrySocket.localPosition = settings.LocalPosition;
        _carrySocket.localRotation = Quaternion.Euler(settings.LocalRotation);
    }

    Animator FindActiveHumanoidAnimator()
    {
        foreach (var candidate in GetComponentsInChildren<Animator>(false))
            if (candidate != null && candidate.isActiveAndEnabled && candidate.isHuman)
                return candidate;

        return null;
    }

    /// Keeps every hand lookup on the body the local player can actually see. Player bodies may
    /// coexist below the same root with old variants disabled; a cached Animator/hand from one of
    /// those variants remains a valid Unity object and therefore used to evade null checks.
    bool EnsureActiveHumanoidRig()
    {
        var active = FindActiveHumanoidAnimator();
        if (active == null) return false;
        if (_animator == active) return true;

        _animator = active;
        _handBone = null;
        _armsLayer = -1;
        _armsChecked = false;
        _swingArm?.RebindAnimator(active);
        return true;
    }

    Transform ResolveHandBone()
    {
        if (!EnsureActiveHumanoidRig()) return null;
        if (_handBone != null && _handBone.gameObject.activeInHierarchy &&
            _handBone.IsChildOf(_animator.transform))
            return _handBone;

        _handBone = null;

        if (_animator != null && _animator.isHuman)
            _handBone = _animator.GetBoneTransform(HumanBodyBones.RightHand);

        return _handBone;
    }

    Transform ResolveLimbHand()
    {
        if (!EnsureActiveHumanoidRig()) return null;

        // The normal weapon slot is the right hand. A severed right arm is the one case where that
        // socket exists only as an invisible animation bone, so use the surviving left hand rather
        // than making the carried limb appear to float beside the robot.
        var injuries = InjurySystem.Instance;
        if (_animator != null && _animator.isHuman && injuries != null &&
            injuries.IsSevered(BodyPart.RightArm) && !injuries.IsSevered(BodyPart.LeftArm))
            return _animator.GetBoneTransform(HumanBodyBones.LeftHand);

        return ResolveHandBone();
    }

    // ── Swing ─────────────────────────────────────────────────────────────────

    bool CanSwingNow()
    {
        if (Time.time < _nextSwingTime) return false;
        if (IsSwinging) return false;
        if (RobotBatteryController.Local != null && RobotBatteryController.Local.IsCharging)
            return false;

        // Aiming at a cursor rather than at the world: a click meant for a panel or a window is
        // not a swing. Kept here rather than in PlayerBusyState because it is about where the
        // click went, not about what he is holding.
        if (Cursor.lockState != CursorLockMode.Locked) return false;

        // Everything else that means "he already has something on" — a box in his arms, a ladder
        // under him or in his hands, the toolkit, a vehicle seat, being down or sliding. One list,
        // shared with weapon pickup, so the two can never disagree about what "free" means.
        return !PlayerBusyState.IsBusy(gameObject);
    }

    IEnumerator Swing(bool charged, uint swingSequence)
    {
        var stats = StatsFor(CurrentWeapon);
        Vector3 swingAim = AimDirection();

        IsSwinging     = true;
        _nextSwingTime = Time.time + stats.Cooldown;
        // The whoosh belongs to the attack itself, so it plays before hit resolution and is
        // still audible when the strike misses. Contact sounds are emitted separately on hits.
        AudioManager.Instance?.PlayAt(GameAudioEvent.MeleeSwing, transform.position);

        // The wind-up is the weapon's own: a sledgehammer commits you for over half a second
        // before it lands, a baton barely at all. What is left of the cooldown is split between
        // swinging through and recovering, in the same ratio it always was.
        float windUp = stats.Windup;
        float left   = Mathf.Max(0.02f, stats.Cooldown - windUp);
        float strike  = left * 0.34f;
        float recover = left * 0.66f;

        // The arm is handed the same three phases the hit test is timed against, so what the
        // player sees and what the rules do cannot drift apart. The weapon model is not
        // animated separately any more — it hangs off the hand and rides the arm, which is why
        // this now reads identically with fists.
        // The fist takes the weapon for the duration; the hands come off it so the arm can throw.
        EnterHand();

        // Bare hands still swing on springs — there is no clip of a robot punching. A weapon
        // swings on the attack clip and nothing else: the springs were a stand-in for exactly this
        // animation, and running both put two swings on one arm.
        if (CurrentWeapon == MeleeWeaponKind.Fists) _swingArm?.Begin(windUp, strike, recover);
        else                                        PlayAttackClip(stats.Cooldown);

        // Multiplayer: the same three phases go out so the blow is visible on this player's
        // puppet in everyone else's game. Fire-and-forget on purpose -- a swing that gets lost
        // costs a bit of animation, never a hit, because the hit is resolved below regardless.
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
            NetworkPlayer.Local?.NotifySwingServerRpc(swingSequence, charged, swingAim);

        yield return new WaitForSeconds(windUp);

        // The blow lands at the start of the follow-through, not at the end of it — the hit
        // has to register while the player is still looking where they aimed.
        ResolveHit(stats, charged, swingSequence, swingAim);

        yield return new WaitForSeconds(strike + recover);

        IsSwinging = false;

        // Back to the carry pose on the arms layer, and onto the socket.
        ReturnToCarryClip();
        EnterCarry();
    }

    // ── Hit resolution ────────────────────────────────────────────────────────

    void ResolveHit(WeaponStats stats, bool charged, uint swingSequence, Vector3 aim)
    {
        // Charging owns the whole body and both arms. This also catches the edge case where
        // charging starts during an already-running swing coroutine, before its wind-up lands.
        if (RobotBatteryController.Local != null && RobotBatteryController.Local.IsCharging)
            return;

        Vector3 origin = transform.position + Vector3.up * 1.1f;

        // Everyone the swing could reach, nearest first. A duelling weapon then takes only the
        // first of them; a sweeping one takes as many as it is worth.
        _inArc.Clear();

        foreach (var thief in ThiefController.Targets)
        {
            if (thief == null) continue;
            if (thief.Current == ThiefController.State.Down) continue;

            Vector3 to = thief.transform.position + Vector3.up * 1.0f - origin;
            to.y = 0f;

            float dist = to.magnitude;
            if (dist > stats.Reach) continue;
            // Standing right on top of him has no meaningful direction to compare against.
            if (dist > 0.15f && Vector3.Angle(aim, to) > stats.HalfAngle) continue;

            _inArc.Add((thief, dist));
        }

        _inArc.Sort((a, b) => a.dist.CompareTo(b.dist));

        int caught = Mathf.Min(_inArc.Count, stats.MaxTargets);

        // A bare metal fist gets its own contact sound, but only when the arc actually caught
        // somebody. The existing swing cue remains the feedback for a miss or weapon attack.
        if (CurrentWeapon == MeleeWeaponKind.Fists && caught > 0)
        {
            Vector3 contact = _inArc[0].thief.transform.position + Vector3.up;
            AudioManager.Instance?.PlayAt(GameAudioEvent.RobotPunchImpact, contact);
        }

        // Once, however many the blow catches. Shake is the player's own feedback that the swing
        // connected, not a tally of bodies — scaling it by the count would make a lucky sweep
        // through four of them throw the camera off the wall.
        float trauma = stats.Trauma + (charged ? _chargeTrauma : 0f);
        CameraShake.AddTrauma(caught > 0 ? trauma : trauma * 0.35f);

        for (int i = 0; i < caught; i++)
        {
            var thief = _inArc[i].thief;

            // Multiplayer: the server owns the fight. A client asks for the hit instead of
            // applying it, so two players swinging at the same raider cannot both "kill" him.
            var net = thief.GetComponent<NetworkThief>();
            if (net != null && net.IsSpawned
                && Unity.Netcode.NetworkManager.Singleton?.IsListening == true)
            {
                net.RequestHitServerRpc(swingSequence);
                continue;
            }

            thief.TakeHit(stats.Damage, aim, charged);
        }
    }

    // Reused between swings rather than allocated per blow — this runs on every attack, and a
    // fight against a gate's worth of raiders is a lot of swings.
    readonly List<(ThiefController thief, float dist)> _inArc = new();

    Vector3 AimDirection()
    {
        if (_cam == null) _cam = Camera.main;

        Vector3 dir = _cam != null ? _cam.transform.forward : transform.forward;
        dir.y = 0f;

        return dir.sqrMagnitude > 0.001f ? dir.normalized : transform.forward;
    }

    // ── Debug ─────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    [ContextMenu("Debug / Equip Bat")]
    void DbgBat() => EquipWeapon(MeleeWeaponKind.Bat, MeleeWeaponItem.BuildVisual(MeleeWeaponKind.Bat));

    [ContextMenu("Debug / Equip Crowbar")]
    void DbgCrowbar() => Equip(MeleeWeaponKind.Crowbar);

    [ContextMenu("Debug / Equip Pipe Wrench")]
    void DbgWrench() => Equip(MeleeWeaponKind.PipeWrench);

    [ContextMenu("Debug / Equip Plank")]
    void DbgPlank() => Equip(MeleeWeaponKind.Plank);

    [ContextMenu("Debug / Equip Rolling Pin")]
    void DbgPin() => Equip(MeleeWeaponKind.RollingPin);

    void Equip(MeleeWeaponKind kind) => EquipWeapon(kind, MeleeWeaponItem.BuildVisual(kind));

    [ContextMenu("Debug / Back to Fists")]
    void DbgFists() => EquipWeapon(MeleeWeaponKind.Fists, null);
#endif
}
