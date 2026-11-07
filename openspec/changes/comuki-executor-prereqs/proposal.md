# Proposal: Comuki executor prerequisites

## Why

Comuki Chat (the first external product consumer of Voluta) currently runs on Voluta 0.3.0 with an EF Core checkpointer in Postgres. To migrate it onto the current runtime as a **procedure executor role** (Comuki hosts procedure definitions; Voluta executes durable, resumable graph runs per thread), Voluta needs a set of prerequisite capabilities that do not exist yet: graph identity carried through checkpoints, per-thread graph resolution, optimistic concurrency on checkpoint writes, transactional participation with host storage, and cancellation of parked threads. Without these, a multi-version production deployment cannot upgrade safely, two writers on one thread can silently lose steps, and a parked (interrupted / long-running) thread cannot be cancelled out-of-band.

## What Changes

- **Graph identity on snapshots.** `CompileOptions.GraphId` and `CompileOptions.GraphVersion` (strings). The runtime stamps the pair into every `CheckpointSnapshot` via new additive nullable fields `GraphId` / `GraphVersion`. The snapshot wire format stays version 1 (same precedent as the additive `PendingInterrupts` field with an empty default). `ResumeAsync` / `ContinueAsync` accept legacy snapshots without a graph identity (no check — Comuki Chat upgrades from 0.3.0 without losing threads); a snapshot whose identity pair differs from the resolving graph fails with a new stable code `graph.version_mismatch`.
- **Per-thread graph resolution.** New `IGraphResolver` interface (`Task<CompiledGraph> ResolveAsync(string threadId, CheckpointSnapshot? snapshot, CancellationToken)`) in the core `Voluta` package (not Abstractions — `CompiledGraph` does not exist there). New DI registration `VolutaBuilder.Graphs(...)` registers an `IGraphResolver` singleton and does **not** register `CompiledGraph`; the existing `.Graph(...)` keeps working and additionally auto-registers a default resolver over that graph (back-compat). `Voluta.Hosting` (`GraphThreadRunner`) and `Voluta.UI` (`VolutaUiSession`, Studio API) switch to injecting `IGraphResolver`. A convenience overload `VolutaBuilder.Graphs(CompiledGraph defaultGraph, params CompiledGraph[] versions)` builds the resolver over an inline version list via the internal `IdentityPairGraphResolver` (default graph for identity-less snapshots; per-snapshot identity-pair matching otherwise).
- **Optimistic concurrency on checkpoint puts.** `ICheckpointer.PutAsync` gains an optional `long? expectedParentStep` parameter (additive). Providers atomically verify that the thread's latest step equals `expectedParentStep` before writing; rewriting the same step is allowed but protected by a provider concurrency token (EF: rowversion/`xmin`). Both races fail with `CheckpointStoreException` code `checkpoint.conflict`; `null` keeps today's blind write (back-compat). The runtime passes `expectedParentStep` on all of its own puts automatically. All five providers implement it: InMemory, File, Redis, S3, EntityFrameworkCore. A conflict escapes to the host as an exception from the stream — it MUST NOT be converted into a `Failed` snapshot.
- **Transactional participation.** New `ICheckpointUnitOfWork` interface in `Voluta.Abstractions` (next to `RunOptions`); hosts adapt it to their own `DbContext`/transaction (EF provider). Passed explicitly via an optional `RunOptions` property; the run engine holds it as a single run-state field and hands it to the checkpointer at put time. No `AsyncLocal`/ambient flow (known hazard with `yield return` in async iterators). No UoW in `RunOptions` → standalone put, unchanged. Goal: the host's work-item write and the checkpoint commit land in one host transaction.
- **Cancel a parked thread.** New `CompiledGraph.CancelAsync(threadId, reason?, ct)`: writes a `Cancelled` snapshot from the thread's last state via a guarded put (expected parent step = latest step); allowed from `Running` and `Interrupted`; cancelling an already-`Cancelled` thread is an idempotent success; `Done` / `Failed` fail with a new code `graph.invalid_cancel`. The reason travels in a new additive nullable `CheckpointSnapshot.CancelReason` field. A live in-process stream is not killed (cooperative token cancellation stays as is — documented in XML docs); the next put of a live run after a cancel fails with `checkpoint.conflict`, consistently.

