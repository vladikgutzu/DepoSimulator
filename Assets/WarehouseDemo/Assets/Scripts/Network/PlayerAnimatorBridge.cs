using UnityEngine;

/// Place on the scene PlayerArmature (same GameObject as ThirdPersonController).
/// Copies animator parameters to the NetworkPlayer's CharacterVisual Animator each frame
/// so NetworkAnimator can replicate them to all clients.
public class PlayerAnimatorBridge : MonoBehaviour
{
    static readonly int SpeedHash      = Animator.StringToHash("Speed");
    static readonly int MotionSpeed    = Animator.StringToHash("MotionSpeed");
    static readonly int Grounded       = Animator.StringToHash("Grounded");
    static readonly int Jump           = Animator.StringToHash("Jump");
    static readonly int FreeFall       = Animator.StringToHash("FreeFall");
    static readonly int IsCarryingBox  = Animator.StringToHash("IsCarryingBox");
    static readonly int IsCrouching    = Animator.StringToHash("IsCrouching");
    static readonly int IsClimbing     = Animator.StringToHash("IsClimbing");
    static readonly int ClimbSpeed     = Animator.StringToHash("ClimbSpeed");
    static readonly int IsSlipping     = Animator.StringToHash("IsSlipping");

    /// Repairing yourself, which on the puppet becomes the weight of the crouch layer.
    ///
    /// Named on RepairCrouch rather than spelled out again here: the two have to agree, and a
    /// string typed in twice is a string that eventually disagrees.
    static readonly int IsRepairing    = Animator.StringToHash(RepairCrouch.CrouchParam);

    Animator              _source;   // this scene player's Animator (driven by ThirdPersonController)
    Animator              _target;   // CharacterVisual Animator on NetworkPlayer prefab
    FirstPersonController _fpc;      // for IsCrouching state (not an Animator param on FPS source)

    /// The parameters both controllers actually have, worked out once when the target resolves.
    ///
    /// Whether a controller has a parameter is a fact about the asset, so asking eleven times a
    /// frame and being told off eleven times a frame is pure noise: one parameter missing from one
    /// of the two controllers put "Parameter 'Hash -1154928320' does not exist" into the console a
    /// few hundred times a second, which is enough to bury anything else the session has to say —
    /// including the warnings meant to explain a bug someone is in the middle of chasing.
    int[] _floats;
    int[] _bools;
    bool  _targetHasCrouch;
    RuntimeAnimatorController _resolvedSourceController;
    RuntimeAnimatorController _resolvedTargetController;

    void Awake()
    {
        _source = GetComponent<Animator>();
        _fpc    = GetComponent<FirstPersonController>();
    }

    void LateUpdate()
    {
        if (_source == null) return;

        // Lazily resolve target once NetworkPlayer.Local is spawned
        if (_target == null)
        {
            var np = NetworkPlayer.Local;
            if (np == null) return;

            // CharacterVisual is a direct child of NetworkPlayer
            var visual = np.transform.Find("CharacterVisual");
            if (visual == null) return;

            _target = visual.GetComponent<Animator>();
            if (_target == null) return;

            ResolveParameters();
        }

        // Vehicles can replace animation clips at runtime. Re-resolve if either controller
        // reference changes so this bridge never keeps querying hashes from an old layout.
        if (_resolvedSourceController != _source.runtimeAnimatorController
            || _resolvedTargetController != _target.runtimeAnimatorController)
            ResolveParameters();

        foreach (int p in _floats) _target.SetFloat(p, _source.GetFloat(p));
        foreach (int p in _bools)  _target.SetBool(p,  _source.GetBool(p));

        // IsCrouching comes from the FPS controller, not the Animator source — so it is asked of
        // the target only.
        if (_targetHasCrouch) _target.SetBool(IsCrouching, _fpc != null && _fpc.IsCrouching);
    }

    /// Keeps only the parameters both controllers declare. A parameter on one and not the other
    /// cannot be copied anywhere useful, and asking anyway is what floods the console.
    void ResolveParameters()
    {
        _resolvedSourceController = _source.runtimeAnimatorController;
        _resolvedTargetController = _target.runtimeAnimatorController;
        var onSource = NameHashes(_source);
        var onTarget = NameHashes(_target);

        _floats = Both(new[] { SpeedHash, MotionSpeed, ClimbSpeed });
        _bools  = Both(new[] { Grounded, Jump, FreeFall, IsCarryingBox, IsClimbing, IsSlipping,
                               IsRepairing });

        _targetHasCrouch = onTarget.Contains(IsCrouching);

        Missing(onSource, "source", _source);
        Missing(onTarget, "target", _target);

        int[] Both(int[] wanted)
        {
            var kept = new System.Collections.Generic.List<int>(wanted.Length);
            foreach (int h in wanted)
                if (onSource.Contains(h) && onTarget.Contains(h)) kept.Add(h);
            return kept.ToArray();
        }

        // Said once, naming the controller, instead of a hash a few hundred times a second.
        void Missing(System.Collections.Generic.HashSet<int> have, string which, Animator anim)
        {
            var gone = new System.Collections.Generic.List<string>();
            foreach (var (hash, label) in Named)
                if (!have.Contains(hash)) gone.Add(label);

            if (gone.Count == 0) return;

            Debug.LogWarning($"[PlayerAnimatorBridge] {which} controller " +
                             $"({(anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "none")}) " +
                             $"has no {string.Join(", ", gone)} — those are not bridged to the puppet.");
        }
    }

    static System.Collections.Generic.HashSet<int> NameHashes(Animator anim)
    {
        var set = new System.Collections.Generic.HashSet<int>();
        if (anim == null || anim.runtimeAnimatorController == null) return set;
        foreach (var p in anim.parameters) set.Add(p.nameHash);
        return set;
    }

    /// Only so the warning above can name what is missing rather than print its hash.
    static readonly (int Hash, string Label)[] Named =
    {
        (SpeedHash, "Speed"), (MotionSpeed, "MotionSpeed"), (Grounded, "Grounded"),
        (Jump, "Jump"), (FreeFall, "FreeFall"), (IsCarryingBox, "IsCarryingBox"),
        (IsCrouching, "IsCrouching"), (IsClimbing, "IsClimbing"), (ClimbSpeed, "ClimbSpeed"),
        (IsSlipping, "IsSlipping"), (IsRepairing, RepairCrouch.CrouchParam),
    };
}
