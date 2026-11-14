using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// Portable warehouse ladder. Player carries it with IK hand targets, then presses E to lean it
/// against a wall. The lean is real physics: an unconstrained Rigidbody gets a push away from the
/// player plus gravity — ordinary collision decides where it actually rests (a wall, a shelf, or
/// straight down to the floor if nothing is there), no manual angle sweep, no joint. The Rigidbody
/// is kinematic except during that brief fall.
/// Once leaning, E climbs it (W/S up/down, E to get off) instead of picking it back up.
///
/// States: Idle → Carried → Leaning ⇄ Climbing
///
/// Multiplayer: stable NetLadderId derived from deterministic scene order (see SceneNetIds),
/// since Ladder.prefab has no NetworkObject. Pickup and climb-entry are both server-arbitrated via
/// NetworkLadderManager (first-come-first-served); the winning client runs the exact same local
/// Pickup()/PlaceLadder()/StartClimb() path as single-player (IK, speed penalty, notifications).
/// Other clients only ever see the *visual* result — ladder parented under the carrier's
/// NetworkPlayer with remote hand IK via CharacterVisualIK, or unparented + leaning once placed,
/// or marked occupied while someone else climbs (their own player position already replicates
/// through the normal PlayerPositionBridge, so climbing needs no per-frame position RPC) — driven
/// by ApplyRemoteCarry / ApplyRemotePlace / ApplyRemoteClimbEnter / ApplyRemoteClimbExit.
/// Single-player (no NetworkManager listening) is completely unaffected.
public class LadderController : MonoBehaviour, IInteractable, ICanInteractCheck
{
    [Header("IK Targets (children of this GO)")]
    [SerializeField] Transform _rightHandTarget;
    [SerializeField] Transform _leftHandTarget;

    [Header("Ladder")]
    [Tooltip("Usable height of the ladder — used to place top-contact check sphere.")]
    [SerializeField] float _ladderHeight = 6.9f;

    [Header("Lean (real physics: free Rigidbody + gravity, no joint)")]
    [Tooltip("Angular impulse (kg·m²/s) rolling the ladder away from the player at the moment it's " +
             "placed — a ladder balanced exactly vertical has ~zero net torque and won't start " +
             "falling on its own, so it needs a real kick to get going. Pure torque, not a force at " +
             "a point: with no joint holding the base still, pushing at a point would also fling the " +
             "whole body across the room instead of just tipping it over.")]
    [SerializeField] float _leanPushForce = 18f;
    [Tooltip("Rigidbody mass while falling/leaning.")]
    [SerializeField] float _leanMass = 8f;
    [Tooltip("Angular drag while falling — higher settles faster with less wobble, but too high " +
             "smothers the initial kick before gravity builds up real speed.")]
    [SerializeField] float _leanAngularDrag = 0.5f;
    [Tooltip("Angular velocity (rad/s) below which the fall is considered settled.")]
    [SerializeField] float _leanSettleThreshold = 0.06f;
    [Tooltip("Minimum time the fall runs before it's even allowed to be considered 'settled' — " +
             "without this, the settle check can trigger within the first frame or two if the kick " +
             "decays fast, freezing the routine before any real motion happened.")]
    [SerializeField] float _leanMinFallTime = 0.35f;
    [Tooltip("Safety cap so a never-quite-settling fall can't hang the coroutine forever.")]
    [SerializeField] float _leanMaxWaitTime = 4f;

    Rigidbody _rb;

    // Static (non-Rigidbody) colliders the ladder's own collider was found overlapping right at
    // placement (e.g. the base landing flush against a shelf it's about to lean on) — ignored for
    // the duration of the fall, since a standing overlap fights the initial kick with depenetration
    // contact forces before gravity gets a chance to take over. Restored once the fall settles.
    // Deliberately excludes anything with a Rigidbody (crates, pallets, etc.): ignoring those too
    // would let the ladder swing straight through them undetected while ignored, then slam collision
    // back on against a now-deeply-interpenetrated box — that's what flings crates across the room.
    // Static geometry never moves, so restoring collision with it later is always safe.
    readonly List<Collider> _tempIgnoredColliders = new();

    [Header("Carry")]
    [Tooltip("Local offset relative to the carrier's transform root.")]
    [SerializeField] Vector3 _carryLocalOffset   = new Vector3(0.55f, 0.2f, 0.8f);
    [Tooltip("Local Euler rotation while carried. Zero = upright.")]
    [SerializeField] Vector3 _carryLocalRotation = Vector3.zero;
    [Tooltip("Gap left between the carried ladder volume and static warehouse geometry.")]
    [SerializeField] float _carryCollisionSkin = 0.04f;
    readonly RaycastHit[] _carrySweepHits = new RaycastHit[32];

    [Header("Climb")]
    [Tooltip("Vertical climb speed, meters/second.")]
    [SerializeField] float _climbSpeed = 2.2f;
    [Tooltip("Headroom left below the ladder's resting contact point so the climber's head " +
             "doesn't clip into the shelf/wall the top rests against.")]
    [SerializeField] float _climbTopMargin = 0.9f;
    [Tooltip("How far in front of the ladder's central axis the climber stands (rung depth). " +
             "0.3 was too close and let the rails clip through the character's torso.")]
    [SerializeField] float _climbStandoff = 0.5f;
    [Tooltip("Sideways correction (along the ladder's right axis) so the climber lines up between " +
             "the two rails — the model's rail spread isn't centered on the transform's own pivot.")]
    [SerializeField] float _climbLateralOffset = 0.33f;

    [Header("Climb IK (partial pull toward real rungs)")]
    [Tooltip("Vertical spacing between rungs on this ladder model, measured from the mesh (not " +
             "the generic mocap clip, which was authored for a different ladder's proportions).")]
    [SerializeField] float _rungPitch = 0.388f;
    [Tooltip("Local X (ladder space) of the left and right rails, measured from the mesh.")]
    [SerializeField] float _leftRailLocalX  = 0.05f;
    [SerializeField] float _rightRailLocalX = 0.65f;
    [Tooltip("How far apart the feet stand on a rung, either side of center.")]
    [SerializeField] float _footStance = 0.12f;
    [Tooltip("Height of the gripping hands relative to the root/climbHeight — climbHeight tracks " +
             "the root at foot level, so hands reach up from there.")]
    [SerializeField] float _handHeightOffset = 1.2f;
    [Tooltip("Height of the standing feet relative to the root/climbHeight (root already sits at " +
             "foot level, so this is close to zero).")]
    [SerializeField] float _footHeightOffset = 0f;
    [Tooltip("Rungs of vertical gap kept between the two hands at all times (right hand always " +
             "this many rungs above the left) — and between the two feet.")]
    [SerializeField] int _handRungGap = 1;
    [SerializeField] int _footRungGap = 1;
    [Tooltip("Fraction of the gap to the next rung, right at the end, where the limb quickly steps " +
             "up to it. Below that it holds steady on the current rung.")]
    [Range(0.05f, 0.9f)]
    [SerializeField] float _rungStepZone = 0.3f;
    [Tooltip("IK influence, 0-1. Full 1.0 rigidly locks limbs onto the rung and can bend joints " +
             "unnaturally when the clip's own pose disagrees — this is a partial pull instead, " +
             "nudging hands/feet toward the rungs while the animation still drives most of the pose.")]
    [Range(0f, 1f)]
    [SerializeField] float _climbIKWeight = 0.1f;