## Capabilities

### New Capabilities

- (none — all deltas extend existing capability boundaries)

### Modified Capabilities

- `checkpoint`: snapshot gains additive graph-identity and cancel-reason fields (wire v1 unchanged, legacy snapshots read by every provider); `ICheckpointer.PutAsync` gains `expectedParentStep` optimistic-concurrency semantics with code `checkpoint.conflict` across all durable providers; `ICheckpointUnitOfWork` transactional participation via `RunOptions`.
- `graph-runtime`: per-thread graph identity — compile-time `GraphId`/`GraphVersion` stamping duty, resolver-based graph selection at run start, and resume/continue version-mismatch validation (`graph.version_mismatch`, legacy snapshots exempt).
- `public-api-hosting`: resolver-based registration (`VolutaBuilder.Graphs(...)`) that registers `IGraphResolver` and not `CompiledGraph`, with `Graph(...)` back-compat auto-registering a default resolver; hosting worker and UI session consume `IGraphResolver`.
- `error-cancellation`: out-of-band cancellation of parked threads (`CompiledGraph.CancelAsync`) with idempotency, invalid-state rejection (`graph.invalid_cancel`), `CancelReason` propagation, and non-kill semantics for live streams.
- `testing-providers`: checkpoint conformance suite extends its behavioral contract with optimistic-concurrency (conflict) semantics exercised against every provider.

## Impact

- **Public API (additive):** `CompileOptions.GraphId` / `.GraphVersion`; `CheckpointSnapshot.GraphId` / `.GraphVersion` / `.CancelReason`; `ICheckpointer.PutAsync(snapshot, ct)` (shipped, frozen) + additive guarded DIM `PutAsync(snapshot, long? expectedParentStep, ICheckpointUnitOfWork?, ct)`; `ICheckpointUnitOfWork` (Abstractions) + `RunOptions` optional property; `IGraphResolver` (core `Voluta` package); `VolutaBuilder.Graphs(IGraphResolver)` and `VolutaBuilder.Graphs(CompiledGraph default, params CompiledGraph[] versions)` (over `IdentityPairGraphResolver`); `CompiledGraph.CancelAsync`; new error codes `graph.version_mismatch`, `graph.invalid_cancel`, `checkpoint.conflict` (+ `PublicAPI.Unshipped.txt` entries per package).
- **Packages touched:** `Voluta.Abstractions`, `Voluta` (runtime, DI, InMemory), `Voluta.DependencyInjection`, `Voluta.Checkpoints.{File, Redis, S3, EntityFrameworkCore}`, `Voluta.Hosting`, `Voluta.UI`, `Voluta.Testing` (conformance).
- **Compatibility:** no wire-format bump (stays v1); legacy snapshots (Voluta ≤ 0.4, incl. Comuki Chat on 0.3.0) load and resume without identity checks; `PutAsync` without `expectedParentStep` and runs without a UoW behave exactly as today; `Graph(...)` DI registration unchanged in observable behavior.
- **Target release:** 0.5.0 (version bump, PublicAPI upkeep, build/test gates are mechanics for the apply step; no NuGet publish, no tag in this change).
- **Non-goals (explicitly deferred — see Follow-ups):** node retry policy, per-task durable writes, wait-for-all joins, `InterruptBefore`/`After`, value-type wire-allowlist registration, budget/time-based stop, unlimited-recursion sentinel, checkpoint migration tooling.

## Follow-ups (out of scope for this change)

Tracked as candidate follow-up changes, deliberately **not** part of `comuki-executor-prereqs`:

- Node-level retry policy (per-node retry with backoff).
- Per-task durable writes (write-through per task instead of per-superstep barrier).
- Wait-for-all join semantics (fan-in barrier beyond current Send completion).
- `InterruptBefore` / `InterruptAfter` node lists on invoke/resume.
- Registration of custom value types into the wire-format v1 allow-list.
- Budget/time-based stop for runs; a dedicated `Unlimited` recursion sentinel (today `RecursionLimit` is disabled via `int.MaxValue` — a sentinel can be added alongside).
- Checkpoint migration tooling (mentioned in `.agents/roadmap.md`, remainder of #75).
