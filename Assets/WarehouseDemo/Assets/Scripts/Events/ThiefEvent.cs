using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;

/// A raid. Three to five intruders walk in through different doors, each with a single job:
/// wreck a pallet, carry a box off, lift a part off the assembly bench, or cut the power.
/// They arrive a few seconds apart rather than all at once, and every job has a wind-up long
/// enough for the team to hear the call-out, run over and beat them off it.
///
/// The event stays open until every raider is neutralised, gone, or timed out, so the
/// GameEventManager slot is held for as long as the situation is actually live.
public class ThiefEvent : IGameEvent
{
    public string Name        => "Thief";
    public string Description => "A raid on the warehouse: intruders wrecking and stealing";

    /// Hard ceiling on the whole incident. A raider wedged behind geometry must not hold the
    /// event slot open for the rest of the shift.
    const float MaxDuration = 170f;

    /// Seconds between each raider walking in — a wave, not a wall.
    const float EntryStagger = 4f;

    /// How long the alarm wails when a raid comes through the doors.
    const float SirenDuration = 3.5f;

    // Raid size. Every raid comes in at least three strong — a lone intruder reads as a
    // nuisance one player wanders over to deal with, not as an incident the shift has to stop
    // for. It still steps up as the team gets through orders. The thresholds stay deliberately
    // low so the incident is introduced during the dense opening section rather than being held
    // back until the end of the hour-long demo.
    const int BaseRaiders            = 3;
    const int FourRaidersAfterOrders = 1;
    const int FiveRaidersAfterOrders = 2;

    // Server-chosen specs, replayed identically on every client.
    readonly List<Spec> _specs = new();

    readonly List<ThiefController> _alive = new();
    MonoBehaviour _host;
    Coroutine     _entry;
    Coroutine     _timeout;
    int           _pending;      // spawned-but-not-yet-resolved bookkeeping for the entry wave
    bool          _done;

    readonly struct Spec
    {
        public readonly ThiefRole       Role;
        public readonly MeleeWeaponKind Weapon;
        public readonly ThiefTemper     Temper;
        public readonly Vector3         Spawn;
        public readonly Vector3         Exit;

        public Spec(ThiefRole role, MeleeWeaponKind weapon, ThiefTemper temper,
                    Vector3 spawn, Vector3 exit)
        {
            Role   = role;
            Weapon = weapon;
            Temper = temper;
            Spawn  = spawn;
            Exit   = exit;
        }
    }

    public ThiefEvent() { }

    /// Network path — specs come from GetNetworkPayload, one
    /// "role:weapon:sx:sy:sz:ex:ey:ez" each.
    public ThiefEvent(string[] specs)
    {
        if (specs == null) return;

        foreach (var spec in specs)
        {
            var p = spec.Split(':');
            if (p.Length < 9) continue;

            var role   = (ThiefRole)int.Parse(p[0], CultureInfo.InvariantCulture);
            var weapon = (MeleeWeaponKind)int.Parse(p[1], CultureInfo.InvariantCulture);
            var temper = (ThiefTemper)int.Parse(p[8], CultureInfo.InvariantCulture);
            _specs.Add(new Spec(role, weapon, temper, ParseV3(p, 2), ParseV3(p, 5)));
        }
    }

    static Vector3 ParseV3(string[] p, int offset) => new(
        float.Parse(p[offset + 0], CultureInfo.InvariantCulture),
        float.Parse(p[offset + 1], CultureInfo.InvariantCulture),
        float.Parse(p[offset + 2], CultureInfo.InvariantCulture));

    // ── Networking ────────────────────────────────────────────────────────────

    public string GetNetworkPayload(MonoBehaviour host)
    {
        var parts = new List<string>();

        foreach (var spec in BuildRaid())
            parts.Add($"{(int)spec.Role}:{(int)spec.Weapon}" +
                      $":{GameEventManager.FormatVec3(spec.Spawn)}" +
                      $":{GameEventManager.FormatVec3(spec.Exit)}" +
                      $":{(int)spec.Temper}");

        return string.Join("|", parts);
    }