    Transform _rightHandIK, _leftHandIK, _rightFootIK, _leftFootIK;

    // ── State ─────────────────────────────────────────────────────────────────

    enum State { Idle, Carried, Leaning, Climbing }
    [Tooltip("Starting/current state. Set to Leaning (with the transform pre-posed against a " +
             "wall/shelf) to have a ladder start the scene already placed, ready to climb.")]
    [SerializeField] State _state = State.Idle;
    GameObject _carrier;
    Collider   _col;
    int        _originalColliderLayer;
    Coroutine  _leanRoutine;

    GameObject _climber;
    StarterAssets.ThirdPersonController _climberController;
    Animator   _climberAnimator;
    float _climbHeight;
    GameObject _pendingClimber;
    // Frame StartClimb() ran on — E's wasPressedThisFrame stays true for the whole Unity frame
    // regardless of script execution order, so without this guard, if this script's own
    // Update() happens to run AFTER PlayerInteraction's in the same frame, UpdateClimb() would
    // see _climber already set and the same E-press as "still pressed", firing EndClimb()
    // immediately on entry.
    int _climbStartedFrame = -1;

    // Multiplayer, observer clients only: the remote climber's NetworkPlayer root, while set,
    // drives UpdateClimbIK() every frame using a height derived from their synced transform
    // (PlayerPositionBridge / ClientNetworkTransform) instead of a local _climbHeight -- there is
    // no per-frame climb-height RPC, so this is the only signal observers have of how high they are.
    Transform _remoteClimber;
    Animator _remoteClimberAnimator;

    static readonly int IsClimbingHash = Animator.StringToHash("IsClimbing");
    static readonly int ClimbSpeedHash = Animator.StringToHash("ClimbSpeed");

    public static bool IsClimbing { get; private set; }

    // Multiplayer: true on every client EXCEPT the actual carrier/placer — this copy is being
    // puppeted by NetworkLadderManager ClientRpcs (ApplyRemoteCarry/ApplyRemotePlace) purely for
    // visualization. Blocks local Update()/Interact() from acting on stale local fields (_carrier
    // is null on these copies).
    bool _remoteControlled;

    // Multiplayer, observer clients only: true while ApplyRemoteCarry is the authoritative reason
    // this copy is remote-controlled. A second player can carry the ladder while a first is still
    // climbing it (see PickupSecondary); without this flag, ApplyRemoteClimbExit firing when that
    // climber steps off would clobber _state back to Leaning even though it's still being carried.
    bool _remoteCarried;

    // Multiplayer: actor waiting on server grab confirmation (set in Interact, consumed in
    // ExecuteConfirmedPickup). Only ever set on the requesting client.
    GameObject _pendingActor;

    public static bool IsCarried { get; private set; }

    // Exposed for local IK (PlayerHandIK) and remote IK (CharacterVisualIK via NetworkLadderManager)
    public Transform RightHandTarget => _rightHandTarget;
    public Transform LeftHandTarget  => _leftHandTarget;

    // ── Multiplayer: stable per-ladder integer ID ─────────────────────────────────

    public int NetLadderId { get; private set; }

    static readonly Dictionary<int, LadderController> s_byNetId = new();

    // Numbered from scene order, not Awake order — see SceneNetIds for why the latter could
    // (and did) hand the same id to different ladders on the host and on a client. Runs after
    // every Awake but before the first FixedUpdate, so ladders are still at their authored
    // positions rather than wherever gravity has since settled them independently per client.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void InstallNetworkIdPass() => SceneNetIds.RunEveryLoad(AssignNetworkIds);

    // Idempotent by construction -- s_byNetId is cleared first -- because SceneNetIds.RunEveryLoad
    // calls this again after every scene load, including the one NGO performs on a joining client.
    static void AssignNetworkIds()
    {
        s_byNetId.Clear();
        var all = new List<LadderController>(
            FindObjectsByType<LadderController>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        SceneNetIds.SortDeterministically(all);
        for (int i = 0; i < all.Count; i++)
        {
            all[i].NetLadderId = i;
            s_byNetId[i]       = all[i];
        }
    }

    /// Find a ladder by its stable network ID. Returns null if not found.
    public static LadderController FindByNetId(int id)
    {
        s_byNetId.TryGetValue(id, out var ladder);
        return ladder;
    }

    /// Whether a specific actor is currently carrying or climbing ANY ladder — deliberately scoped
    /// per-actor rather than the global IsCarried/IsClimbing flags, which are true the instant
    /// ANYONE anywhere touches a ladder and would otherwise freeze every OTHER player's whole
    /// interaction system too (that's what blocked a second player from ever reaching in and
    /// grabbing a ladder someone else was climbing).
    public static bool IsActorBusyWithLadder(GameObject actor)
    {
        foreach (var ladder in s_byNetId.Values)
            if (ladder._carrier == actor || ladder._climber == actor) return true;
        return false;
    }

    /// Whether a specific actor is the one currently CARRYING (not climbing) a ladder — narrower
    /// than IsActorBusyWithLadder, for callers that only care about the carry-speed penalty and
    /// shouldn't apply it to a player who's climbing instead.
    public static bool IsActorCarryingLadder(GameObject actor)
    {
        foreach (var ladder in s_byNetId.Values)
            if (ladder._carrier == actor) return true;
        return false;
    }

    /// Restricts an actor's requested movement so the disabled carried-ladder collider cannot
    /// pass through static warehouse geometry. CharacterController only sweeps its own capsule;
    /// this second sweep accounts for the ladder's much larger volume without enabling a solid
    /// kinematic collider that pushes the camera in and still cannot move the carrier back.
    public static Vector3 ConstrainCarriedMotion(GameObject actor, Vector3 motion)
    {
        if (actor == null) return motion;

        foreach (var ladder in s_byNetId.Values)
            if (ladder != null && ladder._carrier == actor)
                return ladder.ConstrainCarryMotion(motion);

        return motion;
    }

    Vector3 ConstrainCarryMotion(Vector3 motion)
    {
        Vector3 vertical   = Vector3.Project(motion, Vector3.up);
        Vector3 horizontal = motion - vertical;
        float distance = horizontal.magnitude;
        var box = _col as BoxCollider;
        if (distance <= 0.0001f || box == null) return motion;

        Vector3 scale = box.transform.lossyScale;
        scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));

