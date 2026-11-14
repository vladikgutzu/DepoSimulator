using System.Collections.Generic;
using UnityEngine;

/// Dresses a player's body in the livery of the role they are playing.
///
/// In single player there are no roles, so everyone wears the solo set. In co-op the colour is the
/// same one the minimap dot and the co-op HUD use — see PlayerRoleColors — so "go help the green
/// one" means one thing across the whole game.
///
/// Roles are picked in the lobby and can change during a shift, and on a remote peer the role
/// arrives over the network some frames after the body does. Rather than depend on being told, this
/// notices the role it is wearing no longer matches the role it has, which costs one enum
/// comparison a frame and cannot miss an event.
///
/// **Every piece of the body, not one renderer.** This used to dress the first SkinnedMeshRenderer
/// it found and nothing else, which was right for the body it was written against — one skinned
/// mesh, two submeshes — and silently wrong for the one worn now. HumanBot ships as 35 separate
/// objects, one per panel and per chassis piece, each on its own bone and each with its own
/// renderer, and they share a single material. Dressing one of them recoloured a forearm; the other
/// 34 pieces stayed the colour the model came in. In a session that read as every player being
/// identical, which is the whole thing the liveries exist to prevent.
[DisallowMultipleComponent]
public class PlayerSkin : MonoBehaviour
{
    NetworkPlayer  _net;
    bool           _ownsNet;                       // the NetworkPlayer is on this GameObject
    PlayerRoleType _worn = (PlayerRoleType)(-1);   // nothing worn yet
    bool           _wornSolo;

    /// The pieces of the worn body, captured together so a change repaints all of them at once.
    readonly List<Renderer> _pieces = new();

    /// The pieces the livery actually went onto, and what went onto them. Kept apart from
    /// `_pieces` because a piece can be skipped for a material-count mismatch, and a skipped
    /// piece must not be read back as evidence that the livery came off.
    readonly List<Renderer> _dressedPieces = new();
    Material[]              _dressedMats;

    /// The fade that legitimately wears something other than the livery — transparent instances
    /// of it, while the camera is inside the body. Null on the puppet prefab, which never fades.
    PlayerProximityFader _proximityFade;
    TPSCameraController  _cameraFade;

    bool  _warnedCount;
    float _nextBind;

    /// The injury layer on the worn body, which owns any grafted limb.
    ///
    /// A limb refitted from somebody else's body keeps that body's colour — that is the whole
    /// point of it — so those pieces are not the livery's to dress. Resolved on demand because
    /// the component is added at runtime, by whoever gets hurt first.
    BotDamageVisual _damage;

    /// The graft state this body was last dressed against. Compared rather than trusted: a graft
    /// coming off has to put the livery back, and nothing else here would ever notice.
    int _wornGrafts = -1;

    /// Throttled like Bind, and for the same reason: the component is added by whoever gets hurt
    /// first, so on a shift where nobody has been hurt yet this searches for something that does
    /// not exist — and doing that every frame is a walk of the whole body for no answer.
    float _nextDamageLook;

    BotDamageVisual Damage
    {
        get
        {
            if (_damage != null) return _damage;
            if (Time.time < _nextDamageLook) return null;

            _nextDamageLook = Time.time + RebindInterval;
            _damage = GetComponentInChildren<BotDamageVisual>(true);

            return _damage;
        }
    }

    /// How long to wait before looking for a body again after failing to find one. Only ever hit
    /// while there is nothing to dress; a body that exists is found on the first try. Without it,
    /// a player object whose model has not appeared yet re-walks its whole hierarchy and builds a
    /// dictionary every frame, forever.
    const float RebindInterval = 0.5f;

    void Awake()
    {
        _net = GetComponent<NetworkPlayer>();
        _ownsNet = _net != null;

        _proximityFade = GetComponent<PlayerProximityFader>();
        _cameraFade    = GetComponent<TPSCameraController>();
    }

    void Start()  => Apply();
    void Update() => Apply();

