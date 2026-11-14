using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// What this particular thief came here to do. One job each — a raid is several thieves with
/// different roles arriving together, not one saboteur working through a checklist.
public enum ThiefRole
{
    /// Shoves boxes off pallets onto the floor for as long as he is left alone. Steals nothing.
    Vandal,
    /// Rummages through a pallet, then shoulders one box and walks out with it.
    Looter,
    /// Works the assembly bench and pockets a part that was already placed on it.
    PartThief,
    /// Smashes a power box (or tips something slippery over) and leaves the mess behind.
    Saboteur,
}

/// How a raider reacts to the people who work here. Orthogonal to ThiefRole: the role is what
/// he came for, the temper is what he does when somebody walks in on him. A raid mixes them so
/// the team cannot learn one response that works on every zombie.
public enum ThiefTemper
{
    /// Bolts the moment he is spotted and again the moment his job is done. Never fights back:
    /// hit him and he runs rather than squaring up.
    Skittish,
    /// Works with his back to the room and only reacts to being actually hit — the original
    /// behaviour, kept so a raid is not all extremes.
    Bold,
    /// Goes for whoever he sees. He still has a job, but a player in view outranks it.
    Aggressive,
}

/// A raider who walks into the warehouse to do exactly one job, then leave. He crosses open
/// floor on the way in rather than beelining for his target, so the team gets to spot him
/// first, and he is only willing to be inside for so long — that visit budget is the single
/// clock on him. Every job has a deliberate wind-up so the players can hear the call-out, run
/// over and interrupt it: melee hits stagger him AND reset his progress, so even a player who
/// cannot finish him off can keep him from ever completing the job. Hit him and he may hit
/// back — but he will not touch a player who is already on the floor. A wounded thief
/// abandons everything and runs.
///
/// The AI is deliberately server-shaped: Update() early-outs on non-server peers, so the
/// multiplayer pass can drive remote copies from broadcast state without rewriting the FSM.
[RequireComponent(typeof(NavMeshAgent))]
public class ThiefController : MonoBehaviour
{
    public enum State { Prowling, Seeking, Working, Skittering, Fighting, Stunned, Fleeing, Down }

    // ── Registry ──────────────────────────────────────────────────────────────

    public static readonly HashSet<ThiefController> All = new();

    /// Assign via GameEventManager._thiefPrefab — set in Awake so Spawn() uses the model.
    public static GameObject Prefab { get; set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetStatics() { All.Clear(); s_inert.Clear(); Prefab = null; }

    /// Raiders someone else is moving by hand with their brain switched off — the gate breach
    /// walking them through the doorway. A disabled component drops out of All (OnDisable), and
    /// All is what a swing searches, so for the whole walk-in they could not be hit at all: five to
    /// seven seconds of fists passing through the front of the crowd.
    static readonly HashSet<ThiefController> s_inert = new();

    /// Everything a player's swing may land on: the live raiders plus any still being walked in.
    public static IEnumerable<ThiefController> Targets
    {
        get
        {
            foreach (var t in All) yield return t;
            foreach (var t in s_inert) if (t != null && !All.Contains(t)) yield return t;
        }
    }

    System.Action _wakeOnHit;

    /// Marks him hittable while inert. wake is run once, on the first blow, before the blow is
    /// applied — it must hand him his brain back (and call ReleaseInert), so the hit lands on a
    /// raider whose state machine is running rather than on one about to be re-initialised.
    public void HoldInert(System.Action wake)
    {
        _wakeOnHit = wake;
        s_inert.Add(this);
    }

    public void ReleaseInert()
    {
        _wakeOnHit = null;
        s_inert.Remove(this);
    }

    // ── Tuning ────────────────────────────────────────────────────────────────

    [Header("Health")]
    [Tooltip("Fists deal 1, a bat 2, a knife 3 — bare hands need three connects, a knife one.")]
    [SerializeField] int   _maxHealth        = 3;
    [Tooltip("At or below this health he gives up on the job and runs for the exit.")]
    [SerializeField] int   _fleeHealth       = 1;

    // Movement speeds are pinned to what the animation actually depicts. The zombie clips are
    // in-place — they carry no root travel at all — so the speed each one "looks like" was
    // measured off the feet: while a foot is planted it slides backwards through the root at
    // exactly the speed the body would be advancing. Walk reads 0.32 m/s, Walk Aggro 0.70,
    // Run 1.16. Driving the agent any faster than the clip depicts is what made him glide.
    [Header("Movement")]
    // Back on the Run clip's measured stride (1.16 m/s), so the clip plays close to 1:1 and no
    // foot slides.
    //
    // They were briefly raised to 3.2 to make a raider read as running rather than wading. It did
    // not work, and the reason was the pack: its Walk and Run are the same motion at different
    // speeds, so driving the legs faster only produced a faster version of the same gait, never
    // the second gear a player's sprint has.
    //
    // The second gear now comes from a different clip instead of a different speed — the player
    // rig's own run, on the Gait axis of the locomotion tree. See _runSpeed below; _walkSpeed no
    // longer drives anything and is kept only so that a prefab authored against it still loads
    // without complaint.
    [System.Obsolete("Superseded by _runSpeed / _shambleSpeed — the pace now follows the gait.")]
    [HideInInspector]
    [SerializeField] float _walkSpeed        = 1.2f;

    [Tooltip("Speed while running for the exit. Slow on purpose: he has to be catchable.")]
    [SerializeField] float _fleeSpeed        = 1.0f;
    [Tooltip("Speed while running with loot — no faster than the rest, so he does get caught.")]
    [SerializeField] float _lootFleeSpeed    = 1.0f;

    [Tooltip("How much raiders differ from one another in pace, as a fraction either way. At 0 a " +
             "crowd of them moves in lockstep; this is what breaks that up.")]
    [SerializeField] float _paceVariance     = 0.14f;

    [Header("Two gaits")]
    [Tooltip("Speed of a raider running at the stock. Fast on purpose: he is on the player rig's " +
             "own run clip, which depicts about 4.2 m/s, and a body moving far slower than the " +
             "clip depicts skates across the floor whatever the playback is set to.")]
    // 3.3 read as gliding: fast enough that the eye tracks the body over the floor before the legs.
    // 2.6 with the playback floor lowered to match keeps the run clip close to 1:1 (2.6/4.2 ≈ 0.62).
    [SerializeField] float _runSpeed   = 2.6f;

    [Tooltip("Speed of a raider who has turned on a player. This is the shamble — slower than " +
             "walking pace, because being able to back away from one is the point of it.")]
    [SerializeField] float _shambleSpeed = 0.95f;

    [Tooltip("What the run clip depicts, in metres per second. See ZombieAssetSetup.RobotRunStride.")]
    [SerializeField] float _runClipStride     = 4.2f;

    [Tooltip("What the shamble clip depicts. See ZombieAssetSetup.AggroStride.")]
    [SerializeField] float _shambleClipStride = 0.70f;

    [Tooltip("Top threshold of the locomotion blend tree — the Run clip's measured stride. " +
             "Above this there is no faster clip to blend to, so playback is scaled instead. " +
             "Must match ZombieAssetSetup.RunStride.")]
    [SerializeField] float _animTopSpeed     = 1.16f;
    [Tooltip("Ceiling on that playback scaling. Past roughly this the run stops reading as a " +
             "run, so a raider is better off looking slightly floaty than sped-up.")]
    [SerializeField] float _animMaxPlayback  = 1.6f;
    [Tooltip("Floor on it. Slower than this the run turns into slow motion, which looks worse " +
             "than the small foot slide it is there to prevent.")]
    [SerializeField] float _animMinPlayback  = 0.6f;
    [Tooltip("How close to the target before he starts working on it. Measured from the edge of " +
             "the thing, not its middle, so this is a real arm's reach.")]
    [SerializeField] float _reachDistance    = 1.0f;

    [Header("Visit")]
    [Tooltip("Legs of the walk across the warehouse before he gets down to business. " +
             "This is the team's first chance to spot him.")]
    [SerializeField] int   _prowlLegsMin     = 1;
    [SerializeField] int   _prowlLegsMax     = 2;
    [Tooltip("Give up on a leg he cannot reach after this long and move on.")]
    [SerializeField] float _prowlLegTimeout  = 12f;
    [Tooltip("Total seconds he is willing to spend inside, prowling and working. When it " +
             "runs out he leaves with whatever he has — nobody loiters all shift.")]
    [SerializeField] float _maxStayDuration  = 65f;

    [Header("Job timing — the window the players get to intervene")]
    [Tooltip("Telegraph before a raider's strike lands: he rears back, then the pallet goes " +
             "over in one go. This is the whole interruption window — a hit inside it resets " +
             "him to zero. Set it to 0 for an instant, uninterruptible strike.")]
    [SerializeField] float _strikeWindup     = 1.2f;
    [SerializeField] float _knockForce       = 4.5f;

    [Header("Temperament")]
    [Tooltip("How far he notices a player. No line-of-sight check — see SeesPlayer.")]
    [SerializeField] float _sightRange       = 11f;
    [Tooltip("Full width of his vision cone, degrees. Outside it a player can walk right up.")]
    [SerializeField] float _sightAngle       = 110f;
    [Tooltip("Skittish: how much ground he wants between himself and a player before he will " +
             "go back to work.")]
    [SerializeField] float _skitterDistance  = 15f;
    [Tooltip("Skittish: give up running after this long even if the player is still on him — " +
             "otherwise a chasing player pins him in a permanent retreat.")]
    [SerializeField] float _skitterTimeout   = 6f;

    [Header("Fighting back")]
    [Tooltip("How close he needs to be to swing at a player. Measured centre to centre, so it " +
             "is about a third of a metre longer than the gap that is actually visible.")]
    [SerializeField] float _attackRange           = 1.4f;
    [Tooltip("Seconds between his swings — the gap the player attacks into.")]
    [SerializeField] float _attackInterval        = 1.6f;
    [Tooltip("Telegraph before the blow lands: back out of range or hit him first.")]
    [SerializeField] float _attackWindup          = 0.5f;
    [Tooltip("Chance a bat blow puts the player on the floor instead of just hurting.")]
    [SerializeField] float _knockdownChanceArmed  = 0.5f;
    [SerializeField] float _knockdownChanceFists  = 0.25f;
    [Tooltip("Chance an unarmed thief squares up instead of going back to work when hit.")]
    [SerializeField] float _retaliateChanceFists  = 0.6f;
    [Tooltip("He gives up the chase once the player has opened up this much ground.")]
    [SerializeField] float _fightLeash            = 12f;

    [Header("Fleeing")]
    [Tooltip("Sideways swing of the escape run, metres — a weaving target is harder to chase.")]
    [SerializeField] float _weaveAmplitude   = 2.6f;
    [SerializeField] float _weaveFrequency   = 2.2f;

    [Header("Reactions")]
    [SerializeField] float _stunDuration     = 1.5f;
    [Tooltip("Peak speed of the shove that starts a stagger.")]
    [SerializeField] float _knockbackSpeed   = 5f;
    [Tooltip("How long that shove lasts. Deliberately far shorter than the stun: the rest of " +
             "the stagger plays on the spot, where the feet can keep up with the body.")]
    [SerializeField] float _knockbackTime    = 0.22f;
    [Tooltip("Degrees per second he turns to face whoever hit him while staggering.")]
    [SerializeField] float _stunTurnSpeed    = 420f;
    [Tooltip("Backward impulse a fully charged blow puts into the ragdoll.")]
    [SerializeField] float _ragdollPush      = 14f;
    [Tooltip("Upward part of it — without some lift the body just slides along the floor.")]
    [SerializeField] float _ragdollLift      = 5f;
    [SerializeField] float _downFallTime     = 0.55f;
    [SerializeField] float _despawnDelay     = 4.5f;

    [Header("Appearance")]
    [Tooltip("Body skins. The pack ships five per mesh family (A-E) that share one skeleton " +
             "and differ only by material, so a raid can be mixed by swapping this alone. " +
             "Populated by Setup Thief System; leave empty for a single-look raider.")]
    [SerializeField] Material[] _bodyVariants;

    /// One entry per zombie model a raider can turn up as. Filled by
    /// Tools/Warehouse Demo/Assign Thief Bodies.
    ///
    /// A model rather than a recolour, because five shirts on one body is not variety — the team
    /// calls out "the fat one by the shelf", and there has to be a fat one to call out.
    [System.Serializable]
    public class BodyOption
    {
        [Tooltip("The pack body prefab. Its children become the raider's body.")]
        public GameObject Prefab;

        [Tooltip("That model's own humanoid avatar. Wrong avatar, wrong proportions, and the run " +
                 "comes out as a waddle — which is how the heavy body got noticed in the first place.")]
        public Avatar Avatar;

        [Tooltip("Skins for this body. They are per-model textures and do not carry across.")]
        public Material[] Skins;
    }

    [Tooltip("Bodies a raider is rolled from. Empty leaves whatever the prefab already carries.")]
    [SerializeField] BodyOption[] _bodies;

    /// How many skins one body may have, and the multiplier that packs body and skin into the
    /// single int that already crosses the wire.
    const int SkinsPerBody = 16;

    [Header("Score")]
    [SerializeField] int   _neutralizeReward  = 40;
    [SerializeField] int   _stolenBoxPenalty  = 60;
    [SerializeField] int   _stolenPartPenalty = 80;

    // ── Public state ──────────────────────────────────────────────────────────

    public State           Current => _state;
    public ThiefRole       Role    => _role;
    public ThiefTemper     Temper  => _temper;
    public MeleeWeaponKind Weapon  => _weapon;
    public bool            IsArmed => _weapon != MeleeWeaponKind.Fists;
    public int             Health  => _health;
    public bool            HasLoot => _carried != null || _stolenPart != null;

    /// 0..1 across the strike telegraph. Nothing draws it — the head pip it was written for is
    /// gone — but it is the one honest read of "can I still interrupt him", so it stays.
    public float JobProgress => _state == State.Working && _strikeWindup > 0f
        ? Mathf.Clamp01(_workTime / _strikeWindup)
        : 0f;

    /// Raised once when the thief leaves play. escaped = true if he reached the exit.
    public event System.Action<ThiefController, bool> OnResolved;

    // ── Internal ──────────────────────────────────────────────────────────────

    static readonly int SpeedHash       = Animator.StringToHash("Speed");
    static readonly int MotionSpeedHash = Animator.StringToHash("MotionSpeed");
    static readonly int GroundedHash    = Animator.StringToHash("Grounded");
    static readonly int GaitHash        = Animator.StringToHash("Gait");
    static readonly int CarryingHash    = Animator.StringToHash("IsCarryingBox");
    static readonly int AttackHash      = Animator.StringToHash("Attack");
    static readonly int AttackVarHash   = Animator.StringToHash("AttackVariant");
    static readonly int DamageHash      = Animator.StringToHash("Damage");
    static readonly int DownHash        = Animator.StringToHash("Down");

    /// Slap right / slap left / headbutt — the three swings in the zombie controller.
    const int AttackVariants = 3;

    NavMeshAgent _agent;
    Animator     _animator;
    Transform    _carryAnchor;
    Transform    _rightGrip;   // where the right hand is pinned while carrying
    Transform    _leftGrip;

    // The body is 11 SkinnedMeshRenderers (chest, head, each limb) that all wear the same
    // material, so a skin swap is one assignment per renderer. Cached in the init pass, before
    // the weapon and loot visuals get parented on, so those never get skinned by accident.
    SkinnedMeshRenderer[] _bodyRenderers;
    int _bodyVariant = -1;

    // Skins are handed out in rotation rather than rolled per raider: with five skins and up
    // to five raiders, independent rolls would routinely put two identical zombies in one wave.
    static int _variantCursor = -1;


    // Salts, so that pace, gait phase and skin are three unrelated draws off the same id.
    const uint PaceSalt = 0x9E3779B9;
    const uint GaitSalt = 0x85EBCA6B;
    const uint SkinSalt = 0xC2B2AE35;

    /// Scatters an int into [0,1).
    ///
    /// Deliberately not System.Random. Measured on ten ids running in sequence — which is exactly
    /// what a crowd spawned in one frame has — new System.Random(id).NextDouble() comes out as a
    /// sawtooth: adjacent ids step by a flat 0.195 every time, and at the spacing a prefab
    /// hierarchy actually leaves the values collapse into two alternating bands. Used as a pace
    /// that is not a crowd, it is a queue sorted by speed, or two platoons.
    ///
    /// This is an avalanching integer hash, so one bit of difference in the id changes the whole
    /// output. The same ten ids come out unordered across the range.
    static float Hash01(int value, uint salt)
    {
        uint x = (uint)value ^ salt;
        x ^= x >> 16; x *= 0x7feb352d;
        x ^= x >> 15; x *= 0x846ca68b;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / (float)0x1000000;
    }

    /// This raider's own pace, as a multiplier on every speed he ever moves at. Rolled once.
    ///
    /// A crowd all walking at exactly 1.2 m/s stays in formation forever, and the gate breach now
    /// puts ten of them through a doorway at once. Varying the speed rather than the animation
    /// keeps the feet honest: cadence is derived from how fast the body actually crosses the floor
    /// (see PlaybackFor), so a quicker raider takes quicker strides instead of skating.
    public float Pace { get; private set; } = 1f;
    NetworkThief _net;          // null in single player
    ZombieVoice  _voice;
    int          _upperBodyLayer = -1;

    // Remote copies get no FSM at all — their body is moved by NetworkTransform, and this is
    // just enough state to put the walk animation under it.
    Vector3 _lastRemotePos;
    float   _remoteSpeed;

    ThiefRole       _role   = ThiefRole.Vandal;
    ThiefTemper     _temper = ThiefTemper.Bold;
    MeleeWeaponKind _weapon = MeleeWeaponKind.Fists;
    GameObject      _weaponVisual;
    Quaternion      _weaponRestRot = Quaternion.identity;
    State     _state = State.Seeking;
    int       _health;
    bool      _resolved;
    bool      _announced;

    // Job target — only one of these is meaningful, depending on the role.
    PalletStackZone _pallet;
    GameObject      _powerBox;

    float _skitterTimer;
    /// Set when a skitter gives up on its timeout with the player still in view. Without it
    /// ReactToPlayers would re-trigger the skitter the very next frame and the timeout would
    /// achieve nothing — he would retreat forever instead of standing his ground.
    float _sightIgnoreTimer;
    bool  _struck;          // this approach's blow has already landed — do not swing twice
    float _workTime;      // progress into the current job's wind-up
    float _stayTime;      // total time inside the warehouse, prowl included
    float _repathTimer;

    Vector3 _prowlPoint;
    int     _prowlLegsLeft;
    float   _prowlLegTimer;

    // Stall watchdog — see TickStallWatchdog.
    float   _stallTimer;
    Vector3 _stallAnchor;
    int     _stallRecoveries;
    State   _stallState = State.Prowling;

    float _attackTimer;
    float _swingTimer = -1f;   // >= 0 while a telegraphed swing is in flight
    bool  _fightAfterStun;

    float   _stunTimer;
    Vector3 _knockDir;
    State   _stateBeforeStun = State.Seeking;
    bool    _woundedFlee;

    Vector3 _exitPoint;
    bool    _hasExitPoint;
    float   _weaveSeed;

    BoxPhysicsInteractable _carried;
    Rigidbody              _carriedRb;
    PartDefinition         _stolenPart;
    GameObject             _lootVisual;

    float      _downTimer;
    Quaternion _downStartRot;
    Quaternion _downEndRot;
    bool       _downAnimated;   // true when the zombie death clip is playing the fall
    bool       _ragdolled;      // true when physics owns the body instead of either of those

    static bool IsNetworked
    {
        get
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            return nm != null && nm.IsListening;
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        _agent     = GetComponent<NavMeshAgent>();
        _animator  = GetComponentInChildren<Animator>();
        _net       = GetComponent<NetworkThief>();
        _lastRemotePos = transform.position;

        // Off this object's own id rather than off Unity's global generator, which GameEventManager
        // seeds so that every peer lays the shift out identically — several raiders reading it in
        // the same frame come away with the same number. See NextBodyVariant, where that bug was
        // found the first time.
        Pace = 1f + (Hash01(GetInstanceID(), PaceSalt) * 2f - 1f) * _paceVariance;

        // The rig comes from PlayerArmature, whose controller has an UpperBody layer holding
        // the "carrying a box" pose. On the player PlayerCarryLayer zeroes it at Start; that
        // script is stripped out of the thief prefab, so without this he stands around all
        // shift hugging an invisible box.
        if (_animator != null) _upperBodyLayer = _animator.GetLayerIndex("UpperBody");
        SetCarryPose(false);

        // The locomotion clips still carry the player's OnFootstep/OnLand events; without a
        // receiver each one is a console error. See ThiefAnimationEvents.
        if (_animator != null && _animator.GetComponent<ThiefAnimationEvents>() == null)
            _animator.gameObject.AddComponent<ThiefAnimationEvents>();

        DesyncGait();

        // Added here rather than only in the prefab so a raider built before the component
        // existed still growls. It synthesizes its own sounds when no clips are assigned.
        _voice = GetComponent<ZombieVoice>();
        if (_voice == null) _voice = gameObject.AddComponent<ZombieVoice>();
        _health    = _maxHealth;
        _weaveSeed = Random.Range(0f, 10f);

        _agent.speed = GaitSpeed * Pace;

        // Deliberately short. The destination is now a point on the target's own edge rather than
        // its origin, and the navmesh carve around the prop already stops him walking into it —
        // so anything bigger here is a second gap stacked on top of the first, which is how he
        // ended up halting two metres from a pallet and swinging at nothing.
        _agent.stoppingDistance = 0.25f;
        _agent.autoBraking      = true;
        // Boxes on the floor are loose Rigidbodies, not carved obstacles — pushing straight
        // through a scattered pile beats getting permanently wedged behind one.
        _agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;

        // A client's copy is driven entirely by NetworkTransform. Leaving the agent enabled
        // there would have it path on its own and fight the incoming positions.
        if (IsRemoteCopy) _agent.enabled = false;

        var anchor = new GameObject("CarryAnchor").transform;
        anchor.SetParent(transform, false);
        anchor.localPosition = new Vector3(0f, 1.05f, 0.45f);
        _carryAnchor = anchor;

        _bodyRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);

    }

