# Design

## Context

The regression is a mismatch between two halves of the 0.5.0 runtime: guarded writes everywhere,
but an unguarded, zero-based **start** of every run.

Current state (verified against HEAD `4bfcf33`):

- `RunEngine.StreamAsync` (`src/core/Voluta/Runtime/Engine/RunEngine.cs` L63–125) builds a fresh
  `ChannelStore`, seeds it with the caller's input writes, resolves the entry nodes, sets
  `long step = 0` (L82) and writes the `Running` marker (L87–98). That marker is **blind**:
  `RunCheckpointWriter.lastStep` is still `null`, so the put carries `expectedParentStep = null`.
  The superstep loop (`RunLoopAsync`, L699) then increments the counter at L778 and commits the
  first superstep at step `1`.
- `RunCheckpointWriter` (`src/core/Voluta/Runtime/Engine/Support/RunCheckpointWriter.cs`) keeps a
  single `long? lastStep` (L29): `PutAsync` sends it as `expectedParentStep` and then sets it to
  the written step (L38–42); `GetAsync` loads a snapshot and sets it to the loaded step (L45–54).
  So the guard is "the step this run last loaded or wrote" — `null` until the first of the two.
- The guard compares against the **thread's latest step**, not against the step the run expects:
  - InMemory (`src/core/Voluta/Checkpoint/InMemoryCheckpointer.cs` L77–116): `LatestStepLocked`
    (L207–219) is the **max** over the appended history; the check is `latestStep != expected`.
  - File (`src/checkpoints/Voluta.Checkpoints.File/FileCheckpointer.cs` L111–176): head-marker CAS,
    baseline = the head step, or the max over the `{step:D12}.json` documents for legacy threads
    (L247–267).
  - Redis: the highest score of the thread's sorted set (`RedisCheckpointStore.cs` L131–145).
  - S3: the highest step among the thread's keys (`S3Checkpointer.cs` L326+).
  - EF (`src/checkpoints/Voluta.Checkpoints.EntityFrameworkCore/EntityFrameworkCoreCheckpointer.cs`
    L163–191): `MaxAsync(step)` over the thread's rows, then an upsert; the row key is
    `(ThreadId, Step)`.
  All five also allow a **same-step rewrite** (rewriting the expected step itself) — that is what
  the engine's terminal `Done` put relies on today (the `Done` snapshot reuses the last commit's
  step, `RunEngine.cs` L727–741).
- 0.4.0 had no writer and no guard: `git show v0.4.0:src/core/Voluta/Runtime/Engine/RunEngine.cs`
  puts through the shipped two-parameter `PutAsync` (blind) at every site, so a repeated invoke
  simply re-wrote steps `0..N` of the thread (blind puts overwrite on the durable providers, which
  key by `(thread, step)`; the InMemory provider appends, so it kept duplicates).
- Graph identity: `RunEngine.ValidateGraphIdentity` (L43–58) is applied on the resume entry (L149)
  and the continue entry (L312) — **not** on the invoke entry, which never loaded a snapshot before.
  A snapshot without `GraphVersion` is accepted unchecked (legacy escape hatch).
- Conformance: `src/devtools/Voluta.Testing/Conformance/CheckpointerConformance.cs` is a static
  suite whose `RunAllAsync` (L565–581) drives 12 scenarios, including the racing guarded puts
  (`ConcurrentGuardedPutsExactlyOneWinnerAsync`, L482–534). `Voluta.Testing.csproj` references the
  `Voluta` core project, so the suite can drive the engine as well as the store. The suite is bound
  to all five providers by the existing unit suites (`tests/unit/checkpoints/*/…ConformanceShould.cs`
  and `tests/unit/Voluta.Testing.Unit/Conformance/InMemoryCheckpointerConformanceShould.cs`).
- Release surface: version `0.5.0` in `Directory.Build.props` L21, `templates/Directory.Build.props`
  L16 and `templates/Voluta.Templates/content/.template.config/template.json` L39;
  `CHANGELOG.md` has an open `## [0.5.0] - Unreleased` section.

## Goals / Non-Goals

**Goals:**

- A second (and N-th) invocation of a thread completes normally instead of failing with
  `checkpoint.conflict`.
- The thread's history is append-only across invocations: the new run's checkpoints land after the
  previous run's steps, and the previous run's checkpoints stay readable.
- A genuine race on one thread still resolves to exactly one winner; the loser fails loudly with
  `checkpoint.conflict` and nothing is persisted by it.
- The fix stays inside the runtime (two internal files); no provider, wire, or public API change.
- The new behavior is pinned by the shared conformance suite (all five providers) and by runtime
  unit tests.

**Non-Goals:**

