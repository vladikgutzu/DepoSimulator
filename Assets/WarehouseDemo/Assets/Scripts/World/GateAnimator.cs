using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

/// Opens, closes and rattles a roller gate.
///
/// Sits on gate_01_GRP next to the Animator that Tools/Warehouse/Build Gate Animation put there.
/// Nothing in the game calls it yet — the gates are currently only used as delivery and shipping
/// anchors — so this is the seam for whatever does: a delivery arriving, an order shipping, a
/// raider bolting for the exit.
public class GateAnimator : MonoBehaviour
{
    /// Every gate in the scene, for whatever wants to pick one — GateBreachEvent does.
    public static readonly List<GateAnimator> All = new();

    /// Rename a gate to contain this and zombies may come through it.
    ///
    /// A name rather than a component, because the choice is made in the scene by hand: most of
    /// the fourteen gates face somewhere no raid should ever come from, and which ones do is a
    /// level-design decision, not something code can work out. Matched as a substring and
    /// case-insensitively so "gate_01_GRP_zombieSpawn (1)" — what Unity names a duplicate — still
    /// counts.
    public const string SpawnMarker = "zombieSpawn";

    /// True on a gate marked as a place raiders may break in through.
    public bool IsZombieSpawn =>
        name.IndexOf(SpawnMarker, System.StringComparison.OrdinalIgnoreCase) >= 0;

    static readonly int ProgressHash = Animator.StringToHash("Progress");

    [Tooltip("Seconds for the full travel, shut to open. The clip's own easing rides on top.")]
    [SerializeField] float _travelTime = 2.2f;

    [Tooltip("How quickly the rattle fades in and out, in weight per second.")]
    [SerializeField] float _shakeBlend = 4f;

    Animator _animator;
    int      _shakeLayer = -1;
    float    _shake;
    float    _shakeTarget;

    // Where the gate is, 0 shut and 1 open. The controller's single state is scrubbed by this
    // rather than run through open/close states, so the gate holds either end on its own and can
    // be reversed from anywhere in between — see BuildGateAnimation for why that mattered.
    float _progress;
    float _progressTarget;

    // The leaf's own position in the scene, which the clip knows nothing about — see LateUpdate.
    Vector3 _authored;

    /// True once the gate has been told to open, whether or not it has finished travelling.
    public bool IsOpen { get; private set; }

    /// Every gate a raid may come through, in a stable order.
    ///
    /// Sorted by name so the same gate is the same index on every peer — the breach event puts one
    /// of these indices on the wire, and so does a repair.
    public static List<GateAnimator> Marked()
    {
        var list = new List<GateAnimator>();

        // The registry when it has one, a scan when it does not.
        //
        // `All` fills in OnEnable, so it is empty for anything asking before the scene has woken
        // up — and completely empty in the editor, where OnEnable never runs at all. That made
        // every editor tool and every early caller quietly see a warehouse with no gates in it.
        // The scan costs a search of the scene and only happens when there is nothing better.
        var source = All.Count > 0
            ? (IEnumerable<GateAnimator>)All
            : Object.FindObjectsByType<GateAnimator>(FindObjectsInactive.Include,
                                                     FindObjectsSortMode.None);

        foreach (var gate in source)
            if (gate != null && gate.IsZombieSpawn && gate.FloorAnchor != null) list.Add(gate);

        list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return list;
    }

    public static GateAnimator ByIndex(int index)
    {
        var marked = Marked();
        return index >= 0 && index < marked.Count ? marked[index] : null;
    }

    /// This gate's place in Marked(), or -1 if it is not one. Recomputed rather than cached: gates
    /// are few, and a cached index would go stale the moment one is renamed in the editor.
    public int MarkedIndex => Marked().IndexOf(this);

    /// True from the moment a raid forces this gate until somebody repairs it.
    ///
    /// A forced gate stays open and stays cracked. That is the point: the damage is a standing
    /// consequence rather than something the event tidies away after itself, and until it is put
    /// right the next raid walks straight in through the hole instead of having to spend twenty
    /// seconds making one.
    public bool IsBreached { get; private set; }

    /// The raid got through. The gate stays open and broken until repaired.
    public void MarkBreached()
    {
        if (IsBreached) return;

        IsBreached = true;
        GateRepairTarget.Fit(this);
    }

    /// Put right with a toolkit — the shutter comes down and the cracks go.
    public void Repair()
    {
        if (!IsBreached) return;

        IsBreached = false;
        Close();

        var cracks = GetComponent<GateCracks>();
        if (cracks != null) cracks.Clear();
    }