        Vector3 halfExtents = Vector3.Scale(box.size * 0.5f, scale);
        // A slightly shrunken query avoids reading the floor the ladder is carried just above as
        // a sideways obstacle. The cast distance retains the same skin, so walls still stop the
        // visual before it enters them.
        float skin = Mathf.Max(0.005f, _carryCollisionSkin);
        halfExtents = new Vector3(
            Mathf.Max(0.01f, halfExtents.x - skin),
            Mathf.Max(0.01f, halfExtents.y - skin),
            Mathf.Max(0.01f, halfExtents.z - skin));

        Vector3 direction = horizontal / distance;
        Vector3 center    = box.transform.TransformPoint(box.center);
        int hitCount = Physics.BoxCastNonAlloc(
            center,
            halfExtents,
            direction,
            _carrySweepHits,
            box.transform.rotation,
            distance + skin,
            Physics.AllLayers,
            QueryTriggerInteraction.Ignore);

        float allowed = distance;
        for (int i = 0; i < hitCount; i++)
        {
            var hit   = _carrySweepHits[i];
            var other = hit.collider;
            if (other == null || other == _col) continue;
            if (other.transform == transform || other.transform.IsChildOf(transform)) continue;
            if (_carrier != null &&
                (other.transform == _carrier.transform || other.transform.IsChildOf(_carrier.transform)))
                continue;

            // Static shelves, walls and structural geometry block the carry. Dynamic boxes and
            // pallets remain governed by their own physics instead of making the carrier stick.
            if (other.attachedRigidbody != null) continue;
            // Horizontal movement while the ladder's bottom sits near the floor can report the
            // floor seam as a zero-distance hit. Only side-facing contacts constrain walking.
            if (Mathf.Abs(hit.normal.y) > 0.75f) continue;

            allowed = Mathf.Min(allowed, Mathf.Max(0f, hit.distance - skin));
        }

