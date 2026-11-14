# ADR-0001: Server-Authoritative Multiplayer Architecture

## Status

Accepted

## Date

2026-10-01

## Engine Compatibility

| Field | Value |
|---|---|
| Engine | Unity 6000.4.7f1 |
| Domain | Networking / Physics / Core |
| Netcode | Netcode for GameObjects 2.11.2 |
| Transport | Unity Transport 2.7.2 |
| Knowledge Risk | HIGH |
| References | Official Unity NetworkVariable, NetworkTransform, NetworkRigidbody, RPC and Client Anticipation documentation |
| Verification Required | Compilation and multiplayer tests after every migration wave |

## ADR Dependencies

| Field | Value |
|---|---|
| Depends On | None |
| Enables | Server-authoritative multiplayer refactor |
| Blocks | Multiplayer synchronization implementation until Accepted |
| Ordering Note | Persistent state and atomic commands must precede server physics and vehicle prediction |

## Context

### Problem Statement

The current multiplayer implementation mixes server state, owner-authoritative
movement, independently simulated physics and one-shot ClientRpc object creation.

Consequently, clients can disagree about:

- box ownership, position and container;
- pallet cargo and lift state;
- vehicle and fork position;
- ladder carry/climb state;
- deliveries and packed orders;
- event progress, shop effects and runtime objects;
- outcomes of concurrent or delayed operations;
- state observed by late-joining players.

### Constraints

- Current public multiplayer uses listen-server through Lobby/Relay.
- The architecture must remain compatible with a future dedicated server.
- Four-player warehouse gameplay can contain hundreds of resting objects.
- Unity physics is not deterministic between separate machines.
- NGO anticipation does not provide a complete prediction/rollback system.
- Existing single-player behavior must remain available but cannot influence
  multiplayer authority decisions.

### Requirements

- The host/server is the sole writer of shared gameplay state.
- A box can exist in exactly one logical state at a time.
- All gameplay operations are atomic, validated and idempotent.
- Late join reconstructs the complete current match.
- Disconnect releases or preserves owned state according to an explicit policy.
- Clients never decide gameplay collision results.
- Interaction remains responsive under 150 ms RTT, 40 ms jitter and 5% loss.
- Persistent state never depends on replaying an earlier RPC.

## Decision

Use a client-server topology where the host/server owns all consequential
gameplay state and shared physics.

Clients send intent. The server validates and commits. Clients display replicated
state and may predict only local player or vehicle presentation.

### Authority Rules

The server exclusively owns:

- box, pallet, cart, ladder and weapon state;
- container membership and slot poses;
- runtime spawning, despawning and tombstones;
- vehicle simulation and fork state;
- deliveries, packed orders and assembly progress;
- shop inventory, consumables and team currency;
- active events, objectives and repair progress;
- combat hits, injuries, ragdoll triggers and deaths;
- gameplay collision and trigger decisions.

Clients own only:

- local input sampling;
- camera, HUD, audio and cosmetic presentation;
- anticipated player/vehicle presentation pending server snapshots.

Client collision callbacks are cosmetic and may not mutate shared state.

### Discrete Command Protocol

Inventory and interaction commands use reliable RPCs and contain:

- entity reference;
- expected authoritative version;
- unique operation ID;
- action-specific data.

The actor is always resolved from `RpcParams.Receive.SenderClientId`. A client ID
is never accepted from the payload.

The server validates:

- sender identity and connected player;
- role and current player state;
- entity existence;
- expected version;
- phase, holder and container;
- interaction distance and active zone;
- capacity, cooldown and objective state.

An accepted transition increments the entity version exactly once.

Repeated operation IDs return the previous result without applying the operation
again. Stale or invalid operations are rejected with a reason and current state.

New code uses NGO 2.11 universal RPC syntax:

- `[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]` for
  player-self requests;
- `InvokePermission.Everyone` plus explicit validation for a server-owned
  command router;
- specified result targets for requester-only responses.

### Continuous Input Protocol

Player and vehicle movement use a separate unreliable input stream.

Each input frame contains:

- input sequence;
- client tick;
- movement/control values;
- a small number of previous frames for packet-loss redundancy.

The server processes inputs at the authoritative tick and acknowledges the last
consumed sequence. Client tick is metadata, never authority.

Player and vehicle prediction uses a custom circular input/state buffer:

1. Client samples and predicts locally.
2. Server simulates the same motor from validated input.
3. Server publishes authoritative state and acknowledged input sequence.
4. Client removes acknowledged inputs.
5. On material mismatch, client restores the server state and replays remaining
   inputs.
6. Small visual corrections are smoothed; large invalid differences snap.

`AnticipatedNetworkTransform` may assist presentation but is not treated as a
complete reconciliation implementation.

