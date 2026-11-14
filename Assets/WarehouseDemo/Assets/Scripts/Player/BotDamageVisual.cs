using System.Collections.Generic;
using UnityEngine;

/// Which piece of the player is broken. One per thing that can be hurt separately.
public enum BodyPart
{
    None,
    Head,
    Torso,
    LeftArm,
    RightArm,
    LeftLeg,
    RightLeg,
}

/// Shows an injury on the body, by taking the armour off the part that was hurt.
///
/// Nothing is swapped, spawned or deleted to do it. The robot the player is wearing ships as two
/// layers at once: a single skinned mesh of the *battered* chassis, and sixteen intact panels
/// bolted on over it, each parented to its own bone. Switching one panel off uncovers the damage
/// that was already underneath, on exactly that limb, and the panel follows the skeleton on its own
/// because it never stopped being parented to it.
///
/// That is the whole trick, and it is why this is cheap: no mesh work, no rig work, and nothing
/// that can desynchronise from the animation.
[DisallowMultipleComponent]
public class BotDamageVisual : MonoBehaviour
{
    /// Everything that goes away when a part is lost.
    ///
    /// Both layers of it. The robot is built as an inner chassis (`A_*`) with outer plating
    /// (`B_*`) bolted over the top, and on HumanBot_Skinned_Hands every piece of both is a separate
    /// object on its own bone — 33 of them, measured. Hiding only the plating opens a limb up;
    /// hiding the chassis with it takes the limb off, which is the difference between a robot with
    /// a damaged arm and a robot with no arm.
    ///
    /// A limb is cut at the middle joint, not at the shoulder or the hip: the upper arm and the
    /// thigh stay, the forearm, hand, shin and foot go. That leaves a stump that is still part of
    /// the body and still animates, which is what the torn chunk hangs off — parented to the
    /// shoulder it sat still while the arm swung past it, and read as a piece of scenery rather
    /// than as part of him.
    ///
    /// The hips and the neck are never on the list either: a body with no hips is not injured, it
    /// is bisected, and the neck is what the head stump sits on.
    ///
    /// Names that a given body does not have are simply not found. That is what keeps this working
    /// on HumanBot_Skinned_A too, where the chassis is one welded skinned mesh and only the `B_`
    /// half of each entry exists — there it strips a panel, here it removes the limb.
    /// The outer plating. Taking this off dents a limb; the chassis underneath still holds it on.
    static readonly Dictionary<BodyPart, string[]> Plating = new()
    {
        [BodyPart.Head]     = new[] { "B_Head_Geom" },
        [BodyPart.LeftArm]  = new[] { "B_LeftForeArm_Geom",
                                      "B_LeftHand_Geom", "B_LeftHandThumb1_Geom" },
        [BodyPart.RightArm] = new[] { "B_RightForeArm_Geom",
                                      "B_RightHand_Geom", "B_RightHandThumb1_Geom" },
        [BodyPart.LeftLeg]  = new[] { "B_LeftLeg_Geom" },
        [BodyPart.RightLeg] = new[] { "B_RightLeg_Geom" },
    };

    /// The chassis. This is the limb itself — taking it off with the plating removes the limb.
    static readonly Dictionary<BodyPart, string[]> Chassis = new()
    {
        [BodyPart.Head]     = new[] { "A_Head_Geom" },
        [BodyPart.LeftArm]  = new[] { "A_LeftForeArm_Geom", "A_LeftHand_Geom" },
        [BodyPart.RightArm] = new[] { "A_RightForeArm_Geom", "A_RightHand_Geom" },
        [BodyPart.LeftLeg]  = new[] { "A_LeftLeg_Geom", "A_LeftFoot_Geom", "A_LeftToe_Geom" },
        [BodyPart.RightLeg] = new[] { "A_RightLeg_Geom", "A_RightFoot_Geom", "A_RightToe_Geom" },
    };

    // The torso is deliberately absent. Limbs and the head are what break; a back injury is still
    // a real injury with a real penalty, it simply has no plating to lose — taking the chest off a
    // robot reads as gutting it rather than as a strain, and that is not a thing you then walk off.