    void OnEnable()  => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// Called by ThiefEvent right after Instantiate. exitPoint is where he walks out;
    /// weapon is what he brought with him, and what the players get to keep if he goes down.
    public void Initialize(ThiefRole role, MeleeWeaponKind weapon, Vector3 exitPoint,
                           ThiefTemper temper = ThiefTemper.Bold)
    {
        // A raider already on the floor stays there. Handing a body over to the raid AI set it
        // prowling again — agent on, death clip still holding — and the corpse slid after the
        // nearest player in its death pose until it was cleaned up.
        if (_state == State.Down) return;

        _role         = role;
        _temper       = temper;
        _exitPoint    = exitPoint;
        _hasExitPoint = true;

        EquipWeapon(weapon);

        // Usually rolls the body right here. A raider who was already given one — because he had
        // to be looked at before his AI started, as the gate breach needs — keeps it.
        ChooseBody();

        _net?.PublishRole(role);
        _net?.PublishWeapon(weapon);
        _net?.PublishBody(_bodyVariant);

        _prowlLegsLeft = Random.Range(_prowlLegsMin, _prowlLegsMax + 1);
        if (_prowlLegsLeft > 0)
        {
            _state = State.Prowling;
            NextProwlLeg();
        }
    }

    /// Sanity bound on a single strike. A pallet holds a handful of boxes; anything beyond
    /// this is a zone that is not emptying and would otherwise spin the frame.
    const int MaxBoxesPerStrike = 64;

    /// How long a skittish raider steels himself after a retreat that did not shake anyone off.
    const float SightIgnoreAfterSkitter = 5f;

    void EquipWeapon(MeleeWeaponKind weapon)
    {
        _weapon = weapon;
        if (weapon == MeleeWeaponKind.Fists) return;

        _weaponVisual = MeleeWeaponItem.BuildVisual(weapon);

        // Ride the right hand if this is the rigged model; fall back to the carry anchor so
        // the primitive stand-in still visibly holds something.
        Transform hand = _animator != null && _animator.isHuman
            ? _animator.GetBoneTransform(HumanBodyBones.RightHand)
            : null;

        _weaponVisual.transform.SetParent(hand != null ? hand : _carryAnchor, false);

        // Same placement the player uses, off the same asset - a bat should not sit differently
        // in a raider's fist than in yours.
        var def = MeleeWeaponRegistry.Get(weapon);

        _weaponVisual.transform.localPosition = def != null ? def.HandOffset
                                                            : new Vector3(0.03f, 0.06f, 0.02f);
        _weaponVisual.transform.localRotation = Quaternion.Euler(def != null ? def.HandRotation
                                                                             : Vector3.zero);
        _weaponRestRot = _weaponVisual.transform.localRotation;
    }

