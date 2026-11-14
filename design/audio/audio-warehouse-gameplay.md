# Warehouse gameplay audio specification

Status: implementation-ready draft  
Approved source shortlist: 2026-10-03  
Target engine: Unity 6 project

## 1. Goals

The audio system must make warehouse actions readable without adding input
latency, duplicated multiplayer playback, or significant CPU cost. Sound should
communicate four things in priority order:

1. Did the player's interaction succeed or fail?
2. What physical material and weight just moved or collided?
3. What state is nearby machinery in?
4. Did an order, lobby, or warehouse event change state?

Audio must not be used as the sole carrier of gameplay-critical information.
Every critical cue keeps its existing visual/UI equivalent.

## 2. Existing material to preserve and review

The project already contains reusable audio under `Assets/Audio/`, including:

- `driving.wav` through `driving5.wav` for vehicle layers/variations;
- `drill_clip_01.wav` for assembly and repair;
- `electric-motor-whir-77588.wav` for powered mechanisms;
- `robotic-arm-75154.wav`, `robotic-arm-75155.wav`, and
  `robotArmSFX_01.wav` for robot servos and limb interactions;
- `industrial-ambiance-57140.wav` for the warehouse bed;
- `alarm.mp3` and `lamp-key-sfx-312914.wav` for alarms/electrical feedback;
- the existing zombie/raider voice and hit set.

These clips should be auditioned before adding replacements. Reuse is preferred
when the clip fits the event and meets the import rules below.

## 3. Mixer structure

Implemented/required AudioMixer groups:

```text
Master                (all game audio; Vivox voice chat is not routed here)
|- Music              (menu and gameplay background music)
|- UI                 (2D, never occluded)
|- Foley              (boxes, pallets, footsteps, tools)
|- SFX                 (general gameplay one-shots)
|- Vehicles
|  |- VehicleEngine
|  |- VehicleMechanism
|  `- VehicleWarning
|- Robot              (servos, hits, limb actions)
|- WorldEvents        (power, alarm, shelf/gate events)
`- Ambience
```

The current player-facing controls are `Master Volume`, `Music On/Off`, and
`Music Volume`. They persist through `PlayerPrefs` and are available in both the
main menu and the in-game pause menu. Vivox voice chat remains independent and
must never be controlled through `AudioListener.volume` or routed under the
game's Master group.

The audio runtime has two explicit contexts:

- Main menu: music only. Gameplay one-shots, UI clicks, machinery, vehicles,
  foley, and warehouse ambience are muted.
- Gameplay: music plus all routed warehouse groups.

The mixer asset must expose `MasterVolume`, `MusicVolume`, `AmbienceVolume`,
`SFXVolume`, `UIVolume`, `FoleyVolume`, and `VehiclesVolume` in decibels. The
runtime loads `Resources/Audio/WarehouseAudioMixer` when no mixer is assigned in
the inspector. Menu and gameplay music may likewise be assigned in the inspector
or loaded from `Resources/Audio/Music/MenuMusic` and `GameplayMusic`.

## 4. Event catalogue

### 4.1 Interaction and order UI

| Event | Cue | Playback | Notes |
| --- | --- | --- | --- |
| Pickup confirmed | short cardboard/metal handling tick | local 2D or close 3D | Play only after authoritative success |
| Drop confirmed | material/weight-selected drop | 3D | Do not also play a second scripted throw cue |
| Part placed | short mechanical click | 3D + subtle local UI | One semantic event, not two network echoes |
| Part returned | reverse click/soft error | local 2D | Lower priority than order events |
| Purchase confirmed | short cash-register `cha-ching` | local UI 2D | Play only after the local or server-authoritative purchase result succeeds |
| UI button accepted | quiet interface click | local UI 2D | One delegated callback per UI root; disabled controls stay silent |
| Order accepted | positive notification | UI 2D | `ui_success_132473.mp3`, shortened if needed in editor using clip region data, not re-encoding |
| Order completed | stronger positive notification | UI 2D | Same source may use pitch +3% and higher gain |
| Order expired | error notification | UI 2D | `ui_error_147336.mp3` |
| Interaction failed | short error | local 2D | Cooldown required to prevent spam |
| Ping/marker | concise UI ping | UI 2D | Reuse an existing clean scanner/ping where appropriate |

### 4.2 Boxes and pallets

| State | Source | Rules |
| --- | --- | --- |
| Grab/handle | `box_drop_light_453026.mp3` at reduced gain | Short one-shot only after confirmed pickup/return |
| Light box drop | `box_drop_light_453026.mp3` | Small boxes and low impulses |
| Medium/heavy box impact | `box_drop_light_453026.mp3` with intensity response | Short one-shot until dedicated impact transients are available |
| Pallet/wood impact | `pallet_wood_thud_6244.mp3` | Pallet contacts only; minimum impulse threshold |
| Slide/scrape | existing handling source if suitable | Start only after sustained contact; stop promptly |

