# Tasks

## 1. Abstractions: snapshot fields, guarded put, unit of work, error codes

- [x] 1.1 Add `GraphId`, `GraphVersion`, `CancelReason` (nullable `string?`, init-only) to `CheckpointSnapshot` (src/core/Voluta.Abstractions/Checkpoint/CheckpointSnapshot.cs) with XML docs noting additive v1 wire fields (empty default, precedent `PendingInterrupts`). Verify: `dotnet build src/core/Voluta.Abstractions` 0/0.
- [x] 1.2 Add optional `long? expectedParentStep = null` parameter to `ICheckpointer.PutAsync` (src/core/Voluta.Abstractions/Checkpoint/ICheckpointer.cs); XML doc states CAS semantics, same-step rewrite under concurrency token, `checkpoint.conflict` on both races, null = blind write. Verify: build 0/0 shows the additive signature in PublicAPI.Unshipped.txt.
- [x] 1.3 Define `ICheckpointUnitOfWork` in src/core/Voluta.Abstractions (namespace with `RunOptions`); XML doc explains host-adapted transaction, explicit passing, no ambient/`AsyncLocal` flow. Verify: build 0/0.
- [x] 1.4 Add optional `ICheckpointUnitOfWork? UnitOfWork` property to `RunOptions` (src/core/Voluta.Abstractions/Runtime/RunOptions.cs). Verify: build 0/0.
- [x] 1.5 Add error codes `graph.version_mismatch`, `graph.invalid_cancel`, `checkpoint.conflict` to `VolutaErrorCodes` (src/core/Voluta.Abstractions/Diagnostics/VolutaErrorCodes.cs) + `All` list; update PublicAPI.Unshipped.txt in Voluta.Abstractions. Verify: uniqueness test for `All` passes (`dotnet test tests/unit/Voluta.Abstractions.Unit` or nearest unit project).

## 2. Core runtime: identity stamping, resolver, validation, guarded puts, cancel

- [x] 2.1 Add `GraphId` / `GraphVersion` (`string?`, init) to `CompileOptions` (src/core/Voluta/Graph/Options/CompileOptions.cs) with XML docs. Verify: build 0/0.
- [x] 2.2 Define `IGraphResolver` (`Task<CompiledGraph> ResolveAsync(string threadId, CheckpointSnapshot? snapshot, CancellationToken)`) in the core Voluta package (src/core/Voluta/Graph/ or Runtime/), NOT Abstractions; XML doc documents null-snapshot → default graph, snapshot pair → matching graph, engine-side pair validation. Verify: build 0/0 + architecture tests (`tests/Voluta.Architecture.Unit`) still pass (no Abstractions→core reference).
- [x] 2.3 Stamp `CompileOptions.GraphId/GraphVersion` into every `CheckpointSnapshot` the engine constructs; graphs compiled without identity produce null fields. Verify: unit test — compile with pair, run one superstep, assert pair on stored snapshot; compile without, assert nulls.
- [x] 2.4 Add `GraphVersionMismatchException` (code `graph.version_mismatch`) and `GraphInvalidCancelException` (code `graph.invalid_cancel`) as `GraphException` subtypes near the existing exceptions. Verify: unit test asserts `Code` values; build 0/0.
- [x] 2.5 In `RunEngine` (src/core/Voluta/Runtime/Engine/RunEngine.cs): after `GetAsync` in `ResumeAsync` (~line 88) and in `ContinueAsync`, validate — snapshot pair null → accept unchecked; pair != executing graph pair → `GraphVersionMismatchException` before any node/put. Verify: unit tests — legacy snapshot resumes; mismatched pair throws `graph.version_mismatch`; matching pair proceeds.
- [x] 2.6 Add a single internal guarded-put helper (latest loaded/written step → `expectedParentStep`) and route all internal puts through it (RunEngine put sites: lines 49, 283, 351, 408, 433, 524, 549, 761, 791, 829, 868, 969, plus fork/update-state paths). `checkpoint.conflict` escapes the stream untouched — no terminal Failed snapshot synthesis. Verify: unit test with fault-injecting checkpointer — second competing guarded put throws `checkpoint.conflict` out of the stream and no Failed snapshot is written.
- [x] 2.7 Implement `CompiledGraph.CancelAsync(string threadId, string? reason = null, CancellationToken ct = default)`: missing thread → `graph.thread_not_found`; Done/Failed → `graph.invalid_cancel`; Cancelled → idempotent success; Running/Interrupted → guarded put of Cancelled snapshot (`CancelReason = reason`, `expectedParentStep = loaded.Step`); XML doc states live streams are NOT killed (cooperative tokens only). Verify: unit tests per branch incl. double-cancel idempotency and `Get` returning Cancelled + reason.
- [x] 2.8 Carry `RunOptions.UnitOfWork` as one run-state field in the engine and hand it to the checkpointer at put time (provider-side handoff shape per design D8). Verify: unit test with a fake checkpointer asserting the UoW arrives at put; absent UoW → put called without one.

## 3. DI: resolver registration + back-compat

