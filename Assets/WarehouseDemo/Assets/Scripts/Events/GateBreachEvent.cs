using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;

/// Something is hammering on a loading gate. Then it comes in.
///
/// The point of the event is the wait. A raid that simply appears inside is a fight; a raid you can
/// hear coming through a shaking shutter, for long enough to fetch a bat and stand somewhere
/// sensible, is a warning you get to act on. That is why the banging phase is the longest part of
/// it and why nothing crosses the threshold until it ends.
///
/// The raiders exist for the whole of it, standing outside and swinging at the gate, rather than
/// being spawned at the moment it opens. Costs a little more; means the silhouettes in the gate's
/// window are the same men who then walk in.
///
/// Their navmesh only exists indoors, so the outdoor half cannot be an agent walking anywhere:
/// while they are outside the agent and ThiefController are off and this drives them by hand, and
/// both are handed back the moment they are through the doorway and standing on mesh.
///
/// In a session all of that is the server's, and every bit of it is *seen* through NGO from the
/// moment they are put down outside. They used to be spawned into the network only at the
/// hand-over, at the very end — so a client watched a gate shake for twenty seconds with
/// nothing behind it, and then a crowd of raiders appeared out of nowhere already inside the
/// building. The whole point of the event is the wait, and the wait was invisible to everybody
/// except whoever was hosting.
public class GateBreachEvent : IGameEvent
{
    public string Name        => "GateBreach";
    public string Description => "Something is hammering on one of the loading gates";

    // ── Tuning ────────────────────────────────────────────────────────────────

    /// Long enough to cross the warehouse, pick up something to swing and get back to the gate —
    /// and longer than it used to be, because what comes through is now a crowd rather than a
    /// couple of stragglers. The warning has to be worth as much as the thing it warns about.
    const float BangDuration = 20f;

    /// Between blows on the gate.
    const float BangInterval = 1.1f;

    const int MinRaiders = 6;
    const int MaxRaiders = 10;

    /// How fast they come through, in metres per second. Matches the raider prefab's own speed,
    /// which is the stride the run clip depicts — walk them in any faster and the feet skate.
    const float WalkInSpeed = 1.0f;

    /// Gives up on anyone who has not made it through by then, so a raider stuck on a crate in the
    /// doorway cannot hold the event open forever.
    const float WalkInTimeout = 10f;

    /// Shoulder to shoulder, and meant literally — they are pressed against the door, not queuing
    /// politely. Also sets how many fit across one doorway. Tighter than a body is wide, on
    /// purpose: a crowd leaning on a door overlaps, and gaps read as a formation.
    const float ShoulderWidth = 0.45f;

    /// How far behind the front row the next one stands. Tight enough to read as one mass.
    const float RowDepth = 0.5f;

    /// Nudges each of them off the lattice. Without it nine bodies on an exact grid read as a
    /// chess set rather than as a crowd — and the eye catches the rows immediately.
    const float Jitter = 0.09f;

    /// How long before the shutter goes up a solo player is shown which gate it is.
    ///
    /// Short on purpose. Alone, the whole twenty seconds is meant to be spent working it out from
    /// the sound and from which shutter has cracks in it; this is only a backstop, late enough that
    /// finding it yourself is still worth far more than waiting for the map to say.
    const float SoloRevealLead = 3f;

    // ── State ─────────────────────────────────────────────────────────────────

    MonoBehaviour _host;
    GateAnimator  _gate;
    GateCracks    _cracks;
    Coroutine     _run;
    bool          _done;

    readonly List<GameObject> _actors = new();

    /// Measured off the gate leaf when the sequence starts — see SlotOffset.
    float _doorwayWidth = 2.5f;

    /// 0 at the first blow, 1 at the last. Drives both the cracks and how heavy the gate sounds.
    float _bangProgress;

    // Chosen on the server and sent to the clients, so everyone watches the same gate.
    int _gateIndex = -1;
    int _count     = -1;