`box_handling_impacts_32135.mp3` remains in the licensed source library but is not assigned
to automatic pickup, return, drop, collision, or bench events. The recording contains a long
sequence of impacts, so playing it as a one-shot can continue well after the action has ended.

Collision audio is amplitude-driven, not `OnCollisionStay`-driven. Use a short
per-object cooldown and impact thresholds so a settled stack does not chatter.
Recommended starting points:

- ignore relative collision speed below 0.7 m/s;
- light impact: 0.7-2.0 m/s;
- medium impact: 2.0-4.5 m/s;
- heavy impact: above 4.5 m/s;
- per-object collision cue cooldown: 100-160 ms;
- pitch randomization: +/-4%; volume randomization: +/-1.5 dB.

Final thresholds must be tuned in play mode against the actual rigidbody masses.

### 4.3 Reach truck/forklift

| State | Cue/layer | Behaviour |
| --- | --- | --- |
| Enter/start | one-shot motor start | Play once for a successful driver transition |
| Idle | low engine/motor loop | Stable loop; no restart while input fluctuates |
| Drive | `driving*.wav` loop/crossfade | Pitch/volume follow smoothed local speed, not network packet cadence |
| Loaded drive | optional heavier layer | Blend from actual load state with hysteresis |
| Lift/forks moving | electric/hydraulic loop | Start on lift motion, fade out 80-150 ms after stop |
| Reverse | `vehicle_reverse_beep_359231.mp3` | VehicleWarning group; regular interval, not every frame |
| Brake/skid | short one-shot | Only above minimum speed/deceleration |
| Collision | material impact | Collision impulse thresholds and cooldown |
| Exit/stop | motor stop one-shot | Do not abruptly cut WorldEvents or UI |

Continuous local vehicle audio must be evaluated from the locally simulated
controller state. Remote vehicles use interpolated replicated state. Audio must
never write transforms, rigidbody state, or network ownership and must not poll
scene-wide object lists every frame.

### 4.4 Assembly, repair, and packing

| Event | Cue | Notes |
| --- | --- | --- |
| Install/repair progress | `drill_clip_01.wav` or ratchet loop | Fade rather than restart every progress tick |
| Mechanical tighten | `repair_ratchet_33787.mp3` | Use selected short region/one-shot sequence |
| Pack order | `packing_tape_31068.mp3` | Start with packing action; stop/cancel cleanly |
| Instant assembler install | short tool click + completion | Must still communicate completion but not fake a 3-5 s action |
| Slow non-assembler install | loop plus completion | Multiplayer-only role penalty; solo remains unpenalized |

### 4.5 Robot combat and detachable limbs

| Event | Cue | Rules |
| --- | --- | --- |
| Weapon/limb swing | `melee_swing_whoosh_487667.mp3` | Play on committed attack frame once |
| Metal hit | `robot_metal_impact_30254.mp3` | Confirmed collision/hit, varied pitch |
| Robot fall/ragdoll | metal/body impact variation | Trigger on meaningful ground impact only |
| Limb detach | servo release + metal transient | One authoritative semantic event |
| Limb pickup/equip | robotic arm servo/click | Local success plus audible close-range world cue |
| Limb repaired/refitted | servo seat + positive click | Play after state confirms the limb is attached |

### 4.6 Lobby and network feedback

- Player joined/left: quiet UI notification, one play per membership change.
- Role selected/denied: positive/error UI cue after the server confirms the role.
- Lobby error/disconnect: error cue, never looped.
- Do not produce join audio for the initial snapshot of players already present.
- Nickname validation errors use the same error family with a UI cooldown.

### 4.7 Warehouse events

| Event | Cue | Behaviour |
| --- | --- | --- |
| Power loss | `event_power_outage_451574.mp3` | Global event one-shot; follow with ambience/mixer transition |
| Short circuit/sparks | `event_short_circuit_154595.mp3` | 3D at the electrical source; trim playback duration logically by stop/fade, not source re-encode |
| Alarm | existing `alarm.mp3` | WorldEvent loop with explicit start/stop state |
| Gate/shelf crash | existing procedural/event cue | Keep material impact separate from alarm |

## 5. Spatialisation

- UI and local-only confirmation: spatial blend 0.
- Small foley and tools: spatial blend 1, min distance 1-2 m, max 12-18 m.
- Robot hits: min 1.5 m, max 20 m.
- Vehicles: min 3 m, max 35-45 m.
- Alarms and major events: min 8 m, max 60-90 m or global 2D when the whole team
  must hear them.
