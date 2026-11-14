using System.Collections;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;

/// Switches the player between animation-driven movement and full physics ragdoll.
/// Bones must be set up via Editor tool: Tools → Warehouse Demo → Setup Player Ragdoll
[DisallowMultipleComponent]
public class RagdollController : MonoBehaviour
{
    public static RagdollController Instance { get; private set; }
    public bool IsRagdoll { get; private set; }

    [Header("Ragdoll")]
    [SerializeField] float _ragdollDuration = 1.5f;

    [Header("Get-up blend")]
    [Tooltip("Seconds to blend from ragdoll end-pose into the StandingUp animation's own pose.")]
    [SerializeField] float _blendDuration = 0.7f;
    [Tooltip("Extra time (on top of _ragdollDuration) to wait for bones to actually stop moving " +
             "before snapshotting the pose to blend from. Blending from a still-tumbling pose is " +
             "what makes the get-up read as an unnatural puppet snap — capped so a ragdoll that " +
             "never settles (e.g. wedged against geometry) doesn't stall the recovery indefinitely.")]
    [SerializeField] float _maxExtraSettleTime = 1.0f;
    [Tooltip("Extra pause after StandingUp finishes before player can move.")]
    [SerializeField] float _controlReturnDelay = 0.3f;
    [Tooltip("How long StandingUp (face-down get-up) actually plays before its own exit-time " +
             "transition hands off to locomotion. Same clip/speed as PlayerSlipController's " +
             "wet-floor trip: 11.4s clip at 2.5x speed, exitTime 0.9 => ~4.1s.")]
    [SerializeField] float _standingUpDuration = 4.1f;
    [Tooltip("How long GettingUp (face-up get-up) plays before its exit-time transition hands off " +
             "to locomotion. 2.7s clip at 1x speed, exitTime 0.9 => ~2.43s.")]
    [SerializeField] float _gettingUpDuration = 2.43f;

    static readonly int _standingUpHash = Animator.StringToHash("StandingUp"); // face-down get-up
    static readonly int _gettingUpHash  = Animator.StringToHash("GettingUp");  // face-up get-up

    // ThirdPersonController.Move() (which sets these) is one of _scriptsToDisable, so it never
    // runs while ragdolled — if the player was sprinting the instant they got hit, Speed/
    // MotionSpeed stay frozen at the sprint value for the whole ragdoll. The get-up state's own
    // exit-time transition back into the locomotion blend tree fires automatically regardless of
    // whether the script is disabled, so it reads that stale Speed immediately — the character
    // appears to be running in place the instant they stand up.
    static readonly int _speedHash       = Animator.StringToHash("Speed");
    static readonly int _motionSpeedHash = Animator.StringToHash("MotionSpeed");

    [Header("Bone root — assign via Editor tool")]
    [SerializeField] Transform _hipBone;

    [Header("Scripts to disable during ragdoll — assign via Editor tool")]
    [SerializeField] MonoBehaviour[] _scriptsToDisable;

    [Header("Auto-found if blank")]
    [SerializeField] Animator _animator;

    [Header("Camera while down")]
    [Tooltip("The Cinemachine follow target. Left blank, the child named PlayerCameraRoot is used.")]
    [SerializeField] Transform _cameraRoot;
    [Tooltip("How far above the hips the camera target rides while the body is on the floor. " +
             "Zero puts it inside the body; too much and you watch yourself from standing height.")]
    [SerializeField] float _downCameraLift = 0.45f;

    // ── Internal state ────────────────────────────────────────────────────────

    // World space, deliberately. A local-space snapshot is relative to the root — and the
    // root is moved and turned under the body right before the blend starts, so replaying
    // local poses drags the whole body along with it. Pinning world poses instead means the
    // body stays exactly where it came to rest and only the get-up animation moves it.
    struct BonePose
    {
        public Transform  bone;
        public Vector3    worldPos;
        public Quaternion worldRot;
    }

    CharacterController    _cc;
    ThirdPersonController  _tpc;

