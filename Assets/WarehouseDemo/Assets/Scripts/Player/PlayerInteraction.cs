using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerInteraction : MonoBehaviour
{
    public static event Action BoxThrown;

    [SerializeField] private float _range = 2.0f;

    /// How far off the crosshair a weapon on the floor can be and still be picked up. Weapons only;
    /// see the fallback at the end of Scan.
    const float WeaponAimRadius = 0.15f;
    // Stacked boxes are often viewed at a steep angle: a thin centre ray can pass through the
    // centimetre-wide gap between their colliders. This forgiving radius applies to boxes only.
    const float BoxAimRadius = 0.20f;
    const float FlyingBoxCatchRadius = 0.40f;
    const float FlyingBoxCatchRange  = 2.50f;

    [Header("Hover Outline")]
    [SerializeField] float _outlineWidth = 0.005f;

    private Camera        _cam;
    private IInteractable _current;
    private LayerMask     _interactMask;
    private string        _lastPrompt;

    // Hold-to-interact state (IHoldInteractable) — tracked separately from the
    // single-press flow below.
    IHoldInteractable _holdTarget;
    float             _holdTime;

    /// What E is being held down on right now, or null.
    ///
    /// Derived rather than stored: _holdTime only accumulates while the key is down and the target
    /// will accept it, and is zeroed on release, on completion and on looking away — so it already
    /// is the answer, and a second flag beside it could only ever disagree with it.
    ///
    /// An instance property and not a static one, on purpose. There is a PlayerInteraction on every
    /// player object in a session, and a static would be whichever of them happened to write last.
    public IHoldInteractable ActiveHold => _holdTime > 0f ? _holdTarget : null;

    // Throw charge
    bool  _throwCharging;

    /// Temporary: was the meter given a value at all during this wind-up? Paired with the throw
    /// trace, it separates a bar that was never fed from a bar that was fed and did not show.
    bool  _meterFed;
    float _throwChargeTime;

    /// Whether E has been seen released since the box in hand was picked up.
    ///
    /// The wind-up follows the key being down rather than the frame it went down, and this is what
    /// keeps that honest. Arming on the press frame alone looked right and failed constantly in an
    /// actual warehouse: the branch below cancels a pending charge for as long as the crosshair is
    /// on a pallet, a cart or the bench, and the place a player stands while holding a box is
    /// surrounded by all three. Press E with a pallet under the crosshair, look up, keep holding —
    /// the press frame is long gone, so nothing charges, nothing is drawn, and letting go throws
    /// nothing at all. The next tap then leaves at the minimum force with no meter, which is
    /// exactly "throws with no force and there is no charge indicator".
    ///
    /// Without the arming, following the key would break the pickup instead: E is still down on the
    /// frame the grab completes, and the box a player just picked up would immediately start winding
    /// up to be thrown again.
    bool _throwArmed;

    /// Whether there was a box in hand last frame. Picking one up is what disarms the wind-up.
    ///
    /// Deliberately a "carrying or not" flag rather than a remembered box. The first version
    /// compared identities — a different box meant a fresh grab — and that is wrong about the most
    /// ordinary thing a player does with a box: throw it, walk over, and pick the same one up
    /// again. Same box, so nothing was treated as a new grab, so the wind-up stayed armed from
    /// before the throw, and the E still held down from the pickup threw it straight back out of
    /// his hands. From the floor it read as the box being impossible to pick up at all.
    bool _hadBox;

    const float ThrowChargeMaxTime = 1.5f;

    /// Tap or throw is decided by how long the key was held. Let go before this and the box is put
    /// down straight away; hold past it and the wind-up starts counting, the meter appears, and
    /// letting go throws. Nothing before the threshold counts towards the throw's force, so a
    /// hesitant tap can never turn into a lob.
    const float ThrowHoldThreshold = 0.5f;

    /// A put-down, not a throw: just enough to clear the carrier's own capsule.
    const float DropForce          = 1f;
    const float ThrowForceMin      = 3f;
    const float ThrowForceMax      = 32f;

    /// The narrowest the meter is ever drawn while a wind-up is actually running, as a fraction of
    /// the bar. Enough to read as "started" across a room.
    const float MeterFloor         = 0.05f;

    // --- outline state ---
    static Material _matCan;
    static Material _matCannot;
    Renderer[]   _hlRends;
    readonly List<Renderer> _hlSubmeshOverlays = new();
    bool         _hlCan;       // last applied can-state (to avoid redundant swaps)

    // Scan() runs every frame for the local player — reused buffers avoid a fresh
    // RaycastHit[] + a fresh IInteractable[] per ray hit, every single frame.
    readonly RaycastHit[]        _scanHitsBuf     = new RaycastHit[16];
    readonly List<IInteractable> _interactableBuf = new(4);

    sealed class HitDistanceComparer : IComparer<RaycastHit>
    {
        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }
    static readonly HitDistanceComparer s_hitDistanceComparer = new();

    StarterAssets.ThirdPersonController _tpc;

    /// Last time the controller reported ground. ThirdPersonController.Grounded flickers for a
    /// frame or two on every seam, ramp and pallet edge, and the airborne gate below used to eat
    /// whatever E press landed on one of those frames — "the box doesn't always let go".
    float _lastGroundedAt;
    const float GroundedGrace = 0.2f;

    private void Awake()
    {
        // Runtime-added world interactables must exist before the first aim scan. A domain reload
        // while Play Mode is already running does not invoke AfterSceneLoad again, which left the
        // visible station collider with no IInteractable anywhere in its parent chain.
        ChargingStation.Install();

        _tpc = GetComponentInParent<StarterAssets.ThirdPersonController>();
        if (_tpc == null) _tpc = GetComponentInChildren<StarterAssets.ThirdPersonController>();

        // Exclude the Player layer (layer 8 "Outline") — ray must not hit own character model.
        _interactMask = ~LayerMask.GetMask("Outline");
    }

    private void Update()
    {
        var kb = Keyboard.current;
        var gp = Gamepad.current;
        bool interactPressed = GameInputBindings.WasPressed(GameBindingAction.Interact);
        bool eHeld           = GameInputBindings.IsPressed(GameBindingAction.Interact);
        bool dropPressed     = GameInputBindings.WasPressed(GameBindingAction.Secondary);

        var grabbed = BoxPhysicsInteractable.GrabbedBox;

        // Re-raised below for every frame a wind-up is actually showing.
        if (_tpc != null) _tpc.FaceCameraYaw = false;

        // Both of these are settled here, before anything can return early, because both have to
        // hold whatever the player happens to be looking at. Done inside the throw branch, they
        // stopped being updated the moment the crosshair crossed a pallet — and a player carrying
        // a box in a warehouse is looking at a pallet most of the time.
        if ((grabbed != null) != _hadBox)
        {
            // A wind-up thrown away mid-hold is the one failure the bar cannot show: it simply
            // starts again from nothing, and on screen that is a bar which appears and never grows.
            // Worth a line, because the box appearing or vanishing from the hands is a *network*
            // event as much as a local one — a confirmation arriving late, a reconcile sweep, a
            // team-mate taking the box — and none of that is visible to the player holding the key.
            if (_throwChargeTime > 0f)
                WindUpTrace($"charge thrown away after {_throwChargeTime:0.00}s: the box " +
                            (grabbed != null ? "arrived in" : "left") + " the hands mid-hold");

            _hadBox          = grabbed != null;
            _throwArmed      = false;      // the key that grabbed it may not also throw it
            _throwCharging   = false;
            _throwChargeTime = 0f;
        }

        if (!eHeld) _throwArmed = true;

        // No interactions while airborne (jumping/falling) — checked before the carried-box
        // branch below so it also blocks throw-charging, not just the E/G handlers further down.
        bool groundedNow = PlayerSlipController.Instance == null || PlayerSlipController.Instance.IsGrounded;
        if (groundedNow) _lastGroundedAt = Time.time;
        bool grounded = groundedNow || Time.time - _lastGroundedAt < GroundedGrace;
        if (!grounded)
        {
            if (_current != null)
            {
                EndHighlight();
                _current    = null;
                _lastPrompt = null;
                HUDController.Instance?.HidePrompt();
            }
            // A hold already running is not thrown away by a bump in the floor: letting go in the
            // air still drops or throws the box. Discarding it here was one of the ways a quick
            // tap failed to put the box down — the press landed, the release fell on an
            // "airborne" frame, and the box simply stayed in the hands.
            if (_throwCharging && !eHeld && grabbed != null)
                FinishWindUp();

            WindUpTrace("blocked: not grounded (ThirdPersonController.Grounded is false)");
            return;
        }

        // Charge-throw applies to ANY carried box (plain or order-part) — but bench,
        // pallet, and cart interactions must still take priority when aiming at them,
        // since those are where a part box actually needs to go.
        bool alreadyScanned = false;
        if (grabbed != null)
        {
            // Scan once to peek — allow PalletStackZone/bench/cart interaction with any held box.
            Scan();
            alreadyScanned = true;

            // A wind-up already running holds the branch, whatever the crosshair is over.
            //
            // The pallet/cart/bench test is here to decide what E *means* when it is pressed, and
            // that question is answered on the press. Letting it re-decide mid-hold left the third
            // possible outcome, and the worst one to read: the throw branch stopped running, so the
            // clock stopped advancing, but nothing hid the meter either — the bar sat on screen
            // frozen at whatever width it last drew, for as long as the key was down. "Appears and
            // does not fill" is that state, and in a warehouse aisle the crosshair finds a pallet,
            // a cart or a bench without the player ever aiming at one.
            //
            // That includes the frame the key comes up. Requiring the key to still be down let a
            // release that landed while the crosshair crossed a pallet cancel the hold instead of
            // finishing it — the other way a tap failed to put the box down.
            bool windingUp = _throwCharging;

            if (windingUp
                || (!(_current is PalletStackZone) && !(_current is CartStorageInteractable)
                    && !(_current is AssemblyBench)))
            {
                // Not looking at a pallet, cart, or bench — charge-throw or submit packed order.
                if (_current != null) { EndHighlight(); _current = null; }

                bool hasPacked = grabbed.GetComponent<PackedOrderBox>() != null;
                bool inZone    = ShippingZone.Instance?.IsActiveAndPlayerInZone == true;

                if (inZone && hasPacked)
                {
                    WindUpTrace("blocked: in the shipping zone with a packed order");

                    // Submit order — instant, no charge
                    _throwCharging = false; _throwChargeTime = 0f;
                    HUDController.Instance?.HideThrowCharge();
                    string p = $"Deliver order!  {InputHints.E}";
                    if (p != _lastPrompt) { _lastPrompt = p; HUDController.Instance?.ShowPrompt(p); }
                    if (interactPressed) ShippingZone.Instance.Interact(gameObject);
                }
                else
                {
                    // The press starts the hold; what it was is decided when the key comes up.
                    // See _throwArmed for why the press frame alone is not enough to start it.
                    if ((eHeld || interactPressed) && _throwArmed && !_throwCharging)
                    {
                        _throwCharging = true;
                        WindUpTrace("winding up");
                    }
                    else if (eHeld && !_throwArmed && !_throwCharging)
                    {
                        WindUpTrace("blocked: E has not been released since the box was picked up");
                    }

                    if (_throwCharging)
                    {
                        if (eHeld) _throwChargeTime += Time.deltaTime;

                        if (!eHeld)
                        {
                            // Up: a tap puts the box down, a long hold throws it. A tap that went
                            // down and up between two frames lands here on its first frame.
                            FinishWindUp();
                        }
                        else if (_throwChargeTime >= ThrowHoldThreshold)
                        {
                            // Past the threshold — this is a throw. The bar measures the wind-up
                            // from the threshold on; the force curve (pct²) is applied on release.
                            float drawn = Mathf.Max(ThrowPct(), MeterFloor);

                            HUDController.Instance?.ShowThrowCharge(drawn);

                            // Square up to where the camera looks while winding up.
                            if (_tpc != null) _tpc.FaceCameraYaw = true;

                            _meterFed  = true;   // temporary, see the throw trace in FinishWindUp
                            _meterPeak = Mathf.Max(_meterPeak, drawn);

                            string p = $"Release {InputHints.EK} to throw";
                            if (p != _lastPrompt) { _lastPrompt = p; HUDController.Instance?.ShowPrompt(p); }
                        }
                        else
                        {
                            // Still inside the tap window: no meter, letting go now just puts it down.
                            HUDController.Instance?.HideThrowCharge();
                        }
                    }
                    else
                    {
                        string p = $"Drop box  {InputHints.E}  (hold to throw)";
                        if (p != _lastPrompt) { _lastPrompt = p; HUDController.Instance?.ShowPrompt(p); }
                    }
                }
                return;
            }
            else if (!eHeld)
            {
                // Looking at pallet/cart/bench — cancel any pending charge.
                //
                // Only with the key up. A player winding up in a warehouse aisle sweeps the
                // crosshair across a pallet, a shelf leg and a cart on the way to where he is
                // aiming, and resetting on the way past emptied the meter every time he crossed
                // one: the bar stayed on screen and never grew, because the seconds behind it kept
                // going back to zero. What the branch is for is deciding what E *means* when it is
                // pressed, and that is answered on the press, not on everything the view passes
                // over while it is already down.
                _throwCharging = false; _throwChargeTime = 0f;
                HUDController.Instance?.HideThrowCharge();
            }
            else
            {
                WindUpTrace("blocked: crosshair on " + _current.GetType().Name +
                            " and no wind-up was running");
            }
            // Looking at PalletStackZone, CartStorageInteractable, or AssemblyBench → fall through to E handler.
        }
        else if (eHeld)
        {
            WindUpTrace("blocked: no box in hand");
        }

        // Ladder busy-check is scoped to THIS player specifically (LadderController.
        // IsActorBusyWithLadder), not the global IsCarried/IsClimbing flags — those go true the
        // instant ANY player anywhere touches ANY ladder, which would freeze every other player's
        // interactions too and made it impossible for a second player to walk up and grab a ladder
        // someone else was climbing.
        bool blocked = UIManager.IsAnyPanelOpen
                    || CartController.PlayerOnCart
                    || ReachTruckController.PlayerOnTruck
                    || LadderController.IsActorBusyWithLadder(gameObject);

        if (blocked)
        {
            if (_current != null)
            {
                EndHighlight();
                _current    = null;
                _lastPrompt = null;
                HUDController.Instance?.HidePrompt();
            }
            return;
        }

        if (!alreadyScanned)
            Scan();

        if (_current is IHoldInteractable holdable && holdable.RequiresHold)
        {
            if (!ReferenceEquals(_current, _holdTarget)) { _holdTarget = holdable; _holdTime = 0f; }

            bool held = GameInputBindings.IsPressed(GameBindingAction.Interact);
            bool can  = CheckCanInteract();

            if (held && can)
            {
                _holdTime += Time.deltaTime;
                if (_holdTime >= holdable.HoldDuration)
                {
                    _holdTime   = 0f;
                    _holdTarget = null;
                    HUDController.Instance?.HideActionProgress();
                    holdable.Interact(gameObject);
                    AudioManager.Instance?.PlayInteractConfirm();
                }
                else
                {
                    HUDController.Instance?.HidePrompt();
                    HUDController.Instance?.ShowActionProgress(
                        HoldProgressLabel(holdable), _holdTime / holdable.HoldDuration);
                }
            }
            else if (_holdTime > 0f)
            {
                _holdTime   = 0f;
                HUDController.Instance?.HideActionProgress();
                _lastPrompt = holdable.InteractPrompt;
                HUDController.Instance?.ShowPrompt(_lastPrompt);
            }
            return;
        }
        if (_holdTime > 0f || _holdTarget != null)
            HUDController.Instance?.HideActionProgress();
        _holdTarget = null;
        _holdTime   = 0f;

        if (interactPressed)
        {
            if (_current != null && CheckCanInteract())
            {
                _current.Interact(gameObject);
                AudioManager.Instance?.PlayInteractConfirm();
            }
            else
                BoxPhysicsInteractable.ReleaseGrabbed(); // drop carried BoxWithPart when not aiming
        }

        if (dropPressed)
        {
            // G does two things. Aiming at a ladder wins, because that is a deliberate act on a
            // specific object; otherwise it puts the weapon down, which is what keeps picking one
            // up from being a one-way door. There is no backpack to empty — everything a player
            // carries is in his hands.
            if (_current is LadderController ladderTarget)
                ladderTarget.PickupSecondary(gameObject);
            else
                MeleeCombat.Instance?.DropWeapon();
        }
    }

    /// How far through the throw's wind-up the hold is, counted from the tap threshold.
    float ThrowPct() => Mathf.Clamp01((_throwChargeTime - ThrowHoldThreshold) / ThrowChargeMaxTime);

    /// The key came up on a running hold: put the box down if it was a tap, throw it if it was held
    /// past ThrowHoldThreshold.
    void FinishWindUp()
    {
        if (_throwChargeTime < ThrowHoldThreshold)
        {
            WindUpTrace($"tap after {_throwChargeTime:0.00}s: box put down");
            BoxPhysicsInteractable.ReleaseGrabbed(DropForce);
        }
        else
        {
            float pct   = ThrowPct();
            float force = Mathf.Lerp(ThrowForceMin, ThrowForceMax, pct * pct);

            // Temporary, paired with BoxPhysicsInteractable's throw trace: says what the wind-up
            // was actually worth and whether the meter had anything to draw on.
            if (BoxPhysicsInteractable.TraceThrows)
                Debug.Log($"[BoxThrow] charge={_throwChargeTime:0.00}s " +
                          $"pct={pct:0.00} force={force:0.0} " +
                          $"meterFedThisWindUp={_meterFed} " +
                          $"meterPeak={_meterPeak:0.00} " +
                          $"hud={HUDController.Instance != null}");

            // Face the throw: it goes along the camera, so the body should too.
            if (_tpc != null) _tpc.SnapFacingToCamera();

            if (BoxPhysicsInteractable.GrabbedBox != null)
                AudioManager.Instance?.PlayAt(GameAudioEvent.BoxThrow,
                    BoxPhysicsInteractable.GrabbedBox.transform.position);
            BoxPhysicsInteractable.ReleaseGrabbed(force);
            BoxThrown?.Invoke();
        }

        _meterFed        = false;
        _meterPeak       = 0f;
        _throwCharging   = false;
        _throwChargeTime = 0f;
        HUDController.Instance?.HideThrowCharge();
    }

    private void Scan()
    {
        // Re-asked every scan, not cached once, and that is not paranoia. Camera.main is whichever
        // enabled camera is tagged MainCamera *now*, and which one that is changes during play: the
        // TPS rig, the vehicle and cart cameras, and — in a session — the co-op spawn flow, which
        // switches the owner's camera root on after this component has already run.
        //
        // Held from the first scan, a stale reference keeps aiming out of a camera that stopped
        // rendering, so the crosshair ray leaves from somewhere the player is not looking. Prompts
        // still appear, because something is always under that ray; what breaks is everything that
        // asks *what* is under it. Kept as a fallback rather than overwritten with null, so a frame
        // with no tagged camera at all does not blind the scan.
        var live = Camera.main;
        if (live != null) _cam = live;
        if (_cam == null) return;

        // ViewportPointToRay shoots through the screen center — works for both FPS and TPS.
        // Extend cast range by camera-to-player distance so _range stays relative to the character.
        var ray = _cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        float camDist    = Vector3.Distance(_cam.transform.position, transform.position);
        float castRange  = camDist + _range;
        Debug.DrawRay(ray.origin, ray.direction * castRange, Color.red, 0f);

        IInteractable next   = null;
        GameObject    nextGO = null;

        // RaycastAll (non-alloc) so boxes stored INSIDE a cart's BoxCollider are still reachable.
        // Physics.Raycast would stop at the cart body, hiding bottom-layer boxes.
        // Interaction volumes (including charging stations) are triggers so they can be aimed at
        // without physically blocking the player. Non-interactable triggers are skipped below.
        int hitCount = Physics.RaycastNonAlloc(ray, _scanHitsBuf, castRange, _interactMask, QueryTriggerInteraction.Collide);
        if (hitCount > _scanHitsBuf.Length) hitCount = _scanHitsBuf.Length; // buffer filled — extremely dense hit stack
        System.Array.Sort(_scanHitsBuf, 0, hitCount, s_hitDistanceComparer);

        for (int hi = 0; hi < hitCount; hi++)
        {
            var hit = _scanHitsBuf[hi];

            // GetComponentInParent returns disabled MonoBehaviours too — skip them so a
            // disabled BoxPhysicsInteractable doesn't shadow an enabled BoxWithPart.
            IInteractable candidate = null;
            hit.collider.GetComponentsInParent(true, _interactableBuf);
            foreach (var interactable in _interactableBuf)
            {
                if (interactable is MonoBehaviour mb && !mb.enabled) continue;
                candidate = interactable;
                break;
            }
            if (candidate == null) continue;
            if (candidate is MonoBehaviour cmb) nextGO = cmb.gameObject;

            // Prefer BoxInteract-tagged ancestor over nested child decals/sub-meshes.
            var t = hit.collider.transform;
            while (t != null)
            {
                if (t.CompareTag("BoxInteract"))
                {
                    var root = t.GetComponent<IInteractable>();
                    if (root != null && (root is not MonoBehaviour rmb || rmb.enabled))
                    { candidate = root; nextGO = t.gameObject; }
                    break;
                }
                t = t.parent;
            }

            // When holding a box and looking at a box on a pallet → redirect to zone.
            if (BoxPhysicsInteractable.GrabbedBox != null &&
                candidate is BoxPhysicsInteractable targetBox)
            {
                // PalletStackZone detaches authored boxes from the pallet Transform at runtime
                // so their Rigidbody is not moved twice (once by hierarchy inheritance and once
                // by physics/follow). Therefore GetComponentInParent alone cannot identify the
                // pallet when the crosshair is on one of its detached cargo boxes. The cargo's
                // explicit membership is authoritative; hierarchy lookup remains the fallback
                // for older/authored arrangements that have not detached yet.
                var zone = targetBox.StoredOnPallet
                           ?? hit.collider.GetComponentInParent<PalletStackZone>();
                if (zone != null) { candidate = zone; nextGO = zone.gameObject; }
            }

            // When holding a box and looking at a cart → redirect to CartStorageInteractable.
            if (BoxPhysicsInteractable.GrabbedBox != null && candidate is CartController)
            {
                var storage = hit.collider.GetComponentInParent<CartStorageInteractable>();
                if (storage != null) { candidate = storage; nextGO = storage.gameObject; }
            }

            // Cart body hit while NOT holding a box: check if a stored box is also in the
            // ray hits — if so, prefer the box so the player can pick it off the cart.
            if (BoxPhysicsInteractable.GrabbedBox == null &&
                (candidate is CartController || candidate is CartStorageInteractable))
            {
                BoxPhysicsInteractable storedBox = null;
                for (int h2 = 0; h2 < hitCount; h2++)
                {
                    var bpi = _scanHitsBuf[h2].collider.GetComponentInParent<BoxPhysicsInteractable>();
                    if (bpi != null && bpi.StoredInCart != null) { storedBox = bpi; break; }
                }
                if (storedBox != null) { candidate = storedBox; nextGO = storedBox.gameObject; }
            }

            next = candidate;
            break;
        }

        // Nothing under the ray, but a stacked box just beside it. This is deliberately a fallback
        // rather than the main scan, so a box cannot steal focus from a button or repair target
        // that is actually under the crosshair.
        if (next == null)
        {
            int n = Physics.SphereCastNonAlloc(ray, BoxAimRadius, _scanHitsBuf, castRange,
                                               _interactMask, QueryTriggerInteraction.Ignore);
            if (n > _scanHitsBuf.Length) n = _scanHitsBuf.Length;

            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = _scanHitsBuf[i];
                var box = h.collider.GetComponentInParent<BoxPhysicsInteractable>();
                if (box == null || !box.enabled || box == BoxPhysicsInteractable.GrabbedBox) continue;
                if (h.distance >= best) continue;
                best   = h.distance;
                next   = box;
                nextGO = box.gameObject;
            }
        }

        // A thrown box crosses the thin ordinary interaction ray too quickly to target reliably.
        // When the player is empty-handed and no normal target won, give flight catches a slightly
        // wider, short-range aiming volume. It remains a fallback so it can never steal E from a
        // bench, button, pallet or an ordinary box the player is deliberately looking at.
        if (next == null && BoxPhysicsInteractable.GrabbedBox == null)
        {
            float catchCastRange = camDist + FlyingBoxCatchRange;
            int n = Physics.SphereCastNonAlloc(ray, FlyingBoxCatchRadius, _scanHitsBuf,
                                               catchCastRange, _interactMask,
                                               QueryTriggerInteraction.Ignore);
            if (n > _scanHitsBuf.Length) n = _scanHitsBuf.Length;

            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = _scanHitsBuf[i];
                var box = h.collider.GetComponentInParent<BoxPhysicsInteractable>();
                if (box == null || !box.enabled || !box.CanBeCaught) continue;
                if (h.distance >= best) continue;
                best   = h.distance;
                next   = box;
                nextGO = box.gameObject;
            }
        }

        // Nothing under the ray, but a weapon just beside it. A weapon's collider is its real shape
        // now — it has to be, to fall and lie on the floor properly — and a bat lying down is six
        // centimetres tall, which the thin centre ray walks straight past. Fattened for weapons
        // only, and never through whatever the ray itself struck first.
        if (next == null)
        {
            float firstHit = hitCount > 0 ? _scanHitsBuf[0].distance : castRange;
            int n = Physics.SphereCastNonAlloc(ray, WeaponAimRadius, _scanHitsBuf, castRange,
                                               _interactMask, QueryTriggerInteraction.Ignore);
            if (n > _scanHitsBuf.Length) n = _scanHitsBuf.Length;

            float best = firstHit + 0.3f;
            for (int i = 0; i < n; i++)
            {
                var h = _scanHitsBuf[i];
                if (h.distance > best) continue;
                var weapon = h.collider.GetComponentInParent<MeleeWeaponItem>();
                if (weapon == null || !weapon.enabled) continue;
                best   = h.distance;
                next   = weapon;
                nextGO = weapon.gameObject;
            }
        }

        // Nothing under the ray: offer patching yourself up, if there is anything to patch.
        //
        // Last, and only when the ray found nothing at all, so that looking at any real object
        // always wins. Otherwise holding E to open a gate while injured would quietly repair an
        // arm instead — and the player would have no way to tell why.
        if (next == null && SelfRepairTarget.Available)
        {
            next   = SelfRepairTarget.Instance;
            nextGO = SelfRepairTarget.Instance.gameObject;
        }

        if (next != _current)
        {
            EndHighlight();
            _current = next;

            if (_current != null)
            {
                bool can = CheckCanInteract();
                if (_current is not INoOutlineHighlight)
                    BeginHighlight(nextGO, can);
                _lastPrompt = _current.InteractPrompt;
                HUDController.Instance?.ShowPrompt(_lastPrompt);
            }
            else
            {
                _lastPrompt = null;
                HUDController.Instance?.HidePrompt();
            }
        }
        else if (_current != null)
        {
            // Only push prompt to HUD when text actually changes (avoids per-frame allocs
            // in property getters like AssemblyBench.InteractPrompt which build strings).
            var prompt = _current.InteractPrompt;
            if (prompt != _lastPrompt)
            {
                _lastPrompt = prompt;
                HUDController.Instance?.ShowPrompt(prompt);
            }

            bool can = CheckCanInteract();
            if (can != _hlCan) SwapHighlightColor(can);
        }
    }

    bool CheckCanInteract()
    {
        var check = _current as ICanInteractCheck;
        return check == null || check.CanInteract(gameObject);
    }

    // ── Outline management ───────────────────────────────────────────────────

    void BeginHighlight(GameObject go, bool can)
    {
        if (go == null) return;

        var rends = go.GetComponentsInChildren<Renderer>(false);
        if (rends.Length == 0) return;

        _hlRends = rends;
        _hlCan   = can;
        var mat  = OutlineMat(can);
        if (mat == null) return;

        for (int i = 0; i < _hlRends.Length; i++)
        {
            if (TryCreateSubmeshOverlay(_hlRends[i], mat, out var overlay))
            {
                _hlSubmeshOverlays.Add(overlay);
                continue;
            }

            var src = _hlRends[i].sharedMaterials;
            var dst = new Material[src.Length + 1];
            System.Array.Copy(src, dst, src.Length);
            dst[src.Length] = mat;
            _hlRends[i].sharedMaterials = dst;
        }
    }

    // Removes only the specific outline material WE added (found by reference, not position),
    // never a snapshot of the whole array. A snapshot-restore approach used to blindly overwrite
    // whatever the renderer's materials look like NOW with whatever they looked like back when
    // BeginHighlight() ran — if another system (e.g. TutorialHighlighter) independently added or
    // removed its own material on this same renderer while we were tracking it, restoring our
    // stale snapshot here would silently undo that, e.g. resurrecting a tutorial highlight after
    // its step had already cleared it, the moment the player looked away.
    void EndHighlight()
    {
        if (_hlRends == null) return;
        RemoveOwnOutlineMaterial();
        DestroySubmeshOverlays();
        _hlRends = null;
    }

    void SwapHighlightColor(bool can)
    {
        if (_hlRends == null) return;
        var newMat = OutlineMat(can);
        if (newMat == null) return;
        RemoveOwnOutlineMaterial();
        _hlCan = can;

        for (int i = 0; i < _hlSubmeshOverlays.Count; i++)
        {
            var overlay = _hlSubmeshOverlays[i];
            if (overlay == null) continue;
            var materials = overlay.sharedMaterials;
            for (int slot = 0; slot < materials.Length; slot++) materials[slot] = newMat;
            overlay.sharedMaterials = materials;
        }

        for (int i = 0; i < _hlRends.Length; i++)
        {
            if (_hlRends[i] == null) continue;
            if (HasSubmeshOverlayFor(_hlRends[i])) continue;
            var src = _hlRends[i].sharedMaterials;
            var dst = new Material[src.Length + 1];
            System.Array.Copy(src, dst, src.Length);
            dst[src.Length] = newMat;
            _hlRends[i].sharedMaterials = dst;
        }
    }

    // Strips whichever of our two outline materials (_matCan / _matCannot) is currently present
    // on each tracked renderer, wherever it happens to sit in the array — leaves every other
    // slot (the object's real materials, and any other system's own appended material) untouched.
    void RemoveOwnOutlineMaterial()
    {
        for (int i = 0; i < _hlRends.Length; i++)
        {
            if (_hlRends[i] == null) continue;
            var current = _hlRends[i].sharedMaterials;
            int idx = System.Array.IndexOf(current, _matCan);
            if (idx < 0) idx = System.Array.IndexOf(current, _matCannot);
            if (idx < 0) continue;

            var trimmed = new Material[current.Length - 1];
            System.Array.Copy(current, 0, trimmed, 0, idx);
            System.Array.Copy(current, idx + 1, trimmed, idx, current.Length - idx - 1);
            _hlRends[i].sharedMaterials = trimmed;
        }
    }

    Material OutlineMat(bool can)
    {
        if (can)
        {
            if (_matCan == null) _matCan = LoadOrCreate("FPS_Outline_Can", new Color(0.25f, 0.65f, 1.00f));
            return _matCan;
        }
        if (_matCannot == null) _matCannot = LoadOrCreate("FPS_Outline_Cannot", new Color(1.00f, 0.25f, 0.20f));
        return _matCannot;
    }

    static Material LoadOrCreate(string resourceName, Color fallbackColor)
    {
        var mat = Resources.Load<Material>(resourceName);
        if (mat != null) return mat;

        // Fallback: create runtime material (works in Editor; for builds add shader to
        // Project Settings > Graphics > Always Included Shaders, or run
        // Tools > Warehouse Demo > Create Outline Materials first).
        var shader = Shader.Find("Custom/FPS_Interact_Outline");
        if (shader == null)
        {
            Debug.LogWarning("[PlayerInteraction] FPS_Interact_Outline shader not found — no hover outline.");
            return null;
        }
        var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        m.SetColor("_OutlineColor", fallbackColor);
        m.SetFloat("_OutlineWidth", 0.005f);
        return m;
    }

    // ── Temporary: why a wind-up did not start ────────────────────────────────
    //
    // Paired with BoxPhysicsInteractable's throw trace, and to be deleted with it. Six different
    // branches can swallow a wind-up, and not one of them is visible on screen: "the bar fills for
    // him and sometimes not for me" is a report the machine has to answer, because the two players
    // cannot see which branch each of them took.
    //
    // Once per change of answer rather than once per frame, so a held key is one line and not four
    // hundred.
    string _windUpTrace;

    /// The widest the meter was driven during the current wind-up. Temporary, with the rest of the
    /// trace: it is the one number that separates "the bar never filled" from "the bar was never
    /// told to fill".
    float _meterPeak;

    void WindUpTrace(string what)
    {
        if (!BoxPhysicsInteractable.TraceThrows) return;
        if (what == _windUpTrace) return;

        _windUpTrace = what;

        Debug.Log($"[BoxThrow] {what}  |  armed={_throwArmed} charging={_throwCharging} " +
                  $"t={_throwChargeTime:0.00}s box={BoxPhysicsInteractable.GrabbedBox != null} " +
                  $"aim={(_current != null ? _current.GetType().Name : "nothing")}");
    }

    private void OnDisable()
    {
        EndHighlight();
        _throwCharging   = false;
        _throwChargeTime = 0f;
        _throwArmed      = false;
        _hadBox          = false;
        if (_tpc != null) _tpc.FaceCameraYaw = false;

        // This component is switched off whenever the body is taken over — a ragdoll, a vehicle
        // seat. A meter left on screen would report a wind-up that no longer exists.
        HUDController.Instance?.HideThrowCharge();
    }

    static string HoldProgressLabel(IHoldInteractable hold)
    {
        return hold switch
        {
            ReachTruckController => "REPAIRING FORKLIFT",
            AssemblyBench bench  => bench.HoldProgressLabel,
            PowerBoxRepairTarget => "REPAIRING POWER BOX",
            GateRepairTarget     => "REPAIRING GATE",
            RadioRepairPanel     => "REPAIRING RADIO",
            SelfRepairTarget     => "SELF REPAIR",
            SeveredLimbItem      => "FITTING LIMB",
            _                    => "ACTION PROGRESS",
        };
    }

    // Appending one material to a renderer does not cover a multi-submesh mesh: Unity assigns the
    // extra pass only to the final submesh. Packed-order cartons use several material slots, which
    // left whole faces without the interaction shader. A render-only duplicate gives every
    // submesh its own outline slot while the original renderer keeps drawing the cardboard below.
    static bool TryCreateSubmeshOverlay(Renderer source, Material material, out Renderer overlay)
    {
        overlay = null;
        if (source == null) return false;

        Mesh mesh = null;
        if (source is MeshRenderer)
        {
            var filter = source.GetComponent<MeshFilter>();
            mesh = filter != null ? filter.sharedMesh : null;
        }
        else if (source is SkinnedMeshRenderer skinned)
        {
            mesh = skinned.sharedMesh;
        }

        if (mesh == null || mesh.subMeshCount <= 1) return false;

        var go = new GameObject("InteractionHighlight_Submeshes")
        {
            layer = source.gameObject.layer,
            hideFlags = HideFlags.HideAndDontSave
        };
        go.transform.SetParent(source.transform, false);

        if (source is SkinnedMeshRenderer sourceSkinned)
        {
            var target = go.AddComponent<SkinnedMeshRenderer>();
            target.sharedMesh = sourceSkinned.sharedMesh;
            target.bones = sourceSkinned.bones;
            target.rootBone = sourceSkinned.rootBone;
            target.localBounds = sourceSkinned.localBounds;
            target.updateWhenOffscreen = sourceSkinned.updateWhenOffscreen;
            overlay = target;
        }
        else
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            overlay = go.AddComponent<MeshRenderer>();
        }

        overlay.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        overlay.receiveShadows = false;
        overlay.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        overlay.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        overlay.sortingLayerID = source.sortingLayerID;
        overlay.sortingOrder = source.sortingOrder;

        var materials = new Material[mesh.subMeshCount];
        for (int i = 0; i < materials.Length; i++) materials[i] = material;
        overlay.sharedMaterials = materials;
        return true;
    }

    bool HasSubmeshOverlayFor(Renderer source)
    {
        for (int i = 0; i < _hlSubmeshOverlays.Count; i++)
            if (_hlSubmeshOverlays[i] != null && _hlSubmeshOverlays[i].transform.parent == source.transform)
                return true;
        return false;
    }

    void DestroySubmeshOverlays()
    {
        for (int i = 0; i < _hlSubmeshOverlays.Count; i++)
            if (_hlSubmeshOverlays[i] != null)
                Destroy(_hlSubmeshOverlays[i].gameObject);
        _hlSubmeshOverlays.Clear();
    }

}
