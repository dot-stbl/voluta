# Design

## Context

Voluta 0.4.0 is a single-graph runtime: one `CompiledGraph` per host, checkpoints are blind writes keyed `(ThreadId, Step)`, and cancellation of a thread is only possible from inside a live run. Comuki Chat (Voluta 0.3.0, EF Core checkpointer on Postgres) is migrating to Voluta as its procedure executor and needs exactly the prerequisites listed in the proposal — none of which exist today:

- `CheckpointSnapshot` (Abstractions, wire v1) has no graph identity; `ResumeAsync` (`RunEngine.cs:88`) loads the latest snapshot via `GetAsync` and only checks `Status == Interrupted`.
- `ICheckpointer.PutAsync(snapshot, ct)` has no concurrency parameter; the EF checkpointer does a blind `FindAsync` → overwrite upsert. `RunEngine` performs ~12 puts, none guarded.
- `VolutaBuilder.Graph(...)` registers exactly one `CompiledGraph` singleton (`RemoveAll<CompiledGraph>` + factory). `GraphThreadRunner` (Voluta.Hosting) and `VolutaUiSession` (Voluta.UI) take `CompiledGraph` in their constructors.
- `RunOptions` carries only `ThreadId` and `StreamMode`; there is no way to enlist checkpoint writes into a host transaction.
- No out-of-band cancel API; `Cancelled` status exists (`GraphRunStatus.Cancelled = 4`) but is only reachable from live-run cancellation paths.

Constraints: additive public API only (0.5.0 must be a safe upgrade for Comuki 0.3.0 snapshots); wire format stays v1; no `AsyncLocal` ambient flow; AOT core tier (Abstractions/Voluta/DI) must not gain provider dependencies. The confirmed product decisions below are the contract — this document records how they land mechanically and why.

## Goals / Non-Goals

**Goals:**

- Graph identity (id + version) flows from compile options into every written snapshot as additive v1 fields.
- Resume/continue validate identity with a legacy escape hatch (no identity → no check).
- Per-thread graph selection through an injectable resolver, with full back-compat for single-graph registration.
- Guarded checkpoint puts with a stable conflict code, implemented by all five providers and enforced by the conformance suite.
- Host-transactional checkpoint commits via an explicit unit-of-work object (no ambient flow).
- Out-of-band cancel of parked threads with idempotency and a stable invalid-cancel code.

**Non-Goals:**

- Everything in the proposal's Follow-ups section (retry policy, per-task durable writes, joins, InterruptBefore/After, allow-list registration, budget stop, migration tooling).
- Any wire-format bump, snapshot schema migration, or data backfill of existing snapshots.
- Killing live streams via `CancelAsync` (cooperative token cancellation stays the only way to stop a live run).
- Publishing NuGet packages or tagging (mechanics for the apply step: 0.5.0 bump, PublicAPI upkeep, build/test gates only).

## Decisions

### D1. Graph identity as additive nullable fields on CheckpointSnapshot (wire v1 unchanged)

`CompileOptions` gains `GraphId` and `GraphVersion` (both `string?`, init-only). `CheckpointSnapshot` gains `GraphId` and `GraphVersion` (`string?`, init-only, null default). The runtime stamps the compile-options pair into every snapshot it constructs; providers treat the fields as opaque optional v1 fields (JSON property written when non-null, absent → null on read), exactly the mechanism `PendingInterrupts` already uses.

*Why not wire v2:* a bump would force every provider to branch on version and would give Comuki Chat a data migration for zero behavioral gain — the fields are additive with safe defaults. *Why strings:* the host assigns its own versioning scheme (SemVer, monotonic ints, git sha all fit); the runtime only ever compares pairs for equality, never orders them.

### D2. Resume/continue: legacy snapshots pass unchecked, mismatches fail `graph.version_mismatch`