### Entity State

Logical entity state contains compact unmanaged/`INetworkSerializable` data:

- entity reference or stable scene ID;
- version;
- phase;
- holder/occupant;
- container reference and slot;
- authoritative pose and velocity when applicable;
- active/consumed flags.

`NetworkObjectReference` is used for live runtime-object relationships.
Stable content IDs are used only for definitions and scene/save identity.

NetworkObjects may not be nested inside other NetworkObjects. Contained cargo
remains independently spawned and references its container plus local pose.

High-count scene stock may use stable scene IDs and sharded authoritative
manifests. Independently spawned runtime boxes, pallets, delivery parts, packed
orders and world weapon pickups use pooled server-spawned NetworkObjects.

Equipped weapon visuals derive from replicated player equipment state and are
not separate NetworkObjects.

### Physics Lifecycle

Shared physical entities use these modes:

- `Contained`: kinematic, follows authoritative container-local pose;
- `Held`: kinematic, follows authoritative carry anchor;
- `Resting`: kinematic, final pose stored, no continuous traffic;
- `Dynamic`: simulated only by server while awake;
- `Consumed`: despawned with persistent tombstone where required.

Dynamic objects use server-authoritative `NetworkTransform` and
`NetworkRigidbody`. Non-authoritative copies remain kinematic.

When a body sleeps, the server publishes its final pose/version and stops
continuous transform traffic.

Container movement does not stream every contained item independently.

Object pooling uses `INetworkPrefabInstanceHandler`. Interest management and
dynamic-body budgets are introduced before scaling object counts.

### Timing and Performance

- NGO network tick: 30 Hz.
- Server physics remains on Unity FixedUpdate.
- Inputs are consumed at the server tick and held across necessary physics steps.
- Dynamic snapshots are tick-bound and threshold-driven, with a maximum target
  cadence of 20 Hz where appropriate.
- Resting, held and contained entities do not continuously stream transforms.
- Thresholds and budgets live in a `NetworkSimulationConfig` asset.

The 5 cm / 2 degrees figure is an acceptance target, not a NetworkTransform
guarantee:

- p95 owner correction <= 5 cm and <= 2 degrees under the test network profile;
- corrections are smoothed over a configurable interval;
- invalid or extreme divergence uses a configurable hard snap;
- authoritative state always wins.

### Persistent State and Late Join

Persistent facts use NetworkVariables, NetworkLists, spawned NetworkObjects or
chunked manifests. RPCs never reconstruct persistent objects.

Late join includes:

- active order, timer, bench and packed box;
- shipping gate and zones;
- boxes, holders, containers and tombstones;
- pallets, lift state, cargo and pose;
- carts, ladders, vehicles and occupants;
- deliveries, remaining slots and assembled vehicles;
- shop stock, scanner expiry, repair charges and insurance;
- active events, targets, timers and progress;
- weapons, injuries, ragdoll/death and enemy state.

Cross-object references resolve through `NetworkObjectReference` with deferred
retry. Interaction remains disabled until a `SynchronizeComplete` readiness gate
confirms that required references are available.

### Dedicated-Server Boundary

Server simulation cannot depend on:

- Camera or HUD;
- Animator or AudioSource;
- local scene player objects;
- host-only presentation singletons.

Presentation events explicitly target clients and host where appropriate.

The current product remains listen-server. Host migration is not implemented.
If the host disappears, pending commands stop, clients receive a disconnect
reason or timeout, and everyone returns to the lobby without client-authored save
commits.

### Diagnostics

`NetworkSyncAudit` maintains a bounded server-side ring buffer containing numeric
transition records:

- server tick;
- entity type and ID;
- operation ID and sender;
- old/new state;
- version;
- accepted/rejected result;
- rejection reason.

Development builds periodically compare hashes of canonical logical state in
stable entity order. Interpolated transforms and cosmetics are excluded.

On mismatch, diagnostics capture the first differing entity and recent
transitions.

Server invariants include:

- one box belongs to one phase/holder/container;
- ladder cannot be carried and climbed simultaneously;
- vehicle has at most one occupant;
- delivery slot is collected once;
- packed box completes one order;
- one accepted logical transition increments one version.

### Architecture Diagram

```text
Client input
    |
    v
Reliable command / Unreliable input stream
    |
    v
Server validation and authoritative simulation
    |
    +--> Persistent state: NetworkVariables, NetworkLists, NetworkObjects
    |
    +--> Dynamic snapshots and input acknowledgements
    |
    +--> Transient result, SFX and VFX RPCs
    |
    v
Client reconciliation, interpolation and presentation
```

### Key Interfaces

- `INetworkCommandValidator`
  - resolves actor and entity server-side;
  - returns `ValidationResult`;
  - never mutates state during validation.