    void Apply()
    {
        if (!Bound()) Bind();
        if (_pieces.Count == 0) return;

        // A fade in progress is wearing transparent copies of the livery on purpose. Painting
        // over them mid-fade snaps the body back to opaque with the camera still inside it and
        // orphans the copies, so this waits: the fader restores from WornMaterials when it lets
        // go, and anything that changed meanwhile is caught on the frame after that.
        if (Fading) return;

        var net = ResolveNet();

        // Solo covers both actual single player and a networked body whose role has not arrived
        // yet: an unassigned player is grey, not miscoloured as somebody else's job.
        bool solo = net == null || !IsNetworked;
        var  role = solo ? PlayerRoleType.None : net.RoleType;

        var set = solo ? PlayerSkinRegistry.Solo : PlayerSkinRegistry.For(role);

        // No catalog, or none for this role: leave the model's own materials alone rather than
        // blanking the body.
        if (set == null || set.Materials == null || set.Materials.Length == 0) return;

        // The guard is "wearing the right thing", not "was told the right thing". It used to be
        // the latter — role and solo compared, nothing else — and so anything that put other
        // materials on the body held forever, because the role had not changed: a fader restoring
        // a capture it took before the livery was applied put the model's stock albedo back and
        // this never argued. That albedo is Humanbot_Albedo_Blue, the same texture the Assembler
        // livery uses, so a stripped body reads in-game as a wrong role rather than as a bug.
        var damage  = Damage;
        int grafts  = damage != null ? damage.GraftVersion : 0;

        if (solo == _wornSolo && role == _worn && grafts == _wornGrafts && StillWearing(set)) return;

        int dressed = 0, mismatched = 0;

        _dressedPieces.Clear();

        foreach (var piece in _pieces)
        {
            if (piece == null) continue;

            // A count mismatch means the skin was authored against a different body. Applying it
            // would put the armour material on the underlayer and vice versa — and Unity logs its
            // own complaint about the submesh count on top. Skipped per piece rather than
            // abandoning the whole body: a stray multi-material prop among 35 single-material
            // panels should cost that prop, not the livery.
            if (piece.sharedMaterials.Length != set.Materials.Length) { mismatched++; continue; }

            // Somebody else's arm, bolted on and wearing somebody else's paint. Skipped rather
            // than dressed, and left out of _dressedPieces so StillWearing never reads it back as
            // the livery having come off — which would repaint the whole body every frame.
            if (damage != null && damage.IsGrafted(piece)) continue;

            // sharedMaterials, not materials: the latter instances a copy per renderer, which for
            // 35 pieces across a four-player session is 140 throwaway materials that leak on
            // despawn. The sets are assets, so every player on a role shares the same one.
            piece.sharedMaterials = set.Materials;
            _dressedPieces.Add(piece);
            dressed++;
        }

        if (mismatched > 0 && !_warnedCount)
        {
            _warnedCount = true;
            Debug.LogWarning($"[PlayerSkin] {set.name} has {set.Materials.Length} material(s); " +
                             $"{mismatched} of {_pieces.Count} body pieces want a different count " +
                             "and were left as they were.");
        }

        if (dressed == 0) return;

        _dressedMats = set.Materials;
        _worn        = role;
        _wornSolo    = solo;
        _wornGrafts  = grafts;

        // Said again after the pass rather than relied upon to have been skipped. A graft applied
        // in the window before this found the injury layer would have just been painted over, and
        // nothing else would ever put it back.
        damage?.RepaintGrafts();
    }

    // ── Holding the livery on ─────────────────────────────────────────────────

    /// True while every piece this dressed is still in the material it was dressed in.
    ///
    /// One reference compare per piece per frame, and deliberately reading `sharedMaterial`
    /// rather than `sharedMaterials`: the plural getter allocates a fresh array on every call,
    /// which across 35 pieces every frame is a garbage source, not a check.
    bool StillWearing(PlayerSkinSet set)
    {
        if (_dressedMats != set.Materials || _dressedPieces.Count == 0) return false;

        foreach (var piece in _dressedPieces)
        {
            if (piece == null) continue;
            if (piece.sharedMaterial != set.Materials[0]) return false;
        }

        return true;
    }

    /// What this body is wearing under any fade, for whoever needs to put it back.
    ///
    /// The faders take the body's materials to build their transparent copies from, and hand
    /// them back when the fade ends. Handing back a capture is only right while nothing repaints
    /// in between — a role picked in the lobby, a body swapped mid-shift — so they ask here
    /// instead, and the answer is always the livery as of this frame.
    public Material[] WornMaterials(Renderer piece)
    {
        if (piece == null) return null;

        // A borrowed limb first, because it is the one piece of the body whose right answer is not
        // the livery. Without this the fader hands back whatever it captured, and a capture taken
        // before the graft puts the wearer's own colour back onto an arm that is not his.
        var damage = Damage;
        if (damage != null)
        {
            var grafted = damage.GraftMaterials(piece);
            if (grafted != null) return grafted;
        }

        if (_dressedMats == null) return null;
        return _dressedPieces.Contains(piece) ? _dressedMats : null;
    }

