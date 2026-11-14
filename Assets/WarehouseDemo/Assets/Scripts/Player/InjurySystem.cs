using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public enum InjuryType
{
    Bruise,       // bruise
    AnkleSprain,  // sprained ankle
    ArmStrain,    // arm strain
    Concussion,   // concussion
    BackInjury,   // back injury
}

public class InjurySystem : MonoBehaviour
{
    public static InjurySystem Instance { get; private set; }

    // ── Computed debuffs (updated whenever injuries change) ───────────────────
    public float SpeedMultiplier { get; private set; } = 1f;
    public bool  CanCarryHeavy  { get; private set; } = true;
    public bool  HasConcussion  { get; private set; } = false;

    // ── Internal ──────────────────────────────────────────────────────────────

    class ActiveInjury
    {
        public InjuryType type;
        public BodyPart   part;
        public float speedMult;
        public bool  preventsHeavy;
        public bool  isConcussion;

        /// True when the limb is on the floor somewhere and has to be fetched before this can be
        /// mended. False for a dent, and false for a part that has no separable limb to lose — a
        /// back injury, or any body variant without the detachable panels. Recorded per injury
        /// rather than worked out from the type, because it depends on whether a limb actually
        /// came off, and that is something only the attempt can answer.
        public bool  needsLimb;

        /// How much of the limb this actually took.
        ///
        /// Stored rather than derived from the type, because the pair rule can hold a limb on that
        /// its injury would otherwise have taken off — see Admit. The debuff is unaffected by that:
        /// a sprained ankle still costs what a sprained ankle costs whether or not the shin came
        /// with it.
        public BotDamageVisual.Severity severity;

        /// True when the limb only stayed on because losing it would have left him with none of
        /// that pair. Purely so the player can be told why — nothing downstream branches on it.
        public bool  heldTogether;
    }

    /// What is hurt, worst first, for the HUD and for the repair prompt.
    public IEnumerable<(InjuryType type, BodyPart part)> Active =>
        _active.Select(i => (i.type, i.part));

    public int  Count      => _active.Count;
    public bool IsInjured  => _active.Count > 0;

    /// The next thing a repair would put right: the one costing the most speed.
    public (InjuryType type, BodyPart part) Worst
    {
        get
        {
            var worst = _active.OrderBy(i => i.speedMult).FirstOrDefault();
            return worst == null ? (InjuryType.Bruise, BodyPart.None) : (worst.type, worst.part);
        }
    }

    readonly List<ActiveInjury> _active = new();
    float _concussionShakeTimer;
    uint _networkProposalVersion;
    uint _networkIncidentToken;
    bool _networkPublishPending;
    bool _applyingServerRepair;
    PlayerRoleType _serverRepairLivery;

    BotDamageVisual _visual;

