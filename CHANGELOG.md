# Changelog

All notable changes to Voluta are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow the
`Directory.Build.props` version and NuGet tags (`vX.Y.Z`).

## [0.5.0] - Unreleased

### Added

- **Node retry policy**: new `AddNode(..., NodeOptions)` builder overloads with
  `RetryPolicy.Exponential(maxAttempts, baseDelay, retryOn?)`. Attempts are visible
  as `GraphContext.Attempt` (1-based) and persisted in checkpoints (`TaskAttempts`) —
  a crash on attempt 3 resumes at attempt 4. `maxAttempts` counts total executions
  (1 = no retry); `OperationCanceledException` is never retried by the default predicate.
- **Per-task durable writes**: the engine now writes a guarded checkpoint put after every
  completed task of a superstep, filling `CheckpointSnapshot.PendingWrites` and
  `CompletedTaskIds`. Restart applies recorded writes and re-executes only unfinished tasks.
- **Wait-for-all join**: `AddEdge(string[] sources, string target)` declares a barrier
  target that fires only after all its sources completed in the current epoch; the
  barrier reopens after firing (cycle-compatible). Barrier arrivals are durable on
  `CheckpointSnapshot.JoinArrivals`. Join targets keep no other incoming edges.
- **Static pause points**: `CompileOptions.InterruptBefore` / `InterruptAfter` park the
  run with an `Interrupted` snapshot (`RunStopReason.StaticBefore` / `StaticAfter`)
  before a node entry / after a node output. `ContinueAsync` resumes them; static pauses
  are distinguishable from HITL interrupts by the `(PendingInterrupts, StopReason)` pair.
- **Checkpoint type registry**: register host value types in `CheckpointTypeRegistry`
  (surfaced via `CompileOptions.CheckpointTypes`); durable providers (File / Redis / S3 /
  EF) implementing `IRegistryAwareCheckpointer` stamp registered types into the wire
  (`{"$type","$value"}`) and restore typed values instead of raw `JsonElement`.
  Unknown `$type` ids fall back to raw JSON; no registry means unchanged 0.4.0 behavior.
- **Stop by budget/deadline**: `RunGuards.Deadline(...)` / `RunGuards.Budget(...)`
  (`IRunGuard`, evaluated before each superstep) park the run with `RunStopReason`
  `DeadlineExceeded` / `BudgetExhausted` — never Failed. `RecursionLimit.Unlimited` +
  `CompileOptions.WithoutRecursionLimit()` remove the shipped 25-superstep cap
  (the default stays 25 — binary compatibility with 0.4.0).
- **CI diagnostics**: unit tests run per explicit project with TRX results uploaded on
  failure; jobs carry `timeout-minutes`.

### Changed

- **CI unit matrix**: explicit unit-project list replaces the
  `FullyQualifiedName!~Integration` name filter; pack smoke runs an explicit packable
  list (12 of 13 src projects; `Voluta.Testing` is `IsPackable=false`).
- **publish.yml**: honest header (API-key push is the default; OIDC trusted publishing
  only behind `vars.NUGET_OIDC == 'true'`); tag↔package version verification on tag
  pushes (mismatch exits 1 before any push); `workflow_dispatch` with `dry_run=false`
  now pushes; release notes are extracted from `CHANGELOG.md` (falls back to generated
  notes when the section is missing).

### Fixed

- **ForkAsync lost interrupts**: `ForkAsync` now copies `PendingInterrupts` onto the
  forked thread, so resuming a fork of an interrupted thread works instead of failing
  with `graph.invalid_resume`.

### Docs

- New `docs/0.x/concurrency-migration.md` (EF `ConcurrencyToken` column migration,
  guarded writes, `checkpoint.conflict` recovery) and
  `docs/0.x/nuget-trusted-publisher.md` (3-step OIDC switch).

## [0.4.0] - 2026-09-04

Graph identity on checkpoints (guarded puts, graph version mismatch detection,
parked-thread cancel), typed channel restore and public reducers, runtime +
post-MVP packaging.

## [0.3.0] - 2026-08-15

Streaming modes (Values / Updates / Events / Messages), HITL multi-interrupt with
progressive resumes, time-travel (fork / update-state / history), custom reducers,
subgraphs, DI integration, OpenTelemetry. Redis checkpointer; 0.3.1 repackaged icons.

## [0.2.0] - 2026-08-15

HITL interrupt/resume taxonomy, send/PUSH tasks, thread discovery, cancellations with
terminal snapshots.

## [0.1.0] - 2026-08-14

First public preview: StateGraph builder, Pregel superstep engine, LastValue/Append
channels, C-shape checkpoints, InMemory checkpointer.