    // Ground clamp for the get-up. Set while the clip is playing, with the height GroundRootY
    // resolved for the spot he is getting up on.
    bool  _gettingUp;
    float _getUpGroundY;

    // Where the body's surface is, for the get-up ground clamp. Shared with the puppet the other
    // clients watch — see BodyContactPoints.
    BodyContactPoints _contacts;

    // The box that fell out of his hands, whose collisions with the ragdoll are ignored for the
    // whole sequence — so it holds nothing up and must not be mistaken for ground. Every OTHER
    // box can be stood on and is ground like any crate in the warehouse.
    Collider[] _weightlessColliders = System.Array.Empty<Collider>();
    Vector3                _cameraRootHome;   // where the target sits when he is on his feet
    Rigidbody[]         _ragdollBodies;
    Collider[]          _ragdollColliders; // one per bone, same GameObject as its Rigidbody
    BonePose[]          _blendPoses;   // ragdoll end-pose snapshot
    float               _blendWeight;  // 1 = full ragdoll pose, 0 = full anim pose
    bool                _blending;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        var np = GetComponent<NetworkPlayer>();
        if (np == null || Unity.Netcode.NetworkManager.Singleton?.IsListening != true)
            Instance = this;

        _cc  = GetComponent<CharacterController>();
        _tpc = GetComponent<ThirdPersonController>();

        if (_cameraRoot == null) _cameraRoot = transform.Find("PlayerCameraRoot");
        if (_cameraRoot != null) _cameraRootHome = _cameraRoot.localPosition;
        if (!_animator) _animator = GetComponentInChildren<Animator>();

        _ragdollBodies = GetComponentsInChildren<Rigidbody>(true);
        _ragdollColliders = new Collider[_ragdollBodies.Length];
        for (int i = 0; i < _ragdollBodies.Length; i++)
            _ragdollColliders[i] = _ragdollBodies[i].GetComponent<Collider>();

        if (_ragdollBodies.Length == 0)
            Debug.LogWarning("[RagdollController] No bone Rigidbodies found. " +
                             "Run: Tools → Warehouse Demo → Setup Player Ragdoll");

        SetBonePhysics(false);
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    public static void BindLocal(RagdollController controller)
    {
        if (controller != null) Instance = controller;
    }

    public static void UnbindLocal(RagdollController controller)
    {
        if (Instance == controller) Instance = null;
    }

    /// Runs AFTER the Animator has updated bones this frame.
    /// Overrides bone transforms to blend saved ragdoll pose → current anim pose.
    void LateUpdate()
    {
        // The CharacterController is off for the whole fall, so the root — and with it the
        // Cinemachine follow target hanging off it — stays standing where he was hit while the
        // body slides away. Looking around from that spot means looking at yourself from across
        // the aisle, so the target rides the hips until control comes back. Only the position is
        // written: ThirdPersonController owns the target's rotation, and that is what the look
        // input still drives.
        if (_cameraRoot != null && _hipBone != null)
        {
            if (IsRagdoll) _cameraRoot.position = _hipBone.position + Vector3.up * _downCameraLift;
            else if (_cameraRoot.localPosition != _cameraRootHome)
                _cameraRoot.localPosition = _cameraRootHome;
        }

        if (_blending && _blendPoses != null)
        {

        // Parents first (the array comes back in hierarchy order), so a child's world pose is
        // applied after its parent has already been placed.
            foreach (var p in _blendPoses)
            {
                if (p.bone == null) continue;
                // bone is now at the animator pose; blend it back toward where it came to rest
                p.bone.position = Vector3.Lerp(p.bone.position, p.worldPos, _blendWeight);
                p.bone.rotation = Quaternion.Slerp(p.bone.rotation, p.worldRot, _blendWeight);
            }
        }

        // Last, over both the blend and the rest of the clip: keep the body out of the floor.
        if (_gettingUp) ClampGetUpToGround();
    }

