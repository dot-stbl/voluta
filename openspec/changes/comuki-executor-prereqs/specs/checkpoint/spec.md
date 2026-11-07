# Spec Delta

## ADDED Requirements

### Requirement: Graph identity on snapshots
The runtime MUST stamp the compiling graph's identity pair (`GraphId`, `GraphVersion`) into every `CheckpointSnapshot` it writes, via the new additive optional fields `GraphId` and `GraphVersion` (nullable strings, empty/null default). The snapshot wire format version MUST remain 1 — the pair rides as additive optional fields on the v1 document, following the same precedent as the additive `PendingInterrupts` field. Durable providers MUST write the fields when present and MUST accept documents that omit them. The identity pair is host-assigned at compile time and MUST NOT be mutated by providers.

#### Scenario: Snapshot carries identity pair
- **WHEN** a graph compiled with a graph id and graph version checkpoints any step
- **THEN** the stored snapshot exposes that exact pair on `GraphId` and `GraphVersion`

#### Scenario: Wire version stays one
- **WHEN** a durable provider puts a snapshot that carries a graph identity pair
- **THEN** the stored document keeps format version 1 and the pair appears as optional fields on that document

#### Scenario: Legacy document without identity
- **WHEN** a durable provider reads a document that omits `GraphId` and `GraphVersion`
- **THEN** the document maps to a valid snapshot with null identity fields (no error, no version bump)

### Requirement: Cancel reason on snapshots
`CheckpointSnapshot` MUST expose a new additive optional `CancelReason` field (nullable string, null default) that carries the host-supplied reason when a snapshot is written by an out-of-band cancellation. Providers MUST roundtrip the field as an optional v1 field. Absence on legacy documents MUST map to null.

#### Scenario: Cancel reason roundtrips
- **WHEN** a thread is cancelled with a reason and the resulting snapshot is put and fetched
- **THEN** the fetched snapshot carries the same `CancelReason` value

#### Scenario: Legacy snapshot has no cancel reason
- **WHEN** a provider reads a snapshot written before `CancelReason` existed
- **THEN** the field reads as null and no other semantic content is affected

### Requirement: Optimistic concurrency on put
`ICheckpointer.PutAsync` MUST accept an optional `expectedParentStep` (nullable `long`, null default) as an additive parameter. Semantics:
- When `expectedParentStep` is non-null, the provider MUST atomically verify, under the same write operation, that the thread's current latest step equals `expectedParentStep` before persisting the new snapshot; the check and the write MUST NOT be interleavable with a competing writer's write.
- Rewriting the **same** step (latest step equals `expectedParentStep` and the target key equals that step) MUST be allowed, but MUST be protected by a per-row/per-record concurrency token so a lost-update race on the same step also fails (EF: rowversion or `xmin`; equivalent primitives on other providers).
- Both race outcomes — an expected-parent mismatch and a same-step concurrent overwrite — MUST fail by throwing `CheckpointStoreException` with the new stable code `checkpoint.conflict`.
- When `expectedParentStep` is null, the provider MUST perform a blind put exactly as before (backward compatible); provider behavior MUST NOT change for callers that do not pass the parameter.
A conflict is a host-visible write failure: it MUST propagate as an exception out of the run stream and MUST NOT be converted into a `Failed` (or otherwise terminal) snapshot.

#### Scenario: Expected parent step matches
- **WHEN** a provider puts step 7 with `expectedParentStep = 6` while the thread's latest step is 6
- **THEN** the put succeeds and the thread's latest step becomes 7

#### Scenario: Expected parent step mismatch
- **WHEN** two writers each put with `expectedParentStep = 6` and the first commit lands before the second
- **THEN** the second put throws `CheckpointStoreException` with code `checkpoint.conflict` and the second snapshot is not persisted

#### Scenario: Same step rewrite protected
- **WHEN** two writers concurrently rewrite the same step key with a guarded put
- **THEN** exactly one succeeds and the other throws `CheckpointStoreException` with code `checkpoint.conflict`

#### Scenario: Null expected parent stays blind
- **WHEN** a caller puts a snapshot without `expectedParentStep`
- **THEN** the provider writes it unconditionally, matching pre-change behavior