    /// True while a fade owns the body's materials.
    bool Fading =>
        (_proximityFade != null && _proximityFade.Fading) ||
        (_cameraFade    != null && _cameraFade.FadingPlayer);

    // ── Finding the body ──────────────────────────────────────────────────────

    /// True while the pieces we captured are still the body being worn.
    ///
    /// Tested on activeInHierarchy and never on `enabled`: BotDamageVisual switches individual
    /// renderers off to show an injury, and treating a torn-off arm as a body swap would rebind —
    /// and repaint — every time somebody got hurt.
    bool Bound() =>
        _pieces.Count > 0 && _pieces[0] != null && _pieces[0].gameObject.activeInHierarchy;

    /// Captures the pieces of whatever body is being worn now.
    ///
    /// Two filters, and both earn their place.
    ///
    /// **Active only.** PlayerBody leaves a swapped-out body in the hierarchy, deactivated, so the
    /// change can be undone — measured on the live player: 54 renderers under Player, 16 of the
    /// names duplicated between the worn body and a retired one. Including inactive ones dresses
    /// the robot nobody can see.
    ///
    /// **The body's own material.** A weapon in the fist, a box in the arms and a severed limb
    /// being carried are all children of this hierarchy by the time a rebind can happen, and none
    /// of them is part of the robot. They are told apart by what they are made of: the body is the
    /// set of pieces sharing one material, and on this model that is 35 of them against one of
    /// anything else.
    void Bind()
    {
        if (Time.time < _nextBind) return;
        _nextBind = Time.time + RebindInterval;

        _pieces.Clear();
        _dressedPieces.Clear();
        _dressedMats = null;
        _worn        = (PlayerRoleType)(-1);   // a new body has never been dressed
        _wornSolo    = false;
        _wornGrafts     = -1;
        _damage         = null;                // a new body has its own injury layer
        _nextDamageLook = 0f;                  // and it is looked for straight away

        var all = GetComponentsInChildren<Renderer>(false);
        if (all.Length == 0) return;

        var body = Dominant(all);
        if (body == null) return;

        var damage = Damage;

        foreach (var r in all)
        {
            if (r == null) continue;

            // Grafted pieces are kept even though they are wearing something else. They are part
            // of the body and will want dressing again the moment the graft comes off; dropped
            // here, they would be invisible to every later repaint.
            if (r.sharedMaterial == body || (damage != null && damage.IsGrafted(r))) _pieces.Add(r);
        }
    }

    /// The material the most renderers under here are wearing — the body, by weight of numbers.
    static Material Dominant(Renderer[] all)
    {
        var tally = new Dictionary<Material, int>();

        foreach (var r in all)
        {
            var m = r != null ? r.sharedMaterial : null;
            if (m == null) continue;

            tally.TryGetValue(m, out int n);
            tally[m] = n + 1;
        }

        Material best = null;
        int      most = 0;

        foreach (var kv in tally)
            if (kv.Value > most) { most = kv.Value; best = kv.Key; }

        return best;
    }

    // ── Whose role this body wears ────────────────────────────────────────────

    /// The NetworkPlayer whose role this body wears.
    ///
    /// On the NetworkPlayer prefab that is simply the component alongside this one. On the scene
    /// rig -- the body the local player actually looks at, which PlayerPositionBridge mirrors
    /// into a NetworkPlayer puppet rather than being one -- there is no such component, and
    /// without this fallback the rig scored itself solo and stayed in the model's own materials.
    /// The result was a player seeing themselves grey while the rest of the team saw them in
    /// their role colour, so "go help the green one" meant nothing to the green one.
    ///
    /// Re-resolved rather than cached because the local NetworkPlayer spawns some frames after
    /// the rig does, and in single player never spawns at all.
    NetworkPlayer ResolveNet()
    {
        if (_ownsNet) return _net;
        return NetworkPlayer.Local;
    }

    static bool IsNetworked
    {
        get
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            return nm != null && nm.IsListening;
        }
    }
}