    /// True while this gate should be marked on the minimap as the one being broken into.
    ///
    /// Set by whatever raid is using it rather than worked out here, because *when* it goes up is
    /// a design decision that differs by mode: the dispatcher is told at once because relaying it
    /// is the job, and a solo player is told three seconds before the shutter does it for them.
    /// MinimapController reads this the way it reads power boxes — it scans, nothing pushes at it.
    public bool ShowOnMap { get; set; }

    void Awake()
    {
        _animator   = GetComponent<Animator>();
        _shakeLayer = _animator != null ? _animator.GetLayerIndex("Shake") : -1;

        // Where the level put this leaf. Captured before the animator has written anything, which
        // Awake is early enough to guarantee.
        _authored = transform.localPosition;

    }

    // ── Doorway barrier ───────────────────────────────────────────────────────

    GameObject _barrier;

    void SetBarrier(bool on)
    {
        if (!on)
        {
            if (_barrier != null) _barrier.SetActive(false);
            return;
        }

        if (_barrier == null)
        {
            if (!TryGetSides(out Vector3 inside, out Vector3 outside, 1f)) return;

            Vector3 inward = inside - outside;
            inward.y = 0f;
            if (inward.sqrMagnitude < 0.0001f) return;
            inward.Normalize();
            Vector3 across = Vector3.Cross(Vector3.up, inward);

            const float height = 5f;
            const float depth  = 0.4f;
            float width = DoorwayWidth(across) + 0.6f;

            // Not a child of the gate: the leaf's transform is what the clip lifts, and a barrier
            // riding it would go up with the shutter.
            _barrier = new GameObject($"{name}_DoorBarrier");
            _barrier.transform.SetPositionAndRotation(
                FloorAnchor.position + Vector3.up * (height * 0.5f),
                Quaternion.LookRotation(inward, Vector3.up));

            var box = _barrier.AddComponent<BoxCollider>();
            box.size = new Vector3(width, height, depth);

            // Ignore Raycast keeps it out of the Cinemachine camera-collision filter, so the
            // camera doesn't bump into an invisible wall; the CharacterController still hits it.
            _barrier.layer = 2;
        }

        _barrier.SetActive(true);
    }

    void OnDestroy()
    {
        if (_barrier != null) Destroy(_barrier);
    }