    // ── Raid composition ──────────────────────────────────────────────────────

    static List<Spec> BuildRaid()
    {
        var roles   = PickRoles();
        var tempers = PickTempers(roles.Count);
        var result  = new List<Spec>(roles.Count);

        // Doors already handed out this raid. The spawn picker is deterministic, so without
        // this the whole wave walks in through the same entrance one after another.
        var taken = new List<Vector3>(roles.Count);

        for (int i = 0; i < roles.Count; i++)
        {
            Vector3 spawn = ThiefSpawnPoints.PickSpawnPoint(taken);
            taken.Add(spawn);

            // The raiders are zombies: they come as they are. Weapons stay in the Spec so a
            // hand-authored raid can still arm one, but nothing rolls for it any more.
            result.Add(new Spec(role: roles[i], weapon: MeleeWeaponKind.Fists,
                                temper: tempers[i], spawn: spawn,
                                exit: ThiefSpawnPoints.PickExitPoint(spawn)));
        }

        return result;
    }

    /// Tempers for one raid, dealt rather than rolled per raider.
    ///
    /// The point of the mix is that no single response works on the whole wave: charge the
    /// skittish one and he scatters, ignore the aggressive one and he is on you. Independent
    /// rolls would regularly produce an all-timid raid that nobody has to react to, or an
    /// all-aggressive one that is just a brawl — so the first two slots are pinned to one of
    /// each and only the rest are left to chance.
    static List<ThiefTemper> PickTempers(int count)
    {
        var result = new List<ThiefTemper>(count);
        if (count <= 0) return result;

        // Whoever walks in first sets the tone, and a raid should not open on the one that
        // runs away — so the aggressive slot goes first.
        result.Add(ThiefTemper.Aggressive);
        if (count > 1) result.Add(ThiefTemper.Skittish);

        while (result.Count < count)
        {
            float roll = Random.value;
            result.Add(roll < 0.4f ? ThiefTemper.Aggressive
                     : roll < 0.75f ? ThiefTemper.Skittish
                     : ThiefTemper.Bold);
        }

        return result;
    }

    /// Makes the next raid one role throughout. For looking at something specific — the carry,
    /// say, which needs a looter and only one raider in three normally is one.
    ///
    /// Cleared by whoever sets it, on the same call, so a debug raid can never leak into a real
    /// shift. Set on the server: the roles are encoded into the event's network payload, so the
    /// clients follow whatever was rolled here.
    public static ThiefRole? ForceAllRole;

    /// The first raider is always a vandal: the cheapest possible lesson in "go hit him", and
    /// the one role with no scene prerequisite, so it can never turn into a raider standing
    /// around with nothing to do. The bench is only targeted when there is something on it
    /// worth taking.
    static List<ThiefRole> PickRoles()
    {
        int orders = GameManager.Instance?.CompletedOrders ?? 0;

        int count = orders >= FiveRaidersAfterOrders ? 5
                  : orders >= FourRaidersAfterOrders ? 4
                  : BaseRaiders;

        if (ForceAllRole.HasValue)
        {
            var forced = new List<ThiefRole>(count);
            for (int i = 0; i < count; i++) forced.Add(ForceAllRole.Value);
            return forced;
        }

        var roles = new List<ThiefRole> { ThiefRole.Vandal };

        // Which of the two "costs the team something" jobs the raid opens with. Remembered so
        // #4 can cover the other one rather than doubling down on whichever the coin gave.
        bool sabotageFirst = Random.value < 0.5f;
        roles.Add(sabotageFirst ? ThiefRole.Saboteur : ThiefRole.Looter);
        roles.Add(BenchHasParts() ? ThiefRole.PartThief : ThiefRole.Looter);

        // #4 squeezes the side #2 left alone, so a bigger raid pressures both the stock and
        // the power instead of hitting one twice as hard.
        if (count >= 4)
            roles.Add(sabotageFirst ? ThiefRole.Looter : ThiefRole.Saboteur);

        // #5 is a second vandal rather than a second specialist: two Saboteurs would race for
        // the same power boxes, and a vandal always has a pallet to wreck.
        if (count >= 5)
            roles.Add(ThiefRole.Vandal);

        return roles;
    }