    /// Where a lost part's wreckage hangs, and which of the four chunk sizes it is.
    ///
    /// On the **last bone still attached**, so it moves with the stump. That is the whole reason
    /// the limb is now cut at the elbow and the knee rather than removed entirely: a chunk on the
    /// shoulder does not follow an arm that swings, and a robot hitting something with a broken
    /// arm left its own wreckage hanging in the air behind it.
    /// Named through the **avatar** rather than by transform name, with the name kept only as a
    /// fallback for a body that has no humanoid rig.
    ///
    /// Because a name is not an address here. The bodies carry more than one skeleton — the co-op
    /// puppet ships a whole disused `Skeleton_OLD` beside the one it animates, with its own Hips,
    /// Spine, Neck and Head — and a limb the player is carrying is parented into the hand, so it
    /// is inside the search too. A name search picks whichever of those the hierarchy happens to
    /// reach first, and the chunk is then bolted to a bone nothing is driving: it appears, but not
    /// where the player is. `GetBoneTransform` asks the rig that is actually being animated and
    /// cannot be led anywhere else.
    static readonly Dictionary<BodyPart, (HumanBodyBones bone, HumanBodyBones next,
                                          string name, string chunk)> Debris = new()
    {
        [BodyPart.Head]     = (HumanBodyBones.Neck,          HumanBodyBones.Head,
                               "Neck",       "S"),
        [BodyPart.LeftArm]  = (HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,
                               "LeftArm",    "M"),
        [BodyPart.RightArm] = (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
                               "RightArm",   "M"),
        [BodyPart.LeftLeg]  = (HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,
                               "LeftUpLeg",  "L"),
        [BodyPart.RightLeg] = (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
                               "RightUpLeg", "L"),
    };

    /// Rebuilt by Tools/Warehouse/Build Damage Chunks, not the pack's own Damage_1..4.
    ///
    /// Those are empty: instantiate one and its renderer has sharedMesh null, materials null and
    /// bounds of exactly zero — they are legacy Unity 5 prefabs whose references into Damage.fbx
    /// did not survive. Hanging one off a broken limb drew nothing at all, which is exactly what
    /// happened here for a while without anybody being able to see that it had.
    const string DebrisFolder = "Damage";

    [Header("Wreckage")]
    [Tooltip("How far down the bone the torn chunk sits, as a fraction of the bone's length. Near " +
             "1 because it hangs off the far end of the stump — the elbow, the knee — which is " +
             "where the limb was cut.")]
    [SerializeField] [Range(0f, 1f)] float _debrisAlong = 0.9f;

    [Tooltip("Multiplies the chunk's authored size. Build Damage Chunks already brings it to " +
             "about 0.3 m for the largest piece, so this is a nudge rather than a fit.")]
    [SerializeField] float _debrisScale = 1f;

    [Tooltip("Sideways nudge off the bone, in metres. Zero by default, and it should stay there " +
             "now that a limb is cut at the joint rather than removed: the end of a stump is " +
             "already outside the body, and pushing sideways from there put the chunk in his back.")]
    [SerializeField] float _debrisOut = 0f;

    [Tooltip("Turn applied after the chunk has been aimed down the limb. The pack ships these " +
             "with no snapping points at all — its own readme says to rotate them by eye so they " +
             "stick outwards — so this is where that eye-work is recorded.")]
    [SerializeField] Vector3 _debrisTurn = Vector3.zero;

    [Tooltip("Which way round the chunk sits. The piece is a torn socket with cables hanging out " +
             "of one end; this says which of its own axes those cables run along, so they end up " +
             "pointing into the gap the limb left rather than back inside the stump.")]
    [SerializeField] Vector3 _debrisAxis = Vector3.down;

    readonly Dictionary<BodyPart, GameObject> _wreckage = new();