- `AuthoritativeEntityState`
  - compact versioned logical state;
  - exactly one phase and optional container/holder.

- `CommandResult`
  - operation ID;
  - accepted flag;
  - rejection reason;
  - authoritative version;
  - rollback state when required.

- `MovementInputFrame`
  - input sequence;
  - client tick;
  - controls;
  - redundancy window.

- `AuthoritativeMovementSnapshot`
  - server tick;
  - last processed input sequence;
  - position, rotation and velocity;
  - motor-specific state.

- `NetworkSyncAudit`
  - bounded transition recording;
  - invariant checks;
  - development-only state hashes.

## Alternatives Considered

### Harden Current Hybrid Model

Keep client physics and owner transforms while adding validation.

**Pros:**

- smaller migration;
- preserves current responsiveness.

**Cons:**

- separate physics simulations continue to diverge;
- late and reordered messages remain difficult to reconcile;
- shared collisions remain ambiguous.

**Rejection reason:** cannot meet the requirement that all players observe the
same authoritative world.

### Distributed Authority

Allow clients to own subsets of shared world objects.

**Pros:**

- responsive local manipulation;
- reduces host physics workload.

**Cons:**

- ownership transfers and disconnects become more complex;
- authority conflicts remain possible;
- current gameplay depends on centralized orders and economy.

**Rejection reason:** unsuitable for the requested single source of truth.

### One-Shot RPC Reconstruction

Continue creating parallel-local objects from identical RPC payloads.

**Pros:**

- low initial implementation cost.

**Cons:**

- no late-join history;
- local physics objects still differ;
- tombstones and dependencies are unreliable.

**Rejection reason:** transient messages cannot represent persistent world
state.

## Consequences

### Positive

- One authoritative outcome for every gameplay action.
- Concurrent requests become deterministic.
- Late join and reconnect policy become explicit.
- Shared physics no longer diverges between peers.
- Bugs become reproducible through operation IDs and transition logs.
- Dedicated server can be added without rewriting gameplay authority.

### Negative

- Large multi-stage migration.
- Server CPU and bandwidth increase.
- Player/vehicle prediction requires custom buffering and replay.
- Existing parallel-local runtime objects must be replaced.
- Prefabs and scene setup require migration to server-authoritative components.

### Risks

- Too many active NetworkObjects or awake bodies.
  - Mitigation: pooling, lifecycle states, sharded manifests and budgets.
- Prediction motor differs between server and client.
  - Mitigation: shared motor code, fixed input protocol and replay tests.
- State/result messages arrive in different orders.
  - Mitigation: versions, operation IDs and rollback payloads.
- References are unresolved during late join.
  - Mitigation: deferred resolution and readiness gate.
- Migration temporarily mixes old and new authority models.
  - Mitigation: feature flags and one completed entity family per wave.

## GDD Requirements Addressed

No multiplayer GDD exists in the project. This ADR is based on the implemented
cooperative gameplay loop and the requirement that all peers observe one
authoritative world state.

## Performance Implications

- **CPU:** additional server simulation and validation.
- **Memory:** input buffers, authoritative state and bounded audit history.
- **Network:** fewer updates for sleeping/contained objects, more authoritative
  snapshots for active physics.
- **Load Time:** pooled runtime-prefab registration and late-join hydration.
- **Client:** interpolation and prediction replay for local movement only.

## Migration Plan

1. Add command/result primitives, versions, audit logs and invariant checks.
2. Make box, bench and shipping transitions atomic and server-validated.
3. Replace parallel-local packed orders, deliveries, pallets and weapons with
   persistent server state and runtime NetworkObjects.
4. Move shared box/pallet/ladder physics exclusively to the server.
5. Convert vehicle control to server input simulation with prediction/replay.
6. Validate player movement and all player-self RPC ownership.
7. Persist shop, event, combat, injury and ragdoll state.
8. Add late-join readiness and disconnect shutdown behavior.
9. Remove superseded ClientRpc reconstruction and owner-authority paths.
10. Run automated multiplayer integration, latency and soak suites.

## Validation Criteria

- Host plus 1-3 clients complete the full gameplay loop.
- Simultaneous operations repeated 50 times produce one accepted result.
- Late join succeeds during every persistent state and physics phase.
- Disconnect while carrying, driving or climbing releases state correctly.
- Under 150 ms RTT, 40 ms jitter and 5% loss:
  - no duplicated or missing entities;
  - no contradictory holders/containers;
  - p95 predicted-owner correction meets the configured 5 cm / 2 degree target;
  - authoritative state converges for every client.
- Headless-server integration test completes one full order.
- Thirty-minute four-player soak produces no state-hash mismatch.

## Related Decisions

- None.