    static bool BenchHasParts() =>
        AssemblyBench.Instance != null && AssemblyBench.Instance.GetRandomPlacedPart() != null;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Apply(MonoBehaviour host)
    {
        _host = host;

        // Nothing to raid — a thief with no loaded pallet has nothing to wreck or carry off.
        if (!AnyLoadedPallet())
        {
            GameEventManager.Instance?.MarkEventDone(Name);
            return;
        }

        var specs = _specs.Count > 0 ? _specs : BuildRaid();

        // The raiders themselves are NetworkObjects the server spawns for everyone; a client
        // only plays the alarm and waits for the server to close the event.
        if (!IsClientOnly)
        {
            _pending = specs.Count;
            _entry   = host.StartCoroutine(EntryWave(specs));
            _timeout = host.StartCoroutine(TimeoutGuard());
        }

        HUDController.Instance?.AddNotification(
            "INTRUDERS!",
            specs.Count > 1
                ? $"{specs.Count} strangers are coming in through the doors — put them down"
                : "A stranger is in the warehouse — put him down",
            HUDController.NotifType.Danger);

        host.StartCoroutine(RaidSiren());
    }

    /// True on a peer that is neither the server nor running solo — it displays the raid but
    /// does not drive it.
    static bool IsClientOnly
    {
        get
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            return nm != null && nm.IsListening && !nm.IsServer;
        }
    }

    /// Walks them in one at a time. Spawning the whole raid on the same frame reads as a
    /// glitch; four seconds apart reads as a group filtering in through different doors.
    IEnumerator EntryWave(List<Spec> specs)
    {
        for (int i = 0; i < specs.Count; i++)
        {
            if (_done) yield break;

            if (i > 0) yield return new WaitForSeconds(EntryStagger);

            var thief = SpawnThief(specs[i]);
            if (thief == null)
            {
                // Nowhere to put him — release his slot or the event would never close.
                _pending--;
                if (_pending <= 0 && _alive.Count == 0) Finish();
                continue;
            }

            _alive.Add(thief);
            thief.OnResolved += OnThiefResolved;
        }

        _entry = null;

        // Every spawn failed — nothing to fight, so don't hold the slot.
        if (_alive.Count == 0) Finish();
    }

    /// Borrows the fire alarm's siren for a few seconds — same sound the warehouse already
    /// uses for "drop what you are doing", which is exactly the message. It stands down on
    /// its own; if a real fire alarm is running, this leaves the siren entirely alone rather
    /// than fighting it for the audio source.
    IEnumerator RaidSiren()
    {
        var gem = GameEventManager.Instance;
        if (gem != null && gem.IsEventActive("FireAlarm")) yield break;

        var siren = FireAlarmSirenPlayer.Instance;
        if (siren == null) yield break;

        siren.SetClip(gem?.FireAlarmSirenClip);
        siren.Play();

        yield return new WaitForSeconds(SirenDuration);

        // A fire alarm may have started while we were wailing — that one owns the siren now.
        if (gem == null || !gem.IsEventActive("FireAlarm")) siren.Stop();
    }

    public void Remove()
    {
        if (_host != null)
        {
            if (_entry   != null) _host.StopCoroutine(_entry);
            if (_timeout != null) _host.StopCoroutine(_timeout);
        }
        _entry   = null;
        _timeout = null;

        foreach (var thief in _alive)
        {
            if (thief == null) continue;
            thief.OnResolved -= OnThiefResolved;
            DespawnThief(thief);
        }

        _alive.Clear();

        // Remove is also the cancellation/cleanup entry point used by the event manager and QA.
        // Releasing the spawned actors without releasing the manager slot leaves Thief marked as
        // active forever, so no later raid can start during the same shift.
        Finish();
    }

    void OnThiefResolved(ThiefController thief, bool escaped)
    {
        thief.OnResolved -= OnThiefResolved;
        _alive.Remove(thief);
        _pending--;

        // Raiders still walking in keep the event open even with the floor momentarily clear.
        if (_alive.Count > 0 || _pending > 0) return;

        Finish();
    }

    IEnumerator TimeoutGuard()
    {
        yield return new WaitForSeconds(MaxDuration);

        if (_done) yield break;

        // Whatever is still standing gives up and leaves — no fine, no reward.
        foreach (var thief in new List<ThiefController>(_alive))
        {
            if (thief == null) continue;
            thief.OnResolved -= OnThiefResolved;
            DespawnThief(thief);
        }
        _alive.Clear();

        Finish();
    }

    void Finish()
    {
        if (_done) return;
        _done = true;

        if (_host != null)
        {
            if (_entry   != null) _host.StopCoroutine(_entry);
            if (_timeout != null) _host.StopCoroutine(_timeout);
        }
        _entry   = null;
        _timeout = null;

        GameEventManager.Instance?.MarkEventDone(Name);
    }

    // ── Spawning ──────────────────────────────────────────────────────────────

    static ThiefController SpawnThief(Spec spec)
    {
        // Never spawn off-mesh: the agent would sit inert and hold the event open.
        if (!NavMesh.SamplePosition(spec.Spawn, out var hit, 8f, NavMesh.AllAreas)) return null;

        GameObject go;

        if (ThiefController.Prefab != null)
        {
            go = Object.Instantiate(ThiefController.Prefab, hit.position, Quaternion.identity);
        }
        else
        {
            // Fallback so the mechanic runs before the prefab is built: a dark capsule that
            // walks, works and can be beaten exactly like the real model.
            go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.transform.position   = hit.position + Vector3.up * 0.1f;
            go.transform.localScale = new Vector3(0.8f, 0.9f, 0.8f);

            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                var col = new Color(0.13f, 0.13f, 0.16f);
                rend.material.color = col;
                rend.material.SetColor("_BaseColor", col);
            }
        }

        go.name = $"Thief_{spec.Role}";

        // Multiplayer: hand him to NGO before Initialize, so the identity NetworkVariables
        // written in there have a spawned object to replicate from.
        var netObj = go.GetComponent<Unity.Netcode.NetworkObject>();
        if (netObj != null && !netObj.IsSpawned
            && Unity.Netcode.NetworkManager.Singleton != null
            && Unity.Netcode.NetworkManager.Singleton.IsServer)
        {
            netObj.Spawn(destroyWithScene: true);
        }

        var agent = go.GetComponent<NavMeshAgent>();
        if (agent == null) agent = go.AddComponent<NavMeshAgent>();
        agent.radius = 0.35f;
        agent.height = 1.8f;

        var thief = go.GetComponent<ThiefController>();
        if (thief == null) thief = go.AddComponent<ThiefController>();
        thief.Initialize(spec.Role, spec.Weapon, spec.Exit, spec.Temper);

        return thief;
    }

    /// Removes a raider on every peer: a networked one goes through NGO, a solo one is just
    /// destroyed.
    static void DespawnThief(ThiefController thief)
    {
        var netObj = thief.GetComponent<Unity.Netcode.NetworkObject>();
        if (netObj != null && netObj.IsSpawned)
        {
            if (netObj.NetworkManager.IsServer) netObj.Despawn(destroy: true);
            return;
        }

        Object.Destroy(thief.gameObject);
    }

    static bool AnyLoadedPallet()
    {
        foreach (var zone in PalletStackZone.All)
            if (zone != null && zone.BoxesOnPallet.Count > 0) return true;
        return false;
    }
}