- Resume and continue entries are **not** touched (they already load the latest snapshot and seed
  the guard).
- `UpdateStateAsync` / `ForkAsync` / `CancelAsync` keep their current step arithmetic.
- No change to the checkpoint wire format (v1), to the providers' put semantics, or to the
  `ICheckpointer` contract (`PublicAPI.Unshipped.txt` stays untouched — the fix is internal).
- No new configuration switch for hosts ("keep the 0.5.0 behavior" is not offered — it is a bug).
- Not addressed here: the InMemory provider's `GetAsync` returns the **last appended** snapshot
  while its guard compares against the **max** over the history (see Risks) — changing that is a
  shipped-behavior change with its own decision.

## Decisions

### D1. The run entry loads the thread's latest snapshot and continues the numbering

`RunEngine.StreamAsync` loads the latest checkpoint of `options.ThreadId` before its first put
(via the writer, so the guard is seeded from the same read). With the loaded step `N`:

| thread state | first put | supersteps |
| --- | --- | --- |
| no snapshot (fresh thread) | `Running` at step `0`, **blind** (`expectedParentStep = null`) — unchanged | `1`, `2`, … |
| latest snapshot at step `N` | `Running` at step `N+1`, **guarded** with `expectedParentStep = N` | `N+2`, `N+3`, … |

The step counter handed to `RunLoopAsync` becomes `N+1` (so its `step++` at L778 lands the first
superstep at `N+2`); for a fresh thread it stays `0` (superstep at `1`). The `Running` marker keeps
its role: it records the run's start state (status, next nodes, identity) before any node executes.

*Why continue the numbering instead of restarting at 0:* the thread's checkpoints are the run's
audit trail; a second run that reused steps `0..N` would overwrite the first run's history on the
durable providers (they key by `(thread, step)`) — the first run's snapshots would be lost without
any signal. Continuing the numbering keeps the history append-only everywhere.

*Why the marker put is guarded and not blind:* the marker is the run's first write to the thread —
it is exactly where a competing writer must be detected. A blind marker would let two runs both
enter the thread and fight over the following steps; on EF a blind put is an unconditional
last-writer-wins upsert (`BlindStandalonePutAsync`, L203+), which would silently overwrite the
winner's row.

*Rejected — same-step rewrite of the latest (marker at `N` with `expectedParentStep = N`):* the
guarded put would accept it (rewriting the expected step is allowed on every provider), but the
InMemory provider is an **append** store: the marker would be appended as a second entry with step
`N`, so the thread's history would hold two snapshots with the same step and `ListAsync` (ordered
by step) would show a duplicate — the very "history is rewritten" damage this change removes. On the
durable providers the same-step rewrite would overwrite the previous run's step-`N` snapshot (File
writes the same `{step:D12}.json`; EF upserts the same `(thread, step)` row), destroying it.

*Rejected — emulating 0.4.0 (blind puts at steps `0..N` again):* that is the behavior that
destroys the previous run's history on the durable providers and duplicates steps in the InMemory
history. It also removes the race detection the 0.4.0 guard work added.

*Rejected — restoring the previous run's channel values instead of starting fresh:* the run is a
new execution of the graph, not a continuation of the old one. 0.4.0 started from the input, and
the continue/resume entries are the paths that restore state. Restoring would silently mix two
runs' channel values into one history.

### D2. Guard seeding inside `RunCheckpointWriter`

The writer already holds the "last loaded or written step" (`lastStep`, L29) and already seeds it
in `GetAsync` (L50). Two ways to reuse it:

- **(a) Preferred — the engine loads through the writer.** `StreamAsync` calls
  `checkpointer.GetAsync(options.ThreadId, ct)`; the writer sets `lastStep = snapshot.Step` and the
  engine reads the returned snapshot to learn `N`. No new writer API; the guard and the base step
  come from the same read, so they can never disagree.
- **(b) Alternative — an explicit seeding method** (`Seed(long step)` / `SeedFrom(snapshot)`), used
  when the engine already holds a loaded snapshot and does not want the writer to load it (e.g. if
  the load is moved into `CompiledGraph` for resolver dispatch). Slightly more explicit, but it adds
  a second way to set the same state — a future call site could seed it wrongly.
- **(c) Rejected — a constructor parameter** (`new RunCheckpointWriter(checkpointer, uow, seedStep)`):
  the writer is created before the snapshot is loaded and before it is validated; a constructor-seeded
  guard is easy to order wrongly (an unvalidated snapshot could seed the guard). The writer is
  created in `CreateWriter` (L28–35), which has no snapshot at hand.