    /// The armour on the body currently being worn.
    ///
    /// Looked up on demand rather than cached in Awake: the body can be swapped at any time (see
    /// Tools/Warehouse/Player Body), and a reference taken once would be pointing at a skeleton
    /// that no longer exists.
    BotDamageVisual Visual
    {
        get
        {
            if (_visual != null) return _visual;

            var animator = GetComponentInChildren<Animator>();
            if (animator == null) return null;

            _visual = BotDamageVisual.Ensure(animator.gameObject);
            return _visual;
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        var np = GetComponent<NetworkPlayer>();
        if (np == null || Unity.Netcode.NetworkManager.Singleton?.IsListening != true)
            Instance = this;

        // Added here rather than left to the scene: the repair is the only way out of an injury
        // now, so a player who happens to be missing the component would be stuck with a broken
        // arm for the rest of the shift and no way to see why.
        if (GetComponent<SelfRepairTarget>() == null) gameObject.AddComponent<SelfRepairTarget>();

        // Same reasoning: a broken leg that does not change how he walks is the injury system's
        // most visible hole, and it should not depend on somebody remembering to tick a box.
        // InjuredLocomotion swaps the walk for the hobble clip; PlayerLimp is the fallback that
        // bends the hips when that clip is unavailable, and stands aside when it is not.
        if (GetComponent<InjuredLocomotion>() == null) gameObject.AddComponent<InjuredLocomotion>();
        if (GetComponent<PlayerLimp>() == null) gameObject.AddComponent<PlayerLimp>();

        // Without this the hop clip plays over a body gliding at a constant speed, which reads as
        // a broken animation rather than as a broken leg.
        if (GetComponent<HopMovement>() == null) gameObject.AddComponent<HopMovement>();

        // And this is what makes a repair look like work rather than like standing still for six
        // seconds facing your own arm.
        if (GetComponent<RepairCrouch>() == null) gameObject.AddComponent<RepairCrouch>();

        // A head that comes off has to cost something the player feels at the controls, or it is
        // just an arm injury with a different picture. ScrambledControls mirrors his input while
        // it is gone; added here for the same reason as the rest, so it cannot be forgotten on a
        // rig somebody set up by hand.
        if (GetComponent<ScrambledControls>() == null) gameObject.AddComponent<ScrambledControls>();
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    public static void BindLocal(InjurySystem system)
    {
        if (system != null) Instance = system;
    }

    public static void UnbindLocal(InjurySystem system)
    {
        if (Instance == system) Instance = null;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// Called from RagdollController. Classifies injury from impulse magnitude
    /// and the name of the bone that was hit.
    public void AddInjuryFromImpact(float impulseMagnitude, string boneName)
    {
        var injury = Classify(impulseMagnitude, boneName);
        if (injury == null) return;

        injury.part = BotDamageVisual.PartForBone(boneName);

        if (!Admit(injury)) return;

        // The armour comes off the part that was hit. This is the whole point of the change: an
        // injury used to be a number in a debuff and a line of text, and now it is something the
        // player — and, in a session, the rest of the team — can see on the body.
        ShowInjury(injury, Label(injury.type));

        if (injury.severity == BotDamageVisual.Severity.Severed)
            DropCarriedIfArmLost(injury.part);

        if (injury.isConcussion)
            CameraShake.AddTrauma(0.8f);
    }

    /// Decides whether an injury happens at all, and how much of the limb it takes.
    ///
    /// Two rules, and both exist because of states the rest of the game cannot represent.
    ///
    /// **One injury per part.** A second blow to an arm that is already hanging off cannot make it
    /// any more broken, and stacking two would mean two repairs for one visible wound.
    ///
    /// **Never both of a pair.** A robot with no arms cannot pick either of them up, which is a
    /// repair with no way to start it — the same shape of dead end the torso taught us about, and
    /// SeveredLimbItem had to grow a whole second interaction to survive it. Two missing legs is
    /// worse: the hobble is built on there being one leg left to hop on, and there is no honest
    /// animation for none.
    ///
    /// The blow is not thrown away when the rule fires. It lands with its full debuff — a sprained
    /// ankle still costs what a sprained ankle costs — and only the limb stays on, which reads as
    /// the second one being dented rather than torn off. Silently ignoring the hit instead would
    /// make the second arm invulnerable, and a player would notice that long before he noticed a
    /// design rule.
    bool Admit(ActiveInjury injury)
    {
        if (injury.part == BodyPart.None) return false;
        if (_active.Any(i => i.part == injury.part)) return false;

        injury.severity = SeverityOf(injury.type);

        if (injury.severity == BotDamageVisual.Severity.Severed && PairAlreadyGone(injury.part))
        {
            injury.severity     = BotDamageVisual.Severity.Dented;
            injury.heldTogether = true;
        }

        _active.Add(injury);
        // ShowInjury decides whether the part actually detached. Publish only after that decision;
        // otherwise peers briefly receive Severed even when this body has no detachable mesh.
        Recompute(publish: false);
        return true;
    }

    /// Which part is on the other side of the body from which.
    static readonly Dictionary<BodyPart, BodyPart> Pairs = new()
    {
        [BodyPart.LeftArm] = BodyPart.RightArm, [BodyPart.RightArm] = BodyPart.LeftArm,
        [BodyPart.LeftLeg] = BodyPart.RightLeg, [BodyPart.RightLeg] = BodyPart.LeftLeg,
    };

    /// Asked of `needsLimb` and not of the severity, and the difference is not academic: on a body
    /// variant with no detachable panels nothing is ever actually removed, so a second "severed"
    /// arm there costs a hand that is still visibly attached and strands nothing. The dead end this
    /// rule exists to prevent is a limb *on the floor* with no hand left to pick it up.
    bool PairAlreadyGone(BodyPart part) =>
        Pairs.TryGetValue(part, out var other) && _active.Any(i => i.part == other && i.needsLimb);

    /// Everything that happens to the world when a part breaks: the body, the floor, the toolkit.
    ///
    /// One place for it because the three used to be spread across the impact path and the debug
    /// path, and they had already drifted apart once. The order inside matters and is the reason
    /// this is a method rather than three calls: the limb has to be copied off the body *before*
    /// the body stops drawing it.
    void ShowInjury(ActiveInjury injury, string title)
    {
        if (injury.severity == BotDamageVisual.Severity.Severed)
        {
            injury.needsLimb = DropLimb(injury.part);
            if (!injury.needsLimb)
                injury.severity = BotDamageVisual.Severity.Dented;
        }

        Visual?.SetBroken(injury.part, true, injury.severity);

        string label = BotDamageVisual.Label(injury.part);

        string what =
            injury.heldTogether ? $"Your {label} took the hit but stayed on — you only have the " +
                                  $"one. Hold [E] to repair it"
          : injury.needsLimb    ? $"Your {label} came off — find it and refit it"
          :                       $"Your {label} is damaged — hold [E] to repair it";

        HUDController.Instance?.AddNotification(title, what, HUDController.NotifType.Danger);

        // Again, and not redundantly. Admit already published through Recompute, but that ran
        // before the limb had been cut and so before `needsLimb` was known — and the hobble flag is
        // derived from exactly that. Published there, a torn-off leg reached the rest of the
        // session as an intact one.
        Publish();

        EnsureToolkit();
    }

    /// Puts the lost limb on the floor. Returns whether there was one to put there.
    ///
    /// A back injury has no limb, and neither does a body variant welded from a single mesh. Those
    /// stay repairable where the player stands — losing the errand is much better than an injury
    /// with no way out of it at all.
    bool DropLimb(BodyPart part)
    {
        // Never two of the same arm on the floor. One can survive an injury being cleared and
        // re-inflicted by the debug menu, and the stale one would be a repair for an injury that
        // no longer exists.
        var stale = SeveredLimbItem.For(part);
        if (stale != null) Destroy(stale.gameObject);

        // The colour it is painted in, read before the panels come off — SetBroken drops the
        // graft record, and this runs first precisely so the piece on the floor keeps whatever it
        // was wearing. An arm borrowed from a Picker and lost again is still a Picker's arm.
        var visual = Visual;
        var livery = visual != null && visual.TryGraft(part, out var donor) ? donor : LocalLivery;

        var limb = visual?.BuildSeveredLimb(part);
        if (limb == null) return false;

        if (SeveredLimbItem.Attach(limb, part, null, livery) == null) return false;

        return true;
    }

    /// Makes sure a toolkit exists to be found.
    ///
    /// Nothing used to put one in the warehouse when a player got hurt: the repair needed a kit,
    /// and a kit only appeared for the events that spawn one, so an injury taken between events was
    /// simply permanent.
    ///
    /// In a session the server decides, because the toolkit is a parallel-local object: every peer
    /// instantiates its own copy at a position the server chose and sent, the way PowerFailureEvent
    /// and the gate breach already do it. Spawning one here directly broke that invisibly — the
    /// position comes out of Random and an injury happens at a different moment on every machine,
    /// so every player would have found a kit somewhere else and none of them the same one.
    static void EnsureToolkit()
    {
        if (ToolkitItem.IsHeld) return;
        if (FindAnyObjectByType<ToolkitItem>() != null) return;

        var net = NetworkPlayer.Local;
        if (net != null && net.IsSpawned)
        {
            net.RequestToolkitServerRpc();
            return;
        }

        if (ToolkitItem.Spawn() == null) return;

        HUDController.Instance?.AddNotification(
            "Toolkit dropped",
            "There is a toolkit somewhere in the warehouse — you need it to repair yourself",
            HUDController.NotifType.Warning);
    }

    // ── Telling the rest of the session ───────────────────────────────────────

    /// Pushes what is broken onto the network, so the puppet other players look at matches.
    ///
    /// Called after every change rather than polled: the mask is state, and the NetworkVariable
    /// carrying it replays to late joiners, so somebody connecting an hour in still sees the arm.
    ///
    /// Hobbling is published beside it even though it could be derived from the mask, because the
    /// far side does something structurally different with it — see RemoteInjuryVisual.
    void Publish()
    {
        if (_applyingServerRepair) return;
        var net = NetworkPlayer.Local;
        if (net == null || !net.IsSpawned) return;

        // A locally-predicted collision can build the injury before its ServerRpc round-trip has
        // returned. Keep the finished state and send it as soon as the server supplies the token.
        if (_networkIncidentToken == 0u)
        {
            _networkPublishPending = true;
            return;
        }

        int mask = 0;

        foreach (var injury in _active)
            mask = RemoteInjuryVisual.With(mask, injury.part,
                injury.severity == BotDamageVisual.Severity.Severed
                    ? RemoteInjuryVisual.State.Severed
                    : RemoteInjuryVisual.State.Dented);

        net.PublishInjuryStateServerRpc(
            mask,
            Grafts(),
            IsSevered(BodyPart.LeftLeg) || IsSevered(BodyPart.RightLeg),
            ++_networkProposalVersion,
            _networkIncidentToken);

        // Same reliable NetworkObject channel, immediately after the accepted injury proposal:
        // the server sees the severed mask before it validates creation of the loose limb.
        foreach (var injury in _active)
        {
            if (!injury.needsLimb) continue;
            var limb = SeveredLimbItem.For(injury.part);
            if (limb != null)
                net.DropLimbServerRpc(
                    (int)injury.part, limb.transform.position, (int)limb.Livery);
        }

        _networkIncidentToken = 0u;
        _networkPublishPending = false;
    }

    /// Supplies the one-shot incident generated by the server for this impact. For predicted local
    /// ragdolls the injury may already exist; for server-driven hits this runs before AddInjury.
    public void AuthorizeNetworkInjury(uint incidentToken)
    {
        if (incidentToken == 0u) return;
        _networkIncidentToken = incidentToken;
        if (_networkPublishPending)
        {
            Publish();
            // The first request was sent before the server had accepted the predicted injury and
            // was correctly rejected. Re-issue it behind the accepted proposal on the same channel.
            EnsureToolkit();
        }
    }

    /// Which parts of this body came off somebody else, packed for the network.
    ///
    /// Read off the visual rather than tracked here, because the visual is what actually wears
    /// them: a body swapped mid-shift arrives with no grafts and this says so, with nothing to
    /// keep in step.
    int Grafts()
    {
        var visual = Visual;
        if (visual == null) return 0;

        int mask = 0;

        foreach (var part in RemoteInjuryVisual.Parts)
            if (visual.TryGraft(part, out var donor))
                mask = RemoteInjuryVisual.WithGraft(mask, part, donor);

        return mask;
    }

    /// The livery this body wears as its own — what a limb cut off it is painted in, and what a
    /// refitted limb has to match to count as "his own again" rather than as a graft.
    static PlayerRoleType LocalLivery
    {
        get
        {
            var net = NetworkPlayer.Local;
            return net != null && net.IsSpawned ? net.RoleType : PlayerRoleType.None;
        }
    }

    /// Lets go of whatever the lost arm was holding.
    ///
    /// Not cosmetic, at least for the weapon: its model is parented to the RightHand *bone*, and an
    /// injury hides renderers rather than removing bones — so without this a torn-off arm leaves a
    /// bat floating in the air where the hand used to be. A box never had that problem (it is
    /// never parented, see the header of BoxPhysicsInteractable) but a man with one arm should not
    /// be holding one either.
    ///
    /// Which arm matters: the right one is holding it, so losing that drops anything; the left
    /// only costs a two-handed weapon, since a bat needs both hands on the shaft and a crowbar
    /// does not. Forced past the mid-swing guard, because a swing whose arm has just been torn off
    /// is not going to land.
    void DropCarriedIfArmLost(BodyPart part)
    {
        if (part != BodyPart.LeftArm && part != BodyPart.RightArm) return;

        // Including another limb he was carrying to a toolkit. The hand it was in has just come
        // off, and an arm hanging in mid-air where a hand used to be is the same bug the weapon
        // had — see the header above.
        var limb = SeveredLimbItem.Carried;
        if (limb != null)
        {
            limb.PutDown();

            HUDController.Instance?.AddNotification(
                "Limb dropped",
                $"Your {BotDamageVisual.Label(part)} gave out",
                HUDController.NotifType.Warning);
        }

        // A box takes both hands — PlayerBusyState calls that "your hands are full" — so either
        // arm going is enough to lose it. Dropped rather than thrown: he is not putting it down,
        // it is falling.
        if (BoxPhysicsInteractable.GrabbedBox != null)
        {
            BoxPhysicsInteractable.ReleaseGrabbed(throwForce: 0f);

            HUDController.Instance?.AddNotification(
                "Box dropped",
                $"Your {BotDamageVisual.Label(part)} gave out",
                HUDController.NotifType.Warning);
        }

        if (!MeleeCombat.ArmIsNeeded(part)) return;
        if (MeleeCombat.Instance == null || !MeleeCombat.Instance.DropWeapon(force: true)) return;

        HUDController.Instance?.AddNotification(
            "Weapon dropped",
            $"Your {BotDamageVisual.Label(part)} could not hold it",
            HUDController.NotifType.Warning);
    }

    /// How much of the limb an injury actually takes.
    ///
    /// A bruise is a knock: it dents the plating and nothing more. Everything else is the kind of
    /// damage that stops the limb working, and on a robot that reads as the limb coming off. Kept
    /// as one place so the picture and the debuff can never disagree — a torn-off arm that only
    /// costs 15% speed would be a worse lie than no picture at all.
    static BotDamageVisual.Severity SeverityOf(InjuryType type) =>
        type == InjuryType.Bruise
            ? BotDamageVisual.Severity.Dented
            : BotDamageVisual.Severity.Severed;

    /// The worst thing that can be put right where the player is standing, if any.
    ///
    /// Dents and injuries with no limb to fetch. A severed limb is deliberately not on this list:
    /// it is mended at the limb, and offering it here would let a player patch a torn-off arm by
    /// facing a wall — which is exactly the errand this change exists to create.
    public (InjuryType type, BodyPart part) WorstOnTheSpot
    {
        get
        {
            var worst = _active.Where(i => !i.needsLimb).OrderBy(i => i.speedMult).FirstOrDefault();
            return worst == null ? (InjuryType.Bruise, BodyPart.None) : (worst.type, worst.part);
        }
    }

    /// True when there is something a toolkit alone can fix.
    public bool HasOnTheSpotInjury => _active.Any(i => !i.needsLimb);

    /// True when the part is not merely damaged but gone — its limb is on the floor or in a hand.
    ///
    /// Asked by anything that needs to know whether there is still a hand on the end of an arm: a
    /// dented arm can carry things, a severed one cannot.
    public bool IsSevered(BodyPart part) => _active.Any(i => i.part == part && i.needsLimb);

    /// Puts one named part right.
    ///
    /// By part and never by severity. One hold, one limb — a raider who has broken an arm and a leg
    /// is two jobs, not one, which is what stops a single repair wiping away a bad minute — and the
    /// caller has already decided which one it is: he fetched a specific arm, and mending whatever
    /// happens to sort worst instead would repair the leg he is standing next to.
    public bool Repair(BodyPart part, SeveredLimbItem limb = null)
    {
        var injury = _active.FirstOrDefault(i => i.part == part);
        if (injury == null) return false;

        Mend(injury, limb);
        return true;
    }

    /// Applies a repair the server has already validated and paid for.  Network state and limb
    /// consumption are already committed, so this path updates only the owning client's gameplay
    /// presentation and cannot emit a second healing proposal/consume request.
    public bool ApplyServerConfirmedRepair(BodyPart part, PlayerRoleType livery)
    {
        var carried = SeveredLimbItem.Carried;
        if (carried != null && carried.Part != part) carried = null;

        _applyingServerRepair = true;
        _serverRepairLivery = livery;
        try { return Repair(part, carried); }
        finally { _applyingServerRepair = false; }
    }

    /// Puts a part back on, out of whatever was carried here.
    ///
    /// The limb is passed in rather than looked up, because it is no longer necessarily his own.
    /// A left arm fits any robot missing a left arm, so two players who both lost one can simply
    /// trade: the first to find one uses it and the other goes for the one still on the floor.
    /// What does not trade is the paint — the part keeps the livery of the body it was cut from,
    /// which is what makes a swap something the whole shift can see.
    void Mend(ActiveInjury injury, SeveredLimbItem limb)
    {
        _active.Remove(injury);
        Recompute();

        // Nothing carried: a dent, or an injury with no limb to fetch. His own copy, if one is
        // somehow lying about, still goes — see DropLimb on why a stale one is worse than none.
        var used = limb != null ? limb : SeveredLimbItem.For(injury.part);

        // Before the panels come back on, so they light up already wearing the right colour rather
        // than flashing his own for a frame.
        Wear(injury.part, _applyingServerRepair
            ? _serverRepairLivery
            : used != null ? used.Livery : LocalLivery);

        Visual?.SetBroken(injury.part, false);

        // Published here as well as through Recompute: the graft is set after that ran, and a mask
        // sent without it would repaint the arm on every other machine in the wearer's own colour.
        Publish();

        string label = BotDamageVisual.Label(injury.part);

        string what =
            used != null && !used.IsMine ? $"{used.OwnerName} {label} — it fits, and it shows"
          : _active.Count > 0            ? $"{_active.Count} more to patch up"
          :                                "All patched up";

        if (_applyingServerRepair)
        {
            if (used != null) Destroy(used.gameObject);
        }
        else
            used?.Consume();

        HUDController.Instance?.AddNotification(
            $"{Label(injury.type)} repaired", what, HUDController.NotifType.Success);
    }

    /// Records whose paint a part is now wearing, and repaints it.
    void Wear(BodyPart part, PlayerRoleType livery)
    {
        var visual = Visual;
        if (visual == null) return;

        if (livery == LocalLivery) visual.ClearGraft(part);
        else                       visual.SetGraft(part, livery);
    }

    /// Everything at once — for a respawn or a shift reset, not for the repair interaction.
    public void ClearAll()
    {
        _active.Clear();
        Recompute();
        Visual?.Clear();

        // The limbs stay where they fell. They used to be wiped here, as repairs for injuries that
        // no longer exist — but a limb is not a repair token any more, it is a part, and one this
        // body no longer needs is exactly the one a team-mate is out looking for. All that has to
        // happen is that the hand lets go of whatever it was carrying.
        SeveredLimbItem.DropCarried();
    }

    // ── Update ────────────────────────────────────────────────────────────────

    // Injuries no longer heal on their own. They used to run out after 30 to 90 seconds, which
    // made getting hurt a thing you waited out rather than a thing you dealt with — and it would
    // have made a visibly broken arm quietly reassemble itself while the player watched. The only
    // way back now is the toolkit; see Repair.
    void Update()
    {
        if (_active.Count == 0) return;

        // Periodic low-level shake while concussed — disorientation effect.
        if (HasConcussion)
        {
            _concussionShakeTimer -= Time.deltaTime;
            if (_concussionShakeTimer <= 0f)
            {
                CameraShake.AddTrauma(0.2f);
                _concussionShakeTimer = Random.Range(1.5f, 3.5f);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void Recompute(bool publish = true)
    {
        float speed = 1f;
        bool  heavy = true;
        bool  conc  = false;

        foreach (var inj in _active)
        {
            speed  = Mathf.Min(speed, inj.speedMult);
            if (inj.preventsHeavy) heavy = false;
            conc  |= inj.isConcussion;
        }

        SpeedMultiplier = speed;
        CanCarryHeavy   = heavy;
        HasConcussion   = conc;

        // Every path that adds, mends or clears an injury ends here, which makes this the one
        // place the network can be told without a caller ever having to remember to.
        if (publish) Publish();
    }

    // ── Debug ─────────────────────────────────────────────────────────────────
    //
    // Right-click the component in the inspector while the game is running. Injuries no longer
    // pass on their own, so these are also the only quick way back out of one without hunting
    // down the toolkit.

    /// Breaks a part outright, skipping the impact that would normally have caused it.
    ///
    /// `bruise` asks for the light version — the one a knock gives, which dents the plating and
    /// leaves the limb on. It is the only way to see that tier without going and getting hit,
    /// because Classify picks it from an impulse under 25 N·s.
    public void DebugBreak(BodyPart part, bool bruise = false)
    {
        if (bruise)
        {
            var dent = new ActiveInjury
                { type = InjuryType.Bruise, part = part, speedMult = 0.85f };

            if (!Admit(dent)) return;

            ShowInjury(dent, Label(InjuryType.Bruise));
            return;
        }

        // The injury each part would realistically carry, so the debuff matches the picture.
        var injury = part switch
        {
            BodyPart.Head  => new ActiveInjury
                { type = InjuryType.Concussion, speedMult = 0.60f, isConcussion = true },
            BodyPart.LeftLeg or BodyPart.RightLeg => new ActiveInjury
                { type = InjuryType.AnkleSprain, speedMult = 0.65f },
            BodyPart.LeftArm or BodyPart.RightArm => new ActiveInjury
                { type = InjuryType.ArmStrain, speedMult = 0.85f, preventsHeavy = true },
            _ => new ActiveInjury
                { type = InjuryType.BackInjury, speedMult = 0.60f, preventsHeavy = true },
        };

        injury.part = part;
        if (!Admit(injury)) return;

        ShowInjury(injury, Label(injury.type));

        if (injury.severity == BotDamageVisual.Severity.Severed) DropCarriedIfArmLost(part);
    }

    [ContextMenu("Debug / Break head")]       void DbgHead()  => DebugBreak(BodyPart.Head);
    [ContextMenu("Debug / Break left arm")]   void DbgLArm()  => DebugBreak(BodyPart.LeftArm);
    [ContextMenu("Debug / Break right arm")]  void DbgRArm()  => DebugBreak(BodyPart.RightArm);
    [ContextMenu("Debug / Break left leg")]   void DbgLLeg()  => DebugBreak(BodyPart.LeftLeg);
    [ContextMenu("Debug / Break right leg")]  void DbgRLeg()  => DebugBreak(BodyPart.RightLeg);

    [ContextMenu("Debug / Bruise head")]       void DbgBruiseHead() => DebugBreak(BodyPart.Head, bruise: true);
    [ContextMenu("Debug / Bruise left arm")]   void DbgBruiseLArm() => DebugBreak(BodyPart.LeftArm, bruise: true);
    [ContextMenu("Debug / Bruise right arm")]  void DbgBruiseRArm() => DebugBreak(BodyPart.RightArm, bruise: true);
    [ContextMenu("Debug / Bruise left leg")]   void DbgBruiseLLeg() => DebugBreak(BodyPart.LeftLeg, bruise: true);
    [ContextMenu("Debug / Bruise right leg")]  void DbgBruiseRLeg() => DebugBreak(BodyPart.RightLeg, bruise: true);

    [ContextMenu("Debug / Break everything")]
    void DbgAll()
    {
        DebugBreak(BodyPart.Head);
        DebugBreak(BodyPart.LeftArm);
        DebugBreak(BodyPart.RightArm);
        DebugBreak(BodyPart.LeftLeg);
        DebugBreak(BodyPart.RightLeg);
    }

    [ContextMenu("Debug / Repair everything")]
    void DbgFix() => ClearAll();

    [ContextMenu("Debug / What is broken")]
    void DbgReport()
    {
        if (_active.Count == 0) { Debug.Log("[Injury] nothing broken."); return; }

        Debug.Log("[Injury] " + string.Join(", ",
            _active.Select(i => $"{Label(i.type)} ({BotDamageVisual.Label(i.part)})")) +
            $" — speed x{SpeedMultiplier:F2}, heavy={CanCarryHeavy}");
    }

    // Maps impulse magnitude + bone name → injury type and severity.
    ActiveInjury Classify(float impulse, string bone)
    {
        bool isArm  = bone.Contains("Arm")  || bone.Contains("Hand")  || bone.Contains("Shoulder");
        bool isLeg  = bone.Contains("Leg")  || bone.Contains("Foot")  || bone.Contains("Toes");
        bool isHead = bone.Contains("Head") || bone.Contains("Neck");

        if (impulse < 25f)
        {
            // Light hit — bruise only.
            return new ActiveInjury
            { type = InjuryType.Bruise, speedMult = 0.85f };
        }

        if (impulse < 45f)
        {
            if (isArm)
                return new ActiveInjury
                { type = InjuryType.ArmStrain, speedMult = 0.85f, preventsHeavy = true };

            if (isLeg)
                return new ActiveInjury
                { type = InjuryType.AnkleSprain, speedMult = 0.65f };

            // Generic medium hit.
            return new ActiveInjury
            { type = InjuryType.Bruise, speedMult = 0.75f };
        }

        // Heavy hit (45+ N·s).
        if (isHead)
            return new ActiveInjury
            { type = InjuryType.Concussion, speedMult = 0.60f, isConcussion = true };

        return new ActiveInjury
        { type = InjuryType.BackInjury, speedMult = 0.60f, preventsHeavy = true };
    }

    static string Label(InjuryType t) => t switch
    {
        InjuryType.Bruise       => "Bruise",
        InjuryType.AnkleSprain  => "Sprained ankle",
        InjuryType.ArmStrain    => "Arm injury",
        InjuryType.Concussion   => "Concussion",
        InjuryType.BackInjury   => "Back injury",
        _                       => "Injury",
    };
}
