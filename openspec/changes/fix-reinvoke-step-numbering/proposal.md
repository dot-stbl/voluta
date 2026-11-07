# Proposal: repeated invoke keeps the thread's step numbering (0.5.1 hotfix)

## Why

Voluta 0.5.0 turned every runtime checkpoint put into a guarded write (`expectedParentStep` = the
step the run last loaded or wrote) but left the **start** of a run untouched: the first put is
blind (`expectedParentStep = null`) and the step counter always restarts at `0`. A second
`InvokeAsync` on a thread that already carries history therefore writes step `0` again and then
guards its next put against `expectedParentStep = 0`, while the store's latest step is the previous
run's `N` — the guard fails and the invocation dies with `checkpoint.conflict`. 0.4.0 was immune:
every put was blind, so a repeated invoke simply re-wrote steps `0..N`.

Invoking one thread again with fresh input is a legitimate host pattern (a chat turn, a retried
job, a procedure executed twice per request). 0.5.0 broke it against the 0.4.0 behavior it
promised to keep compatible; 0.5.1 restores it.

## What Changes

- **The run entry loads the thread's latest snapshot.** `RunEngine.StreamAsync` reads the latest
  checkpoint of the thread it is about to write to (one extra `GetAsync` per run entry) and uses
  it as the base of the run's step numbering.
- **A run over an existing thread continues the numbering.** With the thread's latest step `N`,
  the run's first checkpoint (the `Running` marker) is written at step `N+1` and is **guarded**
  (`expectedParentStep = N`) — never blind — and the superstep loop continues at `N+2`. The run's
  channels still start from the graph's entry with the caller's input writes (a fresh
  `ChannelStore`), exactly as in 0.4.0 — only the step numbering continues, so the thread's
  history stays append-only and monotonic instead of being rewritten.
- **A run over a fresh thread is unchanged.** No snapshot → the `Running` marker stays a blind put
  at step `0` and the supersteps start at `1`.
- **The invoke entry validates graph identity.** The loaded snapshot is checked with the existing
  `ValidateGraphIdentity`: a foreign identity pair fails with `graph.version_mismatch` before the
  first put; a legacy snapshot without a graph version is accepted unchecked (the same rule as
  resume and continue).
- **A genuine race still fails loudly.** Two concurrent invocations of one thread resolve to
  exactly one winner; the loser fails with `CheckpointStoreException` code `checkpoint.conflict`,
  which escapes the stream — the engine never synthesizes a `Failed` snapshot for it.
- **Conformance scenarios for the new contract** (in the shared `CheckpointerConformance` suite, so
  all five providers run them): repeated invocation of one thread grows the history with monotonic
  steps and no conflict; a real race of two invocations yields exactly one success and exactly one
  `checkpoint.conflict`.
- **Release bookkeeping for 0.5.1**: `CHANGELOG.md` gets a `## [0.5.1] - Unreleased` section
  describing the regression and the fix; the version moves to `0.5.1` in `Directory.Build.props`,
  `templates/Directory.Build.props` and the template's default Voluta reference.

## Capabilities

### New Capabilities

(none — the fix restores behavior inside the existing capability boundaries)

### Modified Capabilities

- `checkpoint`: a run's first checkpoint put on a thread that already has history is guarded
  against the thread's latest step and lands at `latest + 1`, so the thread's history grows
  monotonically across repeated runs instead of being rewritten; a fresh thread keeps the blind
  first put; two runs racing on one thread still resolve to exactly one winner.
- `graph-runtime`: a repeated invocation of one thread restarts the graph from its entry with
  fresh input but continues the thread's step numbering, and the invocation validates the graph
  identity of the thread's latest snapshot before writing anything.

## Impact

- **Code (runtime only)**: `src/core/Voluta/Runtime/Engine/RunEngine.cs` (`StreamAsync`),
  `src/core/Voluta/Runtime/Engine/Support/RunCheckpointWriter.cs` (guard seeding). No provider,
  wire, or public API change — the fix is internal, `PublicAPI.Unshipped.txt` stays untouched.
- **Not touched**: the resume and continue entries (they already load the latest snapshot and seed
  the guard), `UpdateStateAsync` / `ForkAsync` / `CancelAsync`, the checkpoint wire format (v1),
  and the put semantics of all five providers.
- **Cost**: one extra read (`GetAsync`) per run entry.
- **Tests**: two new scenarios in the shared checkpoint conformance suite (run by all five
  providers) plus runtime unit tests on the InMemory provider (monotonic history across N
  invocations, identity mismatch, real race).
- **Release**: 0.5.1 (version bump in three places + changelog). No NuGet publish and no tag in
  this change.