Decision: **(a)**, with (b) as the fallback if the apply step finds the load must be hoisted. Either
way the writer stays `internal` — `PublicAPI.Unshipped.txt` is not touched. The writer's XML doc
must be updated: today it says the guard is "null until the first one, which stays a blind write
exactly as before" — after this change the first put is blind only when the thread has no history.

### D3. The invoke entry validates the graph identity

The snapshot loaded for the guard is validated with the existing `ValidateGraphIdentity` (L43–58)
before the first put: a foreign `(GraphId, GraphVersion)` pair fails with
`GraphVersionMismatchException` (code `graph.version_mismatch`) and the thread's checkpoint is
left unchanged; a legacy snapshot without a `GraphVersion` is accepted unchecked — the same rule
the resume and continue entries already apply.

*Why:* resume and continue validate; the invoke entry did not, because it never loaded a snapshot.
After this change it does load one, and a thread whose history was written by a different graph
version must not be silently extended by a new graph.

*Rejected — validating only when the identity pair is present on both sides (i.e. skipping the
check when the executing graph has no identity):* the existing helper already treats a snapshot
without a version as unchecked; a graph compiled without identity produces snapshots without a
version, so both sides agree and nothing is rejected. Keeping one helper for all three entries
avoids a second, subtly different rule.

### D4. A genuine race still fails with `checkpoint.conflict`

Two concurrent invocations of one thread both load the same latest step `N` and both try to write
the `Running` marker at `N+1` with `expectedParentStep = N`. The provider's guard resolves it: the
first writer lands, the second one sees a latest step of `N+1` and fails with
`CheckpointStoreException` (`checkpoint.conflict`). The conflict escapes the stream as an
exception — the engine never synthesizes a `Failed` snapshot for it (established rule from the
0.4.0 guard work).

The conformance race scenario runs on a thread that **already holds a checkpoint**, so the race is
decided by the guarded marker put and is deterministic on all five providers (InMemory: per-thread
lock + max-over-history check; File: head-marker CAS; Redis: Lua CAS; S3: pointer conditional
write; EF: transactional max-step re-check + primary-key collision on `(thread, step)`).

*Note:* on a **fresh** thread the marker put is blind, so two racers both enter the thread; the
conflict then surfaces at the first superstep's commit instead. The outcome (one winner, one
`checkpoint.conflict`) is the same, but the InMemory history of a raced fresh thread keeps a
duplicate step `0` (append store) — see Risks. The conformance scenario therefore pre-seeds the
thread.

### D5. Where the new conformance scenarios live

`CheckpointerConformance` (in `Voluta.Testing`, which references the `Voluta` core) gains two
scenarios, wired into `RunAllAsync` so that all five provider suites run them without touching the
provider test projects:

- `RepeatedInvokesGrowThreadHistoryAsync(checkpointer)` — builds a linear graph over the provider
  and invokes it several times on one thread id; asserts that no invocation throws, that the
  listed history grows with every invocation, that its steps are non-decreasing, and that every
  step of a later invocation is greater than the last step of the previous one.
- `ConcurrentInvokesHaveExactlyOneWinnerAsync(checkpointer)` — pre-seeds the thread with one
  completed invocation, then starts two invocations concurrently; asserts that exactly one
  completes and the other fails with `CheckpointStoreException` code `checkpoint.conflict`.