    void OnEnable()  => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// The ground-level spot in the doorway. Null on a gate whose model has no floor decal.
    ///
    /// The gate's own transform sits about 4.6 m up — it is the leaf's pivot, not the floor —
    /// and decal_floor under the same gate is the ground-level anchor, which is what
    /// VehicleDeliveryManager already trusts for exactly this reason.
    ///
    /// Resolved on demand rather than in Awake so that editor tools, which run with no Awake ever
    /// having happened, can ask a gate where its doorway is.
    public Transform FloorAnchor
    {
        get
        {
            if (_floorAnchor != null) return _floorAnchor;

            _floorAnchor = transform.root.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name == "decal_floor")
                .OrderBy(t => Vector3.Distance(t.position, transform.position))
                .FirstOrDefault();

            return _floorAnchor;
        }
    }

    Transform _floorAnchor;

    /// How wide the opening is, measured along `across`.
    ///
    /// Taken from the leaf's own renderer rather than typed in: it is the thing that fills the
    /// doorway, so its width is the doorway's. Anyone placing bodies in front of a gate needs this
    /// — four raiders spread on a fixed pitch put the outer two inside the wall.
    public float DoorwayWidth(Vector3 across)
    {
        var leaf = GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(t => t.name == "gate_01");

        var rend = leaf != null ? leaf.GetComponent<Renderer>() : null;
        if (rend == null) return 2.5f;

        // World-axis-aligned bounds, so the width along `across` is whichever of x/z it points at.
        var size = rend.bounds.size;
        float width = Mathf.Abs(across.x) * size.x + Mathf.Abs(across.z) * size.z;

        return Mathf.Clamp(width, 1.2f, 6f);
    }

    /// Which way the warehouse is, from the doorway.
    ///
    /// Taken from the navmesh rather than from the gate's axes. Different gates in this scene
    /// have the doorway on different local axes, and two of them sit in corners where more than
    /// one direction has floor — so "which way is in" cannot be read off a transform. The nearest
    /// navmesh point to the doorway is inside by definition, and the direction to it is the way
    /// through.
    public bool TryGetSides(out Vector3 inside, out Vector3 outside, float depth = 3f)
    {
        inside = outside = Vector3.zero;
        if (FloorAnchor == null) return false;

        Vector3 door = FloorAnchor.position;
        if (!NavMesh.SamplePosition(door, out var hit, 6f, NavMesh.AllAreas)) return false;

        Vector3 inward = hit.position - door;
        inward.y = 0f;
        if (inward.sqrMagnitude < 0.01f) inward = -transform.forward;
        inward.Normalize();

        inside  = door + inward * depth;
        outside = door - inward * depth;

        // The inside point has to be somewhere an agent can actually stand, or the raiders arrive
        // and immediately go inert.
        if (NavMesh.SamplePosition(inside, out var insideHit, 4f, NavMesh.AllAreas))
            inside = insideHit.position;

        outside.y = door.y;
        return true;
    }

    public void Open()   => Set(true);
    public void Close()  => Set(false);
    public void Toggle() => Set(!IsOpen);

    void Set(bool open, bool silent = false)
    {
        // Only when it is actually going to move. Close() is called both to shut a gate that is
        // open and to tidy up after an event that never opened it, and the second one must not
        // start a motor.
        bool moves = open != IsOpen;

        IsOpen          = open;
        _progressTarget = open ? 1f : 0f;

        // Up the instant the gate is told to shut, not when the leaf gets down to head height:
        // the shutter takes over two seconds to travel and a player could duck under it into the
        // corridor the raiders come out of. Dropped the moment it is told to open.
        if (!open && moves) SetBarrier(true);
        else if (open)      SetBarrier(false);

        if (moves && !silent) Audio?.Motor();
    }

    /// The gate's own sound, fitted on demand. Held here rather than left to callers so that
    /// anything driving a gate — a delivery, a raid, a debug menu — is audible without knowing it
    /// needs to be.
    public GateAudio Audio => _audio != null ? _audio : _audio = GateAudio.Ensure(this);

    GateAudio _audio;

    /// Fully shut or fully open with no travel — for setting a gate up at scene start.
    public void SnapTo(bool open)
    {
        // Silent: this is for putting a gate into position at scene start, and a warehouse whose
        // shutters all run their motors on the first frame is not the intent.
        Set(open, silent: true);
        _progress = _progressTarget;
        if (_animator != null) _animator.SetFloat(ProgressHash, _progress);
    }

    /// 0 is still, 1 is the full rattle. Eased rather than snapped: a gate that starts and stops
    /// shaking on a single frame reads as a glitch rather than as strain.
    public void SetShake(float amount)
    {
        _shakeTarget = Mathf.Clamp01(amount);

        // Nothing is created just to be told it is silent — SetShake(0) is the standard cleanup
        // call and runs on gates no raid ever touched.
        if (_audio != null || _shakeTarget > 0f) Audio?.SetStrain(_shakeTarget);
    }

    /// The clip writes a pure offset — (0, lift, 0) plus whatever the additive rattle adds — and
    /// this puts it back where the level actually placed the gate.
    ///
    /// It has to work this way because an animation curve on a Transform is absolute, not relative:
    /// the clip was authored around zero, so playing it moved every gate in the warehouse to its
    /// parent's origin. Baking the authored position into the clip instead would not do either —
    /// the fourteen gates sit at two different local positions, and nothing stops a level author
    /// adding a third.
    ///
    /// In LateUpdate because that is after the animation pass: the animator writes the clip value
    /// during the frame, and this is what turns it into a position. Skipped outright when the
    /// animator is not writing, or the base would be added to its own output over and over.
    void LateUpdate()
    {
        if (_animator == null || !_animator.enabled) return;

        transform.localPosition = _authored + transform.localPosition;
    }

    void Update()
    {
        if (_animator == null) return;

        if (!Mathf.Approximately(_progress, _progressTarget))
        {
            float rate = _travelTime > 0.01f ? Time.deltaTime / _travelTime : 1f;
            _progress  = Mathf.MoveTowards(_progress, _progressTarget, rate);
            _animator.SetFloat(ProgressHash, _progress);
        }

        if (_shakeLayer >= 0 && !Mathf.Approximately(_shake, _shakeTarget))
        {
            _shake = Mathf.MoveTowards(_shake, _shakeTarget, _shakeBlend * Time.deltaTime);
            _animator.SetLayerWeight(_shakeLayer, _shake);
        }
    }

    // ── Debug ─────────────────────────────────────────────────────────────────

    [ContextMenu("Debug / Open")]   void DbgOpen()  => Open();
    [ContextMenu("Debug / Close")]  void DbgClose() => Close();
    [ContextMenu("Debug / Shake on")]  void DbgShakeOn()  => SetShake(1f);
    [ContextMenu("Debug / Shake off")] void DbgShakeOff() => SetShake(0f);
}
