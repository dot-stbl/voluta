# Proposal

## Why

0.4.0 shipped an honest Pregel core (supersteps, LastValue/Append channels, C-shape checkpoints, HITL, streaming) but long-running production hosts still hit hard walls: a flaky node kills the whole superstep, a mid-superstep crash re-executes every task in the step, there is no way to wait for several branches (join), no non-HITL pause points, checkpoint values restore as raw `JsonElement` for host types, and the only stop condition is a superstep count. The 0.5.0 batch closes those gaps additively on top of the shipped surface, plus pays down repo hygiene (fork bug, CI/publish drift, changelog, migration docs).

## What Changes

- **Node retry policy**: new `AddNode` overloads taking `NodeOptions` with `RetryPolicy.Exponential(maxAttempts, baseDelay, retryOn?)`; the attempt number is visible as `GraphContext.Attempt` and is persisted in checkpoints (crash on attempt 3 → resume at attempt 4). `maxAttempts` counts total executions (1 = no retry).
- **Per-task durable writes**: the engine writes a guarded checkpoint put after *every* completed task of a superstep (all tasks, owner decision), filling `CheckpointSnapshot.PendingWrites` (today the engine never writes it) plus completed-task marks; restart applies recorded writes and re-executes only unfinished tasks.
- **Wait-for-all join**: `AddEdge(string[] sources, string target)` declares a barrier target that fires only after all sources completed in the current epoch; the barrier reopens after firing (cycle-compatible); barrier state is durable on the snapshot.
- **Static pause points**: `CompileOptions.InterruptBefore` / `InterruptAfter` pause the run before a node entry / after a node output (LangGraph semantics) with an `Interrupted` snapshot; resume continues; static pauses are distinguishable from HITL interrupts.
- **Checkpoint type registry**: hosts register their value types (`CheckpointTypeRegistry`, surfaced via `CompileOptions.CheckpointTypes`); durable providers stamp registered types into the wire and restore typed values instead of raw `JsonElement`; the allow-list learns the stamped shape.
- **Stop by budget/deadline**: `RecursionLimit.Unlimited` + `CompileOptions.WithoutRecursionLimit()` (shipped `RecursionLimit = 25` default unchanged — binary compatibility with 0.4.0 is non-negotiable); new `IRunGuard` hook evaluated before each superstep with `RunGuards.Deadline(...)` / `RunGuards.Budget(...)` factories (deadline guards read time through an injectable `TimeProvider` — explicit-provider overloads exist, the provider-less factories default to `TimeProvider.System`); a guard stop produces an `Interrupted` snapshot (no new status enum) with a typed `StopReason` (`BudgetExhausted`/`DeadlineExceeded`), and `ContinueAsync` keeps working.
- **Bug fix — ForkAsync**: `CheckpointStateMutation.ForkAsync` does not copy `PendingInterrupts`; a fork of an interrupted thread loses interrupt state. Fix + regression test.
- **Migration notes**: docs page for the EF `ConcurrencyToken` column and guarded `UpdateState` opt-in (on top of PR #103): what hosts must do, how `checkpoint.conflict` surfaces, migration steps.
- **CI/CD revision**: explicit unit-project list in ci.yml (drop `FullyQualifiedName!~Integration` filter), pack-smoke skips `IsPackable=false` projects, TRX artifacts on test failure, `timeout-minutes` on jobs; publish.yml gets an honest header, tag↔nupkg version verification, OIDC login step behind `vars.NUGET_OIDC == 'true'` (API-key push stays default), fixed `workflow_dispatch` push semantics; new Keep-a-Changelog `CHANGELOG.md` feeding GitHub release notes; docs page for nuget.org trusted publisher setup.

## Capabilities

### New Capabilities

(none — all deltas land in existing capabilities; no genuinely new system ability is introduced)

### Modified Capabilities

- `graph-topology`: node options/retry declaration on the builder; multi-source join edges with compile-time validation.
- `graph-runtime`: retry execution semantics and visible attempt; per-task durable writes and crash-resume ready sets; join barrier epoch semantics with durable arrivals; static pause point execution; stop guards and explicit unlimited mode on top of the recursion limit.
- `checkpoint`: engine-written pending writes + completed-task marks + per-task retry attempts on the snapshot; typed `StopReason` field; fork preserves pending interrupts; host type registry participation in provider wire allow-lists and typed restore.
- `hitl-interrupt`: static (non-HITL) pause points reusing the `Interrupted` status and resume surface, and how they are distinguished from node interrupts.
- `quality-engineering`: CI unit matrix by explicit project list, failure diagnostics (TRX), job timeouts, packability-correct pack smoke, publish version/credential policy, changelog requirement.

## Impact

- **Packages**: `Voluta.Abstractions` (new base types: retry, guard, stop reason, type registry, snapshot fields), `Voluta` (builder overloads, engine mechanics), all four durable checkpoint providers (wire additive fields + stamped type support), `Voluta.Testing` (conformance scenarios). No new external dependencies.
- **Compatibility**: fully additive on the 0.4.0 public surface under an explicit evolution policy — shipped signatures are never modified; every addition is an overload, a new type, DIM, or a new member on existing types; `PublicAPI.Shipped.txt` untouched, all additions in `Unshipped.txt`. Wire documents gain optional fields (established additive pattern, roundtrip covered by conformance).
- **Code touched**: `src/core/Voluta` (builder, engine, context), `src/core/Voluta.Abstractions`, `src/checkpoints/*` (4 providers), `src/devtools/Voluta.Testing` (conformance), `tests/unit/*`, `.github/workflows/*`, `docs/`, `CHANGELOG.md`.
- **Out of scope**: new status enum values, changes to shipped `RecursionLimit` default, NuGet publishing itself (orchestrator releases by tag after merge).