        return direction * allowed + vertical;
    }

    // ── IInteractable ─────────────────────────────────────────────────────────

    // E always climbs (only meaningful while Leaning); G always carries (PickupSecondary,
    // wired up in PlayerInteraction's G-key handler) — the two never share a button.
    public string InteractPrompt
    {
        get
        {
            switch (_state)
            {
                case State.Carried:  return $"{InputHints.E} Set down ladder";
                case State.Leaning:  return $"{InputHints.EK} Climb ladder  {InputHints.GK} Pick up";
                case State.Climbing: return $"{InputHints.E} Get off ladder";
                default:             return $"{InputHints.GK} Pick up ladder";
            }
        }
    }

    public bool CanInteract(GameObject interactor)
    {
        // While carried, only the carrier can interact (to prevent mid-lean grabs by others). On an
        // observer's copy _carrier is always null (see ApplyRemoteCarry), so this correctly blocks
        // everyone but the real carrier without needing a separate _remoteControlled check — which
        // used to short-circuit here unconditionally and, since it's ALSO true for a remotely-
        // climbed (not carried) ladder, blocked the whole co-op "grab it out from under the
        // climber" pickup this method's own comment below describes.
        if (_state == State.Carried) return interactor == _carrier;
        // Climbing does NOT block interaction — a second player can still grab (G) the ladder out
        // from under whoever's climbing it, carrying them along for the ride (see PickupSecondary /
        // UpdateClimb). Starting a NEW climb (E) while already climbed is refused separately, by
        // Interact()'s own State.Leaning check below.
        return true;
    }

    /// E — only ever climbs, and only while Leaning. Picking the ladder up (Idle or Leaning) goes
    /// through PickupSecondary (G key) instead — see PlayerInteraction's dropPressed handler.
    public void Interact(GameObject actor)
    {
        if (_remoteControlled) return;
        if (_state != State.Leaning) return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            _pendingClimber = actor;
            NetworkLadderManager.Instance?.RequestEnterClimbServerRpc(NetLadderId);
            return;
        }

        StartClimb(actor);
    }

    /// G — grabs the ladder to carry it, from Idle (first pickup), Leaning (relocate), or Climbing
    /// (a second player grabs it out from under whoever's climbing — Pickup() deliberately never
    /// touches _climber, so the climb rides along; see the note on the _climber != null check in
    /// Update()).
    /// Called directly by PlayerInteraction when _current is a LadderController and G is pressed.
    public void PickupSecondary(GameObject actor)
    {
        // Same fix as CanInteract(): only block while it's genuinely being carried by someone
        // else. A blanket _remoteControlled check here used to also block grabbing it away from
        // a remote climber, since that flag is true for climbing too.
        if (_state == State.Carried) return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            _pendingActor = actor;
            NetworkLadderManager.Instance?.RequestPickupServerRpc(NetLadderId);
            return;
        }

        Pickup(actor);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        // Lives on the mesh child, not this GameObject — the child's own local space is where the
        // mesh's true (tightly-fitting) bounds are authored, so the collider is sized to match the
        // visible ladder instead of an approximate box guessed in this GameObject's frame.
        _col = GetComponentInChildren<Collider>();

        _rb = GetComponent<Rigidbody>();
        if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
        _rb.mass = _leanMass;
        _rb.angularDamping = _leanAngularDrag;
        // Explicit center of mass, roughly the middle of the ladder's length — auto-computed center
        // of mass can end up close to the base depending on the collider, which would give gravity
        // ~zero torque to work with (it'd just hang there instead of actually falling).
        _rb.automaticCenterOfMass = false;
        _rb.centerOfMass = new Vector3(0f, _ladderHeight * 0.5f, 0f);
        // Continuous so a fast enough swing can't tunnel through the floor collider it's supposed
        // to land flat on.
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        // Project default (50 rad/s) is generous enough that a bad collision response — e.g. a
        // glancing hit against nearby clutter while it's mid-fall — can briefly spin the ladder fast
        // enough to tunnel through the floor in a single step even with Continuous Dynamic on. This
        // is a long, thin object; it never legitimately needs anywhere near that much spin.
        _rb.maxAngularVelocity = 4f;

        // Only kinematic while actually held in hand (see Pickup()) — lying idle, leaning, or being
        // climbed all leave it as a genuine physics object, so it can realistically be bumped,
        // knocked further over, etc. instead of freezing solid the instant it settles.
        _rb.isKinematic = false;
        _rb.useGravity  = true;

    }

    void OnDestroy()
    {
        s_byNetId.Remove(NetLadderId);
    }

    void Start()
    {
        // Pre-ignore collisions with all boxes so the static ladder collider never
        // causes depenetration explosions at scene start (same pattern as pallet truck).
        foreach (var box in FindObjectsByType<BoxPhysicsInteractable>(FindObjectsSortMode.None))
            foreach (var bc in box.GetComponentsInChildren<Collider>(true))
                Physics.IgnoreCollision(_col, bc, true);
    }

    void Update()
    {
        if (_remoteControlled)
        {
            // The observer's side of a climb is driven from LateUpdate now: it has to land after
            // whoever moved the ladder this frame, and the two orders here are undefined.
            // Still drive OUR OWN climb even while this client's copy is remote-controlled by
            // someone ELSE's carry — e.g. player A is climbing while player B (a different actor)
            // picks the ladder up out from under them. Without this, the early return below used
            // to stop PositionClimber() from ever running again the moment the ladder started
            // being remotely carried, leaving the climber visually frozen in place while the
            // ladder (and B) moved away.
            if (_climber != null) UpdateClimb();
            return;
        }

        if (_state == State.Carried)
        {
            var kb = Keyboard.current;
            var gp = Gamepad.current;
            // E / pad interact, matching the E-to-climb interact key. PlayerInteraction's E-handling
            // is fully skipped while IsActorBusyWithLadder(actor) is true, so reusing E here
            // can't double-fire an unrelated interact.
            bool placePressed = GameInputBindings.WasPressed(GameBindingAction.Interact);
            if (placePressed)
            {
                PlaceLadder();
            }
            else
            {
                // Persistent on-screen reminder, shown in its own dedicated HUD element
                // (separate from the look-at interact prompt) for as long as we're carrying.
                // PlaceLadder() hides it again.
                HUDController.Instance?.ShowControlsHint($"{InputHints.EK} set down ladder");
            }
        }

        // Independent of carry state: someone can be climbing while a SECOND player picks the
        // ladder up out from under them — PickupSecondary() deliberately leaves _climber alone,
        // so the climb keeps ticking (and PositionClimber() keeps tracking the ladder's new,
        // carrier-driven transform) right through being carried and re-placed.
        if (_climber != null) UpdateClimb();
    }

    // ── Pickup ────────────────────────────────────────────────────────────────

    void Pickup(GameObject actor)
    {
        if (_leanRoutine != null) { StopCoroutine(_leanRoutine); _leanRoutine = null; }
        RestoreTempIgnoredCollisions();

        // This client's own copy may still be marked remote-controlled from a moment ago, if we
        // just took the ladder away from whoever was carrying or climbing it (PickupSecondary can
        // grab it mid-climb) — that flag was set while we were merely an observer of THEM. Now
        // that we're the actual carrier, clear it; otherwise Update()'s _remoteControlled branch
        // keeps early-returning forever and this client can never press F to place the ladder
        // back down, since it never reaches the State.Carried handling below that branch.
        _remoteControlled = false;
        _remoteCarried    = false;

        // _remoteClimber is deliberately NOT cleared with them. Those two are about who controls
        // this copy of the ladder, and that is now us; the rider is a separate fact and is still
        // true — taking the ladder out from under a climber is what PickupSecondary exists for.
        // Clearing it here is what left the rider unpinned on the carrier's own screen: he stayed
        // where he was and got dragged to the ladder a few frames later, by nothing but network
        // interpolation catching up.

        // It's a real physics object whenever it isn't held (see Awake) — freeze it kinematic the
        // moment it's picked up, whether that's mid-fall or already resting, so carrying it isn't
        // fighting gravity.
        _rb.angularVelocity = Vector3.zero;
        _rb.linearVelocity  = Vector3.zero;
        _rb.isKinematic     = true;
        _rb.useGravity      = false;

        _carrier  = actor;
        _state    = State.Carried;
        IsCarried = true;

        // Attach to carrier
        transform.SetParent(actor.transform);
        transform.localPosition = _carryLocalOffset;
        // Set world rotation explicitly so the ladder stays upright regardless of
        // the actor root's pitch/roll. Only the actor's horizontal heading is inherited.
        Vector3 fwd = Vector3.ProjectOnPlane(actor.transform.forward, Vector3.up);
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        transform.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up)
                           * Quaternion.Euler(_carryLocalRotation);

        // A solid kinematic collider moved as the player's child only pushes the follow camera in;
        // it cannot make CharacterController.Move undo the carrier's motion. Keep it disabled and
        // let ConstrainCarriedMotion sweep this same authored volume before movement instead.
        _col.enabled = false;

        PlayerHandIK.Instance?.SetTargets(_rightHandTarget, _leftHandTarget);

        // Controls are covered by the persistent bottom-of-screen hint (see Update()'s
        // ShowControlsHint call for the Carried state) — no need to repeat them here.
        HUDController.Instance?.AddNotification(
            "Ladder", "Picked up",
            HUDController.NotifType.Success);
    }

    /// Called by NetworkLadderManager.ConfirmPickupClientRpc on the winning client only.
    public void ExecuteConfirmedPickup()
    {
        if (_pendingActor == null) return;
        var actor = _pendingActor;
        _pendingActor = null;
        Pickup(actor);
    }

    /// Server rejected a stale, out-of-range, or already-won pickup request.
    public void RejectPendingPickup()
    {
        _pendingActor = null;
    }

    // ── Climb ─────────────────────────────────────────────────────────────────
    // Only available while Leaning. Movement is driven locally here (W/S), not through the
    // normal ThirdPersonController.Move() — that script is paused (MovementLocked) so gravity
    // and ground movement don't fight the climb, but its LateUpdate camera look keeps running
    // untouched, so the player can still free-look while climbing.

    void StartClimb(GameObject actor)
    {
        _climbStartedFrame = Time.frameCount;

        // Same defensive reset as Pickup() — this client's copy shouldn't still be marked
        // remote-controlled by the time _state == Leaning is reachable here (ApplyRemotePlace /
        // ApplyRemoteClimbExit already clear it in the normal flow), but clearing it explicitly
        // costs nothing and keeps this method symmetric with Pickup()'s guard against the same
        // class of stuck-flag bug.
        _remoteControlled = false;
        _remoteCarried    = false;
        _remoteClimber    = null;

        _climber   = actor;
        _state     = State.Climbing;
        IsClimbing = true;

        // PositionClimber() teleports the climber's CharacterController right against the ladder
        // every frame — with the ladder as a free Rigidbody, that contact pushes back on it each
        // frame too, and the further up the climber gets the more torque that push has, which is
        // what was making the ladder visibly shake near the top. Freeze it solid for the climb;
        // EndClimb() lets it go real again once nobody's standing on it.
        _rb.isKinematic = true;

        // The follow camera's own built-in obstacle avoidance (Cinemachine3rdPersonFollow.
        // CameraCollisionFilter) includes this object's layer (Default) and has no per-object
        // exclusion beyond a single ignored tag already used for the player — so right next to a
        // 7m-tall ladder while climbing, its own body reads as a wall and yanks the camera in.
        // Moving the collider to Ignore Raycast for the duration of the climb keeps it out of that
        // check without touching the tag or any other system; EndClimb() puts it back.
        _originalColliderLayer = _col.gameObject.layer;
        _col.gameObject.layer = LayerMask.NameToLayer("Ignore Raycast");

        _climberController = actor.GetComponent<StarterAssets.ThirdPersonController>();
        if (_climberController != null) _climberController.MovementLocked = true;

        _climberAnimator = actor.GetComponentInChildren<Animator>();
        _climberAnimator?.SetBool(IsClimbingHash, true);
        _climberAnimator?.SetFloat(ClimbSpeedHash, 0f);

        EnsureClimbIKAnchors();
        _climbHeight = 0f;
        PositionClimber();
        UpdateClimbIK();
        PlayerHandIK.Instance?.SetTargets(_rightHandIK, _leftHandIK, _climbIKWeight,
                                          refresh: PositionClimber);
        PlayerHandIK.Instance?.SetFootTargets(_rightFootIK, _leftFootIK, _climbIKWeight);

        // Controls are covered by the persistent bottom-of-screen hint (see UpdateClimb's
        // ShowControlsHint call) — no need to repeat them here.
        HUDController.Instance?.AddNotification(
            "Ladder", "Climbing", HUDController.NotifType.Success);
    }

    /// Called by NetworkLadderManager.ConfirmClimbClientRpc on the winning client only.
    public void ExecuteConfirmedClimb()
    {
        if (_pendingClimber == null) return;
        var actor = _pendingClimber;
        _pendingClimber = null;
        StartClimb(actor);
    }

    /// Server rejected a stale, out-of-range, or occupied climb request.
    public void RejectPendingClimb()
    {
        _pendingClimber = null;
    }

    void UpdateClimb()
    {
        if (_climber == null) { EndClimb(); return; }

        var kb = Keyboard.current;
        var gp = Gamepad.current;

        // wasPressedThisFrame stays true for the whole frame regardless of script execution
        // order, so the same E-press that just started the climb would otherwise also read as
        // the exit press right here on the climb's first frame — guard against that.
        bool exitPressed = Time.frameCount != _climbStartedFrame
                         && GameInputBindings.WasPressed(GameBindingAction.Interact);
        if (exitPressed) { EndClimb(); return; }

        // Persistent on-screen reminder, shown in its own dedicated HUD element
        // (separate from the look-at interact prompt) for as long as we're climbing.
        // EndClimb() hides it again.
        HUDController.Instance?.ShowControlsHint($"W/S — up/down   {InputHints.EK} get off");

        float vInput = 0f;
        if (kb != null) vInput += (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        if (gp != null) vInput = Mathf.Clamp(vInput + gp.leftStick.ReadValue().y, -1f, 1f);

        float maxHeight = Mathf.Max(0f, _ladderHeight * 0.93f - _climbTopMargin);
        float prevHeight = _climbHeight;
        _climbHeight = Mathf.Clamp(_climbHeight + vInput * _climbSpeed * Time.deltaTime, 0f, maxHeight);

        // Drives the Climbing state's own speed multiplier: 0 while idle or pinned at the
        // top/bottom limit (freezes the pose instead of playing), positive climbing up,
        // negative climbing down — which plays the same clip in reverse.
        float moveNorm = _climbSpeed * Time.deltaTime > 0.0001f
            ? (_climbHeight - prevHeight) / (_climbSpeed * Time.deltaTime)
            : 0f;
        _climberAnimator?.SetFloat(ClimbSpeedHash, moveNorm);

        PositionClimber();
        UpdateClimbIK();
    }

    /// Where a body at this height on the rungs stands, in world space. One formula, because the
    /// local climber, a remote one being carried, and UpdateRemoteClimbIK's inverse all have to
    /// agree about it — three copies would drift the moment one of the offsets was retuned.
    Vector3 ClimbSlot(float height) => transform.position
                                     + transform.up      * height
                                     - transform.forward * _climbStandoff
                                     + transform.right   * _climbLateralOffset;

    void PositionClimber()
    {
        if (_climber == null) return;
        _climber.transform.SetPositionAndRotation(ClimbSlot(_climbHeight), transform.rotation);
    }

    /// True while somebody is walking off with this ladder — carried by us, or by a player whose
    /// carry reached us as ApplyRemoteCarry.
    bool IsBeingCarried => _state == State.Carried || _remoteCarried;

    // The climber's pose is written here as well as in Update, and for the same reason a carried
    // box is (see BoxPhysicsInteractable.SyncCarryPose): whoever moves the ladder finishes during
    // Update — a carrier's CharacterController, or ClientNetworkTransform interpolating a remote
    // carrier — and the order between their Update and this one is undefined. A pose written
    // before theirs leaves the rider a frame behind a ladder that is being walked across the
    // warehouse, which reads as hanging in the air and skating after it.
    void LateUpdate()
    {
        if (_climber != null) { PositionClimber(); return; }
        UpdateRemoteClimbIK();
    }


    void EndClimb()
    {
        GameInputBindings.SuppressUntilReleased(GameBindingAction.Interact);
        HUDController.Instance?.HideControlsHint();

        // PositionClimber() matches the climber's rotation to the ladder's tilt every frame while
        // climbing — stand them back upright (yaw only) so they don't stay leaned over after
        // stepping off.
        if (_climber != null)
        {
            float yaw = _climber.transform.eulerAngles.y;
            _climber.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        if (_climberController != null) _climberController.MovementLocked = false;
        _climberAnimator?.SetBool(IsClimbingHash, false);
        _climberAnimator?.SetFloat(ClimbSpeedHash, 0f);
        PlayerHandIK.Instance?.ClearTargets();
        PlayerHandIK.Instance?.ClearFootTargets();

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkLadderManager.Instance?.RequestExitClimbServerRpc(NetLadderId);

        // A second player can pick the ladder up out from under whoever's climbing it (see
        // PickupSecondary) — if that's still going on, the ladder is mid-carry, not free-standing:
        // leave its physics/layer/state alone, that's the carrier's business now, not the exiting
        // climber's. Otherwise it's a normal, uneventful climb dismount.
        if (_state != State.Carried)
        {
            // Real physics object again now that nobody's climbing it — can be bumped/knocked
            // over like normal.
            _rb.isKinematic = false;
            _rb.useGravity  = true;

            // Back on its normal layer so it counts as a wall again for the follow camera and any
            // other layer-based system once nobody's actively climbing it.
            _col.gameObject.layer = _originalColliderLayer;

            _state = State.Leaning;
        }

        _climber           = null;
        _climberController = null;
        _climberAnimator   = null;
        _climbHeight       = 0f;
        IsClimbing         = false;
    }

    // ── Climb IK ──────────────────────────────────────────────────────────────
    // The mocap climb clip was authored for a generic ladder, so its hands/feet don't land on
    // THIS model's actual rungs. Four small anchors are snapped each frame to the nearest real rung
    // (measured from the mesh) and fed to PlayerHandIK at partial weight (_climbIKWeight) — just a
    // nudge toward the rung, not a rigid lock, so joints don't bend unnaturally when the clip's own
    // pose disagrees with the target.

    void EnsureClimbIKAnchors()
    {
        if (_rightHandIK != null) return;
        _rightHandIK = new GameObject("ClimbIK_RightHand").transform;
        _leftHandIK  = new GameObject("ClimbIK_LeftHand").transform;
        _rightFootIK = new GameObject("ClimbIK_RightFoot").transform;
        _leftFootIK  = new GameObject("ClimbIK_LeftFoot").transform;
        foreach (var t in new[] { _rightHandIK, _leftHandIK, _rightFootIK, _leftFootIK })
            t.SetParent(transform, false);
    }

    /// Height of the rung a limb currently holds, for a probe height that includes that limb's
    /// own offset from the root. Holds steady on the lower rung, then quickly steps up to the next
    /// one only in the last _rungStepZone fraction of the gap — real hands/feet grip a rung and
    /// let go, they don't slide continuously up the rail between rungs.
    float RungHoldHeight(float probeHeight)
    {
        float rawIndex  = probeHeight / _rungPitch;
        float lowerRung = Mathf.Floor(rawIndex);
        float frac      = rawIndex - lowerRung;

        float t = Mathf.InverseLerp(1f - _rungStepZone, 1f, frac);
        t = Mathf.SmoothStep(0f, 1f, t);

        return Mathf.Lerp(lowerRung * _rungPitch, (lowerRung + 1f) * _rungPitch, t);
    }

    void UpdateClimbIK()
    {
        if (_rightHandIK == null) return;

        // Right hand/foot always _handRungGap / _footRungGap rungs above their left counterpart —
        // both ends of each pair share the same fractional rung progress (the gap is a whole number
        // of rungs), so they hold and step in lockstep while keeping a constant vertical offset.
        float handLowY  = RungHoldHeight(_climbHeight + _handHeightOffset);
        float handHighY = RungHoldHeight(_climbHeight + _handHeightOffset + _handRungGap * _rungPitch);
        float footLowY  = RungHoldHeight(_climbHeight + _footHeightOffset);
        float footHighY = RungHoldHeight(_climbHeight + _footHeightOffset + _footRungGap * _rungPitch);
        float footCenterX = (_leftRailLocalX + _rightRailLocalX) * 0.5f;

        Quaternion rot = transform.rotation;
        _rightHandIK.SetPositionAndRotation(transform.TransformPoint(new Vector3(_rightRailLocalX, handHighY, 0f)), rot);
        _leftHandIK.SetPositionAndRotation(transform.TransformPoint(new Vector3(_leftRailLocalX, handLowY, 0f)), rot);
        _rightFootIK.SetPositionAndRotation(transform.TransformPoint(new Vector3(footCenterX + _footStance, footHighY, 0f)), rot);
        _leftFootIK.SetPositionAndRotation(transform.TransformPoint(new Vector3(footCenterX - _footStance, footLowY, 0f)), rot);
    }

    // ── Place / Lean ──────────────────────────────────────────────────────────

    void PlaceLadder()
    {
        // The E press that sets the ladder down must not also climb it again after IsCarried clears
        // later in this same frame.
        GameInputBindings.SuppressUntilReleased(GameBindingAction.Interact);
        PlayerHandIK.Instance?.ClearTargets();
        HUDController.Instance?.HideControlsHint();

        var player = _carrier;
        _carrier  = null;
        _state    = State.Leaning;
        IsCarried = false;

        transform.SetParent(null);

        // Direction toward where the player is facing (XZ only)
        Vector3 fwd = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;

        // Place base slightly in front of the player, right where it was being carried.
        Vector3 basePos = player.transform.position + fwd * 0.45f;

        // The TPS camera sits over one shoulder (see TPSCameraController._shoulderOffset),
        // so a point straight ahead of the player's body center lands off to the side of
        // the screen. Shift laterally by the camera's own sideways offset so the ladder
        // appears on the camera's sightline — i.e. where the player actually sees "in front".
        // Clamped to the shoulder rig's own small range (~0.4m) — reading the live camera position
        // uncapped is fragile: if Cinemachine's damping is still catching up to a just-turned player
        // (its Body damping is 0.1-0.3s), cam.position can momentarily sit meters from where the
        // shoulder offset alone would put it, throwing the ladder's base far from the player.
        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 right   = Vector3.Cross(Vector3.up, fwd);
            float   lateral = Vector3.Dot(cam.transform.position - player.transform.position, right);
            lateral = Mathf.Clamp(lateral, -0.6f, 0.6f);
            basePos += right * lateral;
        }

        // Snap base to floor via downward raycast
        int noPlayer = ~LayerMask.GetMask("Player");
        if (Physics.Raycast(basePos + Vector3.up, Vector3.down, out RaycastHit floor, 3f,
            noPlayer, QueryTriggerInteraction.Ignore))
            basePos = floor.point;
        else
            basePos.y = 0f;

        // Orient ladder upright, facing the wall: +Z faces the same way the player was facing.
        var rot = Quaternion.LookRotation(fwd, Vector3.up);
        transform.position = basePos;
        transform.rotation = rot;

        // Re-enable collider so it can actually hit the wall/shelf/floor/whatever's really there
        _col.enabled = true;

        // The collider lives on the mesh child and doesn't necessarily bottom out exactly at this
        // GameObject's own origin (the mesh's true bottom can sit a few centimeters off from it) —
        // nudge the whole object vertically so the collider's actual bottom lands on the floor point
        // just detected above, instead of trusting this object's local (0,0,0) to already be it.
        // Without this the ladder can spawn slightly embedded in the floor, and the very next
        // physics step shoves it back out with a violent depenetration pop.
        Physics.SyncTransforms();
        float bottomOffset = _col.bounds.min.y - basePos.y;
        transform.position -= new Vector3(0f, bottomOffset, 0f);

        // It's placed close enough to lean against a wall/shelf that its own collider can already
        // be overlapping that same static geometry the instant it's enabled (e.g. the base landing
        // flush against a shelf's grill mesh). See IgnoreCurrentOverlaps().
        IgnoreCurrentOverlaps();

        _leanRoutine = StartCoroutine(PhysicsLeanRoutine());
    }

    /// Ignores collision (for the duration of the fall) between the ladder and whatever STATIC
    /// geometry its own collider is already overlapping right at placement — a standing overlap
    /// produces continuous depenetration contact forces that fight the push/gravity before it gets
    /// a chance to build up real falling speed, snapping the ladder back toward vertical instead of
    /// letting it tip over. Deliberately skips anything with a Rigidbody (crates, pallets, other
    /// props): those can move, so ignoring collision with them would let the ladder swing straight
    /// through undetected, then slam collision back on against a now-deeply-interpenetrated box —
    /// that's what flings crates across the room. Static geometry never moves, so it's always safe
    /// to restore collision with it later, and anything it swings into DURING the fall (the wall
    /// it's actually meant to hit) is unaffected since this only touches what it's already touching.
    /// The base is deliberately touching the floor (zero gap) by design — that contact must never
    /// be ignored or the ladder falls straight through it with nothing left to catch the fall. A
    /// real floor can be several stitched/stacked colliders at the seam between tiles (this level's
    /// floor tiles overlap in pairs, sometimes with a thin foundation slab underneath too), so
    /// excluding just the one collider a single raycast happened to hit isn't reliable — instead
    /// this only scans the ladder's UPPER portion for overlaps, entirely skipping the bottom slice
    /// where the floor contact lives. Anything genuinely worth ignoring (a shelf poking into the
    /// ladder's body) sits well above that anyway.
    void IgnoreCurrentOverlaps()
    {
        Physics.SyncTransforms();
        var bounds = _col.bounds;
        const float floorContactMargin = 0.5f;
        float scanBottomY = bounds.min.y + floorContactMargin;
        if (scanBottomY >= bounds.max.y) return; // ladder shorter than the margin -- nothing to scan safely
        Vector3 center = bounds.center;
        center.y = (scanBottomY + bounds.max.y) * 0.5f;
        Vector3 extents = bounds.extents;
        extents.y = (bounds.max.y - scanBottomY) * 0.5f;
        foreach (var hit in Physics.OverlapBox(center, extents, Quaternion.identity,
            ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit == _col || hit.attachedRigidbody != null) continue;
            Physics.IgnoreCollision(_col, hit, true);
            _tempIgnoredColliders.Add(hit);
        }
    }

    /// Restores collisions ignored by IgnoreCurrentOverlaps(), once the fall has settled (or been
    /// interrupted by a pickup) — so the ladder behaves like a normal physics object again.
    void RestoreTempIgnoredCollisions()
    {
        foreach (var other in _tempIgnoredColliders)
            if (other != null) Physics.IgnoreCollision(_col, other, false);
        _tempIgnoredColliders.Clear();
    }

    // ── Lean: real physics ───────────────────────────────────────────────────

    IEnumerator PhysicsLeanRoutine()
    {
        _rb.isKinematic = false;
        _rb.useGravity  = true;

        // Roll the ladder over, away from the player (transform.forward, set in PlaceLadder) — pure
        // torque about the same axis a hinge would've used, so the base stays roughly put (held by
        // its own collision with the ground) instead of the whole body launching sideways.
        _rb.AddTorque(transform.right * _leanPushForce, ForceMode.Impulse);

        bool  isMP           = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        float lastFallUpdate = 0f;

        float elapsed = 0f;
        while (elapsed < _leanMinFallTime
               || (_rb.angularVelocity.sqrMagnitude > _leanSettleThreshold * _leanSettleThreshold
                   && elapsed < _leanMaxWaitTime))
        {
            elapsed += Time.deltaTime;

            // Broadcast the live tumble at ~20 Hz — anyone still climbing this ladder rides
            // PositionClimber()'s ladder-relative offset on their OWN client, so without this
            // they'd stay frozen wherever the carry left off until the single final RPC below
            // fires (up to _leanMaxWaitTime later), then snap straight to the settled pose.
            if (isMP && Time.time - lastFallUpdate > 0.05f)
            {
                lastFallUpdate = Time.time;
                NetworkLadderManager.Instance?.UpdateLadderFallServerRpc(
                    NetLadderId, transform.position, transform.rotation);
            }

            yield return null;
        }

        // Stays a real physics object from here — only Pickup() freezes it kinematic again. It'll
        // hold its resting pose via gravity + whatever it's resting against, but can still be
        // realistically bumped/knocked further later.
        _rb.angularVelocity = Vector3.zero;
        _rb.linearVelocity  = Vector3.zero;

        RestoreTempIgnoredCollisions();

        _leanRoutine = null;

        // Multiplayer: tell everyone else where it actually settled so they can unparent + match it.
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkLadderManager.Instance?.RequestPlaceServerRpc(NetLadderId, transform.position, transform.rotation);

            // The accepted final pose is persistent network state. Do not keep simulating only on
            // the placing peer after that point, otherwise a later local bump moves this copy while
            // every observer correctly remains at the server-accepted transform.
            _rb.isKinematic = true;
            _rb.useGravity = false;
        }
    }

    // ── Multiplayer: remote visualization (called by NetworkLadderManager) ───────

    /// Applied on every client except the actual carrier: parents the ladder under the
    /// carrier's NetworkPlayer transform for observers. Mirrors Pickup()'s carry offset
    /// but skips IK/inventory/notifications — NetworkLadderManager handles remote IK.
    public void ApplyRemoteCarry(Transform carrierRoot)
    {
        if (_leanRoutine != null) { StopCoroutine(_leanRoutine); _leanRoutine = null; }

        _remoteControlled = true;
        _remoteCarried = true;
        _state = State.Carried;
        _carrier = null;

        // Observers never run the real fall locally — only the actual placer's client does, and
        // reports the settled result via RPC (ApplyRemotePlace). Keeping this kinematic avoids two
        // clients simulating gravity independently and drifting apart.
        if (_rb != null) { _rb.isKinematic = true; _rb.useGravity = false; }

        transform.SetParent(carrierRoot);
        transform.localPosition = _carryLocalOffset;
        transform.localRotation = Quaternion.Euler(_carryLocalRotation);

        // The owning player performs the clearance sweep and replicates an already-valid pose.
        // Keeping observer colliders disabled also prevents remote carried ladders affecting the
        // local follow camera or duplicating collision responses.
        if (_col != null) _col.enabled = false;
    }

    /// Applied on every client except the placer, repeatedly (~20 Hz) while PhysicsLeanRoutine's
    /// fall plays out on the placer's client — see UpdateLadderFallServerRpc. Unparents on the
    /// first call (still parented to the ex-carrier from ApplyRemoteCarry up to this point) and
    /// then just tracks the live tumble; State/carry flags are left alone, since the placer will
    /// still send the authoritative final pose via ApplyRemotePlace once it settles.
    public void ApplyRemoteFallPosition(Vector3 pos, Quaternion rot)
    {
        _remoteControlled = true;
        _remoteCarried = false;
        if (_remoteClimber == null) _state = State.Leaning;
        _carrier = null;

        // Only the server-accepted pose is rendered on observers. Running Rigidbody physics on
        // every peer would immediately create a different fall/contact result on each machine.
        if (_rb != null)
        {
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        if (_col != null) _col.enabled = false;
        if (transform.parent != null) transform.SetParent(null, worldPositionStays: false);
        transform.SetPositionAndRotation(pos, rot);
    }

    /// Applied on every client except the placer: unparents the ladder at the placer's ALREADY
    /// physics-settled final transform and cosmetically lerps up to it from vertical, then frees
    /// the ladder for interaction again. Observers don't re-simulate physics locally — the placer
    /// already resolved the real result, so this just visually matches it without risking a
    /// different physics outcome settling on different clients.
    public void ApplyRemotePlace(Vector3 basePos, Quaternion baseRot)
    {
        if (_leanRoutine != null) { StopCoroutine(_leanRoutine); _leanRoutine = null; }

        _remoteControlled = false;
        _remoteCarried = false;
        _state = State.Leaning;
        _carrier = null;

        transform.SetParent(null);
        transform.position = basePos;
        transform.rotation = Quaternion.Euler(0f, baseRot.eulerAngles.y, 0f);

        // The authority already resolved the fall. Observers keep the accepted result fixed
        // instead of re-simulating it and drifting away after the final network update.
        if (_rb != null)
        {
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        if (_col != null) _col.enabled = true;

        _leanRoutine = StartCoroutine(CosmeticLeanLerp(baseRot));
    }

    IEnumerator CosmeticLeanLerp(Quaternion targetRot)
    {
        Quaternion startRot = transform.rotation;
        float duration = 0.5f;
        float t = 0f;
        while (t < duration)
        {
            transform.rotation = Quaternion.Slerp(startRot, targetRot, t / duration);
            t += Time.deltaTime;
            yield return null;
        }
        transform.rotation = targetRot;
        _leanRoutine = null;
    }

    /// Applied on every client except the climber: marks the ladder occupied so their own
    /// CanInteract() correctly reports "in use" instead of offering to climb/pick it up, and rigs
    /// up the climber's hand/foot IK so observers see them actually gripping the rungs instead of
    /// floating in a run pose. The climber's actual position replicates via the normal
    /// player-position sync (PlayerPositionBridge → ClientNetworkTransform); there's no per-frame
    /// climb-height RPC, so Update() derives an equivalent height from that synced transform each
    /// frame via UpdateRemoteClimbIK() (from LateUpdate) for as long as _remoteClimber is set.
    public void ApplyRemoteClimbEnter(Transform climberRoot)
    {
        _remoteControlled = true;
        if (!_remoteCarried) _state = State.Climbing;

        if (climberRoot == null) return;
        EnsureClimbIKAnchors();
        _remoteClimber = climberRoot;

        var visual = climberRoot.Find("CharacterVisual");
        if (visual == null) return;
        _remoteClimberAnimator = visual.GetComponentInChildren<Animator>();
        _remoteClimberAnimator?.SetBool(IsClimbingHash, true);
        _remoteClimberAnimator?.SetFloat(ClimbSpeedHash, 0f);
        var ik = visual.GetComponent<CharacterVisualIK>() ?? visual.gameObject.AddComponent<CharacterVisualIK>();
        // Same partial weight as the local climber's own PlayerHandIK (_climbIKWeight) -- a full
        // 1.0 lock (CharacterVisualIK's default, correct for vehicle/carry where hands must sit
        // exactly on a fixed prop) fights the Climbing clip's own arm motion here and produces an
        // over-extended, twisted pose instead of a natural climb.
        ik.SetHandTargets(_rightHandIK, _leftHandIK, _climbIKWeight);
        ik.SetFootTargets(_rightFootIK, _leftFootIK, _climbIKWeight);
    }

    /// Applied on every client except the climber: releases the ladder back to Leaning — unless
    /// the ladder is being carried right now (see PickupSecondary's co-op carry-while-climbing
    /// path), in which case that carry is the authoritative state and must not be clobbered back
    /// to Leaning just because the climber happened to step off first.
    ///
    /// "Being carried" covers OUR OWN carry as well as a remote one. Checking only the remote flag
    /// meant a rider stepping off a ladder we were holding flipped it to Leaning in our hands: the
    /// ladder stayed parented and in front of us while the state said it was standing against a
    /// wall, so E no longer put it down.
    public void ApplyRemoteClimbExit(Transform climberRoot)
    {
        _remoteClimber = null;
        _remoteClimberAnimator?.SetBool(IsClimbingHash, false);
        _remoteClimberAnimator?.SetFloat(ClimbSpeedHash, 0f);
        _remoteClimberAnimator = null;
        var visual = climberRoot != null ? climberRoot.Find("CharacterVisual") : null;
        visual?.GetComponent<CharacterVisualIK>()?.ClearAllTargets();

        if (IsBeingCarried) return;
        _remoteControlled = false;
        _state = State.Leaning;
    }

    /// Observer-side counterpart to UpdateClimb()'s PositionClimber()+UpdateClimbIK() pair: instead
    /// of driving _climbHeight from local input, derives it from the remote climber's synced world
    /// position (inverse of PositionClimber()'s own formula: local Y of a point placed at
    /// transform.position + up*_climbHeight is exactly _climbHeight).
    void UpdateRemoteClimbIK()
    {
        if (_remoteClimber == null) return;

        // Never write the root of another player's NetworkObject here. ClientNetworkTransform is
        // already interpolating that root; overwriting it from every observer created a tug of war
        // and made the climber float beside the ladder or snap to a different rung per client.
        // The owner's replicated position is the authority. We only derive rung height from it and
        // move the local IK anchors, which are presentation-only objects.
        float maxHeight = Mathf.Max(0f, _ladderHeight * 0.93f - _climbTopMargin);
        float measuredHeight = Mathf.Clamp(
            transform.InverseTransformPoint(_remoteClimber.position).y,
            0f,
            maxHeight);
        _climbHeight = Mathf.Lerp(_climbHeight, measuredHeight,
            1f - Mathf.Exp(-18f * Time.deltaTime));

        UpdateClimbIK();
    }
}