    // Recovery is an intent, not a client write. The server only clears the same ragdoll version
    // that this owner finished, so a delayed get-up message cannot cancel a newer fall.
    static void PublishRagdollRecovery()
    {
        var np = NetworkPlayer.Local;
        if (np != null && np.IsSpawned && np.IsOwner)
            np.NotifyRagdollRecoveredServerRpc(np.RagdollVersionNet.Value);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// impulse in N·s — pass collision.impulse directly from OnCollisionEnter.
    public void TriggerRagdoll(Vector3 impulse, Vector3 hitPoint)
        => TriggerRagdollInternal(impulse, hitPoint, notifyServer: true);

    /// Server-confirmed world hit. The server already published the canonical state, so this
    /// starts only the owner's local physics presentation and never echoes a second request.
    public void TriggerNetworkRagdoll(Vector3 impulse, Vector3 hitPoint)
        => TriggerRagdollInternal(impulse, hitPoint, notifyServer: false);

    void TriggerRagdollInternal(Vector3 impulse, Vector3 hitPoint, bool notifyServer)
    {
        if (IsRagdoll || _ragdollBodies.Length == 0) return;

        // A real impact always ragdolls, even mid-slip — cancel the scripted trip
        // animation cleanly so it doesn't keep running (and re-lock movement) underneath.
        PlayerSlipController.Instance?.CancelSlip();

        // Notify remote clients so they play ragdoll on the CharacterVisual.
        if (notifyServer && Unity.Netcode.NetworkManager.Singleton?.IsListening == true)
            NetworkPlayer.Local?.NotifyRagdollServerRpc(impulse, hitPoint);

        StartCoroutine(RagdollSequence(impulse, hitPoint));
    }

    // ── Ragdoll sequence ──────────────────────────────────────────────────────

    IEnumerator RagdollSequence(Vector3 impulse, Vector3 hitPoint)
    {
        IsRagdoll = true;

        // Knock any carried box away from the impact (horizontal, plus a slight upward
        // arc) instead of it just dropping straight down under the player's feet.
        Vector3 awayDir = impulse;
        awayDir.y = 0f;
        awayDir = awayDir.sqrMagnitude > 0.01f ? awayDir.normalized : -transform.forward;
        awayDir = (awayDir + Vector3.up * 0.35f).normalized;

        // A weakly-thrown box (throwForce 4) often lands right next to or on top of the
        // character it was just dropped by. While the character is ragdolled that's a dynamic
        // rigidbody free to keep nudging bone colliders — visible as the body slowly rotating
        // while "lying" there — and it can also end up sitting under the hip when
        // SnapRootToHips ground-checks, making the get-up snap onto the box instead of the
        // floor (floating or sinking depending on where the box happened to settle). Ignore
        // collision between it and the ragdoll bones for the whole sequence; restored once
        // control returns.
        var droppedBox = BoxPhysicsInteractable.GrabbedBox;
        Collider[] droppedBoxColliders = droppedBox != null
            ? droppedBox.GetComponentsInChildren<Collider>(true)
            : System.Array.Empty<Collider>();
        SetBoxCollisionIgnore(droppedBoxColliders, true);
        _weightlessColliders = droppedBoxColliders;   // see GroundRootY

        BoxPhysicsInteractable.ReleaseGrabbed(4f, awayDir);

        // Clear the stale run/walk speed now, while the Animator is still enabled to actually
        // commit it — see field comment above. Must go through ThirdPersonController's own
        // reset, not just the Animator parameter: Move() re-lerps from its own private
        // _animationBlend field every frame and would overwrite a bare SetFloat the instant
        // control returns and Move() runs again.
        if (_tpc != null) _tpc.ResetLocomotionSpeed();
        else if (_animator)
        {
            _animator.SetFloat(_speedHash, 0f);
            _animator.SetFloat(_motionSpeedHash, 0f);
        }

        // Disable animator BEFORE enabling bone physics to avoid fighting.
        if (_animator) _animator.enabled = false;
        SetControlLocked(true);
        if (_cc) _cc.enabled = false;

        SetBonePhysics(true);

        var hitBone = GetNearestBone(hitPoint);
        hitBone?.AddForceAtPosition(impulse, hitPoint, ForceMode.Impulse);

        // Trigger injury based on impact strength and which bone was hit.
        InjurySystem.Instance?.AddInjuryFromImpact(
            impulse.magnitude,
            hitBone != null ? hitBone.name : "");

        // ── Ragdoll phase ─────────────────────────────────────────────────────
        yield return new WaitForSeconds(_ragdollDuration);

        // Keep waiting (up to the cap) until the body has actually stopped tumbling —
        // snapshotting mid-motion is what made the get-up look like a puppet snapping
        // into place instead of a person settling and pushing themselves up.
        float settleElapsed = 0f;
        while (settleElapsed < _maxExtraSettleTime && !BodiesAtRest())
        {
            settleElapsed += Time.deltaTime;
            yield return null;
        }

        // ── Snapshot the ragdoll end-pose ─────────────────────────────────────
        _blendPoses  = SnapshotPoses();
        _blendWeight = 1f;

        // A ragdoll can settle face-down or face-up depending on the impact — StandingUp's
        // first frames are a face-down (prone) pose, GettingUp's are face-up (supine). Using
        // the wrong one means blending into a pose the fall never actually produced, which is
        // its own source of the unnatural "snap". Pick whichever one actually matches — and
        // read it BEFORE the root is turned, because turning the root turns the hips with it.
        bool faceDown = _hipBone != null && Vector3.Dot(_hipBone.forward, Vector3.down) > 0f;

        // Exactly where and how the body came to rest — measured before anything touches the
        // skeleton, and the target the get-up clip has to be fitted to.
        Vector3 restHipPos  = _hipBone != null ? _hipBone.position : transform.position;
        Vector3 restBodyDir = BodyDirection();

        // Reposition capsule under the hips before re-enabling CharacterController.
        SnapRootToHips();
        int  getUpHash    = faceDown ? _standingUpHash    : _gettingUpHash;
        float getUpDuration = faceDown ? _standingUpDuration : _gettingUpDuration;

        // Return bones to animation control, and actually play the matching get-up clip
        // (StandingUp is also what PlayerSlipController uses for the wet-floor trip) instead
        // of jumping straight to locomotion — the ragdoll end-pose is already lying on the
        // ground, so blending into the get-up clip's own early frames (also on the ground)
        // reads as a real recovery, not a snap to idle.
        SetBonePhysics(false);
        if (_animator)
        {
            _animator.enabled = true;
            _animator.Play(getUpHash, 0, 0f);

            // Evaluate the clip's opening frame right now, then move the ROOT so that frame
            // lands on top of the body as it actually lies. The character is never turned to
            // suit the animation — the animation is fitted to him.
            _animator.Update(0f);
            FitGetUpToRestPose(restHipPos, restBodyDir);
        }

        _gettingUp = true;


        // ── Blend phase — LateUpdate lerps from ragdoll pose → anim pose ──────
        _blending = true;
        float elapsed = 0f;
        while (elapsed < _blendDuration)
        {
            _blendWeight = 1f - elapsed / _blendDuration;  // 1 → 0
            elapsed += Time.deltaTime;
            yield return null;
        }
        _blendWeight = 0f;
        _blending    = false;
        _blendPoses  = null;

        // Let the get-up clip keep playing, uninterrupted by the player regaining control,
        // until its own exit-time transition has handed off to locomotion. Asking the animator
        // how far along it is beats a hand-typed duration: StandingUp runs at 2.5x speed in the
        // controller, so any literal here is wrong the moment somebody retunes that — and the
        // player would either freeze after standing or get control back mid-push-up.
        yield return WaitForGetUp(getUpHash, getUpDuration);

        // ── Return control ────────────────────────────────────────────────────
        yield return new WaitForSeconds(_controlReturnDelay);

        _gettingUp = false;

        if (_cc) _cc.enabled = true;
        SetControlLocked(false);
        SetBoxCollisionIgnore(droppedBoxColliders, false);
        _weightlessColliders = System.Array.Empty<Collider>();
        IsRagdoll = false;
        PublishRagdollRecovery();
    }

    /// Takes movement away for the duration of the fall — and deliberately leaves the camera.
    ///
    /// Disabling ThirdPersonController outright (which is what _scriptsToDisable used to carry,
    /// along with the two input components) also kills its LateUpdate, and that is where
    /// CameraRotation lives. The result was a player lying on the floor unable to look at
    /// anything, which in a co-op shift is exactly the moment you want to see who is coming.
    ///
    /// MovementLocked is the project's existing answer to "hands off the body, not off the
    /// camera" — LadderController already uses it for climbing. ThirdPersonController.Update
    /// early-returns on it before JumpAndGravity/GroundedCheck/Move, so nothing touches the
    /// disabled CharacterController, while LateUpdate's CameraRotation keeps running.
    ///
    /// Anything in the list that does NOT own the camera or the input feeding it is still
    /// disabled outright — PlayerInteraction, because picking things up off the floor while
    /// face-down is not a thing. The filter is by type rather than by fixing the serialized
    /// list, so scenes saved before this change behave correctly without being re-tooled.
    void SetControlLocked(bool locked)
    {
        if (_tpc != null) _tpc.MovementLocked = locked;

        foreach (var s in _scriptsToDisable)
        {
            if (s == null || OwnsCameraOrItsInput(s)) continue;
            s.enabled = !locked;
        }
    }

    static bool OwnsCameraOrItsInput(MonoBehaviour s) => s.GetType().Name
        is "ThirdPersonController" or "FirstPersonController"
        or "StarterAssetsInputs"   or "PlayerInput";

    /// Waits out the get-up state by its own normalized time, with the serialized duration
    /// kept only as a safety cap in case the state is never entered at all.
    IEnumerator WaitForGetUp(int stateHash, float fallbackDuration)
    {
        if (_animator == null)
        {
            yield return new WaitForSeconds(fallbackDuration);
            yield break;
        }

        // The animator reports the state it was in until it has ticked at least once.
        yield return null;

        float guard = 0f;
        float cap   = fallbackDuration * 2f + 1f;

        while (guard < cap)
        {
            var info = _animator.GetCurrentAnimatorStateInfo(0);

            if (info.shortNameHash != stateHash) yield break;   // already handed off
            if (info.normalizedTime >= 1f)       yield break;   // played to the end

            guard += Time.deltaTime;
            yield return null;
        }
    }

    void SetBoxCollisionIgnore(Collider[] boxColliders, bool ignore)
    {
        if (boxColliders.Length == 0) return;
        foreach (var boneCol in _ragdollColliders)
        {
            if (boneCol == null) continue;
            foreach (var boxCol in boxColliders)
                if (boxCol != null) Physics.IgnoreCollision(boneCol, boxCol, ignore);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Mirrors the velocity-settle check BoxPhysicsInteractable already uses before freezing
    // a dropped/thrown box — same idea, applied to ragdoll bones instead of a single body.
    const float RestLinearThreshold  = 0.15f; // m/s
    const float RestAngularThreshold = 0.30f; // rad/s

    bool BodiesAtRest()
    {
        foreach (var rb in _ragdollBodies)
        {
            if (rb.linearVelocity.sqrMagnitude  > RestLinearThreshold  * RestLinearThreshold)  return false;
            if (rb.angularVelocity.sqrMagnitude > RestAngularThreshold * RestAngularThreshold) return false;
        }
        return true;
    }

    /// Holds the body on top of the floor for the whole get-up.
    ///
    /// The clip was authored on another skeleton. Humanoid retargeting matches muscle poses, not
    /// world heights, so a crouch that had the source rig's knees just above the ground puts this
    /// body's knees through it — the root is at floor level, and the legs simply reach further
    /// down than the clip ever intended. That is the "gets up waist-deep in the concrete" shot,
    /// and the jump at the end was the CharacterController coming back on and resolving the
    /// overlap in a single frame.
    ///
    /// Fixing it at the root means the pose is never wrong: whatever the clip does, the body is
    /// raised so its lowest point rests on the surface. Only ever raised — pushing him DOWN to
    /// meet a clip that floats would fight the get-up's own rise onto his feet.
    ///
    /// Measured from the capsules rather than the renderers: a skinned mesh reports a loose
    /// axis-aligned box that would lift him off the ground by its own slack.
    void ClampGetUpToGround()
    {
        _contacts ??= new BodyContactPoints(transform, _ragdollBodies, _animator);

        float lowest = _contacts.LowestY();
        if (lowest == float.MaxValue) return;

        // Both ways, not just up. Raising alone was worse than the sink it fixed: the correction
        // taken to get his knees out of the floor mid-clip stayed on his root for the rest of the
        // get-up, so he finished the animation standing that far in the air and dropped only when
        // the CharacterController came back.
        float lift = _getUpGroundY - lowest;
        if (Mathf.Abs(lift) < 0.005f) return;

        transform.position += Vector3.up * lift;

        // The blend holds bones at WORLD positions captured before this correction. Left alone it
        // drags them straight back, the next frame measures the same gap, corrects again — and the
        // root ratchets away while the body stays where it fell. Carrying the snapshot along keeps
        // the two describing the same body.
        if (_blendPoses != null)
            for (int i = 0; i < _blendPoses.Length; i++)
                _blendPoses[i].worldPos.y += lift;
    }

    /// The pose the get-up blends out of — lifted, if the fall left part of him inside the floor.
    ///
    /// A ragdoll does not always come to rest on top of the ground. A crate landing on the body
    /// (a thrown box that comes down on his head is the ordinary case, and its collisions are NOT
    /// ignored — only the one that fell out of his own hands is) presses bones through the
    /// surface, and joints under load settle a few centimetres into it on their own.
    ///
    /// While he is lying there that is barely visible: one shin clipping the concrete. It stops
    /// being cosmetic the moment the get-up starts, because this snapshot is what the blend spends
    /// the next 0.7 s pulling the body back toward — so a pose that was 30 cm inside the floor is
    /// replayed as a body standing up out of the floor, which is the shot the player sends you.
    ///
    /// Lifting the whole snapshot by that overlap keeps the pose exactly as the fall arranged it
    /// — every bone in the same place relative to every other — and only puts it back on top of
    /// the surface it should have been resting on.
    BonePose[] SnapshotPoses()
    {
        var poses = new BonePose[_ragdollBodies.Length];
        for (int i = 0; i < _ragdollBodies.Length; i++)
        {
            var t = _ragdollBodies[i].transform;
            poses[i] = new BonePose
            {
                bone     = t,
                worldPos = t.position,
                worldRot = t.rotation,
            };
        }

        float lift = FloorOverlap();
        if (lift > 0f)
            for (int i = 0; i < poses.Length; i++)
                poses[i].worldPos.y += lift;

        return poses;
    }

    /// How far the settled body is inside whatever it is lying on, or 0 when it is on top of it.
    ///
    /// Measured against the same surface GroundRootY resolves, and with the same allowance for a
    /// bone origin sitting a capsule radius above it, so the two cannot disagree about where the
    /// floor is and fight each other across the blend.
    float FloorOverlap()
    {
        if (_hipBone == null || _ragdollBodies.Length == 0) return 0f;

        int mask = ~(1 << gameObject.layer);
        var hits = Physics.RaycastAll(_hipBone.position + Vector3.up * 1.5f, Vector3.down, 6f,
                                      mask, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        float surfaceY = float.NaN;
        foreach (var hit in hits)
        {
            if (hit.collider.transform.IsChildOf(transform)) continue;
            if (System.Array.IndexOf(_weightlessColliders, hit.collider) >= 0) continue;
            surfaceY = hit.point.y;
            break;
        }
        if (float.IsNaN(surfaceY)) return 0f;

        float lowestBone = LowestBoneY();
        float overlap    = (surfaceY - RestingClearance) - lowestBone;

        // Small overlaps are the ordinary jitter of a settled joint chain and lifting them would
        // pop the body; a real burial is centimetres deep and worth correcting.
        return overlap > 0.03f ? overlap : 0f;
    }

    // Setup Player Ragdoll (Editor tool) leaves bones at ~0 damping, so the off-center bone
    // impulse in TriggerRagdoll keeps them spinning on residual angular momentum for a long
    // time — BodiesAtRest()'s threshold isn't reached until deep into (or past) the settle-wait
    // window, so the character visibly keeps rotating right up until get-up fires. Damping here
    // (same idea as BoxPhysicsInteractable/PalletBehavior's settle damping) makes the ragdoll
    // bleed off that spin quickly so it actually looks like it's landing, not idling as a
    // slowly-decelerating top.
    const float BoneLinearDamping  = 1.5f;
    const float BoneAngularDamping = 5f;

    void SetBonePhysics(bool active)
    {
        foreach (var rb in _ragdollBodies)
        {
            rb.isKinematic      = !active;
            rb.detectCollisions = active;
            if (active)
            {
                rb.linearDamping  = BoneLinearDamping;
                rb.angularDamping = BoneAngularDamping;
                rb.WakeUp();
            }
        }
    }

    void SnapRootToHips()
    {
        if (_hipBone == null) return;

        var pos = _hipBone.position;
        pos.y = transform.position.y;      // height comes from GroundRootY below
        transform.position = pos;

        GroundRootY();
    }

    /// How far below the lowest settled bone a ray-found "ground" may sit before it is treated as
    /// something the body is not actually resting on. Wider than a bone's own clearance above the
    /// surface (a capsule radius, ~0.1) and narrower than the height of anything worth lying on.
    const float MaxGroundDrop     = 0.25f;

    /// Where the surface sits relative to the lowest bone's origin when the body rests on it —
    /// roughly the radius of the capsule around that bone.
    const float RestingClearance  = 0.10f;

    float LowestBoneY()
    {
        float lowest = float.MaxValue;
        foreach (var rb in _ragdollBodies)
            if (rb != null && rb.position.y < lowest) lowest = rb.position.y;

        return lowest == float.MaxValue ? transform.position.y : lowest;
    }

    /// Drops the root onto whatever the body is lying on, leaving its horizontal placement
    /// alone — FitGetUpToRestPose owns that.
    void GroundRootY()
    {
        if (_hipBone == null) return;

        int mask = ~(1 << gameObject.layer);

        // The box he dropped when he went down is the one thing under him that cannot hold him
        // up: its collisions with the ragdoll are ignored for the whole sequence, so the body
        // passes straight through it. Snapping the root to it left him standing up on air.
        //
        // Every other box is real ground. Skipping ALL of them — which this used to do — is what
        // put him through the floor when he came to rest on a pallet stack or a crate: the body
        // lay on the box while the root was pinned to the floor 30-40 cm below, and the get-up
        // blend then dragged the whole silhouette down to meet it.
        var hits = Physics.RaycastAll(_hipBone.position + Vector3.up * 0.5f, Vector3.down, 3f,
                                      mask, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        float groundY = transform.position.y;
        bool  found   = false;

        foreach (var hit in hits)
        {
            if (System.Array.IndexOf(_weightlessColliders, hit.collider) >= 0) continue;

            // His own ragdoll. The layer mask above only excludes the layer of the Player root
            // (Outline), and the bones carry whatever layer the body model came with (Default),
            // so a ray fired from inside the character to find the floor was free to land on his
            // own hip or spine capsule — a "floor" that moves with him, at whatever height the
            // pose happened to leave it. Parentage answers this whatever the layers say.
            if (hit.collider.transform.IsChildOf(transform)) continue;

            groundY = hit.point.y;
            found   = true;
            break;
        }

        // A ragdoll landing in the middle of a box pile (very common in this warehouse) can have
        // EVERY hit within range belong to a box — skipping all of them left the root at the
        // hip's raw height (still ~0.2-0.3m above anything solid for a prone body), so the whole
        // get-up animation played with the character visibly floating. Standing on the box pile
        // it actually landed on reads far better than hovering over it with nothing underneath.
        if (!found && hits.Length > 0) groundY = hits[0].point.y;

        // A single ray down from the hip answers for one point, and the body is not a point. Land
        // half-on a crate and the ray can slip past its edge to the floor 40 cm below, which is a
        // surface he is demonstrably NOT resting on — pinning the root there is what dragged him
        // down through the floor during the get-up blend.
        //
        // The settled skeleton is the better witness: nothing he lies on can be far below his
        // lowest bone. Only a large disagreement is corrected, so an ordinary few centimetres of
        // ragdoll clearance still defers to the real ground hit.
        float lowestBone = LowestBoneY();
        if (lowestBone - groundY > MaxGroundDrop) groundY = lowestBone - RestingClearance;

        var pos = transform.position;
        pos.y = groundY;
        transform.position = pos;

        _getUpGroundY = groundY;
    }

    /// Which way the body is currently lying, flattened to the floor: hips -> head. Read off
    /// the live skeleton, so it answers for whatever pose is on the bones at the time — the
    /// settled ragdoll before the animator takes over, the clip's opening frame after.
    Vector3 BodyDirection()
    {
        if (_hipBone == null) return Vector3.zero;

        Transform head = _animator != null && _animator.isHuman
            ? _animator.GetBoneTransform(HumanBodyBones.Head)
            : null;

        Vector3 dir = head != null ? head.position - _hipBone.position : _hipBone.up;
        dir.y = 0f;
        return dir;
    }

    /// Fits the get-up clip onto the body instead of dragging the body onto the clip.
    ///
    /// The clip is authored relative to the root, so its opening (prone) frame lies whichever
    /// way the root happens to face. Rather than guess how the clip was authored — which axis
    /// the head points along, whether it starts head-first or feet-first — this measures the
    /// direction the clip actually produced on the skeleton and turns the ROOT by the
    /// difference against the direction the ragdoll actually settled in. The visible body does
    /// not swing round: turning the root turns the animated pose with it, onto the resting
    /// pose. The same is then done for position, so the clip's hips start where the real hips
    /// came to rest.
    ///
    /// Net effect: the blend has almost nothing left to correct, and the character pushes
    /// himself up from where he fell, lying the way he fell.
    void FitGetUpToRestPose(Vector3 restHipPos, Vector3 restBodyDir)
    {
        if (_hipBone == null) return;

        Vector3 animBodyDir = BodyDirection();

        // Near-vertical at either end (landed on his feet, crumpled) leaves no direction to
        // read — leave the root alone rather than spin it on noise.
        const float minSqr = 0.0025f;
        if (animBodyDir.sqrMagnitude > minSqr && restBodyDir.sqrMagnitude > minSqr)
        {
            float deltaYaw = Vector3.SignedAngle(animBodyDir, restBodyDir, Vector3.up);
            transform.rotation = Quaternion.AngleAxis(deltaYaw, Vector3.up) * transform.rotation;

            // Re-evaluate so the hips read below come from the corrected orientation.
            if (_animator) _animator.Update(0f);
        }

        Vector3 offset = restHipPos - _hipBone.position;
        offset.y = 0f;                       // height is GroundRootY's job, not this
        transform.position += offset;

        GroundRootY();
    }

    Rigidbody GetNearestBone(Vector3 worldPoint)
    {
        Rigidbody nearest = null;
        float     minSqr  = float.MaxValue;
        foreach (var rb in _ragdollBodies)
        {
            float sqr = (rb.position - worldPoint).sqrMagnitude;
            if (sqr < minSqr) { minSqr = sqr; nearest = rb; }
        }
        return nearest;
    }
}