- [x] 3.1 Add `VolutaBuilder.Graphs(...)` (src/core/Voluta.DependencyInjection/VolutaBuilder.cs): registers `IGraphResolver` singleton, does NOT register `CompiledGraph`; XML doc warns about `graph.version_mismatch` for unregistered versions. Verify: unit test — container resolves `IGraphResolver`, does not resolve a `CompiledGraph` registered via `Graphs(...)`.
- [x] 3.2 Make existing `Graph(...)` overloads additionally register a default resolver (`TryAddSingleton<IGraphResolver>` so `Graphs(...)` wins); observable `CompiledGraph` behavior unchanged. Verify: unit test — host registers graph via `Graph(...)`, resolves both graph and resolver; resolver returns that graph for a null snapshot.
- [x] 3.3 Default resolver over a single graph fails resume with `graph.version_mismatch` on a foreign identity pair (engine validation, resolver just returns its graph). Verify: unit test.

## 4. Providers: guarded puts everywhere

- [x] 4.1 InMemory (src/core/Voluta/Checkpoint/InMemoryCheckpointer.cs): CAS under existing lock — compare latest step, allow same-step rewrite, throw `CheckpointStoreException("checkpoint.conflict", …)` on both races; null expected parent = blind write as today. Verify: unit tests in tests/unit (Voluta core unit project) for accept/conflict/rewrite/blind.
- [x] 4.2 File checkpointer (src/checkpoints/Voluta.Checkpoints.File/): conditional write in the provider's atomic write path (verify latest-step doc before rename/replace); same-step race protected. Verify: tests/unit/checkpoints/File.Unit guarded-put cases.
- [x] 4.3 Redis checkpointer (src/checkpoints/Voluta.Checkpoints.Redis/): check-and-write in one atomic primitive (WATCH/MULTI or Lua — implementer's choice); latest-step pointer verified atomically. Verify: tests/unit/checkpoints/Redis.Unit guarded-put cases.
- [x] 4.4 S3 checkpointer (src/checkpoints/Voluta.Checkpoints.S3/): conditional put on the latest-step key (if-match/object-lock semantics per backend capability). Verify: tests/unit/checkpoints/S3.Unit guarded-put cases (fake S3 client).
- [x] 4.5 EF Core checkpointer (src/checkpoints/Voluta.Checkpoints.EntityFrameworkCore/EntityFrameworkCoreCheckpointer.cs): replace blind `FindAsync` upsert with transactional guarded put (read latest step, verify, insert); add concurrency token to `CheckpointRecord` (rowversion / `xmin`) in `CheckpointRecordConfiguration`; translate `DbUpdateConcurrencyException` → `checkpoint.conflict`; when run's `ICheckpointUnitOfWork` is present, enlist put in host `DbContext`/transaction instead of own commit. Verify: tests/unit/checkpoints/EntityFrameworkCore.Unit — conflict, same-step race, blind put, and UoW enlistment cases.
- [x] 4.6 Update PublicAPI.Unshipped.txt in every provider package for the new PutAsync parameter / internal surface changes. Verify: `dotnet build voluta.slnx` 0/0.

## 5. Consumers: resolver injection in Hosting and UI

- [x] 5.1 `GraphThreadRunner` (src/hosting/Voluta.Hosting/Worker/GraphThreadRunner.cs): inject `IGraphResolver`, resolve graph per thread at run entry (null snapshot → default). Verify: hosting unit tests — parked interrupt + resume wake still complete (existing scenarios pass).
- [x] 5.2 `VolutaUiSession` (src/ops/Voluta.UI/VolutaUiSession.cs): switch primary-constructor parameter to `IGraphResolver`; resolve per operation; Studio API endpoints/statuses/codes unchanged. Verify: Voluta.UI unit tests pass; sample hosts using `MapVolutaUI`/`MapStudioApi` compile (`dotnet build voluta.slnx`).

## 6. Testing package: conformance for conflict + additive fields

- [x] 6.1 Extend conformance suite (src/devtools/Voluta.Testing): guarded-put cases — expected-parent accept, mismatch → `checkpoint.conflict`, same-step rewrite race, blind put unchanged, additive-field roundtrip (with/without `GraphId`/`GraphVersion`/`CancelReason`, wire v1 unchanged). Verify: suite runs green against InMemory in CI (`dotnet test`).
- [x] 6.2 Hook conflict scenarios into every durable provider's conformance run: tests/unit/checkpoints/{EntityFrameworkCore,File,Redis,S3}.Unit + tests/unit/Voluta.Testing.Unit. Verify: `dotnet test voluta.slnx` green across all five.

## 7. Release mechanics and gates

- [x] 7.1 Bump version to 0.5.0 in Directory.Build.props. Verify: `dotnet build voluta.slnx` 0 warnings / 0 errors (warnings-as-errors).
- [x] 7.2 PublicAPI.Unshipped.txt updated in every gated package touched (Abstractions, Voluta, DependencyInjection, File, Redis, S3, EntityFrameworkCore, UI). Explore fact (R2): `Voluta.Hosting` and `Voluta.Testing` are NOT in the PublicApiAnalyzers whitelist and have no `PublicAPI.*.txt` — nothing to update there. Verify: build 0/0 (analyzers treat missing entries as warnings).
- [x] 7.3 Full gate: `dotnet test voluta.slnx` — all unit projects green. No NuGet publish, no tag (explicitly out of scope).

## Workflow follow-up (not tracked)

- Reference: commit format `[voluta](feat/<area>): <subject>`; no step-done from this planning subagent — the master closes the step.
- Reference: post-archive, `.agents/roadmap.md` follow-ups from the proposal's Follow-ups section remain candidate future changes.