    /// Whether anyone else is in the warehouse. Decides who is told which gate and when: in a
    /// session the minimap belongs to the Dispatcher alone (CoopHUDExtension gates it), and telling
    /// the rest of the team is that player's job, over voice, from what the map shows them.
    static bool IsNetworked =>
        Unity.Netcode.NetworkManager.Singleton != null
        && Unity.Netcode.NetworkManager.Singleton.IsListening;

    bool IsClientOnly =>
        Unity.Netcode.NetworkManager.Singleton != null
        && Unity.Netcode.NetworkManager.Singleton.IsListening
        && !Unity.Netcode.NetworkManager.Singleton.IsServer;

    // ── Network payload ───────────────────────────────────────────────────────

    /// Which gate and how many, decided once on the server. Without this each client would roll
    /// its own gate and the shaking shutter would be a different one on every screen.
    public string GetNetworkPayload(MonoBehaviour host)
    {
        PickGate();
        return $"{_gateIndex}:{_count}";
    }

    public void ApplyPayload(string payload)
    {
        var parts = payload.Split(':');
        if (parts.Length < 2) return;

        int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _gateIndex);
        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _count);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Apply(MonoBehaviour host)
    {
        _host = host;

        if (_gateIndex < 0) PickGate();

        _gate = ResolveGate();
        if (_gate == null)
        {
            Complete();
            return;
        }

        // A gate nobody put right is not a warning any more — there is nothing to break, so there
        // is nothing to hear. The team finds out the way they would find out about any other
        // raider: by running into one.
        if (_gate.IsBreached)
        {
            HUDController.Instance?.AddNotification(
                "THEY ARE INSIDE",
                "Straight through the gate nobody fixed.",
                HUDController.NotifType.Danger);
        }
        else
        {
            HUDController.Instance?.AddNotification(
                "SOMETHING'S AT THE GATE",
                "It is trying to get in. Find it before it does.",
                HUDController.NotifType.Danger);
        }

        _run = host.StartCoroutine(Sequence());
    }

    public void Remove()
    {
        if (_host != null && _run != null) _host.StopCoroutine(_run);
        _run = null;

        if (_gate != null)
        {
            _gate.SetShake(0f);
            _gate.ShowOnMap = false;

            // A gate that was forced stays forced — shutting it here would quietly undo the
            // consequence the whole repair loop is built on. Only one that never gave is closed.
            if (!_gate.IsBreached) { _gate.Close(); _cracks?.Clear(); }
        }

        // Only the actors still under this event's control are cleaned up. Once a raider has been
        // handed over to ThiefController he belongs to the raid, not to the doorway he came
        // through, and killing him here would delete a raider mid-fight.
        foreach (var actor in _actors) if (actor != null) Despawn(actor);
        _actors.Clear();

        // Remove must release the manager slot as well as the scene objects. Without this, a
        // cancelled or externally-cleaned breach permanently blocks every later breach.
        Complete();
    }

    void Complete()
    {
        if (_done) return;
        _done = true;
        GameEventManager.Instance?.MarkEventDone(Name);
    }

    /// Hands a raider to NGO, so every client has him. Silent in single player, where there is
    /// nothing to hand him to.
    static void Spawn(GameObject go)
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;

        var netObj = go.GetComponent<Unity.Netcode.NetworkObject>();
        if (netObj != null && !netObj.IsSpawned) netObj.Spawn(destroyWithScene: true);
    }

    /// Takes one back off every machine.
    ///
    /// Destroying a spawned NetworkObject on the server leaves the clients holding a raider that
    /// nothing will ever move or remove again — which is what a plain Destroy here would now do,
    /// since they are spawned from the start. Same shape as ThiefEvent.DespawnThief.
    static void Despawn(GameObject go)
    {
        var netObj = go.GetComponent<Unity.Netcode.NetworkObject>();

        if (netObj != null && netObj.IsSpawned)
        {
            if (netObj.NetworkManager.IsServer) netObj.Despawn(destroy: true);
            return;
        }

        Object.Destroy(go);
    }

    // ── The sequence ──────────────────────────────────────────────────────────

    IEnumerator Sequence()
    {
        if (!_gate.TryGetSides(out Vector3 inside, out Vector3 outside))
        {
            Complete();
            yield break;
        }

        _doorwayWidth = _gate.DoorwayWidth(
            Vector3.Cross(Vector3.up, (inside - outside).normalized));

        // Clients watch the gate and hear it; the raiders themselves are the server's to spawn.
        if (!IsClientOnly) SpawnOutside(outside, inside);

        // Damage spreads from where they are actually swinging: the middle of the doorway at about
        // chest height, not the panel's own centre.
        _cracks = GateCracks.Ensure(_gate);
        if (_cracks != null && _gate.FloorAnchor != null)
            _cracks.SetOrigin(_gate.FloorAnchor.position + Vector3.up * 1.15f);

        // The whole banging phase exists to make a hole. Through one that is already there they
        // simply walk in — no wait, no noise, no cracks to watch spread, and the gate is already up.
        if (_gate.IsBreached)
        {
            // Already forced and never repaired: it is standing open, so there is nothing to wait
            // for. Shown on the map at once whoever is playing — there was no search to protect.
            _gate.ShowOnMap = true;
        }
        else
        {
            yield return Hammer();
            yield return new WaitForSeconds(2.4f);   // clear head height before anyone ducks under
        }

        // From here the gate stays open and stays cracked until somebody repairs it.
        _gate.MarkBreached();
        EnsureToolkit();

        yield return WalkThemIn(outside, inside);

        _gate.ShowOnMap = false;

        Complete();
    }

    /// Twenty seconds of hammering on a shut gate, until it gives.
    IEnumerator Hammer()
    {
        _cracks?.SetProgress(0f);
        _gate.SetShake(1f);

        // The Dispatcher is told at once — reading the map and calling it out is the whole of that
        // role. Alone, nobody is told until the last few seconds, because there is no one to relay
        // it and the search is the event.
        float revealAt = IsNetworked ? 0f : Mathf.Max(0f, BangDuration - SoloRevealLead);

        float t = 0f;
        while (t < BangDuration)
        {
            if (!_gate.ShowOnMap && t >= revealAt) _gate.ShowOnMap = true;

            // How far into the beating this blow is. The sound and the cracks share it, so the
            // gate gets heavier to look at and heavier to listen to together.
            _bangProgress = Mathf.Pow(Mathf.Clamp01((t + BangInterval) / BangDuration), 0.7f);

            Thump();

            // Stepped on the blow rather than ramped through the wait, so the cracks jump when the
            // gate is struck. Eased so the first few hits already show, instead of twenty seconds
            // of a gate that looks fine until the last moment.
            if (_cracks != null) _cracks.SetProgress(_bangProgress);

            yield return new WaitForSeconds(BangInterval);
            t += BangInterval;
        }

        _gate.SetShake(0f);
        _gate.Open();
    }

    /// Puts a toolkit in the warehouse if there is not one already.
    ///
    /// Without this the repair is impossible to actually perform. The toolkit is not a permanent
    /// fixture — every other job that needs one spawns its own and takes it away again — so a
    /// forced gate on a quiet shift would be a job with no tool, and the gate would stay open for
    /// good through no decision of the player's.
    ///
    /// Only ever adds one, and never removes it: another event may be relying on the same kit, and
    /// tidying it away on repair would break that job instead.
    void EnsureToolkit()
    {
        if (IsClientOnly) return;                     // the server owns what exists in the world
        if (ToolkitItem.IsHeld) return;               // somebody is already carrying one

        if (Object.FindAnyObjectByType<ToolkitItem>() != null) return;

        ToolkitItem.Spawn();
    }

    void Thump()
    {
        if (_gate == null) return;

        // From the gate, not from the UI. AudioManager plays one 2D source, so every blow used to
        // arrive at the same loudness from nowhere in particular — on a warehouse with five
        // possible gates, that answered none of the question the warning is asking.
        _gate.Audio?.Impact(_bangProgress);

        CameraShake.Kick(_gate.transform.position - CameraPosition(), 0.18f);

        // Each of them swings at it. Through ThiefController.Swing rather than by poking the
        // animator, even though their controller is switched off for as long as they are outside:
        // the component being disabled stops its Update, not a method called on it, and Swing is
        // the only thing that also sends the blow to the other clients. Driven straight, the
        // hammering happened on the host alone and everybody else watched a silent, still crowd.
        foreach (var actor in _actors)
        {
            var thief = actor != null ? actor.GetComponent<ThiefController>() : null;
            if (thief == null) continue;

            thief.Swing(Random.Range(0, 3));
        }
    }

    static Vector3 CameraPosition() =>
        Camera.main != null ? Camera.main.transform.position : Vector3.zero;

    // ── Actors ────────────────────────────────────────────────────────────────

    /// Stands them outside, facing the gate, inert.
    ///
    /// Not through ThiefEvent's spawner: that one refuses any position off the navmesh, and
    /// outside the gate is exactly that. So they are instantiated bare, with the agent and the
    /// controller switched off, and they get both back on the far side of the doorway.
    void SpawnOutside(Vector3 outside, Vector3 inside)
    {
        if (ThiefController.Prefab == null) return;

        Vector3 inward = (inside - outside).normalized;
        Vector3 across = Vector3.Cross(Vector3.up, inward);

        for (int i = 0; i < _count; i++)
        {
            Vector3 pos = outside + SlotOffset(i, _count, across, inward);

            var go = Object.Instantiate(ThiefController.Prefab, pos,
                                        Quaternion.LookRotation(inward));
            go.name = $"GateBreacher_{i}";

            var agent = go.GetComponent<NavMeshAgent>();
            if (agent != null) agent.enabled = false;

            // Handed to NGO here rather than at the hand-over, so the rest of the team can see
            // what is at their gate while there is still time to do something about it. From this
            // point his position is carried by the prefab's NetworkTransform — including the
            // hand-driven walk-in below, which is written straight onto the transform and so
            // replicates like any other server-side movement.
            //
            // Before ChooseBody, and for the reason ThiefEvent.SpawnThief spells out: which body
            // he wears is a NetworkVariable, and one written on an object NGO has not spawned yet
            // has nothing to replicate from.
            Spawn(go);

            var thief = go.GetComponent<ThiefController>();
            if (thief != null)
            {
                thief.enabled = false;

                // His model now, not when the raid takes him over. Otherwise he hammers on the
                // gate as one man and turns into another the moment he is through it — on every
                // screen in the session, now that the clients can see him standing there.
                thief.ChooseBody();
            }

            _actors.Add(go);
        }
    }

    /// Where the i-th raider stands outside the gate.
    ///
    /// Ranked across the doorway and then backwards, because the doorway is only about two and a
    /// half metres wide: four of them on a fixed pitch put the outer two inside the wall, which is
    /// what a fixed pitch will always eventually do. So the row is as wide as the gate allows and
    /// the rest queue up behind — which is also how a crowd actually presses at a door.
    Vector3 SlotOffset(int index, int total, Vector3 across, Vector3 inward)
    {
        // Body widths that fit between the jambs, never fewer than one.
        float usable = Mathf.Max(ShoulderWidth, _doorwayWidth - ShoulderWidth);
        int   perRow = Mathf.Max(1, Mathf.FloorToInt(usable / ShoulderWidth));

        int row    = index / perRow;
        int inRow  = index % perRow;
        int rowHas = Mathf.Min(perRow, total - row * perRow);

        // Packed at shoulder width rather than spread to fill the opening: a row fanned out across
        // the whole doorway reads as a line-up, and this is supposed to be a crowd leaning on a
        // door. Only when a row would not otherwise fit does it tighten further.
        float pitch = rowHas > 1 ? Mathf.Min(ShoulderWidth, usable / (rowHas - 1)) : 0f;
        float side  = (inRow - (rowHas - 1) * 0.5f) * pitch;
        float depth = row * RowDepth;

        // Deterministic per slot rather than Random: the same raid laid out twice puts the same
        // man in the same place, which keeps this debuggable.
        side  += Wobble(index * 2)     * Jitter;
        depth += Wobble(index * 2 + 1) * Jitter;

        return across * side - inward * depth;
    }

    /// A repeatable -1..1 from an integer.
    static float Wobble(int n)
    {
        float x = Mathf.Sin(n * 12.9898f) * 43758.5453f;
        return (x - Mathf.Floor(x)) * 2f - 1f;
    }

    /// Walks them through the doorway, then hands each one over to the raid AI.
    ///
    /// They are moved at a fixed speed rather than over a fixed time, and the animator is told the
    /// same number. That is the whole difference between walking in and sliding in: the locomotion
    /// state's playback is multiplied by MotionSpeed, which defaults to zero, so setting only the
    /// blend parameter picks the run clip and then holds it on a single frame. Both have to be set,
    /// and both have to agree with how fast the body is actually crossing the floor.
    IEnumerator WalkThemIn(Vector3 outside, Vector3 inside)
    {
        var moving = new List<(GameObject go, Vector3 to, float speed)>();
        Vector3 inward = (inside - outside).normalized;
        Vector3 across = Vector3.Cross(Vector3.up, inward);

        for (int i = 0; i < _actors.Count; i++)
        {
            var go = _actors[i];
            if (go == null) continue;

            // Each keeps his own column through the doorway, so they file in rather than swapping
            // places and walking through one another in the opening.
            Vector3 slot = SlotOffset(i, _actors.Count, across, inward);

            // His own pace, the same one he will keep once the raid takes him over. Ten bodies
            // crossing the floor at an identical 1.2 m/s arrive as a marching band; a few percent
            // either way and they spread out on their own by the time they are inside.
            var thief = go.GetComponent<ThiefController>();
            float pace = thief != null ? thief.Pace : 1f;
            float speed = WalkInSpeed * pace;

            moving.Add((go, inside + across * Vector3.Dot(slot, across), speed));

            // Hittable from the moment the shutter is up, not only once the raid takes him over.
            // A blow hands him over on the spot and then lands.
            if (thief != null)
            {
                var actor = go;
                thief.HoldInert(() => HandOver(actor));
                thief.PublishGaitForSpeed(speed);
            }

            var anim = go.GetComponentInChildren<Animator>();
            if (anim == null) continue;

            // Through ThiefController's own calculation rather than a stride written down here.
            // The locomotion tree now holds two gaits at wildly different strides — a shamble at
            // 0.70 m/s and the player rig's run at 4.2 — so which one a given speed should look
            // like, and how fast to play it, is no longer a constant anybody can hardcode.
            ThiefController.DriveLocomotion(anim, speed, minPlayback: 0.6f);

            // Locomotion starts at frame zero for all of them otherwise, which puts every left
            // foot down on the same frame — the marching band again, this time in step rather
            // than merely abreast.
            thief?.DesyncGait();
        }

        float elapsed = 0f;
        bool  arrived = false;

        while (!arrived && elapsed < WalkInTimeout)
        {
            arrived = true;

            foreach (var (go, to, speed) in moving)
            {
                if (go == null || _handedOver.Contains(go)) continue;

                Vector3 flatTo = new Vector3(to.x, go.transform.position.y, to.z);
                if (Vector3.Distance(go.transform.position, flatTo) < 0.05f)
                {
                    // Each one is his own raid the moment he is through, not when the slowest of
                    // them is. Waiting for the whole crowd kept the front row brainless — and
                    // unhittable — for seconds after it was standing inside.
                    go.transform.position = to;
                    HandOver(go);
                    continue;
                }

                arrived = false;
                go.transform.position = Vector3.MoveTowards(
                    go.transform.position, flatTo, speed * Time.deltaTime);

                Vector3 face = flatTo - go.transform.position;
                if (face.sqrMagnitude > 0.001f)
                    go.transform.rotation = Quaternion.Slerp(
                        go.transform.rotation, Quaternion.LookRotation(face), 6f * Time.deltaTime);
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        foreach (var (go, to, _) in moving)
        {
            if (go == null || _handedOver.Contains(go)) continue;

            go.transform.position = to;
            HandOver(go);
        }

        // They are the raid's problem now, not the doorway's — see Remove().
        _actors.Clear();
        _handedOver.Clear();
    }

    /// Actors already given back to the raid, whether by arriving or by being hit on the way in.
    readonly HashSet<GameObject> _handedOver = new();

    /// Gives the raider his agent and his brain back, now that he is standing on navmesh.
    void HandOver(GameObject go)
    {
        if (go == null || !_handedOver.Add(go)) return;

        // Out of the doorway's hands from here: Remove() must not despawn a raider mid-fight.
        _actors.Remove(go);

        var agent = go.GetComponent<NavMeshAgent>();
        if (agent != null && NavMesh.SamplePosition(go.transform.position, out var hit, 4f, NavMesh.AllAreas))
        {
            go.transform.position = hit.position;
            agent.enabled = true;
        }

        // No Spawn here any more — he has been a NetworkObject since he was put down outside;
        // see SpawnOutside.
        var thief = go.GetComponent<ThiefController>();
        if (thief == null) return;

        thief.ReleaseInert();
        thief.enabled = true;
        go.name = "Thief_Breacher";

        // Everything that came through the gate is here to wreck the place: no box thieves, so
        // the breach reads as an attack rather than as a delivery of shoplifters.
        thief.Initialize(ThiefRole.Vandal, MeleeWeaponKind.Fists,
                         ThiefSpawnPoints.PickExitPoint(go.transform.position),
                         ThiefTemper.Aggressive);

        // ...and then override that exit with the gate he actually came through. The generic one is
        // a point on the edge of a hardcoded rectangle that stopped matching this warehouse long
        // ago — none of the five marked gates is even inside it — so raiders using it evaporated in
        // the open. Home is the hole they made.
        thief.SetGateExit(_gate);
    }

    // ── Choosing a gate ───────────────────────────────────────────────────────

    /// The gates raiders may break in through: the ones whose name carries the marker.
    ///
    /// Sorted by name, and that is not tidiness. The index below travels to the clients, and
    /// GateAnimator.All is in registration order — which is scene load order, and nothing promises
    /// two peers register in the same sequence. Sorting by a name the level author already made
    /// unique gives every machine the same list and so the same gate.
    // The same stable, name-sorted list the repair panel and the minimap index into. Kept as one
    // implementation on GateAnimator so an index on the wire cannot mean two different gates.
    static List<GateAnimator> MarkedGates() => GateAnimator.Marked();

    /// Most of the warehouse's gates open onto places no raid should ever come from, so the ones
    /// that do are chosen by hand in the scene. An unmarked warehouse means the event cannot run,
    /// which is a setup mistake rather than a silent nothing, so it says so.
    void PickGate()
    {
        var usable = MarkedGates();

        if (usable.Count == 0)
        {
            _gateIndex = -1;
            _count     = 0;

            Debug.LogWarning(
                $"[GateBreach] No gate in the scene is marked for it. Rename the gate objects " +
                $"raiders should break in through so the name contains " +
                $"\"{GateAnimator.SpawnMarker}\" — e.g. gate_01_GRP_zombieSpawn.");
            return;
        }

        // A hole nobody closed is the way in. Coming back through the same gate is what makes
        // leaving it broken cost something — otherwise the punishment for ignoring the repair is a
        // one-in-five chance of noticing it, which is no punishment at all.
        int alreadyOpen = usable.FindIndex(g => g.IsBreached);

        _gateIndex = alreadyOpen >= 0 ? alreadyOpen : Random.Range(0, usable.Count);
        _count     = Random.Range(MinRaiders, MaxRaiders + 1);
    }

    GateAnimator ResolveGate()
    {
        var usable = MarkedGates();
        return _gateIndex >= 0 && _gateIndex < usable.Count ? usable[_gateIndex] : null;
    }
}
