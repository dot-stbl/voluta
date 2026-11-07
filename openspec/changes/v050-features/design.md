# Design

## Context

0.4.0 shipped: `StateGraph` (4 `AddNode` overloads, single-source `AddEdge`, conditional edges, `AddChannel` ×5, `Compile` → `Validate` → `GraphTopology`), engine `RunEngine` (superstep loop in `RunLoopAsync` ~L630, partial-resume ~L358, pending-sends ~L511; recursion-limit check in all three; `ContinueAsync` gate at L266–271 requiring `Status == Running`; legacy single-path interrupt fallback `ResolvePendingInterrupts` at L1148–1166), `CheckpointSnapshot` (Abstractions) with wire layers `Wire/*CheckpointJson.cs` in File/Redis/S3/EF (additive optional fields are the established pattern, `EnsureAllowed` shape allow-list), guarded `PutAsync(snapshot, expectedParentStep, unitOfWork?, ct)` DIM on `ICheckpointer` (the engine wrapper `RunCheckpointWriter` manages the guard internally and does not expose `expectedParentStep`), conformance suite `CheckpointerConformance` (11 scenarios incl. `GuardedPutSameStepRewriteAllowedAsync`, `AdditiveFieldsRoundtripAsync`), PublicAPI Shipped/Unshipped convention (Shipped = published 0.4.0, never touched until release; 0.4.0-era Unshipped additions — e.g. the `GraphContext` ctor, PR #103 members — are treated as frozen surface too). Known gaps: engine never writes `PendingWrites` (`RunEngineSnapshots.Build` hardcodes `[]`), `CheckpointStateMutation.ForkAsync` (L122–139) forgets `PendingInterrupts`, `staticEdges` is `Dictionary<string, List<string>>` (source → targets) so multi-source is impossible, `LastValue` rejects concurrent writes (`GraphConcurrentUpdateException`), restore goes through `ChannelRestoreCoercion` by declared channel types (`AddChannel<T>`), unknown shapes arrive as `JsonElement`. Owner constraints: `RecursionLimit` default stays `int = 25` (binary compat with 0.4.0), guard stop uses status `Interrupted` (no new enum values), releases happen by tag after merge — this change ships nothing to NuGet.

## Goals / Non-Goals

**Goals:**
- Concrete public API (signatures below) for all nine items, additive to 0.4.0.
- Engine mechanics specified to the point where an implementer knows which file/loop each behavior lands in.
- Wire deltas for all 4 durable providers + conformance scenario list.
- CI/publish/changelog/docs revisions specified for the implementing step.

**Non-Goals:**
- No new `GraphRunStatus` values; no changes to shipped `PublicAPI.Shipped.txt`; no `RecursionLimit` default change.
- No new provider packages, no Postgres changes, no Studio/ops changes.
- No NuGet publish, no tags (orchestrator releases).

## Decisions

Namespace plan follows the existing split: base/contract types → `Voluta.Abstractions`; builder/engine/types owned by the runtime → `Voluta` (and `Voluta.Graph.Options` for compile/builder options). All new public API goes to `PublicAPI.Unshipped.txt` of the owning project.

### API evolution policy (existing-surface compatibility — hard rule for this change)

0.4.0 is published; its surface (`PublicAPI.Shipped.txt` across all ship projects) is frozen. Every extension in this change obeys one rule: **existing signatures are never modified — regardless of their Shipped/Unshipped status — and new capability lands only as overloads, new types, DIM, or new members on existing types.** (Unshipped entries from the 0.4.0 cycle — e.g. the `GraphContext` ctor, PR #103 members — are treated as frozen too: 0.5.0 is not published, but changing an existing signature is not "additive" just because it sits in Unshipped.) Concretely, per surface:

- `StateGraph`: `AddNode`/`AddEdge` get new **overloads** (existing `AddNode(string, NodeHandler)`, `AddEdge(string, string)`, etc. stay byte-identical); `Validate` gains validation branches, no signature change.
- `CompileOptions`: only **new init-only properties** (`InterruptBefore`, `InterruptAfter`, `RunGuards`, `CheckpointTypes`) and one new static factory; existing `RecursionLimit = 25` default and existing members untouched.
- `CheckpointSnapshot`: only **new init-only properties** with empty defaults (`CompletedTaskIds`, `TaskAttempts`, `JoinArrivals`, `StopReason`) — additive for wire too (optional fields).
- `GraphContext`: `Attempt` is a new property; the existing **10-parameter ctor** (currently recorded in `Unshipped.txt`, no ctor in Shipped) **remains byte-identical** and the attempt variant is a **separate constructor overload** (see item 1) — no parameter is appended to the existing ctor. Because the existing ctor uses optional parameters, the new overload must carry the most parameters (attempt appended last, no default) to satisfy RS0027.
- `ICheckpointer`: **not touched at all**; registry support arrives via the separate capability interface `IRegistryAwareCheckpointer` that providers opt into (DIM evolution of the API family, never widening the existing interface).
- `VolutaErrorCodes`: new `const` members only (additive by nature).
- No new exception types for builder validation: retry-policy and endpoint validation reuse the existing `GraphCompileException` (same as every `StateGraph.Validate` branch today).
- Internal engine types (`RunEngineSnapshots.Build`, `RunEngineRouting`, loop helpers, `GraphTopology` — internal) may gain parameters/properties freely — they are not public API.
- `PublicAPI.Shipped.txt` files are not edited anywhere in this change; everything new is recorded in `Unshipped.txt`. Relocating Unshipped → Shipped happens at release time by the owner/orchestrator, not in this change's apply step.

### 1. Node retry policy

```csharp
// Voluta.Abstractions/Runtime/RetryPolicy.cs
namespace Voluta.Abstractions.Runtime;

public sealed class RetryPolicy
{
    /// <summary>Total executions (1 = no retry). >= 1, validated at compile.</summary>
    public int MaxAttempts { get; }
    /// <summary>Delay before retry k (1-based) = BaseDelay * 2^(k-1).</summary>
    public TimeSpan BaseDelay { get; }
    /// <summary>Null => static ex => ex is not OperationCanceledException.</summary>
    public Func<Exception, bool>? RetryOn { get; }

    public static RetryPolicy Exponential(int maxAttempts, TimeSpan baseDelay, Func<Exception, bool>? retryOn = null);
    public bool ShouldRetry(Exception exception);           // RetryOn ?? default
    public TimeSpan DelayFor(int completedAttempt);         // baseDelay * 2^(completedAttempt-1)
}
```

`Exponential` throws `ArgumentOutOfRangeException` for `maxAttempts < 1` or non-positive/near-overflow `baseDelay`; backoff caps the multiplier shift at 63 to avoid `OverflowException`.

```csharp
// Voluta/Graph/Options/NodeOptions.cs
namespace Voluta.Graph.Options;

public sealed class NodeOptions
{
    public RetryPolicy? Retry { get; init; }
}
```

Builder — four new overloads, options parameter is required (DIM-style, satisfies RS0027):

```csharp
public StateGraph AddNode(string name, NodeHandler handler, NodeOptions options);
public StateGraph AddNode<TNode>(string name, NodeOptions options) where TNode : class, IGraphNode;
public StateGraph AddNode(string name, IGraphNode node, NodeOptions options);
public StateGraph AddNode(string name, Func<IServiceProvider, IGraphNode> nodeFactory, NodeOptions options);
```

Compile-time validation: `options` null → `ArgumentNullException`; `Retry` with `MaxAttempts < 1` → `GraphCompileException` (existing type — the same one every `StateGraph.Validate` branch throws) with new code `VolutaErrorCodes.GraphInvalidRetryPolicy = "graph.invalid_retry_policy"` (only new error code in this change; no new exception type; endpoint problems keep reusing `graph.unknown_endpoint`/`graph.invalid_node`).

Topology (internal type — no PublicAPI lines): `GraphTopology` gains `IReadOnlyDictionary<string, RetryPolicy?> NodeRetries` (absent key = no retry). GraphContext gets the attempt:

```csharp
// GraphContext — Attempt is a new property. The existing 10-parameter ctor (nodeName,
// channelValues, resumePayload?, taskPayload?, services?, threadId?, taskId?, stream?,
// isResume?, resumeKind? — recorded in Unshipped.txt) remains byte-identical; the attempt
// variant is a SEPARATE constructor overload, never a change to the existing ctor.
// The existing ctor chains into the overload with attempt: 0 (signature and observable
// behavior unchanged); the overload carries the most parameters (attempt last, no
// default) to satisfy RS0027 against the optional-parameter ctor.
public int Attempt { get; }   // 1-based; 0 never observable (engine always passes >= 1)
public GraphContext(/* existing 10 params */);                  // unchanged signature
public GraphContext(/* existing 10 params */, int attempt);     // NEW overload (Unshipped)
```

Attempt persistence rides on the per-task progress fields of item 2: `CheckpointSnapshot.TaskAttempts` (see below). Retry loop lives in `RunEngineExecutionHelpers.ExecuteOneAsync` (task body level, *inside* the concurrent task execution — the barrier is unaffected: a task is "complete" only on success/interrupt):

- attempt 1..MaxAttempts: build `GraphContext(..., attempt: n)`, invoke handler.
- on exception: if `!policy.ShouldRetry(ex)` or `n == MaxAttempts` → rethrow (existing failure path: Failed snapshot + `GraphRunFailedException`).
- else: persist progress put with `TaskAttempts[taskId] = n + 1` (same-step guarded put, item 2 machinery), `await Task.Delay(policy.DelayFor(n), ct)`, next attempt.
- `OperationCanceledException` never retried by default predicate → Cancelled path unchanged.

Crash resume: restored snapshot has `TaskAttempts[taskId] = 3` → ready task executes with initial attempt 4. Unit tests in `tests/unit/Voluta.Unit/Runtime/NodeRetryShould.cs` (+ `Graph/` builder-validation cases).

### 2. Per-task durable writes (all tasks of a superstep — owner decision)

Snapshot fields (all additive, `Voluta.Abstractions/Checkpoint/CheckpointSnapshot.cs`):

```csharp
/// <summary>Tasks of the current step that completed (writes in PendingWrites).</summary>
public IReadOnlyList<string> CompletedTaskIds { get; init; } = [];
/// <summary>In-progress retry attempt per task id (from item 1); absent/empty = none.</summary>
public IReadOnlyDictionary<string, int> TaskAttempts { get; init; } =
    new Dictionary<string, int>(StringComparer.Ordinal);
```

Mechanics — the superstep body currently launches all ready tasks with a single `Task.WhenAll` (`RunEngineExecution.TryExecuteReadyAsync`, `RunEngineExecution.cs` L51–71) and applies writes once, driven from `ExecuteSuperstepAsync` (`RunEngine.cs` L833+). New: the completion collection becomes a **WhenEach-style sequential drain** inside `ExecuteSuperstepAsync` — the single edit site for this feature (task bodies in `RunEngineExecution` stay untouched, keeping the retry loop of item 1 where it is):

1. Task completes → accumulate its writes (`TaskChannelWrite[]`) into a step-local buffer keyed by taskId.
2. Immediately issue a **guarded progress put** (before the barrier): `RunEngineSnapshots.Build(...)` with `Status = Running`, `PendingWrites = [.. buffer.Select(w => new PendingWrite { TaskId, ChannelName, Value })]`, `CompletedTaskIds = [.. completedIds]`, `TaskAttempts = [.. retryingAttempts]` — issued through `RunCheckpointWriter.PutAsync`, whose **internal guard (lastStep)** already enforces the parent-step rule: the wrapper takes no `expectedParentStep` (that parameter exists only on the `ICheckpointer` DIM overload). Same-step rewrite between sequentially drained tasks is legal per conformance `GuardedPutSameStepRewriteAllowedAsync`; lost-update is excluded by construction because puts are issued **sequentially** from the single completion-drain loop (never from concurrent task bodies), with batch merging of completions that drain between awaits. This is the documented reason progress puts are drain-side, not task-side.
3. Visible state is untouched — `store` still only applies at the barrier (`TryApplyWrites`); progress writes live only in the snapshot. `LastValue` concurrent-write rejection therefore cannot be triggered by progress puts.

Snapshot copiers: the new fields must be carried by **all three snapshot-copy sites**, otherwise `InMemory` Get silently drops them and every crash-resume test of retry/join/guards breaks: `InMemoryCheckpointClone.Clone` (`InMemoryCheckpointer.cs` L252–270), `CompiledGraph.CancelAsync` (L431–446), `CheckpointStateMutation.ForkAsync` (L122–139 — same initializer as the `PendingInterrupts` fix of item 7).

Resume paths (touches the three engine entries):

- **Partial resume / continue on Running snapshot with progress** (`RunEngine` ~L358 partial-resume and `ContinueAsync` load): restore → apply `PendingWrites` through the same reducer path (`RunEngineExecution.TryApplyWrites` on the restored `ChannelStore`) → ready set = scheduled tasks minus `CompletedTaskIds` (pull tasks matched by node name + `TaskAttempts`/task payload; send tasks by `TaskId`).
- **Pending-sends path (~L511)**: keeps the A2 contract (sends scheduled, `NextNodes` not re-driven) but now also merges *uncompleted* pull tasks when progress marks them so: ready = PendingSends ∪ (pull tasks from NextNodes not in CompletedTaskIds). Conformance requirement unchanged; new unit coverage in `Runtime/PerTaskWritesShould.cs` (crash injection via fault-injecting checkpointer from `Voluta.Testing`).
- Boundary snapshot: the post-barrier commit put is built with `CompletedTaskIds = []`, `TaskAttempts = []`, `PendingWrites = []` — step boundary leaves no residue (spec).

### 3. Wait-for-all join

Builder:

```csharp
/// <summary>Barrier edge: target fires when ALL sources completed in the current epoch.</summary>
public StateGraph AddEdge(string[] sources, string target);
```

Compile validation (`StateGraph.Validate`): `sources` null/empty → `graph.invalid_edge`; duplicates in `sources` → `graph.invalid_edge`; unknown/sentinel endpoints → `graph.unknown_endpoint`; target with any other incoming static/join edge → `graph.invalid_edge` (mixed in-edges are ambiguous, rejected). A source may keep other outgoing edges; conditional edges from the target are fine.

Topology: keep `StaticEdges` untouched (source → targets, join targets included so downstream routing still sees the target); add reverse index:

```csharp
// GraphTopology
public IReadOnlyDictionary<string, IReadOnlyList<string>> JoinSources { get; }
// target -> its sources (stable order)
```

Execution — `RunEngineRouting` gains join resolution, driven by durable arrival state on the snapshot:

```csharp
// CheckpointSnapshot
/// <summary>Join target -> sources arrived in the current epoch (cleared when the join fires).</summary>
public IReadOnlyDictionary<string, IReadOnlyList<string>> JoinArrivals { get; init; } =
    new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
```

- Post-apply routing (where `NextNodes` is computed from completed nodes): for each completed node n, for each join j where n ∈ sources(j): add n to `JoinArrivals[j]` if absent; if `JoinArrivals[j]` now covers all sources → clear `JoinArrivals[j]` and add j to next ready set. Sources completing in the same superstep both arrive in one routing pass → target scheduled immediately.
- Epoch semantics fall out for free: arrivals reset on fire, so the next full pass of sources re-triggers the target (cycle-compatible).
- `JoinArrivals` is written on every commit put (via `RunEngineSnapshots.Build`) → crash-safe (spec: durable arrivals).
- `LastValue` conflicts: join targets are ordinary nodes; two different join targets scheduled in the same superstep writing one `LastValue` channel still throw `GraphConcurrentUpdateException` — unchanged, documented.
- Wire: new optional `joinArrivals` object on all 4 providers; conformance `AdditiveFieldsRoundtripAsync` extended (below).
- Unit tests: `Runtime/JoinBarrierShould.cs` (wait, fire, epoch reopen, crash-resume arrivals, mixed-in-edge rejection in `Graph/StateGraphCompileShould.cs` — builder tests live there today, no new file).

### 4. Static pause points (InterruptBefore / InterruptAfter)

```csharp
// CompileOptions additions (init-only, class stays)
/// <summary>Nodes to pause BEFORE entering (LangGraph InterruptBefore semantics).</summary>
public IReadOnlyList<string> InterruptBefore { get; init; } = [];
/// <summary>Nodes to pause AFTER output is applied and routed.</summary>
public IReadOnlyList<string> InterruptAfter { get; init; } = [];
```

`Compile`/`Validate`: unknown names → `graph.unknown_endpoint`. `GraphTopology` mirrors both lists (`InterruptBefore`/`InterruptAfter`, `IReadOnlyList<string>`).

Engine — one helper, three call sites (same entries that check the recursion limit: `RunLoopAsync`, partial-resume, pending-sends):

- `StaticInterrupts.EvaluateBefore(readyTasks, topology)`: any ready node ∈ InterruptBefore → park BEFORE step execution: put snapshot `Interrupted`, `NextNodes = [ready nodes]`, `PendingInterrupts = []`, `StopReason = StaticBefore`, `Step` unchanged (the step is not consumed). Return Interrupt terminal event.
- `StaticInterrupts.EvaluateAfter(executedNodes, topology)`: called after the barrier applied writes and routing produced `nextTasks`; any executed node ∈ InterruptAfter → park with `StopReason = StaticAfter`, `NextNodes` = already computed next ready set (routing already done; nothing re-runs).
- Cycles: the check is executed on *every* entry to a superstep, including post-resume entries → every pass re-parks (LangGraph behavior).
- Resume: `ContinueAsync` on a `StaticBefore/After` snapshot pulls from `NextNodes` (no payload resolution — there are no per-task interrupts). This requires **weakening the continue gate** at `RunEngine.cs` L266–271, which today throws `GraphInvalidContinueException` unless `Status == Running`: the gate must admit `Interrupted` snapshots whose `StopReason` is `StaticBefore`/`StaticAfter` (bare continue, payload resolution skipped). The same weakened gate serves guard stops (item 6). `ResumeAsync` with a `Command` payload against a static pause → `GraphInvalidResumeException` (existing code `graph.invalid_resume`) — and this rejection **must be checked BEFORE the legacy single-path fallback** `ResolvePendingInterrupts` (`RunEngine.cs` L1148–1166), which synthesizes a fake `PendingInterrupt` from `NextNodes[0]` for legacy snapshots; ordering after it would hand the host a phantom interrupt to answer.
- Distinction from HITL is data-driven, no API beyond `StopReason` (item 6): HITL = `PendingInterrupts.Count > 0`, `StopReason = None`; static = empty + `Static*`; guard = empty + `Budget/Deadline`. Hosts branch on this pair; spec'd in hitl-interrupt.
- Ordering per superstep entry (deliberate): cancellation → **guards (item 6)** → recursion limit → **static-before check** → execute. Guard wins over static pause because a policy stop means "no more work now"; the ready set preserved by the guard still contains the would-be paused node, so nothing is lost.
- Unit tests: `Runtime/StaticInterruptsShould.cs`.

### 5. Type registry for typed restore

Single registration point, provider-side stamping (engine stays out of the value path — this keeps `InMemory` behavior byte-identical, since it never serializes):

```csharp
// Voluta.Abstractions/Serialization/CheckpointTypeRegistry.cs
namespace Voluta.Abstractions.Serialization;

public sealed class CheckpointTypeRegistry
{
    public static CheckpointTypeRegistry Empty { get; }
    public CheckpointTypeRegistry Register<T>(string typeId) where T : notnull;   // fluent
    public CheckpointTypeRegistry Register(Type runtimeType, string typeId);
    public bool TryGetTypeId(Type runtimeType, [NotNullWhen(true)] out string? typeId);
    public bool TryResolveType(string typeId, [NotNullWhen(true)] out Type? type);
}
```

Duplicate `typeId` with a different CLR type, or duplicate CLR type with a different id → `ArgumentException` at registration. Type ids are stable strings (host-owned, e.g. `"host:order"`).

```csharp
// CompileOptions
/// <summary>Host type registry for provider wire stamping / typed restore.</summary>
public CheckpointTypeRegistry? CheckpointTypes { get; init; }

// Voluta.Abstractions/Checkpoint/IRegistryAwareCheckpointer.cs
/// <summary>Capability interface: durable providers that stamp/restore registered types.</summary>
public interface IRegistryAwareCheckpointer
{
    CheckpointTypeRegistry? TypeRegistry { get; set; }
}
```

Flow: `CompileOptions.CheckpointTypes` reaches the engine through topology — `StateGraph.Compile` (L232–245) fills a **new internal property on `GraphTopology`** (ctor L12–20), which `CompiledGraph`/`RunEngine` read; the writer factory `RunEngine.CreateWriter` (RunEngine.cs L28–35) receives `RunOptions`, not `CompileOptions`, so the assignment into the provider happens where the topology is available (CompiledGraph construction / run start): `if (checkpointer is IRegistryAwareCheckpointer aware) aware.TypeRegistry = topology.CheckpointTypes` (assignment, not interface method — keeps `ICheckpointer` DIM surface untouched; the property lives on the capability interface, so nothing existing changes). File/Redis/S3/EF checkpointer implementations opt in and thread the registry into their `Wire/*CheckpointJson.cs` encode/decode:

- **Encode**: value whose exact runtime type is registered → wire as stamped shape `{"$type": "<id>", "$value": <payload>}` (payload = `SerializeToElement(value, web)`). Primitive/string values stay raw (no stamping round-trip cost). `EnsureAllowed` learns the stamped shape **only when a registry is attached** — with `EnsureAllowedRegistryAware(value, registry, path, depth)`: `$type` must be a non-empty string ≤ 128 chars that resolves in the registry, `$value` recursed as usual. No registry → `$type`/`$value` object keys remain rejected exactly as today (allow-list participation, spec).
- **Decode**: stamped shape + registry resolves `$type` → `JsonSerializer.Deserialize(payload, type)` → typed instance; `$type` unknown → return the raw `$value` JSON element (forward compat with newer producers), log warning via existing logger category. Unstamped values → existing `ChannelRestoreCoercion` path untouched.
- Applies to channel values, `PendingWrite.Value`, `PendingSend.Payload`/`TaskPayload`, `PendingInterrupt.Payload`/`TaskPayload`, `InterruptPayload` — every value slot that goes through provider encode/decode.
- `InMemory` never implements `IRegistryAwareCheckpointer` → zero behavior change (spec scenario "No registry means unchanged behavior").
- Conformance: `CheckpointerConformance` gains a capability-gated scenario `StampedTypeRoundtripAsync` — when the checkpointer under test implements `IRegistryAwareCheckpointer`, attach a test registry, put a snapshot containing a registered record in a channel + an interrupt payload, get, assert typed instances and a second get **without** registry asserting raw-JSON fallback. `AdditiveFieldsRoundtripAsync` is extended (not renamed) with `completedTaskIds`, `taskAttempts`, `joinArrivals`, `stopReason`. All 4 durable providers + InMemory run the suite (existing CI binding).

### 6. Stop by budget/time; Unlimited recursion

```csharp
// Voluta/Graph/Options/RecursionLimit.cs
namespace Voluta.Graph.Options;

/// <summary>Sentinel for "no superstep limit"; does not collide with CompileOptions.RecursionLimit.</summary>
public static class RecursionLimit
{
    public const int Unlimited = int.MaxValue;
}

// CompileOptions
public static CompileOptions WithoutRecursionLimit() => new() { RecursionLimit = RecursionLimit.Unlimited };

// CompileOptions additions
/// <summary>Evaluated before each superstep; first stop wins.</summary>
public IReadOnlyList<IRunGuard> RunGuards { get; init; } = [];
```

```csharp
// Voluta.Abstractions/Runtime/IRunGuard.cs
namespace Voluta.Abstractions.Runtime;

public enum RunGuardAction { Continue = 0, Stop = 1 }

/// <summary>Observed before scheduling superstep N (N = NextStep).</summary>
public sealed class RunGuardContext
{
    public required long NextStep { get; init; }          // about to run
    public required long CompletedSteps { get; init; }    // committed supersteps so far
    public required DateTimeOffset StartedAt { get; init; }
}

public readonly record struct RunGuardDecision(RunGuardAction Action, string? Reason)
{
    public static RunGuardDecision Continue { get; }
    public static RunGuardDecision Stop(string reason);
}

public interface IRunGuard
{
    RunGuardDecision Evaluate(RunGuardContext context);
}

public static class RunGuards
{
    public static IRunGuard Deadline(DateTimeOffset at);      // reason "deadline_exceeded"
    public static IRunGuard Deadline(TimeSpan fromNow);       // at = provider now + fromNow, anchored at this call
    public static IRunGuard Deadline(DateTimeOffset at, TimeProvider timeProvider);
    public static IRunGuard Deadline(TimeSpan fromNow, TimeProvider timeProvider);
    public static IRunGuard Budget(Func<RunGuardContext, bool> isExhausted); // reason "budget_exhausted"
}
```

Note `fromNow` is anchored at construction time (documented) — a guard instance created before a long compile still measures from its construction; hosts wanting run-relative deadlines capture `StartedAt` from the context via `Budget` or pass absolute `DateTimeOffset`.

Time source (owner decision 2026-11-07): deadline guards read "now" through an injectable `TimeProvider` — `DeadlineGuard` stores the provider and evaluates `GetUtcNow() >= at`. The provider-less factories remain and delegate with `TimeProvider.System` (default), so wall-clock behavior is unchanged; the overloads taking an explicit provider let hosts freeze/advance time deterministically in tests (`FakeTimeProvider`, `Microsoft.Extensions.TimeProvider.Testing` — test-only dependency, core stays zero-dep). A `null` provider on the public overloads throws a domain `ArgumentException` (house guard style, no ThrowIfNull helpers). The engine's own `StartedAt` capture keeps reading wall-clock `UtcNow` at run entry — outside the scope of this decision.

```csharp
// CheckpointSnapshot — typed stop reason (status stays Interrupted; no enum changes)
public RunStopReason StopReason { get; init; } = RunStopReason.None;

// Voluta.Abstractions/Runtime/RunStopReason.cs
public enum RunStopReason
{
    None = 0,
    BudgetExhausted = 1,
    DeadlineExceeded = 2,
    StaticBefore = 3,   // item 4
    StaticAfter = 4,    // item 4
}
```

Engine:

- Limit check: replace `if (step > topology.RecursionLimit)` at the three sites (`RunEngine.cs` L358 partial-resume, L511 pending-sends, L630 `RunLoopAsync`) with `RunEngineLoopHelpers.IsOutOfSteps(step, topology.RecursionLimit)` ⇒ `limit != RecursionLimit.Unlimited && step > limit` — explicit branch, no reliance on overflow; `step` is `long`, `Unlimited` never compared. Out-of-steps behavior (Failed + `GraphOutOfStepsException`) unchanged.
- Guards: before `step++` in the loop, evaluate each guard with `NextStep = step + 1`, `CompletedSteps = step` (committed so far), `StartedAt` captured once at run entry (`StreamAsync`/`ContinueAsync` start). First `Stop` wins; reason maps to `RunStopReason` (`deadline_exceeded` → `DeadlineExceeded`, `budget_exhausted` → `BudgetExhausted`; custom reasons from `Budget`'s predicate are allowed but map to `BudgetExhausted` — the typed enum stays closed). Stop → put `Interrupted` snapshot: `NextNodes = [current ready set]`, `PendingInterrupts = []`, `PendingSends` preserved, `StopReason` set; emit `StreamEventKind.Interrupt` terminal with payload = reason string; return normally (no throw). `ContinueAsync` on such a snapshot resumes via the **same weakened continue gate** as static pauses (item 4, `RunEngine.cs` L266–271) — guard stops carry `StopReason` with empty `PendingInterrupts`, so the gate admits them and payload resolution is skipped; next commit put clears `StopReason`.
- Unit tests: `Runtime/RunGuardShould.cs`, `Runtime/UnlimitedRecursionShould.cs` (cycle > 25 steps that reaches END under `WithoutRecursionLimit()`).

### 7. ForkAsync bug fix

`CheckpointStateMutation.ForkAsync` initializer (L122–139): add `PendingInterrupts = [.. source.PendingInterrupts],` (precedent: `CompiledGraph.CancelAsync` L445 and `InMemoryCheckpointClone` copy the list). No API change. Regression test extends `tests/unit/Voluta.Unit/Runtime/UpdateStateForkReplayShould.cs`: fork of an Interrupted source → fork carries the same `PendingInterrupts` + `InterruptPayload` + `Status` → resume on the fork does not hit "not interrupted" (`graph.invalid_resume`).

### 8. Migration notes (docs)

New page `docs/0.x/concurrency-migration.md` (RU/EN parity per user-docs conventions can be a follow-up; this change ships the EN page). Outline:

- **What 0.5.0 changes on the wire**: optional snapshot fields (`completedTaskIds`, `taskAttempts`, `joinArrivals`, `stopReason`, stamped `{"$type","$value"}` values) — old providers can read new snapshots additively; new providers can read old snapshots.
- **EF ConcurrencyToken**: `CheckpointRecord.ConcurrencyToken` (`CheckpointRecord.cs` L33, `long`) configured with `.IsConcurrencyToken()` (`CheckpointRecordConfiguration.cs` L62–63); a same-step lost update surfaces as `DbUpdateConcurrencyException` and is mapped to `CheckpointStoreException` with code `checkpoint.conflict` (`EntityFrameworkCoreCheckpointer.cs` L118–130). Host steps: (1) add an EF migration adding the `ConcurrencyToken` column (existing stores need it when upgrading — the record comment says so explicitly); (2) no opt-in and no code change for guarded writes: both the engine's puts and `UpdateStateAsync` already go through `RunCheckpointWriter`, whose internal guard (lastStep) enforces the parent-step rule — guarded `UpdateState` is the behavior, not an option; (3) how to handle a conflict: reload latest via `GetStateAsync`/`GetHistoryAsync`, re-apply the host edit, retry.
- **Rollback**: columns are additive; 0.4.0 consumers ignore new columns/fields.

The doc cites only real symbols (verified against HEAD: the four anchors above); no flags or options types are invented.

### 9. CI/CD revision (implementation lands in the apply step)

**ci.yml**:
- `unit tests` step: explicit project matrix instead of the solution-wide filter —
  `dotnet test tests/unit/Voluta.Unit tests/unit/Voluta.Abstractions.Unit tests/unit/Voluta.Architecture.Unit tests/unit/Voluta.DependencyInjection.Unit tests/unit/Voluta.Generators.Unit tests/unit/Voluta.Hosting.Unit tests/unit/Voluta.OpenTelemetry.Unit tests/unit/Voluta.Testing.Unit tests/unit/Voluta.Agents.AI.Unit tests/unit/checkpoints/*.Unit` (exact csproj list enumerated at implementation; the rule is: every `tests/unit/**` project is listed, integration projects never match).
  Rationale: `FullyQualifiedName!~Integration` is a name filter — a new unit project is silently included (ok) but a project accidentally containing `Integration`-named tests or a renamed folder breaks the gate silently; explicit list fails loudly on typos (`--no-build` per project) and guarantees provider unit projects run.
- `--logger "trx;logfiledirectory=${RUNNER_TEMP}/trx"` on the test step + a new step `if: failure()` using `actions/upload-artifact@v4` uploading `${RUNNER_TEMP}/trx` as `test-results`.
- `timeout-minutes: 30` on the job.
- `pack smoke`: replace the `find src -name '*.csproj'` loop with an explicit packable list — **12 of 13 src projects** (everything except `Voluta.Testing`, which is `IsPackable=false`). Note `Voluta.UI` sets no explicit `IsPackable` (defaults to packable, and publish.yml's src loop packs it today) — include it; the ci list must stay in parity with publish.yml's src enumeration. `templates/Voluta.Templates` needs no ci entry: publish.yml packs it in its own dedicated step (L90–93). Keep the existing nupkg-existence assertions.
- Leave `openspec validate` step as is (already `continue-on-error`).

**publish.yml**:
- Header comment rewritten honestly: "Push via API key (`secrets.NUGET_API_KEY`) by default; optional OIDC trusted publishing behind `vars.NUGET_OIDC`". Today's comment claims OIDC trusted publishing — it lies (the push step uses an API key).
- New step `verify tag vs packages` (`if: github.event_name == 'push'`, before push): `expected="${GITHUB_REF_NAME#v}"`; for each `*.nupkg` assert its filename version equals `$expected`; mismatch → `exit 1` before any push. Fixes the silent `v0.5.1`-tag-publishes-0.5.0 footgun.
- Push steps become mutually exclusive branches:
  - `if: vars.NUGET_OIDC == 'true' && push-or-opted-dispatch` → OIDC login (official `nuget/login@v1` action writing a temp NuGet.config) then `dotnet nuget push --configfile` …
  - `if: vars.NUGET_OIDC != 'true' && push-or-opted-dispatch` → existing `--api-key` push (unchanged behavior).
  where `push-or-opted-dispatch` = `github.event_name == 'push' || (github.event_name == 'workflow_dispatch' && inputs.dry_run == false)`.
  Justification for fixing dispatch-push (vs documenting dispatch as pack-only): the owner runs sanity releases from non-default refs via dispatch; the current workflow **advertises** `dry_run: false` as "pack only, do not push (sanity check)" yet a dispatch with `dry_run=false` can never push — the input is dead. Opt-in push from dispatch is the smallest honest fix; tag pushes remain the canonical release path and the tag-version check above only applies to tag events (dispatch publishes whatever `Directory.Build.props` declares).
- `dry-run summary` step condition stays but text updated ("dispatch with dry_run=false now pushes; this step only runs for dry runs").
- Release notes from changelog: new step extracts the tagged version's section:
  `awk` between `## [<version>]` and the next `## [` heading into `${RUNNER_TEMP}/notes.md`; then `gh release create "$tag" --notes-file "${RUNNER_TEMP}/notes.md" --verify-tag` (replacing `--generate-notes`). Missing section → fall back to `--generate-notes` with a warning (never fail the release on a doc miss).
- `id-token: write` permission can stay (harmless without OIDC flag; required when the flag flips).

**CHANGELOG.md** (repo root): Keep a Changelog format, `## [0.5.0] - Unreleased` with all nine items summarized; linked at the top of README is out of scope here.

**docs/**: `docs/0.x/nuget-trusted-publisher.md` — the 3 steps for the future OIDC switch: (1) nuget.org → account → Trusted Publishing: add policy per package ID prefix (`Voluta`, `Voluta.*`) with owner/repo `dot-stbl/voluta`, workflow filename `publish.yml`, environment `nuget`; (2) repo settings: create environment `nuget`, set repository variable `NUGET_OIDC=true`; (3) next tag release runs the OIDC branch — verify push in the run log, then optionally revoke `NUGET_API_KEY`.

## PublicAPI.Unshipped additions (apply step adds exact strings; analyzer output gates)

- **Voluta.Abstractions**: `RetryPolicy` (+`Exponential`/`ShouldRetry`/`DelayFor`), `RunStopReason`, `RunGuardAction`, `RunGuardContext`, `RunGuardDecision` (+`Continue`/`Stop`), `IRunGuard`, `RunGuards` (+`Deadline` ×4/`Budget` — two provider-less factories + two `TimeProvider` overloads), `CheckpointTypeRegistry` (+members), `IRegistryAwareCheckpointer`, `VolutaErrorCodes.GraphInvalidRetryPolicy`, `CheckpointSnapshot.CompletedTaskIds`, `CheckpointSnapshot.TaskAttempts`, `CheckpointSnapshot.JoinArrivals`, `CheckpointSnapshot.StopReason`.
- **Voluta**: `StateGraph.AddNode` ×4 new overloads (options variant of the DI overload + handler + instance + factory), `StateGraph.AddEdge(string[] sources, string target)` (new overload), `NodeOptions`, `CompileOptions.InterruptBefore/InterruptAfter/RunGuards/CheckpointTypes` (new init-only properties), `CompileOptions.WithoutRecursionLimit()`, `RecursionLimit.Unlimited`, `GraphContext.Attempt` + separate 11-parameter ctor overload. (`GraphTopology.NodeRetries/JoinSources/InterruptBefore/InterruptAfter` get **no PublicAPI lines** — `GraphTopology` is internal.)
- **Voluta.Checkpoints.File/Redis/S3/EntityFrameworkCore**: implementations of `IRegistryAwareCheckpointer` (member additions per provider; `File`/`S3`/`Redis`/`Ef` `EnsureAllowed` registry-aware variants stay internal — no public surface beyond the capability interface unless provider options expose it, which they do not in this design).

## Risks / Trade-offs

- [New snapshot fields silently lost by in-memory copy sites] → `CompletedTaskIds`/`TaskAttempts`/`JoinArrivals`/`StopReason` must be propagated in all three copiers — `InMemoryCheckpointClone.Clone` (`InMemoryCheckpointer.cs` L252–270), `CompiledGraph.CancelAsync` (L431–446), `CheckpointStateMutation.ForkAsync` (L122–139); otherwise InMemory Get drops them and the crash-resume tests of retry/join/guards fail. Explicit task + clone-roundtrip test pin this.
- [Weakening the `ContinueAsync` gate changes the error contract] → the L266–271 gate widens only for `Interrupted` snapshots carrying a `StopReason` (static/guard pauses); `Failed`/`Cancelled`/`Done` and bare-HITL `ResumeAsync` misuse keep throwing `GraphInvalidContinueException`/`GraphInvalidResumeException` — unit tests pin both accept and reject paths.
- [Registry never reaches the provider] → `CheckpointTypes` travels `Compile → GraphTopology (internal property) → CompiledGraph/RunEngine` because `CreateWriter` sees only `RunOptions`; a provider-side assertion in `StampedTypeRoundtripAsync` (typed restore only works if the registry arrived) gates the whole transport.
- [Drain rewrite touches the superstep hot path] → the single edit site is `ExecuteSuperstepAsync` (`RunEngine.cs` L833+) replacing the `WhenAll` at `RunEngineExecution.cs` L51–71 with a WhenEach-style sequential drain; task bodies and retry loop stay untouched. Superstep timing/barrier semantics covered by existing unit matrix plus new per-task tests.
- [Progress puts multiply checkpoint writes on big fan-out steps] → progress puts are skipped when the provider is `InMemory`-blind *and* the run is ephemeral? No — InMemory must behave identically for conformance; instead cap: a progress put is issued per completed task, but the drain merges several completions between awaits into a single put (batch merging). Documented cost: ≤ 1 extra put per task per superstep on durable providers.
- [Same-step progress put races with the terminal failure/commit put] → the drain loop is the only progress-put writer and the commit put strictly follows the drain in the same async flow; failure paths write after drain cancellation is checked. Guarded puts make the last writer win deterministically by parent-step rule.
- [Join + `LastValue` in same superstep still throws `GraphConcurrentUpdateException`] → kept deliberately (shipped semantics); doc'd in spec scenario; hosts use Append channels for multi-source value fan-in.
- [`TaskAttempts`/`JoinArrivals`/`StopReason` grow the wire] → optional fields, established additive pattern; conformance `AdditiveFieldsRoundtripAsync` extended to pin all four providers.
- [Retry changes node-failure timing (delay between attempts inside the barrier)] → backoff delays run inside the task body; barrier waits as it already waits for slow nodes; cancellation honored in `Task.Delay`. Superstep-wide timeout is explicitly not added (guards cover it).
- [Dispatch-push changes release semantics] → guarded behind explicit `dry_run=false`; tag path untouched; the tag-version check prevents the real footgun (wrong version publish).
- [PublicAPI growth] → all in Unshipped; Shipped untouched; owner relocates at release.

## Migration Plan

All changes are additive per the API evolution policy above: hosts upgrade by recompiling against 0.5.0 with no source breaks (the existing `GraphContext` ctor — 10 parameters — and all other existing signatures are unchanged; new capability is overloads/new types/new members only). EF hosts run one migration for the `ConcurrencyToken` column if their store predates it (doc: `docs/0.x/concurrency-migration.md`). Wire documents stay readable by 0.4.0 providers (optional fields). Rollback = return to 0.4.0 packages; no data migration back needed (new fields ignored by old readers).

## Open Questions

None — all nine items carry owner decisions fixed in the proposal; the remaining micro-choices (exact unit-project list, exact csproj packable list) are verification tasks inside the apply step, not design ambiguities.