    // Deliberately no name-to-renderer cache.
    //
    // PlayerBody keeps every body the project has ever worn in the scene side by side, with only
    // one of them active — so a name like B_Head_Geom exists on several of them at once. Measured
    // on the live player: 54 renderers under Player and **16 duplicated names**, one copy on the
    // worn body and one on a deactivated old one. A dictionary keyed by name keeps whichever it
    // saw last, and half the time that is the invisible body: the armour comes off a robot nobody
    // can see while the one on screen stays intact.
    //
    // Resolved on demand instead, preferring what is actually active. This runs a handful of times
    // a shift, so the search costs nothing worth caching against that failure.

    /// Fits the visual to a body, or hands back the one already on it.
    public static BotDamageVisual Ensure(GameObject body)
    {
        if (body == null) return null;

        var visual = body.GetComponent<BotDamageVisual>();
        if (visual == null) visual = body.AddComponent<BotDamageVisual>();

        return visual;
    }

    /// The renderer of that name on the body that is actually being worn.
    ///
    /// Renderers rather than GameObjects throughout: switching a renderer off leaves the transform
    /// in the hierarchy, so anything else parented to that bone — a held weapon, an IK target — is
    /// untouched by an injury.
    Renderer Find(string name)
    {
        Renderer fallback = null;

        foreach (var renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer.name != name) continue;
            if (OnALooseLimb(renderer)) continue;

            if (renderer.gameObject.activeInHierarchy) return renderer;
            fallback ??= renderer;
        }