*Why in the shared suite and not only in `Voluta.Unit`:* the invariant is a **store** contract
(step numbering must not rewind, a race must be detected by the store's guard), and only the
shared suite runs against all five providers. The runtime unit tests in `Voluta.Unit/Runtime` pin
the engine-level behavior on the InMemory provider (history growth, identity mismatch, race).

### D6. Scope of the code change

Two internal files:

- `src/core/Voluta/Runtime/Engine/RunEngine.cs` — `StreamAsync` only: load the latest snapshot,
  validate the identity, compute the base step, write the marker at `base + 1` with the guard
  seeded from the load, hand `base + 1` to `RunLoopAsync`.
- `src/core/Voluta/Runtime/Engine/Support/RunCheckpointWriter.cs` — the guard seeding (D2) and the
  doc comment describing when the first put is blind.

`ResumeAsync` / `ContinueAsync` / `CancelAsync` / `UpdateStateAsync` / `ForkAsync` are untouched.
No provider, wire, or `PublicAPI.*.txt` change.

### D7. The extra read per run entry

Every invocation now reads the thread's latest checkpoint before its first put. For a fresh thread
this is a miss (cheap); for an existing thread it is one read of the store's head — the same read
the resume and continue entries already perform. Accepted: the run performs several puts, and a
run without the read cannot know the thread's step base.

## Risks / Trade-offs

- **[The InMemory provider returns the last appended snapshot while its guard compares against the
  max over the history]** → the two agree for every history built by the engine (the marker is
  appended first, then the steps ascend), and they diverge only after a *raced* fresh-thread start
  (two blind markers with step `0`, one of them appended after a higher step). In that case a later
  invocation would read a lower step than the store's max and its first guarded put would fail
  with `checkpoint.conflict` — the thread is "poisoned" for further invocations, and the host must
  fork to a new thread. Mitigation for 0.5.1: accept and document (the thread was already the
  subject of a genuine race, which is a host-level error). Alternatives, if the apply step wants
  them: (i) compute the base step from `ListAsync` (max over history) when the provider supports
  listing, falling back to the latest snapshot's step — one extra read and a `NotSupportedException`
  fallback; (ii) change `InMemoryCheckpointer.GetAsync` to return the max-step entry — a
  shipped-behavior change (it also fixes the "last history entry matches `GetStateAsync`" spec
  scenario in the presence of duplicates) and therefore **out of scope** for this hotfix.
- **[A run over a thread that holds history now consumes step numbers]** → step numbers are no
  longer per-run but per-thread, which is what the guard needs. Hosts that display step numbers
  (stream events, `ThreadSnapshot.Step`) see larger numbers on a repeated invocation. Mitigation:
  documented in the changelog; the numbers stay monotonic per thread.
- **[The marker put is guarded, so a run started on a thread whose latest snapshot was written by a
  different graph version now fails]** → intentional (D3); the host gets `graph.version_mismatch`
  instead of a mixed history.
- **[Two runs on one thread now interleave differently]** → the loser fails with
  `checkpoint.conflict` instead of silently overwriting the winner's history. This is the intended
  behavior, but hosts that previously relied on the last-writer-wins behavior of 0.4.0 will see a
  new exception. Mitigation: the changelog entry; the conflict code is stable and documented.
- **[A repeated run re-executes the graph from the entry]** → side effects of the nodes run again.
  This is the 0.4.0 semantics and is unchanged by this fix, but the fix makes repeated runs work
  again, so hosts will start doing it. Mitigation: documented (use `ForkAsync` + `ContinueAsync`
  when the host wants to continue a specific step instead of re-running the graph).
- **[The conformance race scenario depends on wall-clock interleaving]** → the scenario starts two
  invocations on a pre-seeded thread and awaits both; the first guarded marker put decides the
  race. If the two runs happen to serialize (one finishing before the other starts), the second one
  would see the advanced latest step and fail with `checkpoint.conflict` as well — the assertion
  "exactly one success, one conflict" still holds. The apply step must double-check the assertion
  shape against that interleaving.

## Migration Plan

- Runtime-only change: no wire migration, no data migration, no provider upgrade. Existing threads
  keep their checkpoints; the first invocation after the upgrade continues their numbering.
- Rollback: deploy the previous 0.5.0 build. A thread whose history grew under 0.5.1 stays readable
  (the steps are simply higher than before); 0.5.0's blind marker put would append a step `0`
  entry on such a thread — the pre-fix behavior, harmless but visible in the InMemory history.
- Release mechanics for this change: `CHANGELOG.md` gets `## [0.5.1] - Unreleased` with a `Fixed`
  entry describing the regression and the fix; the version moves to `0.5.1` in
  `Directory.Build.props`, `templates/Directory.Build.props` and the template's default Voluta
  reference. No NuGet publish, no tag (the orchestrator releases).

## Open Questions

Deferred to the explore / apply step (they do not change the specs or the approach — only the
exact placement of already-decided behavior):

1. **Exact placement of the `Running` marker.** The marker must land at `N+1` and the superstep
   loop must start at `N+2`; the apply step must confirm which `step` value `RunLoopAsync` receives
   (the call site at `RunEngine.cs` L110–121) and that the terminal `Done` put (L727–741) and the
   park puts (`TryEnterSuperstepAsync`) remain legal same-step rewrites on all five providers.
2. **Guard seeding: reuse `RunCheckpointWriter.GetAsync` (D2a) or add an explicit seeding method
   (D2b).** Both are internal; pick the one that keeps "the guard and the base step come from the
   same read" true.
3. **EF behavior for the guarded marker put** — the apply step must confirm that a guarded put whose
   expected parent is the thread's latest step inserts a **new** row (step `N+1`, no primary-key
   collision) and that a racing insert of the same step collides on `(ThreadId, Step)` and is
   translated to `checkpoint.conflict` (the mechanism the existing
   `ConcurrentGuardedPutsExactlyOneWinnerAsync` scenario already relies on).
4. **Exact assertion shape of the conformance race scenario** (see the last risk) and whether the
   unit-test race needs a deterministic hook (the `Voluta.Testing` fault-injecting checkpointer can
   hold a put at a chosen point).