    /// True on a peer that only displays this thief: the server owns the whole FSM.
    static bool IsRemoteCopy
    {
        get
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            return nm != null && nm.IsListening && !nm.IsServer;
        }
    }

    void Update()
    {
        if (IsRemoteCopy) { DriveAnimatorRemote(); return; }

        // Walking back out through a gate is hand-driven, like walking in was — there is no navmesh
        // out there. While that is running the FSM must keep its hands off him entirely, or it
        // repaths him back inside the moment he crosses the threshold.
        if (_leavingThroughGate) return;

        TickStayBudget();

        // Noticing a player can change what he is doing before he does any more of it, so this
        // runs first and the tick below is skipped on the frame the state actually changes.
        if (!ReactToPlayers())
        {
            switch (_state)
            {
                case State.Prowling:   TickProwling();   break;
                case State.Seeking:    TickSeeking();    break;
                case State.Working:    TickWorking();    break;
                case State.Skittering: TickSkittering(); break;
                case State.Fighting:   TickFighting();   break;
                case State.Stunned:    TickStunned();    break;
                case State.Fleeing:    TickFleeing();    break;
                case State.Down:       TickDown();       break;
            }
        }

        // Refreshed every frame rather than only when a state is entered: what he is doing can
        // change without the state changing — noticing a player, picking up loot — and a stale
        // agent speed would leave him running at a shamble or shambling at a run.
        if (_agent != null && _agent.enabled && _state != State.Down)
            _agent.speed = GaitSpeed * Pace;

        DriveAnimator();

        if (_voice != null) _voice.Aggressive = _state == State.Fighting;
    }

    void OnDestroy()
    {
        s_inert.Remove(this);

        // A thief destroyed mid-job (scene unload, event cancelled, wave timed out) must not
        // take the goods with him — both are live objects the rest of the warehouse tracks.
        if (_carried != null) DropCarried(Vector3.zero);
        if (_stolenPart != null) ReturnStolenPart(silent: true);
    }

    // ── States ────────────────────────────────────────────────────────────────

    /// The only clock on a raider: how long he is prepared to be inside at all. Everything
    /// else — how long he wrecks, how long a chase lasts — falls out of what the players do.
    void TickStayBudget()
    {
        if (_state == State.Down || _state == State.Fleeing) return;

        KeepOnNavMesh();
        TickStallWatchdog();

        _stayTime += Time.deltaTime;
        if (_stayTime < _maxStayDuration) return;

        // He finishes what is already in flight before turning to leave: a stagger plays
        // out, and a telegraphed swing lands rather than evaporating in front of the player.
        if (_state == State.Stunned) return;
        if (_state == State.Fighting && _swingTimer >= 0f) return;

        BeginFlee();
    }

    /// A raider standing perfectly still with nothing to show for it is a bug, not a plan —
    /// a target that turned out to be empty, a path that never resolved, an agent nudged off
    /// the mesh. Rather than leave him as scenery for the rest of his visit, drop whatever he
    /// was doing and re-decide; if that does not help either, he gives up and leaves. The
    /// warning names the state he was stuck in, so the next report comes with a cause.
    void TickStallWatchdog()
    {
        // Standing still is not the same as stuck: he stands to shove boxes, to work the
        // bench, to fight. Actual work calls MarkProgress, a wind-up counts as progress on its
        // own, and so does any change of state — what is left is genuinely doing nothing.
        if (_state != _stallState)
        {
            _stallState = _state;
            MarkProgress();
            return;
        }

        bool moving = _agent != null && _agent.enabled && _agent.velocity.sqrMagnitude > 0.01f;
        bool busy   = _state == State.Fighting
                   || (_state == State.Working && _workTime > 0f && _strikeWindup > 0f);

        if (moving || busy || Vector3.Distance(transform.position, _stallAnchor) > 0.35f)
        {
            _stallAnchor = transform.position;
            _stallTimer  = 0f;
            return;
        }

        _stallTimer += Time.deltaTime;
        if (_stallTimer < 8f) return;

        _stallTimer = 0f;
        _stallRecoveries++;

        Debug.LogWarning($"[Thief] {name} stalled in {_state} (role {_role}, " +
                         $"pallet {(_pallet != null ? _pallet.name : "none")}, " +
                         $"onMesh {(_agent != null && _agent.isOnNavMesh)}) — recovering.");

        if (_stallRecoveries >= 2) { BeginFlee(); return; }

        _pallet   = null;
        _powerBox = null;
        _state    = State.Seeking;
        _repathTimer = 0f;
        StopAgent(false);
    }

    /// Called whenever he actually accomplishes something. Keeps the watchdog off the back of
    /// a vandal who is standing in one place doing exactly what he came to do.
    void MarkProgress()
    {
        _stallTimer  = 0f;
        _stallAnchor = transform.position;
    }

    void TickProwling()
    {
        _prowlLegTimer -= Time.deltaTime;

        _repathTimer -= Time.deltaTime;
        if (_repathTimer <= 0f)
        {
            _repathTimer = 0.5f;
            SetDestination(_prowlPoint);
        }

        bool arrived = FlatDistance(transform.position, _prowlPoint) <= 2.5f;
        if (!arrived && _prowlLegTimer > 0f) return;

        _prowlLegsLeft--;
        if (_prowlLegsLeft > 0) { NextProwlLeg(); return; }

        _state = State.Seeking;
        _repathTimer = 0f;
    }

    void NextProwlLeg()
    {
        _prowlPoint    = ThiefSpawnPoints.PickWanderPoint(transform.position);
        _prowlLegTimer = _prowlLegTimeout;
        _repathTimer   = 0f;
        StopAgent(false);
    }

    void TickSeeking()
    {
        if (!AcquireTarget()) { BeginFlee(); return; }   // nothing to do — go home

        // The edge of the thing, not its middle — see ApproachPoint. Recomputed each tick because
        // the nearest edge changes as he walks around the stack.
        Vector3 target = ApproachPoint();

        // A pallet can be driven away by a forklift mid-approach — re-issue the path
        // periodically rather than walking to where it used to be.
        _repathTimer -= Time.deltaTime;
        if (_repathTimer <= 0f)
        {
            _repathTimer = 0.5f;
            SetDestination(target);
        }

        if (!HasArrivedAt(target)) return;

        _state    = State.Working;
        _workTime = 0f;
        _struck   = false;
        StopAgent(true);

        // The telegraph is the swing animation itself: he rears back here and the pallet goes
        // over one _strikeWindup later, so what the player sees and what the rules do agree.
        Swing(Random.Range(0, AttackVariants));

        AnnounceArrival();
    }

    /// Close enough to start. A plain radius check is not enough on its own: a pallet's
    /// origin sits in the middle of a big prop, and the nearest spot the navmesh actually lets
    /// him stand can be further away than arm's reach — he would then walk up, stop, and wait
    /// out his whole visit one step short of the boxes. If the agent says the path is
    /// finished, this is as close as he is ever going to get.
    bool HasArrivedAt(Vector3 target)
    {
        if (FlatDistance(transform.position, target) <= _reachDistance) return true;

        if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh) return false;
        if (_agent.pathPending) return false;

        return _agent.remainingDistance <= _agent.stoppingDistance + 0.2f;
    }

    /// Hit-and-run. He walks up, rears back, and one blow does the whole job — the pallet goes
    /// over in a single go rather than a box at a time. Then he leaves the spot: a skittish one
    /// runs for the door, anybody else picks the next target. What he must not do is stand
    /// there afterwards, which is what the old per-box drip amounted to.
    void TickWorking()
    {
        if (!IsTargetUsable()) { _state = State.Seeking; StopAgent(false); return; }

        FaceTowards(TargetPosition());

        _workTime += Time.deltaTime;
        if (_workTime < _strikeWindup) return;   // telegraph still playing — interruptible

        _struck = true;

        switch (_role)
        {
            case ThiefRole.Vandal:
                ScatterPallet();
                MoveOnAfterStrike();
                break;

            case ThiefRole.Looter:
                // He takes one and tips the rest over — in that order, which matters.
                //
                // It used to scatter first and then reach for a box, and since one strike empties
                // the whole pallet (MaxBoxesPerStrike is 64, larger than any stack), there was
                // never anything left to take: every looter fled empty-handed and the theft the
                // HUD warns about could not happen at all.
                TryPickUpBox();
                ScatterPallet();
                BeginFlee();
                break;

            case ThiefRole.PartThief:
                if (TryStealPart()) BeginFlee();
                else { _state = State.Seeking; StopAgent(false); }   // bench went empty
                break;

            case ThiefRole.Saboteur:
                DoSabotage();
                BeginFlee();
                break;
        }
    }

    /// What a vandal does once a pallet is on the floor. He never stays: the whole point of the
    /// rework is that a raider is somewhere else by the time you get to where he was.
    void MoveOnAfterStrike()
    {
        _pallet = null;         // forces AcquireTarget to pick a fresh one
        _struck = false;
        // _announced deliberately stays set: a vandal now takes a pallet every few seconds and
        // there can be five of them, so one call-out per raider is all the HUD can carry.

        // A skittish raider does one thing and gets out; the others go looking for the next
        // pallet and keep the pressure up until their stay budget runs out.
        if (_temper == ThiefTemper.Skittish) { BeginFlee(); return; }

        _state       = State.Seeking;
        _repathTimer = 0f;
        StopAgent(false);
    }

    // ── Noticing people ───────────────────────────────────────────────────

    /// Whether a player is in front of him and close enough to register.
    ///
    /// Distance and a vision cone, deliberately with no line-of-sight raycast. The warehouse is
    /// full of racks, pallets and boxes spread across assorted layers, and a mask that is wrong
    /// in either direction is worse than no mask at all — either he is blind to somebody
    /// standing in the open, or he spots people through a wall. A cone alone is predictable:
    /// approach a zombie from behind and he does not see you.
    bool SeesPlayer(out Transform player, out NetworkPlayer owner)
    {
        player = NearestPlayer(out owner);
        if (player == null) return false;

        Vector3 to = player.position - transform.position;
        to.y = 0f;

        float dist = to.magnitude;
        if (dist > _sightRange) return false;
        if (dist < 0.01f) return true;              // standing on top of him counts

        return Vector3.Angle(transform.forward, to / dist) <= _sightAngle * 0.5f;
    }

    /// Temper-driven reaction to seeing somebody, run before the state tick. Returns true when
    /// it changed state, so the caller skips this frame's tick rather than running a state that
    /// has just been left. A bold raider ignores this entirely and reacts only to being hit.
    bool ReactToPlayers()
    {
        if (_temper == ThiefTemper.Bold) return false;

        _sightIgnoreTimer -= Time.deltaTime;
        if (_sightIgnoreTimer > 0f) return false;

        // Only while he is going about his business. Mid-fight, mid-stagger, already running
        // or already down, the reaction is whatever that state says it is.
        if (_state != State.Prowling && _state != State.Seeking && _state != State.Working)
            return false;

        if (!SeesPlayer(out var player, out _)) return false;

        if (_temper == ThiefTemper.Aggressive)
        {
            BeginFight();
            return true;
        }

        BeginSkitter(player.position);
        return true;
    }

    // ── Skittering ──────────────────────────────────────────────────────

    /// Backing off from somebody who walked in on him. Distinct from Fleeing: he is not leaving
    /// the building, he is putting distance between himself and a player and fully intends to
    /// come back for whatever he was doing.
    void BeginSkitter(Vector3 threat)
    {
        if (_state == State.Down) return;

        _state        = State.Skittering;
        _skitterTimer = _skitterTimeout;
        _agent.speed  = _fleeSpeed;
        _repathTimer  = 0f;

        StopAgent(false);
        PickSkitterPoint(threat);
    }

    void TickSkittering()
    {
        _skitterTimer -= Time.deltaTime;

        var   player = NearestPlayer(out _);
        float dist   = player != null
            ? FlatDistance(transform.position, player.position)
            : float.MaxValue;

        // Either he has the ground he wanted, or he has been running long enough that the
        // player is clearly staying on him — carrying on would pin him in a permanent retreat
        // and he would never do anything again.
        if (dist >= _skitterDistance || _skitterTimer <= 0f)
        {
            // Gave up while still being followed: ignore what he can see for a moment, or he
            // would turn straight back around and run again.
            if (_skitterTimer <= 0f && dist < _skitterDistance)
                _sightIgnoreTimer = SightIgnoreAfterSkitter;

            _agent.speed = GaitSpeed * Pace;
            _state       = State.Seeking;
            _repathTimer = 0f;
            MarkProgress();
            return;
        }

        _repathTimer -= Time.deltaTime;
        if (_repathTimer <= 0f)
        {
            _repathTimer = 0.4f;
            if (player != null) PickSkitterPoint(player.position);
        }
    }

    /// Straight away from the threat. SetDestination samples the navmesh, so a point that lands
    /// in a wall resolves to the nearest reachable spot rather than failing outright.
    void PickSkitterPoint(Vector3 threat)
    {
        Vector3 away = transform.position - threat;
        away.y = 0f;
        away = away.sqrMagnitude < 0.01f ? -transform.forward : away.normalized;

        SetDestination(transform.position + away * _skitterDistance);
    }

    void TickStunned()
    {
        _stunTimer -= Time.deltaTime;

        // The push is a short shove at the front of the stagger, not a glide across the whole
        // stun. Spreading it over the full 1.5 s sent him back the better part of three metres
        // with the animator playing a standing pose over it — the feet had nothing to do with
        // where the body was going, which is exactly what reads as skating.
        //
        // Squared falloff over a fraction of a second covers maybe half a metre: a stumble. The
        // rest of the stun he stands where he landed and plays the stagger out.
        float elapsed = _stunDuration - _stunTimer;
        if (elapsed < _knockbackTime && _agent.enabled && _agent.isOnNavMesh)
        {
            float k = 1f - Mathf.Clamp01(elapsed / _knockbackTime);
            _agent.Move(_knockDir * (_knockbackSpeed * k * k * Time.deltaTime));
        }

        // Turned into the blow as he takes it. A body that is knocked back while still squarely
        // facing where it was working looks pushed by nothing; rotating even slightly sells the
        // hit as having come from somewhere.
        Vector3 face = -_knockDir;
        face.y = 0f;
        if (face.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, Quaternion.LookRotation(face), _stunTurnSpeed * Time.deltaTime);

        if (_stunTimer > 0f) return;

        StopAgent(false);
        if (_woundedFlee) { BeginFlee(); return; }

        if (_fightAfterStun && _stateBeforeStun != State.Fleeing)
        {
            _fightAfterStun = false;
            BeginFight();
            return;
        }
        _fightAfterStun = false;

        if (_stateBeforeStun == State.Fleeing) { _state = State.Fleeing; return; }

        // A skittish one who has just been hit does not calmly go back to work: he puts ground
        // between himself and whoever swung, then reconsiders.
        if (_temper == ThiefTemper.Skittish)
        {
            var attacker = NearestPlayer(out _);
            BeginSkitter(attacker != null
                ? attacker.position
                : transform.position - _knockDir * _skitterDistance);
            return;
        }

        // Working is re-entered through Seeking so the approach distance is re-checked — the
        // knockback may well have pushed him out of arm's reach of what he was working on.
        _state = State.Seeking;
    }

    void TickFleeing()
    {
        Vector3 exit   = _hasExitPoint ? _exitPoint : transform.position;
        float   toExit = FlatDistance(transform.position, exit);

        // Arrived counts as out: the exit point can sit a couple of metres off the navmesh,
        // and a raider frozen one step short of the door is nobody's idea of an escape.
        if (toExit <= 2f || (toExit < 6f && HasArrivedAt(exit)))
        {
            // At the doorway. If he came in through a gate he walks the rest of the way out by
            // hand and is removed outside; anyone else is simply done here.
            if (_exitGate != null) { StartCoroutine(WalkOutThroughGate()); return; }

            Escape();
            return;
        }

        _repathTimer -= Time.deltaTime;
        if (_repathTimer <= 0f)
        {
            _repathTimer = 0.35f;
            SetDestination(WeavePoint(exit, toExit));
        }
    }

    void TickDown()
    {
        TickDownRotation();
        if (_downTimer >= _despawnDelay) Despawn();
    }

    // Tip over, then hold the pose. Runs on clients too, from DriveAnimatorRemote.
    // With the death clip playing there is nothing to tip: only the despawn clock runs.
    void TickDownRotation()
    {
        _downTimer += Time.deltaTime;

        // Physics owns the body; there is nothing here to tip and writing rotation would fight
        // the simulation.
        if (_ragdolled || _downAnimated) return;

        float t = Mathf.Clamp01(_downTimer / _downFallTime);
        transform.rotation = Quaternion.Slerp(_downStartRot, _downEndRot, t * t);
    }

    /// Networked thieves are despawned through NGO so every client loses them together; a
    /// single-player one is just destroyed.
    void Despawn()
    {
        var netObj = GetComponent<Unity.Netcode.NetworkObject>();
        if (netObj != null && netObj.IsSpawned)
        {
            if (netObj.NetworkManager.IsServer) netObj.Despawn(destroy: true);
            return;
        }

        Destroy(gameObject);
    }

    // ── Targeting ─────────────────────────────────────────────────────────────

    /// Makes sure this thief has something to walk to. False means his job is impossible
    /// (no loaded pallets, empty bench) and he should just leave.
    bool AcquireTarget()
    {
        switch (_role)
        {
            case ThiefRole.Vandal:
            case ThiefRole.Looter:
                if (IsPalletUsable(_pallet)) return true;
                _pallet      = PickPallet();
                _repathTimer = 0f;
                return _pallet != null;

            case ThiefRole.PartThief:
                return AssemblyBench.Instance != null
                    && AssemblyBench.Instance.GetRandomPlacedPart() != null;

            case ThiefRole.Saboteur:
                if (_powerBox != null) return true;
                _powerBox    = PickPowerBox();
                _repathTimer = 0f;
                // No power boxes in the scene: he can still tip something slippery over
                // wherever he happens to be standing.
                return true;
        }

        return false;
    }

    bool IsTargetUsable() => _role switch
    {
        ThiefRole.Vandal    => IsPalletUsable(_pallet),
        ThiefRole.Looter    => IsPalletUsable(_pallet),
        ThiefRole.PartThief => AssemblyBench.Instance != null,
        _                   => true,
    };

    Vector3 TargetPosition() => _role switch
    {
        ThiefRole.Vandal    => _pallet != null ? _pallet.transform.position : transform.position,
        ThiefRole.Looter    => _pallet != null ? _pallet.transform.position : transform.position,
        ThiefRole.PartThief => AssemblyBench.Instance != null
                                   ? AssemblyBench.Instance.transform.position
                                   : transform.position,
        _                   => _powerBox != null ? _powerBox.transform.position : transform.position,
    };

    /// Where he walks to, as opposed to what he is aiming at.
    ///
    /// TargetPosition is the middle of the thing — right for turning to face it, wrong for
    /// standing next to it. A pallet stack is over a metre across and a workbench more, so
    /// closing to arm's reach of the *origin* leaves him swinging at air a full body-length short
    /// of the boxes. This returns the nearest point on the target's own footprint instead, so
    /// "within reach" means within reach of the surface he is about to hit.
    Vector3 ApproachPoint()
    {
        Vector3 origin = TargetPosition();

        if ((_role == ThiefRole.Vandal || _role == ThiefRole.Looter) && _pallet != null)
            return _pallet.ClosestEdgePoint(transform.position);

        // Everything else is asked for its collider's nearest point, which handles the bench and
        // the power box without either of them needing to know about raiders.
        var target = _role == ThiefRole.PartThief
            ? (AssemblyBench.Instance != null ? AssemblyBench.Instance.gameObject : null)
            : (_powerBox != null ? _powerBox.gameObject : null);

        var collider = target != null ? target.GetComponentInChildren<Collider>() : null;

        return collider != null ? collider.ClosestPoint(transform.position) : origin;
    }

    /// Prefers a loaded pallet the players are not standing next to — being spotted mid-shove
    /// is the point of the event, but starting the fight on top of someone is not.
    PalletStackZone PickPallet()
    {
        var players = CollectPlayerPositions();

        PalletStackZone best = null;
        float bestScore = float.MinValue;

        foreach (var zone in PalletStackZone.All)
        {
            if (!IsPalletUsable(zone)) continue;

            Vector3 p = zone.transform.position;

            float nearestPlayer = 20f;
            foreach (var pos in players)
                nearestPlayer = Mathf.Min(nearestPlayer, FlatDistance(p, pos));

            // Reward distance from the players, penalise a long walk, reward a full pallet.
            float score = Mathf.Min(nearestPlayer, 25f)
                        - FlatDistance(p, transform.position) * 0.35f
                        + zone.BoxesOnPallet.Count * 0.8f;

            if (score > bestScore) { bestScore = score; best = zone; }
        }

        return best;
    }

    GameObject PickPowerBox()
    {
        GameObject best = null;
        float bestDist = float.MaxValue;

        foreach (var go in PowerBoxRegistry.GetAll())
        {
            if (go == null) continue;
            float d = FlatDistance(go.transform.position, transform.position);
            if (d < bestDist) { bestDist = d; best = go; }
        }

        return best;
    }

    static bool IsPalletUsable(PalletStackZone zone)
    {
        if (zone == null) return false;

        // Not just "the list is non-empty": a zone only drops a box from its list when
        // something explicitly tells it to, so a box that was destroyed (broke, fell through
        // the world) leaves a null behind. Counting those as stock parked the thief at an
        // empty pallet, shoving nothing, until his time ran out.
        if (!HasTakeableBox(zone)) return false;

        // A pallet in the air on a forklift's forks cannot be reached on foot.
        var pb = zone.GetComponent<PalletBehavior>();
        if (pb == null) pb = zone.GetComponentInParent<PalletBehavior>();
        return pb == null || !pb.IsLifted;
    }

    /// At least one box on this pallet is real and not already in somebody's hands.
    static bool HasTakeableBox(PalletStackZone zone)
    {
        foreach (var box in zone.BoxesOnPallet)
        {
            if (box == null) continue;
            if (BoxPhysicsInteractable.GrabbedBox == box) continue;
            return true;
        }
        return false;
    }

    // ── Damage ────────────────────────────────────────────────────────────────

    /// Called by MeleeCombat. fromDirection is the horizontal direction the blow travelled in.
    /// heavy marks a fully charged blow. It always puts him down, whatever is left of his
    /// health, and throws the body instead of playing the death clip — that is the whole payoff
    /// for having stood still long enough to wind it up.
    public void TakeHit(int damage, Vector3 fromDirection, bool heavy = false)
    {
        // In a session only the server may mutate health/state/rewards. Clients receive the
        // resulting stagger/down presentation from NetworkThief broadcasts.
        if (Unity.Netcode.NetworkManager.Singleton?.IsListening == true
            && _net != null && _net.IsSpawned && !_net.IsServer) return;
        if (_state == State.Down) return;

        // Still being walked in: take him over first, then let the blow land on the real thing.
        if (_wakeOnHit != null)
        {
            var wake = _wakeOnHit;
            _wakeOnHit = null;
            wake();
        }

        _health -= Mathf.Max(1, damage);

        if (heavy || _health <= 0) { GoDown(fromDirection, heavy); return; }

        // The real payoff for a hit that does not kill: whatever he was part-way through goes
        // back to zero. A player who cannot finish him can still stop the job from landing.
        bool interrupted = _state == State.Working && !_struck && _strikeWindup > 0f;
        _workTime = 0f;

        // Hit him and what happens next is his temper: an aggressive one always squares up,
        // a skittish one never does and bolts instead, and a bold one falls back to the old
        // roll. Either way it resolves after the stagger, so the player who swung first keeps
        // the initiative.
        _fightAfterStun = _temper switch
        {
            ThiefTemper.Aggressive => true,
            ThiefTemper.Skittish   => false,
            _                      => _weapon != MeleeWeaponKind.Fists
                                   || Random.value < _retaliateChanceFists,
        };

        PlayDamage(fromDirection);
        _net?.BroadcastDamage(fromDirection);

        if (_state != State.Stunned) _stateBeforeStun = _state;
        _state     = State.Stunned;
        _stunTimer = _stunDuration;
        StopAgent(true);

        fromDirection.y = 0f;
        _knockDir = fromDirection.sqrMagnitude > 0.001f
            ? fromDirection.normalized
            : -transform.forward;

        if (_health <= _fleeHealth && !_woundedFlee)
        {
            _woundedFlee = true;
            Announce(
                "The thief is running!",
                "Wounded — he is heading for the exit",
                HUDController.NotifType.Warning);
        }
        else if (interrupted)
        {
            Announce(
                "Interrupted!",
                "You knocked him off it — he has to start over",
                HUDController.NotifType.Success);
        }
    }

    void GoDown(Vector3 fromDirection, bool heavy = false)
    {
        _state     = State.Down;
        _downTimer = 0f;

        if (_carried != null) DropCarried(fromDirection);
        if (_stolenPart != null) ReturnStolenPart(silent: false);
        DropWeapon();

        DownVisuals(fromDirection, heavy);
        _net?.BroadcastDown(fromDirection, heavy);

        GameManager.Instance?.AddBonusScore(_neutralizeReward);

        Announce(
            "Thief neutralized",
            $"+{_neutralizeReward} to the budget",
            HUDController.NotifType.Success);

        Resolve(false);
    }

    /// The part of going down that every peer plays: the body tips over, stops colliding and
    /// stops animating. Split out so a client can replay it from one broadcast.
    void DownVisuals(Vector3 fromDirection, bool heavy = false)
    {
        _downTimer = 0f;

        StopAgent(true);
        if (_agent != null) _agent.enabled = false;

        // Every neutralisation uses physics. A normal final hit gives the body a restrained fall;
        // a fully charged blow or a sufficiently hard box impact gets the full launch impulse.
        // ThiefRagdoll repairs missing prefab references from the humanoid Avatar in Awake.
        var ragdoll = GetComponent<ThiefRagdoll>();
        if (ragdoll != null && ragdoll.EnsureRig())
        {
            _ragdolled = true;

            Vector3 push = fromDirection;
            push.y = 0f;
            push = push.sqrMagnitude > 0.001f ? push.normalized : -transform.forward;

            // Up as well as back: a blow that only pushes horizontally slides the body along the
            // floor, which reads as a shove rather than as being knocked off his feet.
            float impulseScale = heavy ? 1f : 0.35f;
            Vector3 impulse = (push * _ragdollPush + Vector3.up * _ragdollLift)
                            * impulseScale;
            ragdoll.Activate(impulse, transform.position + Vector3.up * 1.1f);

            // Only the root's own hit capsule goes: the bone colliders ARE the ragdoll, and
            // disabling them would drop the body through the floor.
            foreach (var col in GetComponents<Collider>()) col.enabled = false;

            if (_voice != null) _voice.Death();
            CameraShake.AddTrauma(0.6f);
            return;
        }

        // The zombie body has a death animation and no ragdoll rig, so the fall is a clip that
        // holds its last frame. A body without that clip (an older prefab, the primitive
        // stand-in) still gets the old trick: freeze the pose and tip the whole thing over.
        _ragdolled    = false;
        _downAnimated = HasParameter(DownHash);

        if (_downAnimated)
        {
            // A swing queued in the same frame would yank him straight back out of the death
            // state, since both are entered from Any State.
            _animator.ResetTrigger(AttackHash);
            _animator.ResetTrigger(DamageHash);
            _animator.SetTrigger(DownHash);
        }
        else
        {
            if (_animator != null) _animator.speed = 0f;

            fromDirection.y = 0f;
            Vector3 fall = fromDirection.sqrMagnitude > 0.001f
                ? fromDirection.normalized
                : transform.forward;
            _downStartRot = transform.rotation;
            _downEndRot   = Quaternion.AngleAxis(82f, Vector3.Cross(Vector3.up, fall)) * transform.rotation;
        }

        foreach (var col in GetComponentsInChildren<Collider>()) col.enabled = false;

        if (_voice != null) _voice.Death();

        CameraShake.AddTrauma(0.35f);
    }

    // ── Jobs ──────────────────────────────────────────────────────────────────

    /// One blow, the whole stack on the floor. The old behaviour shoved one box per interval,
    /// which parked a raider at the same pallet for his entire visit and read as a man standing
    /// still. Taking the pallet in one strike is what lets him move on to the next one.
    int ScatterPallet()
    {
        int knocked = 0;

        // Bounded rather than while(true): TakeTopBoxFromPallet only returns null once the
        // zone is empty, and a zone that somehow never empties would hang the frame.
        while (knocked < MaxBoxesPerStrike)
        {
            if (!ShoveOneBox()) break;
            knocked++;
        }

        return knocked;
    }

    /// False when the pallet had nothing left to take.
    bool ShoveOneBox()
    {
        var box = TakeTopBoxFromPallet();
        if (box == null) return false;

        Vector3 away = box.transform.position - transform.position;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = transform.forward;
        away.Normalize();

        // Fan the throws out instead of piling every box in one spot — the point of a vandal
        // is a floor nobody can walk across, not a neat second stack.
        away = Quaternion.Euler(0f, Random.Range(-55f, 55f), 0f) * away;

        box.KnockOff(away * (_knockForce * Random.Range(0.8f, 1.4f))
                     + Vector3.up * (_knockForce * 0.25f));
        MarkProgress();
        return true;
    }

    void TryPickUpBox()
    {
        if (_carried != null) return;

        var box = TakeTopBoxFromPallet();
        if (box == null) return;

        _carried   = box;
        _carriedRb = box.GetComponent<Rigidbody>();

        if (_carriedRb != null)
        {
            _carriedRb.isKinematic = true;
            _carriedRb.useGravity  = false;
            // Colliders stay on but out of the simulation, so the carried box cannot shove
            // its own carrier around or topple the pallet stack as he turns away.
            _carriedRb.detectCollisions = false;
        }

        // Follow rather than SetParent: only the server may re-parent a NetworkObject, and
        // the follow is re-derived locally every LateUpdate on every peer — the same reason
        // CartStorageInteractable stacks boxes this way.
        box.BeginFollow(_carryAnchor, Vector3.zero, Quaternion.identity);
        SetCarryPose(true);
        MarkProgress();
        _net?.BroadcastCarry(box.NetBoxId, true);

        Announce(
            "THEFT!",
            "The thief shouldered a box — stop him before he reaches the exit",
            HUDController.NotifType.Danger);
    }

    void DropCarried(Vector3 pushDir)
    {
        if (_carried == null) return;

        var box = _carried;
        _carried = null;

        box.EndFollow();
        SetCarryPose(false);
        _net?.BroadcastCarry(box.NetBoxId, false);

        if (_carriedRb != null)
        {
            _carriedRb.isKinematic      = false;
            _carriedRb.useGravity       = true;
            _carriedRb.detectCollisions = true;

            pushDir.y = 0f;
            Vector3 lateral = pushDir.sqrMagnitude > 0.001f ? pushDir.normalized : Vector3.zero;
            _carriedRb.AddForce(lateral * 2f + Vector3.up * 1.5f, ForceMode.Impulse);
        }

        _carriedRb = null;
    }

    /// Detaches the highest box tracked on the target pallet and hands it back untracked.
    /// Mirrors BoxPhysicsInteractable.Grab()'s pallet bookkeeping so the zone (and, in
    /// multiplayer, NetworkPalletManager) learns the box has left the stack.
    BoxPhysicsInteractable TakeTopBoxFromPallet()
    {
        if (_pallet == null) return null;

        BoxPhysicsInteractable best = null;
        float bestY = float.MinValue;

        foreach (var box in _pallet.BoxesOnPallet)
        {
            if (box == null) continue;
            if (BoxPhysicsInteractable.GrabbedBox == box) continue;   // a player is holding it
            float y = box.transform.position.y;
            if (y > bestY) { bestY = y; best = box; }
        }

        if (best == null) return null;

        var zone = best.StoredOnPallet;
        if (zone != null)
        {
            zone.OnBoxPickedUp(best);
            best.StoredOnPallet = null;
        }
        best.EndFollow();

        return best;
    }

    /// Lifts one already-placed part straight off the assembly bench. The order loses it the
    /// moment he takes it — the players see the hole in the bench and know what he is carrying.
    bool TryStealPart()
    {
        var bench = AssemblyBench.Instance;
        if (bench == null) return false;

        PartDefinition stolen;

        if (IsNetworked)
        {
            // Server-only path (Update early-outs on clients): mirror server-side removal and
            // echo the removal so every client's bench ghost matches.
            stolen = bench.GetRandomPlacedPart();
            if (stolen == null || !bench.TryRemovePartById(stolen.PartID)) return false;
            CoopGameManager.Instance?.BroadcastBenchPartRemovedClientRpc(stolen.PartID);
        }
        else
        {
            stolen = bench.TryRemovePlacedPart();
            if (stolen == null) return false;
        }

        _stolenPart = stolen;
        BuildLootVisual(stolen);
        _net?.PublishLoot(stolen.PartID);
        MarkProgress();

        CameraShake.AddTrauma(0.25f);
        AudioManager.Instance?.PlayError();

        Announce(
            "PART STOLEN!",
            $"«{stolen.PartName}» is off the bench — put him down before he reaches the exit",
            HUDController.NotifType.Danger);

        return true;
    }

    /// Puts a stolen part back where it came from. Called when the players drop him — the
    /// takedown is worth more than the score bonus alone.
    void ReturnStolenPart(bool silent)
    {
        var part = _stolenPart;
        _stolenPart = null;
        DestroyLootVisual();
        _net?.PublishLoot(null);

        if (part == null) return;

        var bench = AssemblyBench.Instance;
        if (bench == null) return;

        bench.PlacePartFromNetwork(part);

        if (IsNetworked)
            CoopGameManager.Instance?.BroadcastBenchPartRestoredClientRpc(part.PartID);

        if (silent) return;

        Announce(
            "Part recovered",
            $"«{part.PartName}» is back on the bench",
            HUDController.NotifType.Success);
    }

    /// Breaks something on his way out. Reuses the existing incidents rather than inventing a
    /// parallel one, so the repair flow players already know is what fixes it.
    void DoSabotage()
    {
        MarkProgress();

        var gem = GameEventManager.Instance;
        if (gem == null) return;

        // Prefer cutting the power if he actually reached a power box and the lights are on;
        // otherwise he tips something slippery over instead.
        bool cutPower = _powerBox != null
                     && !gem.IsEventActive("PowerFailure")
                     && gem.ForceStartEvent("PowerFailure");

        if (cutPower)
        {
            CameraShake.AddTrauma(0.5f);
            Announce(
                "SABOTAGE!",
                "The thief smashed a power box — the lights are out",
                HUDController.NotifType.Danger);
            return;
        }

        if (gem.ForceStartEvent("WetFloor"))
        {
            Announce(
                "SABOTAGE!",
                "He tipped a drum over — the floor is slick",
                HUDController.NotifType.Danger);
        }
    }

    // ── Fighting back ─────────────────────────────────────────────────────────

    void BeginFight()
    {
        _state       = State.Fighting;
        _attackTimer = _attackInterval * 0.5f;   // he does not swing the instant he recovers
        _swingTimer  = -1f;
        _agent.speed = GaitSpeed * 1.25f * Pace;
        StopAgent(false);
    }

    void TickFighting()
    {
        var player = NearestPlayer(out var owner);
        if (player == null) { ReturnToJob(); return; }

        float dist = FlatDistance(transform.position, player.position);
        if (dist > _fightLeash) { ReturnToJob(); return; }   // lost him — back to work

        FaceTowards(player.position);

        // He does not kick a man while he is down. He stands over the player and waits for
        // him to get back up, which is also the window to crawl away or for a team-mate to
        // jump in.
        if (IsDown(owner))
        {
            StopAgent(true);
            _swingTimer  = -1f;
            _attackTimer = _attackInterval * 0.75f;   // a beat of grace after standing up
            ResetWeaponPose();
            return;
        }

        if (dist > _attackRange)
        {
            StopAgent(false);
            _repathTimer -= Time.deltaTime;
            if (_repathTimer <= 0f)
            {
                _repathTimer = 0.25f;
                SetDestination(player.position);
            }
            return;
        }

        StopAgent(true);

        // A swing already in flight: play out the telegraph, then land it.
        if (_swingTimer >= 0f)
        {
            _swingTimer -= Time.deltaTime;
            AnimateSwing(1f - Mathf.Clamp01(_swingTimer / Mathf.Max(0.01f, _attackWindup)));

            if (_swingTimer <= 0f)
            {
                _swingTimer = -1f;
                ResetWeaponPose();
                LandBlow(player, owner);
            }
            return;
        }

        _attackTimer -= Time.deltaTime;
        if (_attackTimer > 0f) return;

        _attackTimer = _attackInterval;
        _swingTimer  = _attackWindup;

        // The wind-up the player reads is the animation itself — it is now the only tell there
        // is, so the swing starts here and LandBlow fires when it connects, one
        // _attackWindup later.
        Swing(Random.Range(0, AttackVariants));
    }

    void LandBlow(Transform player, NetworkPlayer owner)
    {
        // Backing out of reach during the wind-up is a clean dodge.
        if (FlatDistance(transform.position, player.position) > _attackRange + 0.4f) return;
        if (IsDown(owner)) return;

        // The thud of it connecting. LandBlow itself is server-side, so the sound is sent on
        // to the clients the same way the swing is.
        PlayImpact();
        _net?.BroadcastImpact();

        bool armed = _weapon != MeleeWeaponKind.Fists;

        Vector3 dir = player.position - transform.position;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 0.01f ? dir.normalized : transform.forward;

        bool knockdown = Random.value < (armed ? _knockdownChanceArmed : _knockdownChanceFists);

        Vector3 impulse   = dir * (armed ? 12f : 8f) + Vector3.up * 3f;
        Vector3 hitPoint  = player.position + Vector3.up * 1.2f;
        float   injury    = armed ? Random.Range(26f, 40f) : Random.Range(14f, 22f);
        string  bone      = armed ? (Random.value < 0.5f ? "LeftArm" : "LeftLeg") : "Spine";

        if (owner != null)
        {
            // Two broadcasts, because they have different audiences. The flinch is for everyone
            // watching; the ragdoll and the injury are the victim's own body and go to him alone.
            owner.PlayHitReactionClientRpc(dir, armed ? 1f : 0.7f);

            // The server commits the canonical fall/injury transaction before addressing the
            // physical presentation to the owner.
            owner.ServerTakeWorldBlow(impulse, hitPoint, knockdown, injury,
                new Unity.Collections.FixedString32Bytes(bone));
        }
        else
        {
            // Singleplayer: nobody is watching, but the local body still flinches — the player
            // sees his own shoulders take it in third person.
            var body = PlayerFinder.Find();
            if (body != null) HitReaction.Ensure(body.gameObject)?.Play(dir, armed ? 1f : 0.7f);

            CameraShake.Kick(dir, armed ? 0.6f : 0.4f);
            AudioManager.Instance?.PlayAt(GameAudioEvent.RobotDamageImpact, hitPoint);

            if (knockdown && RagdollController.Instance != null)
            {
                // TriggerRagdoll feeds its own impulse into InjurySystem, so the knockdown
                // already carries a bruise with it — no second injury on top.
                RagdollController.Instance.TriggerRagdoll(impulse, hitPoint);
            }
            else if (!knockdown)
            {
                InjurySystem.Instance?.AddInjuryFromImpact(injury, bone);
            }
        }

        if (knockdown)
        {
            Announce(
                armed ? "The bat caught someone!" : "He put someone on the floor!",
                "Get him off them before he goes back to work",
                HUDController.NotifType.Danger);
        }
    }

    /// The player he is squaring up to. In multiplayer the host's own body is just one more
    /// entry in the roster, so the whole team is fair game for whoever is nearest.
    Transform NearestPlayer(out NetworkPlayer owner)
    {
        owner = null;

        if (!IsNetworked) return PlayerFinder.Find();

        Transform best = null;
        float bestDist = float.MaxValue;

        foreach (var p in NetworkPlayer.All)
        {
            if (p == null) continue;
            float d = FlatDistance(p.transform.position, transform.position);
            if (d >= bestDist) continue;
            bestDist = d;
            best     = p.transform;
            owner    = p;
        }

        return best;
    }

    /// Is that player already on the floor? Remote ones report it through NetworkPlayer,
    /// since only their own client runs their ragdoll.
    static bool IsDown(NetworkPlayer owner)
    {
        if (owner != null) return owner.IsRagdolledNet.Value;
        return RagdollController.Instance != null && RagdollController.Instance.IsRagdoll;
    }

    void ReturnToJob()
    {
        _state       = State.Seeking;
        _agent.speed = GaitSpeed * Pace;
        _swingTimer  = -1f;
        ResetWeaponPose();
        StopAgent(false);
    }

    // Rears the weapon back and brings it down across the wind-up. Zombies are always
    // bare-handed, so in practice this does nothing and the swing clip plus his stopping to
    // face you are the whole tell — which is the point: you read the raider, not a marker.
    void AnimateSwing(float t)
    {
        if (_weaponVisual == null) return;

        Quaternion back = _weaponRestRot * Quaternion.Euler(-75f, 0f, -20f);
        Quaternion thru = _weaponRestRot * Quaternion.Euler(60f, 0f, 15f);

        _weaponVisual.transform.localRotation = t < 0.7f
            ? Quaternion.Slerp(_weaponRestRot, back, t / 0.7f)
            : Quaternion.Slerp(back, thru, (t - 0.7f) / 0.3f);
    }

    void ResetWeaponPose()
    {
        if (_weaponVisual != null) _weaponVisual.transform.localRotation = _weaponRestRot;
    }

    /// His bat hits the floor as a pickup when he does. Beating an armed thief bare-handed is
    /// how the players arm themselves — the weapon economy is the raid's own drop table.
    void DropWeapon()
    {
        if (_weapon == MeleeWeaponKind.Fists) return;

        var kind = _weapon;
        _weapon  = MeleeWeaponKind.Fists;

        if (_weaponVisual != null)
        {
            Destroy(_weaponVisual);
            _weaponVisual = null;
        }

        Vector3 where = transform.position + transform.forward * 0.4f + Vector3.up * 0.15f;
        MeleeWeaponItem.Spawn(kind, where);
        _net?.PublishWeapon(MeleeWeaponKind.Fists);
        _net?.BroadcastWeaponDrop((int)kind, where);

        Announce(
            "He dropped his bat",
            $"Pick it up {InputHints.E} — two hits per thief instead of three",
            HUDController.NotifType.Success);
    }

    // ── Transitions ───────────────────────────────────────────────────────────

    // ── Leaving by the gate they came in through ──────────────────────────────

    GateAnimator _exitGate;
    bool         _leavingThroughGate;

    /// Sends him home the way he arrived, rather than to a generic exit point.
    ///
    /// Worth being explicit about: the generic exit is a point on the edge of a hardcoded rectangle
    /// that has not matched this warehouse in a long time, so a raider "escaping" evaporated in the
    /// middle of the floor in plain view. A raid that came through a gate has an obvious door to
    /// leave by, and it is one the event already opens and shuts.
    public void SetGateExit(GateAnimator gate)
    {
        if (gate == null || !gate.TryGetSides(out Vector3 inside, out Vector3 _)) return;

        _exitGate     = gate;
        _exitPoint    = inside;      // as far as the agent goes — the navmesh stops at the doorway
        _hasExitPoint = true;
    }

    /// The last few metres, out past the shutter, driven by hand.
    ///
    /// Mirrors GateBreachEvent.WalkThemIn for the same reason: outside is off the navmesh, so the
    /// agent cannot take him there. He is only removed once he is past the doorway, so he leaves
    /// rather than vanishing where the team can watch him do it.
    System.Collections.IEnumerator WalkOutThroughGate()
    {
        _leavingThroughGate = true;

        if (_exitGate.TryGetSides(out Vector3 _, out Vector3 outside))
        {
            StopAgent(true);
            if (_agent != null && _agent.enabled) _agent.enabled = false;

            float speed  = _fleeSpeed * Pace;
            Vector3 flat = new(outside.x, transform.position.y, outside.z);

            // MotionSpeed defaults to zero, and a locomotion state played at zero holds one frame
            // — he would slide out of the building in a fixed pose.
            DriveLocomotion(_animator, speed, _shambleClipStride, _runClipStride,
                            _animMinPlayback, _animMaxPlayback);

            // Timed out as well as distance-checked: the doorway can be blocked, and a raider stuck
            // on a crate must not keep the raid alive forever.
            float deadline = Time.time + 8f;

            while (Time.time < deadline &&
                   Vector3.Distance(transform.position, flat) > 0.2f)
            {
                // Killed in the doorway. Update() skips the whole FSM while this flag is up, so
                // nothing else would stop the hand-drive — the corpse used to keep sliding out
                // through the gate lying down, and then Escape() counted it as a getaway.
                if (_state == State.Down) break;

                transform.position = Vector3.MoveTowards(
                    transform.position, flat, speed * Time.deltaTime);

                Vector3 face = flat - transform.position;
                face.y = 0f;
                if (face.sqrMagnitude > 0.001f)
                    transform.rotation = Quaternion.Slerp(
                        transform.rotation, Quaternion.LookRotation(face), 6f * Time.deltaTime);

                yield return null;
            }
        }

        _leavingThroughGate = false;
        if (_state == State.Down) yield break;
        Escape();
    }

    void BeginFlee()
    {
        if (_state == State.Down) return;

        _state       = State.Fleeing;
        _agent.speed = (HasLoot ? _lootFleeSpeed : _fleeSpeed) * Pace;
        _repathTimer = 0f;
        StopAgent(false);

        if (!_hasExitPoint)
        {
            // No exit was handed in — leave via the point farthest from the players.
            _exitPoint    = ThiefSpawnPoints.PickExitPoint(transform.position);
            _hasExitPoint = true;
        }
    }

    /// A serpentine path to the exit: chasing him means predicting the swing, not holding W.
    /// The last stretch is straight so he does not orbit the doorway.
    Vector3 WeavePoint(Vector3 exit, float toExit)
    {
        if (toExit < 6f) return exit;

        Vector3 forward = exit - transform.position;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f) return exit;
        forward.Normalize();

        Vector3 side = Vector3.Cross(Vector3.up, forward);

        // Cut harder when someone is right behind him.
        float amp    = _weaveAmplitude * (NearestPlayerDistance() < 4f ? 1.6f : 1f);
        float offset = Mathf.Sin((Time.time + _weaveSeed) * _weaveFrequency) * amp;

        // Aim a few metres ahead so the agent commits to the curve instead of stuttering.
        return transform.position + forward * Mathf.Min(toExit, 7f) + side * offset;
    }

    void Escape()
    {
        if (_carried != null)
        {
            var stolen = _carried;
            DropCarried(Vector3.zero);
            Destroy(stolen.gameObject);

            GameManager.Instance?.AddBonusScore(-_stolenBoxPenalty);
            Announce(
                "Box stolen",
                $"The thief got away with the goods — -{_stolenBoxPenalty} from the budget",
                HUDController.NotifType.Danger);
        }
        else if (_stolenPart != null)
        {
            string partName = _stolenPart.PartName;
            _stolenPart = null;
            DestroyLootVisual();
            _net?.PublishLoot(null);

            GameManager.Instance?.AddBonusScore(-_stolenPartPenalty);
            Announce(
                "Part stolen",
                $"«{partName}» left with him — -{_stolenPartPenalty}, the order needs a replacement",
                HUDController.NotifType.Danger);
        }
        else
        {
            Announce(
                "The thief got away",
                _role == ThiefRole.Vandal
                    ? "He left the mess behind him"
                    : "He left empty-handed",
                HUDController.NotifType.Warning);
        }

        Resolve(true);
        Despawn();
    }

    void Resolve(bool escaped)
    {
        if (_resolved) return;
        _resolved = true;
        OnResolved?.Invoke(this, escaped);
    }

    /// One call-out per thief, the moment he starts working — this is the cue that sends the
    /// players running, so it names the place, not the crime.
    void AnnounceArrival()
    {
        if (_announced) return;
        _announced = true;

        switch (_role)
        {
            // The window is now the strike telegraph rather than a several-second job, so the
            // call-out has to read as "right now" instead of quoting a countdown.
            case ThiefRole.PartThief:
                Announce(
                    "He's at the assembly bench!",
                    "He is reaching for a part — hit him now",
                    HUDController.NotifType.Danger);
                break;

            case ThiefRole.Saboteur:
                Announce(
                    "He's at the power box!",
                    "One swing and the lights go out — hit him now",
                    HUDController.NotifType.Danger);
                break;

            case ThiefRole.Looter:
                Announce(
                    "He's going through a pallet",
                    "He is about to tip it over and walk off with a box",
                    HUDController.NotifType.Warning);
                break;

            default:
                Announce(
                    "He's wrecking a pallet",
                    "One blow and the stack is on the floor",
                    HUDController.NotifType.Warning);
                break;
        }
    }

    // ── Loot visual ───────────────────────────────────────────────────────────

    void BuildLootVisual(PartDefinition part)
    {
        DestroyLootVisual();
        if (part == null) return;

        if (part.VisualPrefab != null)
        {
            _lootVisual = Instantiate(part.VisualPrefab);
            foreach (var col in _lootVisual.GetComponentsInChildren<Collider>()) Destroy(col);
            foreach (var rb in _lootVisual.GetComponentsInChildren<Rigidbody>()) Destroy(rb);
            ScaleToFit(_lootVisual, 0.45f);
        }
        else
        {
            _lootVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(_lootVisual.GetComponent<Collider>());
            _lootVisual.transform.localScale = Vector3.one * 0.3f;

            var rend = _lootVisual.GetComponent<Renderer>();
            if (rend != null)
            {
                rend.material.color = part.DebugColor;
                rend.material.SetColor("_BaseColor", part.DebugColor);
            }
        }

        _lootVisual.name = $"StolenPart_{part.PartID}";
        _lootVisual.transform.SetParent(_carryAnchor, false);
        _lootVisual.transform.localPosition = Vector3.zero;
        _lootVisual.transform.localRotation = Quaternion.identity;
    }

    void DestroyLootVisual()
    {
        if (_lootVisual == null) return;
        Destroy(_lootVisual);
        _lootVisual = null;
    }

    // Parts are authored at wildly different scales — shrink whatever came in to a size that
    // reads as "carried in two hands" rather than swallowing the thief.
    static void ScaleToFit(GameObject go, float maxDimension)
    {
        var rends = go.GetComponentsInChildren<Renderer>();
        if (rends.Length == 0) return;

        var bounds = rends[0].bounds;
        foreach (var r in rends) bounds.Encapsulate(r.bounds);

        float largest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        if (largest <= 0.0001f || largest <= maxDimension) return;

        go.transform.localScale *= maxDimension / largest;
    }

    // ── Multiplayer presentation ──────────────────────────────────────────────

    /// Every call-out in this class goes through here: on a server it reaches all clients,
    /// in single player it is a plain local notification.
    void Announce(string title, string sub, HUDController.NotifType type)
    {
        if (_net != null) { _net.Announce(title, sub, type); return; }
        HUDController.Instance?.AddNotification(title, sub, type);
    }

    /// Client-side: the body is moved by NetworkTransform, so the walk cycle has to be
    /// derived from how far it actually travelled rather than from the (disabled) agent.
    void DriveAnimatorRemote()
    {
        if (_state == State.Down) { TickDownRotation(); return; }
        if (_animator == null) return;

        float travelled = Vector3.Distance(transform.position, _lastRemotePos);
        _lastRemotePos = transform.position;

        float instant = Time.deltaTime > 0f ? travelled / Time.deltaTime : 0f;
        _remoteSpeed  = Mathf.Lerp(_remoteSpeed, instant, 10f * Time.deltaTime);

        _animator.SetFloat(SpeedHash, _remoteSpeed, 0.1f, Time.deltaTime);
        // Never written on clients before: the blend stayed wherever the controller left it, so a
        // client's raiders ran on one clip whatever the host's were doing.
        _animator.SetFloat(GaitHash, _remoteGait, 0.18f, Time.deltaTime);
        _animator.SetFloat(MotionSpeedHash, PlaybackFor(_remoteSpeed, _remoteGait));
        _animator.SetBool(GroundedHash, true);
    }

    /// Role still crosses the wire: it is what colours his dot on the Dispatcher's minimap,
    /// even though nothing about it shows on the body any more.
    public void ApplyRemoteRole(ThiefRole role) => _role = role;

    /// Clients take the skin the server rolled — a raider must look the same on every screen,
    /// because "the one in the red shirt" is how a team calls out which zombie to hit.
    public void ApplyRemoteBody(int variant) => ApplyBodyVariant(variant);

    /// Gives this raider his model now instead of at Initialize.
    ///
    /// For anyone visible before his AI starts. The gate breach stands its raiders outside for a
    /// dozen seconds hammering on the shutter, and rolling the body at Initialize meant every one
    /// of them silently turned into a different person the moment he stepped inside and the raid
    /// took him over.
    public void ChooseBody()
    {
        if (_bodyChosen) return;

        _bodyChosen = true;
        ApplyBodyVariant(NextBodyVariant());

        // Published here and not left to Initialize, for the reason this method exists at all —
        // only now the failure is a networked one. A raider spawned early stands at the gate
        // wearing whatever the prefab ships with, and turns into a different man the moment the
        // raid takes him over. Which is the exact bug the comment above describes, seen from the
        // other side of the wire.
        _net?.PublishBody(_bodyVariant);
    }

    bool _bodyChosen;

    /// A body and a skin for it, packed into the one int that already replicates, or -1 when
    /// nothing is configured (the primitive stand-in, or a prefab built before the bodies were
    /// assigned).
    int NextBodyVariant()
    {
        if (_bodies != null && _bodies.Length > 0)
        {
            // Body stepped through, skin rolled: a raid of three should not be three of the same
            // model, and two of the same model should not be wearing the same shirt.
            _variantCursor = _variantCursor < 0
                ? Random.Range(0, _bodies.Length)
                : (_variantCursor + 1) % _bodies.Length;

            var body = _bodies[_variantCursor];
            int skins = body != null && body.Skins != null ? body.Skins.Length : 0;

            // The skin is rolled off this raider's own generator, not Unity's global one.
            // GameEventManager seeds the global generator so every peer lays the shift's weapons
            // out identically, and a seeded sequence read from several raiders in the same frame
            // handed three of them the same shirt. Hashing this raider's own id cannot correlate
            // that way — and unlike seeding System.Random with it, does not merely trade the
            // repeat for a tidy ramp of shirts in spawn order. See Hash01.
            int skin = 0;
            if (skins > 0)
            {
                skin = Mathf.Min((int)(Hash01(GetInstanceID(), SkinSalt) * skins), skins - 1);
            }

            return _variantCursor * SkinsPerBody + skin;
        }

        // No body table: fall back to the old behaviour of recolouring whatever body the prefab
        // already carries.
        int count = _bodyVariants != null ? _bodyVariants.Length : 0;
        if (count == 0) return -1;

        _variantCursor = _variantCursor < 0
            ? Random.Range(0, count)
            : (_variantCursor + 1) % count;

        return _variantCursor;
    }

    void ApplyBodyVariant(int variant)
    {
        if (variant < 0) return;
        if (_bodyVariant == variant) return;   // the replicated value re-arrives on every join

        if (_bodies != null && _bodies.Length > 0)
        {
            _bodyVariant = variant;
            ApplyBodyOption(variant / SkinsPerBody, variant % SkinsPerBody);
            return;
        }

        if (_bodyRenderers == null || _bodyVariants == null) return;
        if (variant >= _bodyVariants.Length) return;

        var only = _bodyVariants[variant];
        if (only == null) return;

        _bodyVariant = variant;
        ApplySkin(only);
    }

    /// Swaps the whole model, not just its texture.
    ///
    /// The body is rebuilt rather than toggled between pre-placed children: the pack's skeletons
    /// share their bone names, so two of them under one Animator would leave it binding to
    /// whichever it happened to walk into first. One body in the hierarchy at a time is the only
    /// version of this that cannot go quietly wrong.
    void ApplyBodyOption(int bodyIndex, int skinIndex)
    {
        if (_bodies == null || bodyIndex < 0 || bodyIndex >= _bodies.Length) return;

        var option = _bodies[bodyIndex];
        if (option == null || option.Prefab == null) return;

        // Out with the old body. The carry anchor and its grip points are ours, not the body's,
        // and the raider would lose his hold on a box with them.
        //
        // Unparented before being destroyed, and that order matters: Destroy is deferred to the
        // end of the frame, so without the detach the old skeleton is still hanging under the
        // raider when Rebind runs below. Both skeletons use the same bone names, the animator
        // binds to whichever it reaches first, and when the old one is finally destroyed it is
        // left holding dead transforms — humanoid, animating, and GetBoneTransform returning null
        // for every bone. That takes the hand IK and the flinch down with it.
        var doomed = new List<Transform>();
        foreach (Transform child in transform)
            if (child != _carryAnchor) doomed.Add(child);

        foreach (var child in doomed)
        {
            child.SetParent(null, false);
            Destroy(child.gameObject);
        }

        var spawned = Instantiate(option.Prefab, transform.position, transform.rotation);
        var moved   = new List<Transform>();
        foreach (Transform child in spawned.transform) moved.Add(child);
        foreach (var child in moved) child.SetParent(transform, false);
        Destroy(spawned);

        if (_animator != null)
        {
            if (option.Avatar != null) _animator.avatar = option.Avatar;

            // Rebind, or the animator keeps writing to the bones of the body just destroyed.
            _animator.Rebind();
            _animator.Update(0f);
        }

        _bodyRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);

        if (option.Skins != null && option.Skins.Length > 0)
            ApplySkin(option.Skins[Mathf.Clamp(skinIndex, 0, option.Skins.Length - 1)]);

        // Both of these cached bone transforms of the body that has just been thrown away.
        GetComponent<HitReaction>()?.Rebind();
        CarryIK.Ensure(_animator);

        // Rebind above put the state machine back to the start of its default state, so whatever
        // phase this raider had is gone and a group that swapped bodies together is in step again.
        DesyncGait();
    }

    /// sharedMaterial, not material: the latter would instance a copy per renderer per raider —
    /// eleven throwaway materials each — and leak them when he despawns.
    void ApplySkin(Material skin)
    {
        if (_bodyRenderers == null || skin == null) return;

        foreach (var smr in _bodyRenderers)
            if (smr != null) smr.sharedMaterial = skin;
    }

    public void ApplyRemoteWeapon(MeleeWeaponKind weapon)
    {
        if (_weapon == weapon && (_weaponVisual != null || weapon == MeleeWeaponKind.Fists)) return;

        if (_weaponVisual != null) { Destroy(_weaponVisual); _weaponVisual = null; }
        EquipWeapon(weapon);
    }

    public void ApplyRemoteLoot(string partId)
    {
        if (string.IsNullOrEmpty(partId)) { DestroyLootVisual(); return; }

        var part = PartRegistry.Get(partId);
        if (part != null) BuildLootVisual(part);
    }

    public void ApplyRemoteDown(Vector3 fallDirection, bool heavy)
    {
        _state = State.Down;
        DownVisuals(fallDirection, heavy);
    }

    /// Mirrors the server's carry on a client: the box follows his shoulder the same way a
    /// box on a cart follows the cart, which is re-derived locally every frame and therefore
    /// needs no NetworkObject re-parenting.
    public void ApplyRemoteCarry(ulong boxNetId, bool carrying)
    {
        var box = BoxPhysicsInteractable.FindByNetId(boxNetId);
        if (box == null) return;

        SetCarryPose(carrying);
        if (!carrying) { box.EndFollow(); return; }

        var rb = box.GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }

        box.BeginFollow(_carryAnchor, Vector3.zero, Quaternion.identity);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// An agent that ends up off the navmesh silently ignores every SetDestination and stands
    /// there for the rest of its life. Knockback during a stun is the usual way it happens, so
    /// put him back on the mesh instead of letting him become scenery.
    void KeepOnNavMesh()
    {
        if (_agent == null || !_agent.enabled || _agent.isOnNavMesh) return;

        if (NavMesh.SamplePosition(transform.position, out var hit, 5f, NavMesh.AllAreas))
            _agent.Warp(hit.position);
    }

    // NavMeshAgent throws if isStopped is touched while the agent is off-mesh or disabled.
    void StopAgent(bool stopped)
    {
        if (!_agent.enabled || !_agent.isOnNavMesh) return;
        _agent.isStopped = stopped;
    }

    void SetDestination(Vector3 pos)
    {
        if (!_agent.enabled || !_agent.isOnNavMesh) return;
        if (NavMesh.SamplePosition(pos, out var hit, 4f, NavMesh.AllAreas))
            _agent.SetDestination(hit.position);
    }

    void FaceTowards(Vector3 pos)
    {
        Vector3 dir = pos - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) return;
        transform.rotation = Quaternion.Slerp(
            transform.rotation, Quaternion.LookRotation(dir), 6f * Time.deltaTime);
    }

    /// Puts the arms on the box without taking the legs off the run.
    ///
    /// Two halves. The masked UpperBody layer holds the arms-forward pose, which is the shape of
    /// a man carrying something and costs the base layer nothing — the legs go on running
    /// underneath it. Then IK pins the wrists to two points on the box itself, so the hands land
    /// on the thing rather than merely near it, whatever size that box happens to be.
    ///
    /// The pose alone was not enough: box widths differ, and hands floating a hand's breadth off
    /// the cardboard is exactly the kind of thing that reads as wrong without being nameable.
    void SetCarryPose(bool carrying)
    {
        if (_animator == null) return;

        _animator.SetBool(CarryingHash, carrying);

        if (_upperBodyLayer >= 0 && _upperBodyLayer < _animator.layerCount)
            _animator.SetLayerWeight(_upperBodyLayer, carrying ? 1f : 0f);

        var ik = CarryIK.Ensure(_animator);
        if (ik == null) return;

        if (!carrying) { ik.Clear(); return; }

        EnsureGripPoints();
        ik.SetTargets(_rightGrip, _leftGrip);
    }

    /// Where the hands go: either side of the carried box, at the anchor it rides on.
    ///
    /// Parented to the anchor rather than to the box, and deliberately. The box is glued to the
    /// anchor in LateUpdate, while the IK pass reads its goals before that — targets on the box
    /// would be a frame behind the body and the hands would visibly chase it. The anchor is a
    /// child of the raider himself and is therefore already where it belongs when the solver
    /// looks, which is the same trap PlayerHandIK's refresh callback exists to get around.
    void EnsureGripPoints()
    {
        if (_carryAnchor == null) return;

        _rightGrip ??= MakeGrip("GripRight");
        _leftGrip  ??= MakeGrip("GripLeft");

        // Re-measured on every pickup rather than once: a raider carries several boxes over a
        // shift, and a small crate is not held as wide as a big one. Sizing this at creation
        // meant every later box was held at the first one's width.
        float half = 0.22f;
        if (_carried != null)
        {
            var rend = _carried.GetComponentInChildren<Renderer>();
            if (rend != null) half = Mathf.Clamp(rend.bounds.extents.x, 0.12f, 0.45f);
        }

        _rightGrip.localPosition = new Vector3( half, 0f, 0f);
        _leftGrip.localPosition  = new Vector3(-half, 0f, 0f);
    }

    Transform MakeGrip(string name)
    {
        var t = new GameObject(name).transform;
        t.SetParent(_carryAnchor, false);

        // Palms facing inward, onto the box, rather than however the hand happened to be posed.
        t.localRotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        return t;
    }

    /// Whether the animator on this body actually knows a parameter — the raider prefab can
    /// be anything from the zombie model to a capsule with no controller at all.
    bool HasParameter(int hash)
    {
        if (_animator == null || _animator.runtimeAnimatorController == null) return false;

        foreach (var p in _animator.parameters)
            if (p.nameHash == hash) return true;

        return false;
    }

    /// The swing animation, played on the server and mirrored on every client.
    /// Swings, and tells the rest of the session he swung.
    ///
    /// The two halves were written out by hand at every call site, which held for as long as every
    /// swing came from the FSM. It stopped holding the moment something outside drove him: the
    /// gate breach beats on a shutter for twenty seconds through a raider whose controller is
    /// switched off, and it poked the animator directly — so on every machine but the server, a
    /// crowd stood at the gate perfectly still while it shook.
    public void Swing(int variant)
    {
        PlayAttack(variant);
        _net?.BroadcastSwing(variant);
    }

    public void PlayAttack(int variant)
    {
        if (_state == State.Down) return;
        if (_animator == null || _animator.runtimeAnimatorController == null) return;
        _animator.SetInteger(AttackVarHash, Mathf.Clamp(variant, 0, AttackVariants - 1));
        _animator.SetTrigger(AttackHash);

        // Both of these run on clients too (NetworkThief mirrors the swing), so the roar is
        // heard by everyone, not only the host.
        if (_voice != null) _voice.Attack();
    }

    /// The stagger, same deal — it is what makes a landed player hit read as landed.
    /// The thud of his fist landing on somebody. Sound only — the stagger belongs to whoever
    /// got hit, not to him.
    public void PlayImpact()
    {
        if (_state == State.Down) return;
        if (_voice != null) _voice.Hit();
    }

    public void PlayDamage(Vector3 fromDirection = default)
    {
        if (_state == State.Down) return;

        if (_voice != null) _voice.Hit();

        // The flinch runs alongside the clip rather than instead of it, so a blow still shows
        // even when the stagger cannot start — mid-swing, or on a raider already staggering
        // from the hit before.
        HitReaction.Ensure(gameObject).Play(fromDirection, 1f);

        if (_animator == null || _animator.runtimeAnimatorController == null) return;
        _animator.SetTrigger(DamageHash);
    }

    /// How fast this raider ought to be moving right now.
    ///
    /// The role decides the pace and the pace decides the animation, rather than the other way
    /// round. One on his way to wreck a pallet runs at it; one who has turned on a player shambles,
    /// because being able to back away from that is what makes it a different threat.
    float GaitSpeed
    {
        get
        {
            if (_state == State.Fleeing)  return HasLoot ? _lootFleeSpeed : _fleeSpeed;
            if (_state == State.Fighting || _state == State.Skittering) return _shambleSpeed;

            return _runSpeed;
        }
    }

    /// Which clip that speed should look like: 0 shamble, 1 run.
    ///
    /// Derived from the speed rather than from the state, so the two can never disagree. That
    /// matters because they otherwise would: a raider fleeing with a box moves at 1.2 m/s on
    /// purpose — slow enough to be caught, which is the point of the theft — and putting a 4.2 m/s
    /// sprint clip on that would have him skating out of the building.
    ///
    /// The ends are the strides themselves, pulled in a little so each clip is used pure over most
    /// of its own range instead of being permanently half-blended with the other.
    float GaitBlend(float speed) => GaitBlendFor(speed, _shambleClipStride, _runClipStride);

    static float GaitBlendFor(float speed, float shambleStride, float runStride)
    {
        float lo = shambleStride * 1.3f;
        float hi = runStride * 0.55f;

        return hi <= lo ? 0f : Mathf.Clamp01(Mathf.InverseLerp(lo, hi, speed));
    }

    /// Drives a raider's animator from a speed, for anything moving him by hand.
    ///
    /// The gate breach walks its raiders in and out itself — there is no navmesh outside — and it
    /// was setting MotionSpeed against a single hardcoded stride. With two gaits in the tree that
    /// is no longer a number anyone can write down: it depends on which clip the blend has landed
    /// on. Sharing the calculation is what stops a raider shambling in at a run's pace.
    public static void DriveLocomotion(Animator anim, float speed,
                                       float shambleStride = 0.70f, float runStride = 4.2f,
                                       float minPlayback = 0.7f, float maxPlayback = 1.6f)
    {
        if (anim == null || anim.runtimeAnimatorController == null) return;

        float gait   = GaitBlendFor(speed, shambleStride, runStride);
        float stride = Mathf.Lerp(shambleStride, runStride, gait);

        anim.SetFloat(SpeedHash, speed);
        anim.SetFloat(GaitHash, gait);
        anim.SetFloat(MotionSpeedHash,
                      stride <= 0.01f ? 1f : Mathf.Clamp(speed / stride, minPlayback, maxPlayback));
        anim.SetBool(GroundedHash, true);
    }

    // Smoothed speed the animator is driven from; see DriveAnimator.
    float _animSpeed;

    void DriveAnimator()
    {
        if (_animator == null) return;

        bool  alive    = _state != State.Down && _agent.enabled;
        float speed    = alive ? _agent.velocity.magnitude : 0f;
        _animSpeed     = Mathf.Lerp(_animSpeed, speed, 10f * Time.deltaTime);

        // Which clip is picked from the pace he is set to keep, not from how fast the agent
        // happens to be going this frame. In a crowd the agent's velocity never holds still —
        // avoidance, corners, the one in front — and the run only fully takes over at 2.31 m/s.
        // With the run at 2.6 and pace varying by 14%, the slower raiders sat right on that line
        // and every wobble in velocity swung the blend between an upright run and a hunched
        // shamble: the torso jerking back and forth.
        float gait = alive ? GaitBlend(_agent.speed) : 0f;

        _animator.SetFloat(SpeedHash, _animSpeed, 0.1f, Time.deltaTime);

        // Eased rather than snapped: swapping clip on the frame he notices a player would pop.
        _animator.SetFloat(GaitHash, gait, 0.18f, Time.deltaTime);

        _animator.SetFloat(MotionSpeedHash, PlaybackFor(_animSpeed, gait));
        _animator.SetBool(GroundedHash, true);

        _net?.PublishGait(gait);
    }

    /// Sets the gait clients show for a raider the server is moving by hand (the gate walk-in),
    /// where DriveAnimator is not running to publish it.
    public void PublishGaitForSpeed(float speed) => _net?.PublishGait(GaitBlend(speed));

    float _remoteGait;

    /// The gait the server picked, for a client's copy — which has no agent to derive it from.
    public void ApplyRemoteGait(float gait) => _remoteGait = gait;

    /// Drops him into a random point in the stride instead of the start of it.
    ///
    /// Everything in an animator starts a state at normalized time zero, so a group spawned or
    /// re-bound in the same frame plants every left foot on the same frame for as long as they
    /// keep the same speed — synchronised swimming rather than a crowd.
    ///
    /// Worth calling again after anything that resets the state machine. Animator.Rebind does
    /// exactly that, which is why a body swap re-syncs a raider who had already been offset.
    ///
    /// Cosmetic and unsynchronised on purpose: each peer rolls its own phase. Nothing in the game
    /// reads it, and a byte on the wire per raider to make two machines agree about which foot is
    /// forward would be a byte wasted.
    public void DesyncGait()
    {
        if (_animator == null || !_animator.isInitialized) return;

        int hash = _animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
        _animator.Play(hash, 0, Hash01(GetInstanceID(), GaitSalt));
    }

    /// Playback multiplier for the locomotion states, driven through the MotionSpeed parameter
    /// rather than Animator.speed so it never touches the attack, damage or death clips.
    ///
    /// The empty-handed tree has no walk clip in it any more — a raider runs or stands, nothing
    /// in between — so the cadence can no longer come from blending down to a shorter stride.
    /// It comes from here instead: the run is played at whatever fraction of its own measured
    /// stride the body is actually covering, which is what keeps a planted foot planted whether
    /// he is edging round a corner or charging across the floor.
    ///
    /// The floor matters as much as the ceiling. Scaled all the way down the run turns into
    /// slow motion, which reads worse than a little foot slide; scaled all the way up it stops
    /// reading as a run at all.
    float PlaybackFor(float speed, float gait)
    {
        // Against the stride of what is actually on screen, which during a blend is a mix of both.
        // One constant cannot serve them: the run depicts about 4.2 m/s and the shamble 0.70, and
        // using the wrong one is a planted foot against a skating one by a factor of six.
        float stride = Mathf.Lerp(_shambleClipStride, _runClipStride, gait);

        if (stride <= 0.01f) stride = _animTopSpeed;
        if (stride <= 0.01f) return 1f;

        return Mathf.Clamp(speed / stride, _animMinPlayback, _animMaxPlayback);
    }

    float NearestPlayerDistance()
    {
        float nearest = 999f;
        foreach (var pos in CollectPlayerPositions())
            nearest = Mathf.Min(nearest, FlatDistance(pos, transform.position));
        return nearest;
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = b.y;
        return Vector3.Distance(a, b);
    }

    static List<Vector3> CollectPlayerPositions()
    {
        var result = new List<Vector3>(4);

        var local = PlayerFinder.Find();
        if (local != null) result.Add(local.position);

        foreach (var np in NetworkPlayer.All)
            if (np != null) result.Add(np.transform.position);

        return result;
    }

    // Role colours for the minimap dots. There is deliberately no in-world marker any more:
    // a raider used to carry a floating pip that named his job before he had done anything,
    // which meant the fight had no reading left to do. What he is after now shows in what he
    // walks at, and how he takes being hit shows in the fight itself.
    public static Color MarkerColorFor(ThiefRole role) => role switch
    {
        ThiefRole.Looter    => new Color(0.95f, 0.55f, 0.10f),   // orange — carries goods off
        ThiefRole.PartThief => new Color(0.85f, 0.20f, 0.85f),   // magenta — after the bench
        ThiefRole.Saboteur  => new Color(0.95f, 0.85f, 0.15f),   // yellow — after the power
        _                   => new Color(0.90f, 0.15f, 0.12f),   // red — plain vandal
    };

}