- Use logarithmic rolloff unless a cue has a specific gameplay reason not to.
- Doppler starts at 0 for warehouse machinery; enable only after listening tests.
- Avoid per-source real-time occlusion raycasts for this pass. If later added,
  budget and batch them explicitly.

## 6. Multiplayer rules

The server/host remains authoritative for gameplay state, but it does not stream
audio. Network messages carry compact semantic events or replicated state:

```text
eventId + networkObjectId + cueId + position + compact variant/strength
```

Rules:

1. Play action sounds only after the authoritative action is accepted.
2. If local prediction is used, tag it and suppress the matching confirmed echo.
3. Deduplicate by `(eventId, networkObjectId)` in a short rolling window.
4. Continuous sounds are derived from replicated state, not sent every frame.
5. A late joiner receives current loop states, not historical one-shots.
6. Cosmetic audio never changes physics, ownership, order state, or damage.

This prevents the previously observed class of duplicate throws, packet-paced
vehicle feedback, and host/client sound mismatches.

## 7. Import settings

Recommended Unity defaults after the original files are downloaded:

| Clip type | Load type | Compression | Sample rate | Preload |
| --- | --- | --- | --- | --- |
| UI/short impacts under 1 s | Decompress On Load | PCM or ADPCM as size permits | Optimize Sample Rate | Yes |
| Short SFX 1-10 s | Compressed In Memory | Vorbis, quality 65-80 | Optimize Sample Rate | Yes |
| Long loops/ambience | Streaming | Vorbis, quality 55-70 | Preserve/Optimize after audition | No/streamed |

Keep source MP3s unchanged in `Assets/Audio/Pixabay/`; Unity's platform importer
handles runtime compression. Never make an MP3-from-MP3 derivative merely to
reduce size. Mono is preferred for 3D point-source effects; stereo is reserved
for UI, ambience, and deliberately wide machinery layers.

## 8. Performance budgets

Target budgets for the warehouse scene:

- maximum audible real voices: 32 on desktop;
- maximum simultaneously active vehicle loops: 12 total layers;
- pooled 3D one-shot sources: 16, expandable to 24 only after profiling;
- UI one-shots: maximum 4 concurrent;
- collision one-shots: maximum 8 concurrent, priority by distance/impulse;
- total imported SFX memory target for this pass: <= 24 MB at runtime;
- audio-system main-thread CPU target: <= 0.5 ms average, <= 1.0 ms peak;
- zero managed allocations per frame from continuous audio controllers;
- no `FindObjects*`, LINQ, or new `AudioSource` creation in per-frame paths.

Quality tiers:

- High: budgets above.
- Medium: 24 voices, 12 pooled one-shots, reduced max distances by 20%.
- Low: 16 voices, 8 pooled one-shots, no optional load/servo layers.

## 9. Loudness and priority guide

- UI confirmation: clearly audible but below critical alarm level.
- Critical error/order expiry: +2 to +4 dB relative to ordinary UI.
- Vehicle warning: audible through engine at close range, limited by max distance.
- Impacts: scale by physical strength; never hard-clip at heavy collisions.
- Ambience: leave headroom for gameplay; target at least 8 dB below major events.
- Voice and critical warnings receive higher AudioSource priority than foley.

Final loudness requires an in-engine mix pass; numeric levels are intentionally
not locked before the actual clips are imported and auditioned.

## 10. Acceptance criteria

1. Every cue in the event catalogue plays at most once per semantic event.
2. A host and client hear the same confirmed one-shot at the same world event,
   subject only to distance and user volume settings.
3. Entering/driving/exiting the reach truck adds no measurable recurring GC and
   stays inside the audio CPU budget.
4. Forklift loops are smooth locally and on remote clients; they do not follow
   raw network packet timing.
5. Settled box stacks remain silent and do not generate collision chatter.
6. Box and pallet impacts communicate light/medium/heavy weight without exceeding
   the collision voice limit.
7. UI, SFX, ambience, and voice volume controls persist independently.
8. Solo play does not depend on multiplayer transport for any sound.
9. Missing clips fail silently with one development warning, not per-frame logs.
10. `Assets/Audio/Pixabay/SOURCES.md` records source, author, official URL,
    acquisition date, license URL, AI label, and checksum for every downloaded
    third-party file.

## 11. Validation checklist

- Test host, remote client, and solo for pickup/drop, throw/hit, limb detach and
  refit, assembly, packing, order completion, and event alarms.
- Drive the reach truck empty and loaded while profiling CPU, GC, and voices.
- Drop multiple box sizes and move a loaded pallet; listen for double triggers.
- Join a running lobby and confirm no historical notification burst.
- Change each volume slider and verify only the intended mixer groups change.
- Verify all Pixabay files against the manifest and store SHA-256 checksums.