At the top of `RunEngine` resume/continue entries (where `GetAsync` + status checks already live, e.g. `RunEngine.cs:88`), after resolving the executing graph: if the snapshot's `GraphVersion` (or the whole pair) is null → accept without check; else if the pair differs from the executing graph's compile-options pair → throw a new `GraphVersionMismatchException` (new `GraphException` subtype, code `graph.version_mismatch` in `VolutaErrorCodes`), before any node executes and before any put. `ContinueAsync` gets the same check (it also loads a snapshot). New code `graph.invalid_cancel` lands in the same file and its `All` list.

*Why unchecked legacy:* the whole point of the migration is that Comuki's existing Postgres threads (0.3.0, no identity fields) resume in place after upgrade. *Alternative considered — treat null identity as "host default version":* rejected; silently assuming an identity would mis-resolve threads after topology renames. Explicit no-check is the honest contract.

### D3. IGraphResolver in the core Voluta package, not Abstractions

Signature: `Task<CompiledGraph> ResolveAsync(string threadId, CheckpointSnapshot? snapshot, CancellationToken cancellationToken);`. Contract: null snapshot → host default graph; snapshot with identity pair → graph matching that pair; mismatching pair on resume/continue surfaces `graph.version_mismatch` (D2) — the resolver chooses the candidate graph, the engine still validates the pair against it, so a resolver bug cannot silently run the wrong topology.

*Why not Abstractions:* `CompiledGraph` lives in the core package; Abstractions must not reference it (architecture tests enforce the package graph). *Why an interface rather than a delegate:* DI consumers (Hosting worker, UI session) need a resolvable service type; a delegate would force a wrapper anyway.

### D4. `VolutaBuilder.Graphs(...)` registers resolver only; `Graph(...)` back-compat auto-registers a default resolver

`Graphs(...)` registers `IGraphResolver` as a singleton and does **not** touch the `CompiledGraph` registration (no `RemoveAll`, no factory) — hosts that opt in resolve the resolver, and `GraphThreadRunner` / `VolutaUiSession` are updated to inject `IGraphResolver`. The existing `Graph(...)` overloads keep their current registrations and additionally register a default resolver (registered last-wins-friendly: `TryAdd` so `Graphs(...)` is never overridden by it) that returns the registered graph; the engine's D2 validation still guards it. In Voluta.UI, `VolutaUiSession` switches its primary-constructor parameter from `CompiledGraph` to `IGraphResolver` and resolves the default graph per operation; the Studio API surface (endpoints, codes, statuses) does not change.

*Why auto-register on `Graph(...)`:* otherwise every existing single-graph host would break the moment shared components (runner, UI) depend on the resolver. `TryAdd` keeps `Graphs(...)` authoritative.

### D5. Guarded puts via optional `expectedParentStep` on `ICheckpointer.PutAsync`

Additive parameter via default interface method (owner ruling 2026-11-07, step 7): the shipped `PutAsync(CheckpointSnapshot snapshot, CancellationToken cancellationToken = default)` signature is **frozen** — a 0.4.0 consumer (Comuki Chat, published NuGet) must compile against 0.5.0 unchanged, and `PublicAPI.Shipped.txt` must not lose entries in a minor release. The guarded, transactional put ships as an additive DIM on `ICheckpointer` — `PutAsync(CheckpointSnapshot snapshot, long? expectedParentStep, ICheckpointUnitOfWork? unitOfWork, CancellationToken cancellationToken)` with a default body that **delegates to the shipped blind put** (external 0.4.0 implementors degrade gracefully); all in-repo providers override it with CAS + unit-of-work semantics. No optional parameters on the DIM (avoids overload-resolution ambiguity with the shipped two-parameter form). Semantics per the checkpoint delta: non-null `expectedParentStep` → atomic check-and-write (latest step must equal `expectedParentStep`; same-step rewrite allowed but protected by a concurrency token); both race outcomes throw `CheckpointStoreException("checkpoint.conflict", …)`; going through the shipped member stays a blind write.

Provider mechanics:

- **InMemory** — single-threaded-state CAS under its existing lock: compare current latest step, then swap.
- **File** — conditional write under the provider's existing in-process file lock: enumerate the thread's step documents (`{Step:D12}.json`), verify the latest step, then write. No cross-process atomicity exists in the provider today (explore fact); the guard is a check-and-write under the same lock.
- **Redis** — the latest-step pointer is checked and written in one atomic primitive (`WATCH/MULTI` or a Lua script; implementer's choice at apply time, the conformance suite judges the semantics, not the primitive).
- **S3** — conditional PutObject via object-lock/if-match semantics on the latest-step key (or a transactional metadata record); same-step races rely on S3 conditional writes.
- **EntityFrameworkCore** — guarded put executes inside one transaction: read latest step (max-step query), verify, insert; `CheckpointRecord` gains a provider-agnostic application-managed concurrency token (configured in `CheckpointRecordConfiguration`) so the same-step overwrite race throws `DbUpdateConcurrencyException`, translated to `checkpoint.conflict`. Explore fact: store-generated tokens (`IsRowVersion` = SQL Server-specific, `xmin` = Npgsql-specific) are not produced by the repo's test backends (SQLite in-memory, EF InMemory) — the token must be enforced there, so it is application-managed; rowversion/xmin remain valid provider-specific upgrades and may be layered on later.

The runtime substitutes `expectedParentStep` at every internal put site (`RunEngine` puts at lines 49, 283, 351, 408, 433, 524, 549, 761, 791, 829, 868, 969; fork/update paths) with the step of the snapshot it last loaded or wrote for that thread — a single internal helper so call sites stay mechanical. `CancelAsync` also uses a guarded put (D7).

*Why a null-default parameter instead of an options object:* one additive optional, no new type, existing call sites compile untouched. *Why exception not result type:* `CheckpointStoreException` is the established provider failure channel; a conflict is a storage-level race, not a run outcome — and it must escape the stream (D6) rather than become state.

### D6. Conflict escapes as an exception; never a Failed snapshot

A `checkpoint.conflict` from an internal put propagates out of the stream/invocation to the host as-is (the existing failure path already surfaces put exceptions). The engine MUST NOT intercept it to write a terminal Failed snapshot — writing one would itself race with the winner of the conflict. This matches the existing error-cancellation rule that only node/superstep faults produce failure checkpoints.

### D7. `CancelAsync` on CompiledGraph: guarded Cancelled snapshot from last state

`CompiledGraph.CancelAsync(string threadId, string? reason = null, CancellationToken cancellationToken = default)`:
1. `GetAsync(threadId)` → null → `GraphThreadNotFoundException` (existing `graph.thread_not_found`).
2. Status `Done` / `Failed` → new `GraphInvalidCancelException`, code `graph.invalid_cancel` (new `GraphException` subtype + `VolutaErrorCodes` entry).
3. Status `Cancelled` → return success (idempotent; no write needed, or a no-op guarded rewrite at the implementer's discretion — both satisfy idempotency).
4. Status `Running` / `Interrupted` → put a snapshot derived from the loaded one with `Status = Cancelled`, `CancelReason = reason`, guarded with `expectedParentStep = loaded.Step`.

It does **not** touch any live in-process run: XML docs state explicitly that live streams stop only via their own cancellation tokens. A live run that later does its guarded put loses with `checkpoint.conflict` — the parked-cancel is the durable winner, which is exactly the consistency Comuki needs when an operator cancels a thread another instance may be re-driving.

*Why snapshot-from-last-state instead of a tombstone record:* keeps `Get`/`List`/thread discovery/UI the single consistent view of thread status; no new read path anywhere.

### D8. Transactional participation: explicit `ICheckpointUnitOfWork` on `RunOptions`, no ambient flow

New interface in Abstractions (next to `RunOptions`): the host adapts its own transaction; the EF provider checks for it and, when present, enlists the checkpoint put in the host's `DbContext`/transaction instead of its own commit. `RunOptions` gains an optional `UnitOfWork` property (`ICheckpointUnitOfWork?`). The engine stores it once in run state (it already threads run-scoped context through the loop) and passes it to the checkpointer at put time — via the additive DIM on `ICheckpointer` (see D5; `unitOfWork` rides the guarded put), so `PublicAPI.Shipped.txt` keeps every 0.4.0 entry and existing implementations keep compiling. The conformance suite covers both modes.

*Why not `AsyncLocal`:* Comuki hit dead re-entrant context twice with `yield return` in async iterators (their issue #228); Voluta's whole execution surface is async-iterator streams, so ambient flow is structurally the wrong tool here. *Why on `RunOptions` not `CompileOptions`:* the transaction is per run (per work item), not per topology.

*Design note for apply:* the exact shape of the provider-side handoff (extra optional parameter vs. internal put-context object) is an implementation detail; the spec contract is "explicit per-run option, engine carries it as one run-state field, no ambient flow, absent → standalone put".

*Explore amendment (R5):* `CompiledGraph.ResumeAsync` / `ContinueAsync` construct `RunOptions` internally, so a host-passed `RunOptions` cannot reach the engine there today. Comuki's primary transactional path is resume (parked thread → work-item write + checkpoint commit in one host transaction), so this change adds additive overloads that accept `RunOptions` (host sets `StreamMode` and `UnitOfWork`); existing overloads keep their behavior (internal `RunOptions`, `UnitOfWork = null` → standalone put). No ambient flow is introduced anywhere.

## Risks / Trade-offs

- [Guarded puts add a read-before-write cost on hot put paths] → All five providers already read/derive latest step in the put vicinity; the check folds into that read. Redis/S3 conditional writes are single round-trips. Benchmarks are non-goals here; conformance asserts semantics only.
- [Resolver misconfiguration: `Graphs(...)` host forgets a version → resume fails `graph.version_mismatch` at runtime] → That is the intended loud failure; the studio/hosting surfaces already map `graph.*` codes to 4xx/409-style responses. Documented in XML docs of `Graphs(...)`.
- [Same-step rewrite token on EF may surprise hosts that relied on silent overwrite] → Silent same-step overwrite only happened on the legacy blind path, which stays available via `expectedParentStep = null`. Guarded rewrites protect runtime history, which is the point.
- [Idempotent cancel via "return success without write" leaves step untouched — UI list shows Cancelled anyway since Get reads latest] → Conformance/host tests assert Get returns Cancelled after double cancel.
- [PublicAPI surface grows across 7+ packages] → Apply step updates every `PublicAPI.Unshipped.txt` per PublicApiAnalyzers rules; build gate (0 warnings, warnings-as-errors) enforces completeness.
- [Consumer migration (Hosting/UI) touches two product areas in one change] → Both changes are constructor-parameter swaps with resolver resolution; the studio contract is untouched, keeping the delta surface small.

## Migration Plan

1. Land additive surface: snapshot fields, `CompileOptions` identity, `PutAsync` parameter, UoW interface + `RunOptions` property, resolver interface, `CancelAsync`, error codes (with `All` list and PublicAPI entries).
2. Wire runtime behavior: identity stamping, guarded internal puts, resume/continue validation, `CancelAsync` implementation, run-state UoW handoff.
3. Providers: InMemory → File → Redis → S3 → EF (token + transactional enlist), each passing the extended conformance suite; provider test projects (`tests/unit/checkpoints/*.Unit`, `tests/unit/Voluta.Testing.Unit`) get conflict + additive-field cases.
4. Consumers: `GraphThreadRunner`, `VolutaUiSession`, studio API paths switch to `IGraphResolver`; `.Graph(...)` host samples keep compiling unchanged.
5. Rollback: the change is additive — a revert restores blind puts and single-graph DI without data migration; snapshots written with identity fields remain readable by older code (fields ignored), so downgrade is safe too.

## Open Questions

- Redis conditional-write primitive (`WATCH/MULTI` vs Lua) and S3 conditional-write mechanism are backend-detail choices for the apply step; the conformance suite, not this document, judges them. No spec impact.
- Provider-side UoW handoff shape (optional parameter vs put-context object) — see D8 note; contract is fixed, mechanics are not.
