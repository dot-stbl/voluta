[![Voluta — stateful, cyclic, durable agent graphs for .NET](https://raw.githubusercontent.com/dot-stbl/voluta/main/assets/banner.png)](https://github.com/dot-stbl/voluta)

[![CI](https://github.com/dot-stbl/voluta/actions/workflows/ci.yml/badge.svg)](https://github.com/dot-stbl/voluta/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Voluta.svg?style=flat-square&label=nuget)](https://www.nuget.org/packages/Voluta)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](https://github.com/dot-stbl/voluta/blob/main/LICENSE)

**Stateful agent graphs for .NET** — cycles, typed channels, checkpoints, streaming, and
human-in-the-loop. You describe nodes and edges; Voluta runs them in Pregel-style supersteps and
can pause a thread for days, then resume in another process.

```text
→ cycles not DAGs
→ typed channels not dictionary soup
→ checkpoints not in-process memory
→ .NET-native not a Python port
→ AOT-ready core not a kitchen sink
```

> **v0.4.0** is on NuGet. Shipped surface is frozen (`PublicAPI.Shipped.txt`).
> Host time-travel (`GetStateAsync` / `GetHistoryAsync` / `UpdateStateAsync` / `ForkAsync` / `Continue*`)
> is available but still listed in `PublicAPI.Unshipped.txt` (can still move).
> `dotnet add package Voluta --version 0.4.0` — see [Quick Start](#quick-start).

## See it in action

A ReAct-style loop (agent ⇄ tools) with no LLM — pure simulation so you can run it offline.
Real output of `dotnet run --project samples/HelloWorld`:

```text
  ◆  voluta · HelloWorld

  simulated ReAct · agent ⇄ tools · no LLM
  thread   react-sample-1
  stream   Updates
  ────────────────────────────────────────
  [agent] round 0 · requesting tools
  · step 1  Updates  [agent]
      status ← tools
      messages ← agent: call get_weather (round 1)
  [tools] executing simulated tool · round 1
  · step 2  Updates  [tools]
      tool_rounds ← 1
      messages ← tools: observation — temp=12C (round 1)
      status ← agent
  …
  [agent] enough tool data · finishing
  · step 5  Updates  [agent]
      status ← done
      messages ← agent: final answer — cloudy, 12°C in Oslo
  · step 5  End  [—]

  ▸ result
  status      Done
  ✓ done
```

The loop exists because one edge is **conditional**. `messages` accumulates (`Append`);
`status` replaces (`LastValue`):

```csharp
var graph = new StateGraph()
    .AddChannel("messages", ChannelKind.Append)
    .AddChannel("status", ChannelKind.LastValue)
    .AddChannel("tool_rounds", ChannelKind.LastValue)
    .AddNode("agent", AgentNodeAsync)
    .AddNode("tools", ToolsNodeAsync)
    .AddEdge(GraphConstants.Start, "agent")
    .AddConditionalEdges(
        "agent",
        static context => context.Read<string>("status") == "tools" ? "tools" : GraphConstants.End)
    .AddEdge("tools", "agent")
    .Compile(checkpointer, new CompileOptions { RecursionLimit = 32 });

await foreach (var item in graph.StreamAsync(
                   input,
                   new RunOptions { ThreadId = "react-1", StreamMode = StreamMode.Updates }))
{
    // item.Kind, item.NodeNames, item.Writes
}
```

<details>
<summary><strong>Human-in-the-loop — interrupt, then resume (even later, another process)</strong></summary>

A node returns `NodeResult.Interrupt` instead of writes. The run stops, the checkpoint holds the
payload, and `ResumeInvokeAsync` continues with a `Command`. Real output of
`samples/InterruptResume`:

```text
  ▸ invoke · expect interrupt

  · step 0  Start  [—]
  [gate] interrupting for human approval
  · step 1  Interrupt  [gate]
      payload ← { action = transfer, amount = 50, currency = USD }
  checkpoint  Interrupted

  ▸ resume · Command.Kind = approve

  [gate] resumed · payload=ok · approving
  · step 2  End  [—]
  status      Done
  ✓ done
```

```csharp
static Task<NodeResult> GateNodeAsync(GraphContext context, CancellationToken cancellationToken)
{
    if (!context.IsResume)
    {
        return Task.FromResult<NodeResult>(
            NodeResult.Interrupt(new { action = "transfer", amount = 50, currency = "USD" }));
    }

    return Task.FromResult<NodeResult>(
        NodeResult.Continue(new ChannelWrite("messages", "gate: transfer approved")));
}

// first process
var terminal = await graph.InvokeAsync(input, new RunOptions { ThreadId = "hitl-1" });
// terminal.Kind == StreamEventKind.Interrupt

// later — same or new process, same checkpointer root / store
// closed kinds: Command.Approve / Reject / Update (Command.Kinds.*)
var done = await graph.ResumeInvokeAsync(
    "hitl-1",
    Command.Approve(
        "ok",
        // optional: patch channels before the node re-runs
        new Dictionary<string, object?> { ["decision"] = "go" }));
```

</details>

<details>
<summary><strong>Time-travel read (GetState / GetHistory)</strong></summary>

`ICheckpointer` is the storage seam (shipped). There is **no** `putWrites` — a put is always a full C-shape snapshot:

| Method | Contract |
|--------|----------|
| `PutAsync(CheckpointSnapshot)` | Persist this snapshot (and its step in history when the provider supports it) |
| `GetAsync(threadId)` | **Latest** snapshot only. Miss returns `null` (does not throw) |
| `ListAsync(threadId)` | **Full** step history, oldest first. Providers that cannot list **must** throw `NotSupportedException` rather than return a silent partial |

InMemory, File, EF, S3, and Redis all implement history. `IVolutaStore` is a separate cross-thread KV (`Put`/`Get`/`List`/`Delete` by namespace) — `ListAsync(namespace)` lists **all keys in that namespace**, not checkpoints.

Host-facing projection — no need to spelunk C-shape fields. These methods are available (`PublicAPI.Unshipped.txt`, not frozen):

```csharp
// latest snapshot for ops / HTTP / UI  (wraps ICheckpointer.GetAsync)
var state = await graph.GetStateAsync("order-9");
// state?.Status, state?.Step, state?.Values, state?.InterruptPayload

// full step history, oldest first  (wraps ICheckpointer.ListAsync)
var history = await graph.GetHistoryAsync("order-9");
// ordered by Step ascending; history[^1] matches GetState when present
```

Ops UI: `GET /voluta/api/threads/{id}/history` lists steps; inspector shows them after Load.

</details>

<details>
<summary><strong>Update state / fork / continue</strong></summary>

Ops and support workflows can edit channel values and branch threads without re-invoking from scratch.
There is **no** `putWrites`. Channel edits go through `UpdateStateAsync(threadId, IEnumerable<ChannelWrite>)`
(Unshipped): it loads the latest checkpoint, applies the same LastValue / Append
reducers as runtime input seeding, and `Put`s a **new** history step.

```csharp
// patch latest checkpoint (Append / LastValue reducers apply)
var patched = await graph.UpdateStateAsync(
    "order-9",
    [new ChannelWrite("messages", "ops-note")]);

// branch a new thread from history step N (same step index on the new thread)
var fork = await graph.ForkAsync(sourceThreadId: "order-9", step: 2, newThreadId: "order-9-retry");

// re-drive a Running thread after update/fork (not for Interrupted — use Resume*)
// WARNING: nodes in NextNodes re-execute; make side effects idempotent
var terminal = await graph.ContinueInvokeAsync("order-9-retry");
```

- **Interrupted** → still use `ResumeAsync` / `ResumeInvokeAsync` (optionally after `UpdateStateAsync`).
- **Done** → `UpdateStateAsync` may still patch values; `Continue*` throws `graph.invalid_continue`.
- **Failed/Cancelled** → update promotes status to Running so continue can re-drive last NextNodes.

</details>

<details>
<summary><strong>Typed state with <code>[GraphState]</code> (source generator)</strong></summary>

String channel names work. When you want compile-time names and updates that only write what you
set:

```csharp
[GraphState]
public partial class ReviewState
{
    [Channel(ChannelKind.Append)]
    public IList<object?> Notes { get; set; } = new List<object?>();

    [Channel(ChannelKind.LastValue)]
    public string? Verdict { get; set; }
}

var graph = new StateGraph()
    .AddChannels(ReviewState.CreateSchema())
    .AddNode(
        "review",
        static (context, cancellationToken) => Task.FromResult<NodeResult>(
            NodeResult.Continue(
                new ReviewState.ReviewStateUpdate { Verdict = "approved" }.ToWrites())))
    .AddEdge(GraphConstants.Start, "review")
    .AddEdge("review", GraphConstants.End)
    .Compile(new InMemoryCheckpointer());
```

Unset properties emit **no** write. An explicit `null` is a clear. Interface-typed properties use
`OptionalValue<T>.Of(value)`.

</details>

<details>
<summary><strong>Typed channels and custom reducers</strong></summary>

Checkpoints stay dumb JSON (allow-listed primitives / lists / string-key dicts). Types live on the
**compiled graph**. `AddChannel<T>` records `T` so restore coerces wire values (`long` → `int`,
JSON object text → `Dictionary<string, string>`, append list elements to `string`). Untyped
`AddChannel(name, kind)` still works and coexists on the same graph.

```csharp
var graph = new StateGraph()
    .AddChannel<int>("score", ChannelKind.LastValue)
    .AddChannel<string>("messages", ChannelKind.Append)
    .AddChannel("notes", ChannelKind.Append) // untyped
    .AddChannel<Dictionary<string, object?>>("artifacts", new DictMergeReducer())
    .AddNode("left", LeftAsync)
    .AddNode("right", RightAsync)
    .AddEdge(GraphConstants.Start, "left")
    .AddEdge(GraphConstants.Start, "right")
    .AddEdge("left", GraphConstants.End)
    .AddEdge("right", GraphConstants.End)
    .Compile(checkpointer);
```

`IChannelReducer` is public; `IChannel` stays internal. Built-ins: `LastValueReducer`,
`AppendReducer`, `DictMergeReducer`. Custom reducers use a separate `AddChannel(name, reducer)`
overload — `ChannelKind` stays `LastValue | Append`. Durable restore contract:
`FileCheckpointerRuntimeShould.RehydrateTypedChannelsViaDeclaredValueType`.

</details>

<details>
<summary><strong>Durable file checkpoint (survive process restart)</strong></summary>

```csharp
// Host DI (recommended) — same root on every process
services.AddVoluta(v =>
{
    v.Checkpoints.UseFile("/var/lib/voluta/threads");
    v.Graph((sp, checkpointer) => new StateGraph()
        // … nodes …
        .Compile(checkpointer, new CompileOptions { Services = sp }));
});

// process A
var graph = sp.GetRequiredService<CompiledGraph>();
await graph.InvokeAsync(input, new RunOptions { ThreadId = "order-9" });
// → Interrupted

// process B (new host, same UseFile root) — resume
await graph.ResumeInvokeAsync("order-9", Command.Approve("ok"));
```

Values serialize with `System.Text.Json` — prefer JSON-friendly types (strings, numbers, lists of
primitives). **Durable rehydrate is lossy:** File / EF / S3 / Redis `FromElement` restores
`string` / `bool` / `long` / `double` and arrays as `List<object?>`. JSON objects (`{}`) become
`GetRawText()` — a JSON **string**, not the original CLR type and usually not `JsonElement`.
`InMemoryCheckpointer` keeps CLR references (no JSON). Declare `AddChannel<T>` so the compiled
graph coerces those wire values on restore. Custom POCOs still fail Put unless they are
allow-listed shapes.

For tests, `InMemoryCheckpointer` is enough; every storage implements `ICheckpointer` and can
run `CheckpointerConformance.RunAllAsync`. Provider types (`FileCheckpointer`, EF, S3, Redis)
are constructed via `Use*` only — not `new`.

</details>

<details>
<summary><strong>Fan-out with <code>Send</code> / subgraphs</strong></summary>

```csharp
// Map one item to many dynamic tasks (runtime-scheduled nodes)
return NodeResult.ContinueWithSends(
    new Send("worker", payload: orderLine1),
    new Send("worker", payload: orderLine2));

// Nest a compiled graph as a single node (stable child thread: parentId/child)
.AddNode("child", Subgraph.AsNode(
    childCompiledGraph,
    inputChannels: ["messages"],
    outputChannels: ["result"]));
// Custom multi-agent nest namespace:
// threadIdFactory: ctx => $"{ctx.ThreadId}/agent/{ctx.NodeName}"
```

Child interrupt → parent interrupt (same HITL `Command` resume on the parent
thread resumes the nested child). Default nested checkpoint key:
`{parentThreadId}/{nodeName}`.

`CompiledGraph.Describe()` returns topology (nodes, edges, channels) for the ops UI or docs.

</details>

<details>
<summary><strong>Host with DI + ops console</strong></summary>

```csharp
// Program.cs
builder.Services.AddVoluta(sp =>
{
    var checkpointer = sp.GetRequiredService<ICheckpointer>();
    return new StateGraph()
        // …
        .Compile(checkpointer);
});

// Optional ops UI (ASP.NET)
var session = new VolutaUiSession(graph, checkpointer);
builder.Services.AddVolutaUI(session);
app.MapVolutaUI(options => options.PathPrefix = "/voluta");
app.MapStudioApi(); // versioned /api/v1 for the SPA and any FE client
// → Studio SPA:  http://localhost:5188/voluta/studio
// → Legacy shell: http://localhost:5188/voluta  (see samples/UiHost)
```

</details>

<details>
<summary><strong>Testing without fighting the runtime</strong></summary>

`Voluta.Testing` ships doubles you’d otherwise write by hand:

| Helper | Role |
|--------|------|
| `RecordingCheckpointer` | Records every Put/Get/List |
| `FaultInjectingCheckpointer` | Fails the *n*-th write |
| `CheckpointerConformance.RunAllAsync` | Suite every `ICheckpointer` must pass |
| `GraphFixtures.Linear()` / `.Cycle()` / `.Interrupt()` | Ready graphs |
| `StreamCapture` | Drain a stream in tests |

Unit tests cover linear/conditional/cycle, fan-out, Send, HITL (`Command.Values`, resume payload),
stream modes (Updates / Events), Append flatten vs string, LastValue sequential overwrite, and
File rehydrate + resume.

```bash
dotnet test voluta.slnx
```

</details>

<details>
<summary><strong>Sample: Hybrid-style marketing desk (mock tools)</strong></summary>

Not a product agent — a demo that **exercises the graph** against Hybrid console.platform nouns
(Campaign, SSP, DirectDeal, AdLibrary):

```bash
# terminal 1
dotnet run --project samples/MockAdMcp          # http://localhost:5190

# terminal 2
dotnet run --project samples/MarketingAgent -- --offline
```

Flow: `brief → creative → setup (create RK → SSP → banner → Active) → review`. Tools are a
simplified HTTP tools surface for demos — not the official MCP SDK and not live Hybrid API.

</details>

## Why Voluta

- **Cycles are the point** — think → act → observe is a loop. Conditional edges + `RecursionLimit`
  make loops terminate on purpose.
- **The run outlives the process** — every superstep can checkpoint. Interrupt, inspect, resume, or
  replay from storage; `ICheckpointer` is the only seam.
- **Multi-writer state is defined** — same-superstep writes go through reducers (`Append` /
  `LastValue`). Concurrent `LastValue` writers fail fast instead of last-write-wins races.
- **Core stays small** — `Voluta` + Abstractions + DI are `IsAotCompatible`, zero third-party deps
  on the hot path. No LLM SDK, no logging framework required to run a graph.

Voluta is **orchestration**, not a Claude Code / OpenCode clone. You bring tools, MCP, prompts, and
policy; Voluta runs the stateful graph.

## Quick Start

**Requires .NET 10 SDK** (`dotnet --version` ≥ 10.0.100).

Scaffold a HITL agent (no clone required once the template pack is installed):

```bash
dotnet new install Voluta.Templates
dotnet new voluta-agent -n MyAgent
cd MyAgent && dotnet run
```

Or clone and run in-repo samples:

```bash
git clone https://github.com/dot-stbl/voluta.git
cd voluta
dotnet build voluta.slnx
dotnet run --project samples/HelloWorld
dotnet run --project samples/InterruptResume
```

Reference from your app:

```bash
dotnet add package Voluta --version 0.4.0
# or, from a clone:
dotnet add reference path/to/voluta/src/Voluta/Voluta.csproj
```

### Minimal complete program

Writer + critic loop until score ≥ 8 — prints `End after N supersteps`:

```csharp
using Voluta;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Checkpoint;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;

var graph = new StateGraph()
    .AddChannel("draft", ChannelKind.LastValue)
    .AddChannel("notes", ChannelKind.Append)
    .AddChannel("score", ChannelKind.LastValue)
    .AddNode("write", WriteAsync)
    .AddNode("critique", CritiqueAsync)
    .AddEdge(GraphConstants.Start, "write")
    .AddEdge("write", "critique")
    .AddConditionalEdges(
        "critique",
        static context => (context.Read<int?>("score") ?? 0) < 8 ? "write" : GraphConstants.End)
    .Compile(new InMemoryCheckpointer(), new CompileOptions { RecursionLimit = 16 });

var final = await graph.InvokeAsync(
    [new ChannelWrite("draft", "checkpoints are nice")],
    new RunOptions { ThreadId = "post-42" });

Console.WriteLine($"{final.Kind} after {final.Step} supersteps");

static Task<NodeResult> WriteAsync(GraphContext context, CancellationToken cancellationToken)
{
    var round = (context.Read<int?>("score") ?? 0) / 4;
    return Task.FromResult<NodeResult>(
        NodeResult.Continue(
            new ChannelWrite("draft", $"revision {round + 1}"),
            new ChannelWrite("notes", $"writer: produced revision {round + 1}")));
}

static Task<NodeResult> CritiqueAsync(GraphContext context, CancellationToken cancellationToken)
{
    var score = (context.Read<int?>("score") ?? 0) + 4;
    return Task.FromResult<NodeResult>(
        NodeResult.Continue(
            new ChannelWrite("score", score),
            new ChannelWrite("notes", $"critic: scored {score}/10")));
}
```

Swap `InvokeAsync` for `StreamAsync` (`StreamMode.Values` / `Updates` / `Events`). Under a host:
`services.AddVoluta(provider => …)`.

### DI nodes + Microsoft AI (optional)

Native path — no extension-method glue:

```csharp
// 1) IGraphNode + DI (core)
services.AddSingleton<PlanNode>();
services.AddVoluta(v =>
{
    v.Checkpoints.UseInMemory();
    v.Graph((sp, checkpointer) => new StateGraph()
        .AddNode<PlanNode>("plan")
        .AddEdge(GraphConstants.Start, "plan")
        .AddEdge("plan", GraphConstants.End)
        .Compile(checkpointer, new CompileOptions { Services = sp }));
});

// 2) MEAI IChatClient as a node (Voluta.Agents.AI)
ChatClientNodes.Add(
    graph, "answer", "answer",
    context => [new ChatMessage(ChatRole.User, context.Read<string>("question") ?? "")],
    stream: true,
    usageChannel: "usage",
    toolCallsChannel: "tool_calls");

// 3) Microsoft Agent Framework AIAgent as a node
AgentNodes.Add(graph, "research", agent, outputChannel: "draft", inputChannel: "question");
```

`ChatClientNodes` covers: assistant text → `OutputChannel`; optional `Stream` token
deltas via `GraphContext.Stream` (`StreamEventKind.Messages`); optional `UsageChannel`
(`ChatClientUsage` from `ChatResponse.Usage`, best-effort on stream from `UsageContent`);
optional `ToolCallsChannel` (Append-friendly `List<object?>` of `ChatClientToolCall`,
empty when the model made no calls). Windowing and ReAct routing stay in the host graph.

## Long-running / workers

HITL and multi-minute agent turns must not pin to an HTTP request. Pattern:

1. **Wake** a `threadId` (queue, bus, or in-process channel).
2. **Invoke** or **ResumeInvoke** until the stream hits interrupt / end / fail.
3. **Park** on interrupt (checkpoint is source of truth — process may exit).
4. **Complete** on done; **dead-letter / alert** on fail (last-good checkpoint remains;
   do not `ResumeInvoke` a Failed thread).

Package: **`Voluta.Hosting`** — `IThreadWakeBus`, `InMemoryThreadWakeBus`,
`GraphThreadRunner`, `GraphWorkerService`, `AddVolutaWorkerHosting()`.
Sample: [`samples/WorkerHost`](samples/WorkerHost/). No Hangfire/Quartz; NATS/SQS
are host-owned implementations of the interface.

```csharp
services.AddVoluta(v => { /* checkpoints + graph */ });
services.AddVolutaWorkerHosting(); // IThreadWakeBus + BackgroundService worker

// producer (HTTP approve, bus consumer, …)
await wakes.EnqueueAsync(ThreadWake.Start(threadId, inputWrites));
// later, after human approval:
await wakes.EnqueueAsync(ThreadWake.Resume(threadId, Command.Approve("ok")));
```

### Multi-instance (k8s scale-out)

- Use a **shared durable checkpointer** (File / EF / S3 / Redis), not in-memory, across replicas.
- Wakes are **hints**; the checkpoint decides invoke vs resume vs already-terminal.
- **Partition or lease** by `threadId` so two pods do not run the same thread at once.
- Use a **shared durable checkpointer** (File / EF / S3 / Redis), not in-memory, across replicas.
  **Checkpointer is the source of truth**; wakes are only hints.
- Replace `InMemoryThreadWakeBus` with a durable queue implementing `IThreadWakeBus`.
- **Partition or lease** by `threadId` so two pods do not run the same thread at once
  (`GraphWorkerService` only dedupes in-flight wakes **within one process**).
- Interrupt park is multi-process safe: pod A parks, pod B resumes hours later against
  the same store.

## Host DI — `AddVoluta`

One composition root: checkpoints + graph (and later UI hooks). Prefer this over raw
`AddSingleton<ICheckpointer>(…)` / ad-hoc factories.

```csharp
// Recommended: checkpoints + graph in one call
services.AddVoluta(v =>
{
    v.Checkpoints.UseInMemory(); // or UseFile / UseEntityFrameworkCore / UseS3 / UseRedis
    v.Graph((sp, checkpointer) => new StateGraph()
        // …nodes, edges, channels…
        .Compile(checkpointer, new CompileOptions { Services = sp }));
});

// Graph only (already compiled, or factory that owns its own checkpointer)
services.AddVoluta(prebuiltGraph);
services.AddVoluta(sp => BuildGraph(sp));

// Checkpoints only (resolve ICheckpointer yourself)
services.AddVolutaCheckpoints(c => c.UseFile("./.voluta/checkpoints"));
```

### Checkpoint providers (`v.Checkpoints.Use*`)

Exactly one `Use*` per host:

```csharp
v.Checkpoints.UseInMemory();
v.Checkpoints.UseFile("./.voluta/checkpoints");

// EF — register IDbContextFactory first (any provider: Npgsql, SqlServer, SQLite, …)
services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString));
v.Checkpoints.UseEntityFrameworkCore<AppDbContext>();

// S3 — register IAmazonS3 first
services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(RegionEndpoint.EUCentral1));
v.Checkpoints.UseS3(o =>
{
    o.BucketName = "voluta";
    o.KeyPrefix = "runs";
});

// Redis — register IConnectionMultiplexer first
services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("localhost:6379"));
v.Checkpoints.UseRedis(o =>
{
    o.KeyPrefix = "voluta:";
    // o.Database = 0;
});
```

<details>
<summary><strong>Host DbContext shape (EF)</strong></summary>

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IVolutaCheckpointDbContext
{
    public DbSet<CheckpointRecord> Checkpoints => Set<CheckpointRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyVolutaCheckpointModel(); // table voluta_checkpoints
        // …your entities…
    }
}
```

Schema ownership is yours: migrations or `EnsureCreated()` in tests. Column names follow
**your** EF naming convention (no forced snake_case). Snapshot payload is a JSON column via
EF value conversion — the checkpointer itself does not call `JsonSerializer`.

</details>

<details>
<summary><strong>Errors and limits</strong></summary>

- **Miss on Get** → `null` (not an exception).
- **Storage failures** → `CheckpointStoreException` with stable `Code`
  (`checkpoint.put_failed` / `get_failed` / `list_failed`). Graph logic still uses
  `GraphException` and friends.
- **Channel values (wire format v1)** on File / EF / S3 / Redis: allow-list only — `null`, primitives,
  string, `Guid`, date/time, `JsonElement`, `byte[]`, lists and string-key dictionaries of those
  (depth ≤ 8). Unsupported types fail Put with `checkpoint.unsupported_value_type` (no silent
  loss). **InMemory** is process-local and does not enforce the allow-list.
- **S3 keys:** `{prefix}/{safeThreadId}/{step:D12}.json`.

Every provider is exercised by `CheckpointerConformance.RunAllAsync` in `Voluta.Testing`
(InMemory, File, EF SQLite + EF InMemory, S3 with a fake client).

Full code + EventId catalog and host HTTP mapping sample:
[Error codes & EventIds](#error-codes--eventids).

</details>

## Error codes & EventIds

Voluta does **not** ship an ASP.NET package. Exceptions carry a stable `Code` (dot.case);
hosts map codes to HTTP/gRPC/worker outcomes themselves. Catalog types:

| Type | Package | Role |
|------|---------|------|
| `VolutaErrorCodes` | `Voluta.Abstractions` | Stable code strings |
| `VolutaEventIds` | `Voluta` | MEL `EventId` (id + name = code) |
| `VolutaExceptionLogging.GetEventId(...)` | `Voluta` | Code / exception → EventId |

Log full exception + `Code` + EventId; never put secrets or PII in `Message`.

```csharp
catch (GraphException exception)
{
    logger.LogError(
        VolutaExceptionLogging.GetEventId(exception),
        exception,
        "Graph failed with {ErrorCode}",
        exception.Code);
    // host maps exception.Code → ProblemDetails / status
}
```

### Code → suggested HTTP status (host sample)

| Code | Typical status | Notes |
|------|----------------|-------|
| `graph.invalid_*` / `graph.duplicate_*` / `graph.no_nodes` / `graph.missing_start` / `graph.unknown_endpoint` | **400** | Compile-time topology errors |
| `graph.invalid_resume` | **409** | Resume when thread is not interrupted |
| `graph.out_of_steps` | **422** or **400** | Recursion limit; product choice |
| `graph.run_failed` | **500** | Uncaught node/superstep fault |
| `channel.concurrent_update` | **409** | LastValue multi-writer in one superstep |
| `checkpoint.put_failed` / `get_failed` / `list_failed` / `corrupt_payload` | **502** / **500** | Store / IO / corrupt payload |
| `checkpoint.unsupported_format_version` | **409** or **500** | Wire newer than this package |
| `checkpoint.unsupported_value_type` | **400** | Reserved (serde #31) |
| `command.invalid_kind` / `command.invalid_payload` | **400** | Reserved (command taxonomy #32) |

`Get` miss stays `null` — not an exception. Interrupt control flow stays
`NodeResult.Interrupt` (not exceptions).


<details>
<summary><strong>Failure &amp; recovery (checkpoint policy)</strong></summary>

When a node throws, a superstep merge fails, or the recursion limit is hit:

1. **Stream** surfaces a `Failed` event (and the invoke/stream API rethrows the graph fault).
2. **Checkpoint Put** writes a **terminal** snapshot at the **failing superstep** with
   `GraphRunStatus.Failed` (or `Cancelled` on cooperative cancel). Incomplete writes from the
   failed superstep are **not** applied.
3. **Channel values** on that terminal snapshot are the **last successful** apply — last-good
   payload, not a wipe and not a half-merge.
4. **Get** returns that latest terminal document (`Status = Failed`, last-good channels).
5. **List** (when supported) still enumerates earlier `Running` / `Interrupted` / `Done` steps
   for tooling; prior step keys are never overwritten by the failure put.
6. **Resume** (`ResumeInvokeAsync`) still requires `Interrupted` — Failed/Cancelled are terminal
   for HITL resume. Hosts re-invoke with a new thread, or rebuild input from last-good channels /
   list history.

Contracts: [`openspec/specs/error-cancellation/`](openspec/specs/error-cancellation/).

</details>

## How a superstep works

One tick, in order: collect ready nodes → run them **concurrently** → **barrier** → merge writes
through channel reducers → checkpoint → evaluate edges.

- Nodes in the same superstep **never** see each other’s writes (barrier isolation).
- Conditional edges run **after** the merge, on committed state.

Contracts: [`openspec/specs/`](openspec/specs/) (12 capabilities).

## How it compares

**vs. [LangGraph](https://github.com/langchain-ai/langgraph)** (Python) — Origin of the execution
model (supersteps, channels, checkpoint-first). Voluta rebuilds the surface on .NET generics, typed
reducers, and `IAsyncEnumerable`. Not a port; a peer.

**vs. Microsoft Agent Framework** — Better for multi-agent chat and function calling. It does not
give cyclic graphs with durable per-thread state. Compose: run MAF agents *inside* Voluta nodes.

**vs. Durable Functions / Durable Task** — Stronger storage ecosystem for business workflows.
Orchestrator + replay vs graph + explicit channels — Voluta fits think-act-observe loops more
directly.

**vs. a hand-rolled `while`** — Works until “state at step 7?”, “resume after restart?”, or “two
nodes wrote the same field”. Those three questions *are* the library.

## Packages

How-to for checkpoints (DI `Use*` + host DbContext) is under
[Durable checkpoints](#durable-checkpoints). Behavior contracts:
[`openspec/specs/checkpoint/`](openspec/specs/checkpoint/). Until NuGet publishes,
browse source under [`src/`](src/) (each package is one folder; no per-package README yet).

| Package | Role | Source |
|---------|------|--------|
| `Voluta.Abstractions` | Contracts: channels, checkpoints, `NodeResult`, `Send`, streaming | [`src/core/Voluta.Abstractions`](src/core/Voluta.Abstractions/) |
| `Voluta` | Pregel runtime + InMemory + `Subgraph.AsNode` + `Describe()` | [`src/core/Voluta`](src/core/Voluta/) |
| `Voluta.DependencyInjection` | `AddVoluta(v => { v.Checkpoints…; v.Graph… })` | [`src/core/Voluta.DependencyInjection`](src/core/Voluta.DependencyInjection/) |
| `Voluta.OpenTelemetry` | `AddVolutaInstrumentation()` for OTel Tracer/Meter providers | [`src/hosting/Voluta.OpenTelemetry`](src/hosting/Voluta.OpenTelemetry/) |
| `Voluta.Generators` | `[GraphState]` source generator | [`src/devtools/Voluta.Generators`](src/devtools/Voluta.Generators/) |
| `Voluta.Testing` | Test doubles + checkpointer conformance suite | [`src/devtools/Voluta.Testing`](src/devtools/Voluta.Testing/) |
| `Voluta.Checkpoints.File` | JSON file-system checkpointer (`UseFile`) | [`src/checkpoints/Voluta.Checkpoints.File`](src/checkpoints/Voluta.Checkpoints.File/) |
| `Voluta.Checkpoints.EntityFrameworkCore` | Provider-agnostic EF Core (`UseEntityFrameworkCore<T>`) | [`src/checkpoints/Voluta.Checkpoints.EntityFrameworkCore`](src/checkpoints/Voluta.Checkpoints.EntityFrameworkCore/) |
| `Voluta.Checkpoints.S3` | AWS S3 / S3-compatible (`UseS3`) | [`src/checkpoints/Voluta.Checkpoints.S3`](src/checkpoints/Voluta.Checkpoints.S3/) |
| `Voluta.Checkpoints.Redis` | Redis sorted-set checkpointer (`UseRedis`) | [`src/checkpoints/Voluta.Checkpoints.Redis`](src/checkpoints/Voluta.Checkpoints.Redis/) |
| `Voluta.Agents.AI` | MAF `AIAgent` + MEAI as `IGraphNode` | [`src/agents/Voluta.Agents.AI`](src/agents/Voluta.Agents.AI/) |
| `Voluta.Hosting` | Wake bus + `GraphWorkerService` presets | [`src/hosting/Voluta.Hosting`](src/hosting/Voluta.Hosting/) |
| `Voluta.UI` | **Studio SPA** (React + Fluent) + legacy shell + `/api/v1` via `MapStudioApi` | [`src/ops/Voluta.UI`](src/ops/Voluta.UI/) |

**Native AOT** applies to the core tier only — `Voluta`, `Abstractions`, and
`DependencyInjection` are `IsAotCompatible`, with a publish smoke test in `samples/AotSmoke`.
Checkpoint providers (File / EF / S3 / Redis), UI, Agents.AI, Hosting, and OpenTelemetry
are regular-CLR packages and do not claim AOT.

### OpenTelemetry

Core `Voluta` always emits BCL `ActivitySource` / `Meter` named `"Voluta"` (no OTel SDK dependency).
Wire the SDK with `Voluta.OpenTelemetry`:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Voluta.OpenTelemetry;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddVolutaInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddVolutaInstrumentation()
        .AddOtlpExporter());
```

  Spans: `voluta.superstep`, `voluta.node.execute`, `voluta.checkpoint.put|get|list`.  
  Metrics: `voluta.superstep.duration`, `voluta.node.duration` (ms), `voluta.interrupt.count`,
  `voluta.checkpoint.*.count`, `voluta.stream.dropped`. Tags: `node.name`, `run.status`,
  `error.type`, `provider.name`, `stream.kind` (no raw thread ids).

  **No library `ILogger` output** — hosts log exceptions via `VolutaExceptionLogging.GetEventId`
  + `VolutaErrorCodes`. Full catalog: [`docs/0.x/concepts/observability.mdx`](docs/0.x/concepts/observability.mdx).


<details>
<summary><strong>Production hardening (Continue / multi-HITL / stream)</strong></summary>

- **Continue** with `PendingSends`: schedules incomplete push tasks only — does not re-pull `NextNodes` (avoids double side effects after a completed map+Send).
- **Multi-interrupt**: `Command.Resumes` may be a **partial** map; remaining `PendingInterrupts` stay Interrupted until covered. Unknown task ids fail `command.invalid_payload`.
- **Live Custom/Messages**: bounded buffer (capacity 256); overflow drops and increments `voluta.stream.dropped` (`stream.kind` tag). Order is best-effort per node.

See OpenSpec `checkpoint` / `hitl-interrupt` / `streaming` and issue #63.

</details>

## Samples

| Sample | What it shows |
|--------|----------------|
| [`HelloWorld`](samples/HelloWorld/) | Cyclic agent ⇄ tools, `StreamMode.Updates` |
| [`InterruptResume`](samples/InterruptResume/) | HITL interrupt + `Command` resume |
| [`AotSmoke`](samples/AotSmoke/) | Native AOT publish smoke |
| [`ReviewBot`](samples/ReviewBot/) | CLI: plan → sandbox tools → review (+ optional HITL) |
| [`DocQ`](samples/DocQ/) | Docs Q&A over a sandboxed folder |
| [`MarketingAgent`](samples/MarketingAgent/) | Hybrid desk setup over mock tools |
| [`MockAdMcp`](samples/MockAdMcp/) | Hybrid-shaped tool server (Campaign / SSP / deal / AdLibrary) |
| [`UiHost`](samples/UiHost/) | ASP.NET host: `MapVolutaUI` + `MapStudioApi` + Studio SPA |
| [`StudioHost`](samples/StudioHost/) | Studio `/api/v1` contract host with seeded demo threads |
| [`WorkerHost`](samples/WorkerHost/) | Durable `BackgroundService` runner (wake / park / resume) |

Index: [`samples/README.md`](samples/README.md).

```bash
dotnet run --project samples/HelloWorld
dotnet run --project samples/InterruptResume
dotnet run --project samples/WorkerHost
dotnet run --project samples/ReviewBot -- --offline --root .
dotnet run --project samples/DocQ -- --offline --root . --question "What is Voluta?"
dotnet run --project samples/MockAdMcp          # :5190
dotnet run --project samples/MarketingAgent -- --offline
dotnet run --project samples/UiHost             # Studio: http://localhost:5188/voluta/studio
```

## What isn't here yet

Stated plainly so you can judge the fit:

- **PublicAPI surface can still move** before a major bump (tracked with PublicApiAnalyzers).
  Shipped surface up to `v0.4.0` is frozen in `PublicAPI.Shipped.txt`. Host time-travel
  (`GetStateAsync` / `GetHistoryAsync` / `UpdateStateAsync` / `ForkAsync` / `Continue*`)
  is available but still Unshipped.
- **Studio SPA auth** is not built in; `/api/v1` supports an optional single API key
  (`StudioApiOptions.ApiKey`). Multi-tenant auth is out of scope for now.
- **Checkpoint serde** is JSON at the store; `AddChannel<T>` coerces declared types on restore
  (see [Typed channels](#typed-channels-and-custom-reducers) and
  [Durable file checkpoint](#durable-file-checkpoint-survive-process-restart)).
  Versioning/evolution is still open.
- **MCP in samples** is a light HTTP bridge (inlined in MarketingAgent), not a product package;
  real MCP is `ModelContextProtocol` (+ AspNetCore) on top of Voluta.
- **No built-in coding agent** (bash/edit/permissions) — Voluta is the graph runtime, not Claude Code.

## Documentation

Product docs live in [`docs/`](docs/) (`product.md` + version trees such as `0.x/**/*.mdx`).
On every `v*.*.*` tag the publish workflow packs them into **`docs.tgz`** (tarball root =
`product.md` + `0.x/…`, not nested under an extra `docs/` folder) and attaches the asset to
the GitHub Release for that tag. Public site: [docs.stbl.space/voluta](https://docs.stbl.space/voluta).

```bash
# local dry-run of the release asset layout
tar -czf docs.tgz -C docs .
tar -tzf docs.tgz | head
```

## Development

```bash
dotnet build voluta.slnx                      # 0 warnings, 0 errors — the gate
dotnet test  voluta.slnx                      # xUnit + Shouldly + NSubstitute
dotnet format voluta.slnx --severity hidden   # style drift
dotnet run -c Release --project benchmarks/Voluta.Benchmarks
```

`TreatWarningsAsErrors` + `EnforceCodeStyleInBuild` are on. Specs:
[`openspec/specs/`](openspec/specs/). Agent handbook: [`CLAUDE.md`](CLAUDE.md).

## Contributing

Small fixes → PR. Non-trivial changes → issue first (contracts still move).

```bash
git config core.hooksPath .githooks   # once per clone
```

Commit shape: `[voluta](feat/scope): subject`. Details:
[CONTRIBUTING.md](CONTRIBUTING.md).

## Inspiration

Execution model from [LangGraph](https://github.com/langchain-ai/langgraph) (MIT) — supersteps,
channel/reducer state, checkpoint-first persistence. The API diverges substantially; design
mistakes are ours, not theirs.

## License

MIT — see [LICENSE](LICENSE).