        // Nothing active by that name. Returned anyway so that a body which is off purely because
        // the whole player is disabled still works.
        return fallback;
    }

    /// True for anything belonging to a limb that has already come off this body.
    ///
    /// A lost limb is carried in the free hand, and it is carried by being *parented to the hand
    /// bone* — so a player walking to a toolkit with his own head under his arm has a second
    /// `A_Head_Geom` and a second `B_Head_Geom` inside his own hierarchy: exact copies, by name, of
    /// the two the body has stopped drawing.
    ///
    /// Find prefers whatever is active, and the copy is the active one — switching a renderer off
    /// leaves its GameObject `activeInHierarchy`, so the original never disqualifies itself. The
    /// copy therefore wins whenever it comes first in the hierarchy, and the rig's own bone order
    /// decides which parts that is: legs, left arm, right arm, and only then the neck and the head.
    /// The free hand is the left one, so the head and the right arm are exactly the parts whose
    /// copy is walked past *before* the place the panel belongs.
    ///
    /// That is the whole of "the head never comes back". The repair switched the carried copy back
    /// on, the copy was destroyed a line later along with the item it belonged to, and the panel on
    /// the neck was never touched at all.
    static bool OnALooseLimb(Component thing) =>
        thing.GetComponentInParent<SeveredLimbItem>(true) != null;

    /// True when this body actually has the separable panels. A body swapped for one of the other
    /// variants — the single-mesh ones — has nothing to take off, and says so rather than
    /// pretending the injury is showing.
    public bool Supported
    {
        get
        {
            foreach (var names in Plating.Values)
                foreach (string name in names)
                    if (Find(name) != null) return true;

            return false;
        }
    }

    /// How badly a part is hurt.
    public enum Severity
    {
        /// Plating off, limb still attached. What a bruise looks like.
        Dented,
        /// Limb gone.
        Severed,
    }

    /// Shows an injury, or clears one.
    ///
    /// Two tiers, because one was wrong: every injury used to take the whole limb off, including a
    /// bruise from a light knock. A robot losing an arm to a bump is not an injury system, it is a
    /// bug that happens to be dramatic. A dent takes the plating and leaves the chassis — which is
    /// exactly what the pack's two layers are for.
    public void SetBroken(BodyPart part, bool broken, Severity severity = Severity.Severed)
    {
        // A limb that comes off takes its paintwork with it, borrowed or not. Cleared before the
        // panels go, so BuildSeveredLimb — which runs first, on renderers still wearing the graft —
        // is the last thing to see it, and the piece on the floor keeps the colour it had.
        if (broken && severity == Severity.Severed) ClearGraft(part);

        SetLayer(Plating, part, hidden: broken);

        // The chassis only goes on a severed limb. On a dent it stays, and stays *shown* — which
        // also means downgrading an existing injury puts the limb back without touching the rest.
        SetLayer(Chassis, part, hidden: broken && severity == Severity.Severed);

        // Wreckage belongs to a limb that came off. Hanging torn cables on a dented one would say
        // the arm is gone while the arm is plainly still there.
        if (broken && severity == Severity.Severed) AddWreckage(part);
        else                                        RemoveWreckage(part);

        // Panels that have just come back on are bare until this runs: SetLayer only switches a
        // renderer, and a limb fitted from somebody else's body is somebody else's colour.
        if (!broken) PaintGraft(part);
    }

    // ── Borrowed limbs ────────────────────────────────────────────────────────
    //
    // A limb on the warehouse floor fits any robot missing that part, so a player can walk off a
    // fight wearing a team-mate's arm — and it stays the team-mate's colour, because that is the
    // only honest way to show where it came from. The colour is carried as the donor's *role*
    // rather than as a material: liveries are per-role assets every peer already has, so one
    // three-bit number repaints the arm identically on every machine.

    /// Which parts are wearing somebody else's paint, and whose.
    readonly Dictionary<BodyPart, PlayerRoleType> _grafts = new();

    /// The renderer names those parts cover, kept flat so PlayerSkin can ask about a piece
    /// without walking two dictionaries per piece per frame.
    readonly HashSet<string> _graftedNames = new();

    /// Bumped on every change, and read by PlayerSkin.
    ///
    /// PlayerSkin decides whether to repaint by asking whether the body is already wearing the
    /// right livery, and a grafted piece is deliberately not in the set it checks — so without a
    /// version to compare, a graft going *away* would leave the piece in the donor's colour
    /// forever, with nothing to notice it should be dressed again.
    public int GraftVersion { get; private set; }

    /// True when this part is wearing a limb cut from another body, and whose livery that is.
    public bool TryGraft(BodyPart part, out PlayerRoleType donor) =>
        _grafts.TryGetValue(part, out donor);

    /// Fits a borrowed limb's paint onto a part.
    public void SetGraft(BodyPart part, PlayerRoleType donor)
    {
        if (_grafts.TryGetValue(part, out var already) && already == donor) return;

        _grafts[part] = donor;
        Rebuilt();
        PaintGraft(part);
    }

    /// Takes the borrowed paint off — the limb went, or the whole body was reset.
    public void ClearGraft(BodyPart part)
    {
        if (!_grafts.Remove(part)) return;

        Rebuilt();
        // The livery comes back on its own: PlayerSkin sees the version move and redresses.
    }

    void Rebuilt()
    {
        GraftVersion++;

        _graftedNames.Clear();
        foreach (var part in _grafts.Keys)
            foreach (string name in PanelNames(part))
                _graftedNames.Add(name);
    }

    /// True for a piece of the body that belongs to a borrowed limb. Asked by PlayerSkin, which
    /// must dress everything else and leave these alone.
    public bool IsGrafted(Renderer piece) =>
        piece != null && _graftedNames.Count > 0 && _graftedNames.Contains(piece.name);

    /// What a grafted piece should be wearing, for whoever is putting materials back on the body.
    public Material[] GraftMaterials(Renderer piece)
    {
        if (!IsGrafted(piece)) return null;

        foreach (var pair in _grafts)
            foreach (string name in PanelNames(pair.Key))
                if (name == piece.name) return Livery(pair.Value);

        return null;
    }

    /// Re-asserts every graft on the body.
    ///
    /// For whoever has just painted over it. PlayerSkin dresses 35 pieces in one pass and skips the
    /// grafted ones, but it can only skip what it knows about — and on the frame a body is first
    /// bound it may not have found this component yet. Cheap enough to simply say it again after
    /// any repaint rather than reason about which frame that was.
    public void RepaintGrafts()
    {
        foreach (var part in _grafts.Keys) PaintGraft(part);
    }

    /// Puts the donor's livery onto one part, or does nothing when it is wearing its own.
    void PaintGraft(BodyPart part)
    {
        if (!_grafts.TryGetValue(part, out var donor)) return;

        var mats = Livery(donor);
        if (mats == null || mats.Length == 0) return;

        foreach (string name in PanelNames(part))
        {
            var piece = Find(name);
            if (piece == null) continue;

            // Same guard PlayerSkin uses: a piece authored for a different material count would
            // come out with the armour material on the underlayer, and Unity would complain about
            // the submesh count on top.
            if (piece.sharedMaterials.Length == mats.Length) piece.sharedMaterials = mats;
        }
    }

    /// The livery of a role, with the solo set standing in for a player who has not been given
    /// one. Falls through to the same answer on every machine, which is the point.
    static Material[] Livery(PlayerRoleType role)
    {
        var set = PlayerSkinRegistry.For(role) ?? PlayerSkinRegistry.Solo;
        return set != null ? set.Materials : null;
    }

    /// Every renderer a part covers, chassis and plating together.
    static IEnumerable<string> PanelNames(BodyPart part)
    {
        if (Chassis.TryGetValue(part, out var chassis))
            foreach (string name in chassis) yield return name;

        if (Plating.TryGetValue(part, out var plating))
            foreach (string name in plating) yield return name;
    }

    void SetLayer(Dictionary<BodyPart, string[]> layer, BodyPart part, bool hidden)
    {
        if (!layer.TryGetValue(part, out var names)) return;

        foreach (string name in names)
        {
            var renderer = Find(name);
            if (renderer != null) renderer.enabled = !hidden;
        }
    }

    /// Hangs a torn chunk off the stump.
    ///
    /// The exposed chassis alone is a real change but a quiet one — the underlying robot is itself
    /// fairly intact, so a missing panel reads as an opened-up shoulder rather than as a broken
    /// arm. The chunk is what makes it obvious at the distance the game is actually played at.
    void AddWreckage(BodyPart part)
    {
        if (_wreckage.ContainsKey(part)) return;
        if (!Debris.TryGetValue(part, out var spec)) return;

        var bone = Bone(spec.bone, spec.name);
        if (bone == null) return;

        // The joint the limb was cut at. Resolved once and passed on, so the direction and the
        // length can never be measured against two different children of the same bone.
        var joint = NextBone(bone, spec.next);

        var prefab = Resources.Load<GameObject>($"{DebrisFolder}/Damage_{spec.chunk}");
        if (prefab == null)
        {
            Debug.LogWarning($"[BotDamageVisual] no chunk at Resources/{DebrisFolder}/" +
                             $"Damage_{spec.chunk} — run Tools/Warehouse/Build Damage Chunks. " +
                             "The panel still comes off; there is just nothing hanging from it.");
            return;
        }

        var piece = Instantiate(prefab, bone);

        // Aimed down the limb rather than placed at a typed-in angle: the rig runs its arms along
        // bone-local -X and its legs along -Y, so a rotation written for one would be wrong for
        // the other. Pointing the chunk's own up-axis along whatever direction this bone actually
        // runs in works for every limb, and _debrisTurn then tips it out of the body.
        Vector3 along = BoneDirection(bone, joint);

        // Down the limb to the cut, and that is all.
        //
        // The sideways push this used to apply was for a limb removed whole, where the chunk had
        // to be lifted clear of a body it would otherwise be buried in. Cutting at the elbow and
        // the knee removed the need and the push became the bug: measured from the body's centre
        // and squared against the upper arm, "outward" pointed backwards, and the chunk ended up
        // embedded in his back.
        Vector3 outward = _debrisOut > 0.0001f ? OutwardFrom(bone, along) : Vector3.zero;

        piece.transform.localPosition = along * (BoneLength(bone, joint) * _debrisAlong)
                                      + outward * _debrisOut;

        // Aimed along the limb, so the torn end continues the line of the arm rather than sticking
        // out across it — and aimed the right way round, so the cables hang into the empty space
        // instead of back into the stump, where they cannot be seen at all.
        var axis = _debrisAxis.sqrMagnitude < 1e-4f ? Vector3.down : _debrisAxis.normalized;

        piece.transform.localRotation = Quaternion.FromToRotation(axis, along)
                                      * Quaternion.Euler(_debrisTurn);

        // The prefab's own scale, kept rather than replaced.
        //
        // Overwriting it with one was a real bug and a loud one: the chunk meshes are authored at
        // roughly 8 to 16 units, so a chunk that measures 0.25 m as an asset came out **16 metres**
        // once it was parented to an arm. Dividing by the bone's world scale on top keeps the size
        // honest even if some bone in the chain is not at unit scale.
        Vector3 parentScale = bone.lossyScale;

        piece.transform.localScale = new Vector3(
            SafeDivide(prefab.transform.localScale.x * _debrisScale, parentScale.x),
            SafeDivide(prefab.transform.localScale.y * _debrisScale, parentScale.y),
            SafeDivide(prefab.transform.localScale.z * _debrisScale, parentScale.z));

        _wreckage[part] = piece;
    }

    static float SafeDivide(float value, float by) => Mathf.Abs(by) < 1e-5f ? value : value / by;

    void RemoveWreckage(BodyPart part)
    {
        if (!_wreckage.TryGetValue(part, out var piece)) return;

        _wreckage.Remove(part);
        if (piece == null) return;

        if (Application.isPlaying) Destroy(piece);
        else                       DestroyImmediate(piece);
    }

    /// Away from the body and across the limb, in the bone's own space.
    ///
    /// Both halves are needed. Away-from-the-centre alone is useless on an arm: an arm points
    /// sideways, so "out from the body" is the same direction the bone already runs in, and the
    /// chunk slides further down the arm instead of out of it — which is exactly what the first
    /// attempt did. Taking only the part perpendicular to the bone is what turns it into a
    /// direction that actually leaves the limb.
    Vector3 OutwardFrom(Transform bone, Vector3 along)
    {
        Vector3 fromCentre = bone.position - transform.position;
        fromCentre.y = 0f;

        // A bone on the centre line — the neck — has no sideways to speak of, so it goes out of
        // the front instead, which is where a torn-off head plate would fall anyway.
        Vector3 world = fromCentre.sqrMagnitude < 1e-4f
            ? transform.forward
            : fromCentre.normalized;

        Vector3 local = Across(bone.InverseTransformDirection(world), along);

        // Still collapsed — the limb runs straight out from the body. Anything square to the bone
        // will do, and the front of the body is the side a player is looking at.
        if (local.sqrMagnitude < 1e-4f)
            local = Across(bone.InverseTransformDirection(transform.forward), along);

        if (local.sqrMagnitude < 1e-4f)
            local = Across(Vector3.forward, along);

        return local.normalized;
    }

    /// The part of a direction that is square to the bone.
    static Vector3 Across(Vector3 direction, Vector3 along) =>
        direction - along * Vector3.Dot(direction, along);

    /// The humanoid rig this body is animated by, or null when it has none.
    ///
    /// Looked up on the component's own object first, because that is where Ensure puts it — both
    /// InjurySystem and RemoteInjuryVisual call it with the Animator's GameObject.
    Animator Rig
    {
        get
        {
            if (_rig == null)
            {
                var found = GetComponent<Animator>();
                if (found == null) found = GetComponentInChildren<Animator>(true);
                _rig = found;
            }

            return _rig != null && _rig.isHuman ? _rig : null;
        }
    }

    Animator _rig;

    /// A named bone of the skeleton actually being animated, falling back to a name search on a
    /// body with no avatar. See the Debris table for why the avatar is asked first.
    Transform Bone(HumanBodyBones humanoid, string name)
    {
        var rig = Rig;

        if (rig != null)
        {
            var mapped = rig.GetBoneTransform(humanoid);
            if (mapped != null) return mapped;
        }

        return FindBone(name);
    }

    /// The joint at the far end of a bone: the elbow, the knee, the base of the skull.
    ///
    /// Asked of the avatar, and only guessed at if the avatar has no answer. The guess below is a
    /// heuristic over children, and a bone picks up children that are not skeleton — a weapon
    /// socket, an IK target, a limb being carried, a piece of wreckage. Every one of those makes
    /// the guess return something that is not the joint, and the chunk is then placed along a
    /// direction and a distance measured to a prop.
    ///
    /// A descendant rather than a direct child is fine and is sometimes what a rig gives: twist
    /// bones sit between the shoulder and the elbow on plenty of skeletons. What matters is that
    /// the transform is the joint the limb was cut at.
    Transform NextBone(Transform bone, HumanBodyBones humanoid)
    {
        var rig = Rig;

        if (rig != null)
        {
            var mapped = rig.GetBoneTransform(humanoid);
            if (mapped != null && mapped != bone && mapped.IsChildOf(bone)) return mapped;
        }

        return NextBone(bone);
    }

    /// Same rule as Find: the bone on the body being worn, not one on a body kept in reserve.
    Transform FindBone(string name)
    {
        Transform fallback = null;

        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name != name) continue;
            if (OnALooseLimb(t)) continue;

            if (t.gameObject.activeInHierarchy) return t;
            fallback ??= t;
        }

        return fallback;
    }

    /// The next bone along, skipping anything that is geometry rather than skeleton.
    ///
    /// Tested by what a child *is* — does it carry a renderer — rather than by what it is called.
    /// The name test this used to do only knew about the `B_` plating, and on the detachable body
    /// there is an `A_` chassis part hanging off every bone as well. It matched that instead, so a
    /// 0.28 m upper arm measured 0.10 m, and the torn chunk meant for the elbow sat up by the
    /// shoulder — which is how it ended up looking like it was stuck in his back.
    static Transform NextBone(Transform bone)
    {
        for (int i = 0; i < bone.childCount; i++)
        {
            var child = bone.GetChild(i);

            if (child.GetComponent<Renderer>() != null) continue;
            if (child.name.StartsWith("Damage_")) continue;   // our own wreckage

            return child;
        }
        return null;
    }

    static Vector3 BoneDirection(Transform bone, Transform next)
    {
        if (next == null) return Vector3.up;

        Vector3 local = bone.InverseTransformPoint(next.position);
        return local.sqrMagnitude < 1e-6f ? Vector3.up : local.normalized;
    }

    /// The bone's length **in its own space**, because that is the space the chunk is placed in.
    ///
    /// Measuring it in world metres and then using the number as a local offset only agrees when
    /// the bone sits at unit scale, and these do not: the chain carries about 0.53, so a chunk
    /// meant for the elbow landed halfway up the upper arm.
    static float BoneLength(Transform bone, Transform next)
    {
        return next == null ? 0.2f : bone.InverseTransformPoint(next.position).magnitude;
    }

    /// Builds the limb that just came off, as a thing lying on the floor.
    ///
    /// A copy of the player's own arm or leg, not a stand-in for one: the same meshes, in the same
    /// materials, frozen in the pose they were in at the moment of the cut. That is possible only
    /// because of how this pack is built — every panel and every chassis piece is its own object on
    /// its own bone, so a limb is already a list of renderers rather than a region of one big mesh.
    ///
    /// The hand is the exception and has to be handled as one. It is the piece that makes this body
    /// worth wearing — it is skinned across 22 bones so the fingers move — and a skinned renderer
    /// cannot simply be copied out of a skeleton it no longer belongs to. It is baked instead,
    /// which flattens the current pose into a plain mesh: the hand keeps whatever grip it had.
    ///
    /// Call this **before** hiding the limb. Nothing here reads `enabled`, but a body that has been
    /// swapped in the meantime has different renderers, and this is the last moment they are
    /// certain to be the right ones.
    public GameObject BuildSeveredLimb(BodyPart part)
    {
        if (!Chassis.TryGetValue(part, out var chassisNames)) return null;

        var pieces = new List<Renderer>();

        foreach (string name in chassisNames)
        {
            var renderer = Find(name);
            if (renderer != null) pieces.Add(renderer);
        }

        // The plating too, so what falls off looks like the limb did a second ago rather than like
        // its skeleton.
        if (Plating.TryGetValue(part, out var platingNames))
            foreach (string name in platingNames)
            {
                var renderer = Find(name);
                if (renderer != null) pieces.Add(renderer);
            }

        if (pieces.Count == 0) return null;

        // Rooted at the cut, and the cut is wherever the first chassis piece is bolted — the elbow
        // for an arm, the knee for a leg. Derived rather than named, so the same code holds if the
        // limb is ever cut somewhere else.
        var root = new GameObject($"SeveredLimb_{part}");
        root.transform.SetPositionAndRotation(pieces[0].transform.position,
                                              pieces[0].transform.rotation);

        foreach (var source in pieces)
        {
            Mesh mesh;

            if (source is SkinnedMeshRenderer skinned)
            {
                if (skinned.sharedMesh == null) continue;
                mesh = new Mesh { name = source.name + "_baked" };
                skinned.BakeMesh(mesh, true);
            }
            else
            {
                var filter = source.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                mesh = filter.sharedMesh;
            }

            var piece = new GameObject(source.name);
            piece.transform.SetParent(root.transform, false);

            // The renderer's own place in the world, which for a baked skinned mesh is the body
            // root — that is the space BakeMesh writes its vertices in.
            piece.transform.SetPositionAndRotation(source.transform.position,
                                                   source.transform.rotation);

            Vector3 scale = source.transform.lossyScale;
            piece.transform.localScale = new Vector3(SafeDivide(scale.x, root.transform.lossyScale.x),
                                                     SafeDivide(scale.y, root.transform.lossyScale.y),
                                                     SafeDivide(scale.z, root.transform.lossyScale.z));

            piece.AddComponent<MeshFilter>().sharedMesh    = mesh;
            piece.AddComponent<MeshRenderer>().sharedMaterials = source.sharedMaterials;
        }

        // Nothing was copyable after all — a body variant without these panels.
        if (root.transform.childCount == 0)
        {
            if (Application.isPlaying) Destroy(root); else DestroyImmediate(root);
            return null;
        }

        return root;
    }

    /// Everything back on — for a fresh body, or a fully repaired one.
    public void Clear()
    {
        // Borrowed paint goes with them. This is a fresh body — a respawn, a new shift — and an
        // arm in somebody else's colour on it would be a graft nobody performed.
        foreach (var part in Plating.Keys) ClearGraft(part);

        foreach (var part in Plating.Keys) SetBroken(part, false);
    }

    /// Whether this part is currently showing damage.
    public bool IsBroken(BodyPart part)
    {
        if (!Plating.TryGetValue(part, out var names)) return false;

        foreach (string name in names)
        {
            var renderer = Find(name);
            if (renderer != null) return !renderer.enabled;
        }

        return false;
    }

    /// Which part a bone belongs to, for turning a hit into a visible injury.
    ///
    /// Matched on the bone names the rig actually uses, side included — the injury system already
    /// receives the bone that was struck, it simply had nowhere to put the information.
    public static BodyPart PartForBone(string bone)
    {
        if (string.IsNullOrEmpty(bone)) return BodyPart.None;

        bool left = bone.Contains("Left");

        if (bone.Contains("Head") || bone.Contains("Neck")) return BodyPart.Head;

        if (bone.Contains("Arm") || bone.Contains("Hand") || bone.Contains("Shoulder"))
            return left ? BodyPart.LeftArm : BodyPart.RightArm;

        if (bone.Contains("Leg") || bone.Contains("Foot") || bone.Contains("Toes"))
            return left ? BodyPart.LeftLeg : BodyPart.RightLeg;

        return BodyPart.Torso;
    }

    public static string Label(BodyPart part) => part switch
    {
        BodyPart.Head     => "head",
        BodyPart.Torso    => "torso",
        BodyPart.LeftArm  => "left arm",
        BodyPart.RightArm => "right arm",
        BodyPart.LeftLeg  => "left leg",
        BodyPart.RightLeg => "right leg",
        _                 => "body",
    };
}
