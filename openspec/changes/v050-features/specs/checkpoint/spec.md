## ADDED Requirements

### Requirement: Superstep progress on the snapshot
The checkpoint snapshot MUST carry the engine's superstep progress: completed-task identifiers, per-task in-progress retry attempts, and pending writes of completed tasks. A checkpoint at a step boundary MUST carry no residue from the finished superstep.

#### Scenario: Clean step boundary
- **WHEN** all tasks of a superstep complete and the superstep commits normally
- **THEN** the committed snapshot has empty completed-task marks and no unapplied pending writes

### Requirement: Progress fields roundtrip additively
All providers MUST roundtrip the progress fields additively, and snapshots written by older versions without these fields MUST restore as empty progress.

#### Scenario: Progress fields roundtrip on durable providers
- **WHEN** a snapshot with completed-task marks, a recorded retry attempt, and pending writes is Put then Get on each conforming provider
- **THEN** all three fields roundtrip and a legacy snapshot without them restores with empty progress

### Requirement: Typed stop reason on paused snapshots
The snapshot MUST carry a typed stop reason distinguishing non-terminal pause causes: static pause points record `StaticBefore`/`StaticAfter`, guard stops record `BudgetExhausted`/`DeadlineExceeded`; HITL interrupts are identified by non-empty pending interrupts and need no stop reason. The field MUST roundtrip additively on all providers and be absent/empty on legacy snapshots and terminal statuses.

#### Scenario: Stop reason roundtrips and defaults for legacy snapshots
- **WHEN** a snapshot with `StopReason` is Put then Get, and separately a snapshot written without the field is restored
- **THEN** the reason roundtrips exactly and the legacy snapshot reads as "no stop reason"

### Requirement: Fork preserves pending interrupts
Forking a thread at a step MUST copy the source snapshot's pending interrupts (and interrupt payload) onto the forked thread, so a fork of an interrupted thread is resumable in the same way as the source.

#### Scenario: Fork of an interrupted thread stays resumable
- **WHEN** a thread is parked Interrupted with pending interrupts and a host forks it to a new thread id
- **THEN** the forked snapshot carries the same pending interrupts and payload, and resuming the fork does not fail with "not interrupted"

### Requirement: Host type registry for typed restore
Hosts MUST be able to register value types in a `CheckpointTypeRegistry` (surfaced via `CompileOptions.CheckpointTypes`), keyed by a stable type id. Registry-aware durable providers MUST stamp values whose runtime type is registered when encoding and MUST materialize registered types back to typed instances on restore. The provider value allow-list MUST accept the stamped shape only when a registry is attached; without a registry — and on the InMemory provider — behavior MUST be unchanged.

#### Scenario: Registered type restores typed
- **WHEN** a host registers a record type, runs a graph whose channel carries that type, puts a checkpoint on a registry-aware durable provider, and reads it back
- **THEN** the restored channel value is an instance of the registered type, not a JSON element

#### Scenario: Unknown type id falls back to raw JSON
- **WHEN** a consumer restores a stamped payload whose `$type` is not in its registry
- **THEN** the read succeeds and returns the raw inner JSON value instead of failing

#### Scenario: No registry means unchanged behavior
- **WHEN** no registry is attached to compile or provider
- **THEN** wire values and restored values behave exactly as in 0.4.0, and a stamped payload from a registry-configured producer reads back as raw JSON on a registry-less consumer