#### Scenario: Conflict escapes the stream as an exception
- **WHEN** a runtime put loses a guarded-write race during a streamed run
- **THEN** the stream surfaces the `checkpoint.conflict` exception to the host and does not synthesize a Failed checkpoint for it

### Requirement: All providers implement guarded puts
Every shipped checkpointer provider — InMemory (core), File, Redis, S3, and EntityFrameworkCore — MUST implement the `expectedParentStep` semantics above, including the same-step concurrency token on durable stores, and MUST pass the shared conformance conflict scenarios. Providers without native compare-and-swap primitives MUST implement the check atomically within a single storage operation or a transaction (for example conditional writes, transactions, or distributed locks provided by the backend).

#### Scenario: InMemory guarded put
- **WHEN** the InMemory checkpointer receives a guarded put whose expected parent step no longer matches
- **THEN** it throws `CheckpointStoreException` with code `checkpoint.conflict`

#### Scenario: File provider guarded put
- **WHEN** the File checkpointer receives a guarded put whose expected parent step no longer matches
- **THEN** it throws `CheckpointStoreException` with code `checkpoint.conflict`

#### Scenario: Redis provider guarded put
- **WHEN** the Redis checkpointer receives a guarded put whose expected parent step no longer matches
- **THEN** it throws `CheckpointStoreException` with code `checkpoint.conflict`

#### Scenario: S3 provider guarded put
- **WHEN** the S3 checkpointer receives a guarded put whose expected parent step no longer matches
- **THEN** it throws `CheckpointStoreException` with code `checkpoint.conflict`

#### Scenario: EF Core provider guarded put
- **WHEN** the EF Core checkpointer receives a guarded put whose expected parent step no longer matches
- **THEN** it throws `CheckpointStoreException` with code `checkpoint.conflict`

### Requirement: Transactional participation via unit of work
A host that writes its own work item (or any side data) in the same storage transaction as the checkpoint MUST be able to make the two writes atomic. The product MUST expose an `ICheckpointUnitOfWork` abstraction (in the abstractions package, alongside `RunOptions`) that the host supplies per run; the EF Core provider MUST adapt it to the host's `DbContext`/transaction so the checkpoint put enlists in the host transaction and commits (or rolls back) with it. The unit of work MUST be passed explicitly on `RunOptions` as an optional property; the runtime MUST carry it as run-scoped state and hand it to the checkpointer at put time. Ambient/`AsyncLocal` flow MUST NOT be used. When `RunOptions` carries no unit of work, the put MUST behave as a standalone write exactly as before.

#### Scenario: Work item and checkpoint commit together
- **WHEN** a host runs a graph with a unit of work bound to its own transaction and the run checkpoints
- **THEN** the host's work-item write and the checkpoint snapshot commit in one transaction — committing makes both visible, rolling back leaves neither

#### Scenario: Rollback discards both writes
- **WHEN** the host rolls back its transaction after a run that checkpointed through the unit of work
- **THEN** the checkpoint snapshot is not visible afterwards

#### Scenario: No unit of work keeps standalone put
- **WHEN** a run executes without a unit of work on its run options
- **THEN** checkpoints are written standalone as before and no transactional enlistment occurs

### Requirement: Runtime stamps expected parent step automatically
The runtime MUST pass `expectedParentStep` on every checkpoint put it performs during a run, using the step of the thread's latest snapshot it loaded (or wrote) before the put, without requiring the host to supply it. Host-initiated puts outside a run (for example state update / fork) remain host's choice and stay backward compatible when the parameter is omitted.

#### Scenario: Multi-superstep run uses guarded puts
- **WHEN** a run completes superstep N and checkpoints superstep N+1 through the runtime
- **THEN** the put is guarded with the step of the snapshot the runtime last loaded or wrote for that thread

#### Scenario: Competing run on one thread loses
- **WHEN** a second run is started on a thread that a first (still live) run is checkpointing, and the second run later performs its own guarded put
- **THEN** the second run's put fails with `checkpoint.conflict` instead of silently overwriting the first run's history
