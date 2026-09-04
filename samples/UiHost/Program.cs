// UiHost — WebApplication hosting Voluta.UI with a realistic multi-node graph
// and several seeded threads (HITL + completed) so the console has data to inspect.
//
// Run:
//   dotnet run --project samples/UiHost
// Open:
//   http://localhost:5188/voluta       (legacy shell)
//   http://localhost:5188/api/v1/*     (versioned Studio HTTP/SSE API)

using Voluta;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;
using Voluta.UI;
using Voluta.UI.Studio;

var checkpointer = new InMemoryCheckpointer();
var graph = new StateGraph()
    .AddChannel("goal", ChannelKind.LastValue)
    .AddChannel("plan", ChannelKind.LastValue)
    .AddChannel("evidence", ChannelKind.Append)
    .AddChannel("risk", ChannelKind.LastValue)
    .AddChannel("verdict", ChannelKind.LastValue)
    .AddChannel("messages", ChannelKind.Append)
    .AddChannel("status", ChannelKind.LastValue)
    .AddNode("intake", IntakeAsync)
    .AddNode("plan", PlanAsync)
    .AddNode("retrieve", RetrieveAsync)
    .AddNode("risk_gate", RiskGateAsync)
    .AddNode("synthesize", SynthesizeAsync)
    .AddNode("notify", NotifyAsync)
    .AddEdge(GraphConstants.Start, "intake")
    .AddEdge("intake", "plan")
    .AddEdge("plan", "retrieve")
    .AddEdge("retrieve", "risk_gate")
    .AddConditionalEdges(
        "risk_gate",
        static context => context.Read<string>("status") == "blocked"
            ? GraphConstants.End
            : "synthesize")
    .AddEdge("synthesize", "notify")
    .AddEdge("notify", GraphConstants.End)
    .Compile(checkpointer, new CompileOptions { RecursionLimit = 32 });

var session = new VolutaUiSession(graph, checkpointer);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVolutaUI(session);

// Studio API v1: bind the StudioApi section (PathPrefix, ApiKey). Both optional —
// with no section the API mounts at /api/v1 with key auth off.
var studioApiOptions = new StudioApiOptions();
builder.Configuration.GetSection(StudioApiOptions.SectionName).Bind(studioApiOptions);

var app = builder.Build();
app.MapGet("/", static () => Results.Redirect("/voluta"));
app.MapVolutaUI(static options => options.PathPrefix = "/voluta");
app.MapStudioApi(studioApiOptions);

await SeedDemoThreadsAsync(session, app.Logger);

app.Logger.LogInformation(
    "Voluta UI host ready. UI: {UiUrl}, Studio API: {StudioApiUrl} (key auth {KeyAuth}) — threads: payment-hitl, deploy-hitl, research-done, audit-blocked",
    "http://localhost:5188/voluta",
    "http://localhost:5188/api/v1",
    string.IsNullOrEmpty(studioApiOptions.ApiKey) ? "off" : "on");

await app.RunAsync();

static async Task SeedDemoThreadsAsync(VolutaUiSession session, ILogger logger)
{
    // 1) Payment transfer — stops at risk_gate (HITL)
    await DrainAsync(
        session.StreamInvokeAsync(
            "payment-hitl",
            [
                new ChannelWrite("goal", "wire transfer $4,200 to vendor ACME"),
                new ChannelWrite("messages", "user: please pay the ACME invoice"),
                new ChannelWrite("status", "start"),
            ]));

    // 2) Deploy to prod — second interrupt, different payload
    await DrainAsync(
        session.StreamInvokeAsync(
            "deploy-hitl",
            [
                new ChannelWrite("goal", "deploy release 0.1.4 to production"),
                new ChannelWrite("messages", "user: ship 0.1.4"),
                new ChannelWrite("status", "start"),
            ]));

    // 3) Research — auto-approve risk (no HITL) by seeding resume path via low risk
    //    We use goal keyword "docs" so risk_gate continues without interrupt.
    await DrainAsync(
        session.StreamInvokeAsync(
            "research-done",
            [
                new ChannelWrite("goal", "docs: summarize StateGraph compile options"),
                new ChannelWrite("messages", "user: how does CompileOptions work?"),
                new ChannelWrite("status", "start"),
            ]));

    // 4) Audit — high risk that ends as blocked after reject-style path
    //    Keyword "purge" → risk_gate interrupts; we resume with reject → status blocked.
    await DrainAsync(
        session.StreamInvokeAsync(
            "audit-blocked",
            [
                new ChannelWrite("goal", "purge stale customer PII from analytics lake"),
                new ChannelWrite("messages", "user: delete old PII partitions"),
                new ChannelWrite("status", "start"),
            ]));

    await DrainAsync(
        session.StreamResumeAsync(
            "audit-blocked",
            Command.Reject("reject: policy — no bulk PII purge without DPO")));

    logger.LogInformation(
        "Seeded threads: payment-hitl (interrupt), deploy-hitl (interrupt), research-done (Done), audit-blocked (Done/blocked)");
}

static async Task DrainAsync(IAsyncEnumerable<StreamEvent> stream)
{
    await using var enumerator = stream.GetAsyncEnumerator();
    while (await enumerator.MoveNextAsync())
    {
    }
}

static Task<NodeResult> IntakeAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var goal = context.Read<string>("goal") ?? "unspecified goal";
    return Task.FromResult<NodeResult>(
        NodeResult.Continue(
            new ChannelWrite("messages", $"intake: accepted goal «{goal}»"),
            new ChannelWrite("status", "planning")));
}

static Task<NodeResult> PlanAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var goal = context.Read<string>("goal") ?? "";
    var plan = goal.Contains("deploy", StringComparison.OrdinalIgnoreCase)
        ? "1) freeze change window 2) canary 5% 3) full rollout 4) watch SLOs"
        : goal.Contains("docs", StringComparison.OrdinalIgnoreCase)
            ? "1) locate API surface 2) extract options 3) write short answer"
            : goal.Contains("purge", StringComparison.OrdinalIgnoreCase)
                ? "1) scope tables 2) legal hold check 3) dry-run delete 4) execute"
                : "1) verify payee 2) check balance 3) dual-control approve 4) settle";
    return Task.FromResult<NodeResult>(
        NodeResult.Continue(
            new ChannelWrite("plan", plan),
            new ChannelWrite("messages", "plan: checklist ready"),
            new ChannelWrite("status", "retrieve")));
}

static Task<NodeResult> RetrieveAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var goal = context.Read<string>("goal") ?? "";
    var hits = EvidenceForGoal(goal);

    var writes = new List<ChannelWrite>
    {
        new("messages", $"retrieve: {hits.Count} evidence item(s)"),
        new("status", "risk"),
    };
    foreach (var hit in hits)
    {
        writes.Add(new ChannelWrite("evidence", hit));
    }

    return Task.FromResult<NodeResult>(NodeResult.Continue(writes));
}

static IReadOnlyList<string> EvidenceForGoal(string goal)
{
    return goal switch
    {
        _ when goal.Contains("deploy", StringComparison.OrdinalIgnoreCase) =>
        [
            "ci: green on main @ a7fad91",
            "slo: error-rate 0.12% (budget 0.5%)",
            "change-calendar: prod window open until 18:00 UTC",
        ],
        _ when goal.Contains("docs", StringComparison.OrdinalIgnoreCase) =>
        [
            "src/Voluta/Graph/Options/CompileOptions.cs — RecursionLimit",
            "openspec/specs/graph-runtime/spec.md — superstep barrier",
            "samples/HelloWorld — conditional edges demo",
        ],
        _ when goal.Contains("purge", StringComparison.OrdinalIgnoreCase) =>
        [
            "table analytics.events_pii rows≈12.4M",
            "retention policy: 90d · legal hold: ON for tenant acme",
            "last purge job: 2026-07-02 (failed approval)",
        ],
        _ =>
        [
            "vendor ACME · IBAN ····4821 · currency USD",
            "open invoice INV-20418 · amount 4200.00",
            "wallet balance 18_240.55 · dual-control required > 1_000",
        ],
    };
}

static Task<NodeResult> RiskGateAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var goal = context.Read<string>("goal") ?? "";

    // Low-risk docs path: no interrupt.
    if (goal.Contains("docs", StringComparison.OrdinalIgnoreCase))
    {
        return Task.FromResult<NodeResult>(
            NodeResult.Continue(
                new ChannelWrite("risk", "low"),
                new ChannelWrite("messages", "risk_gate: auto-approved (docs)"),
                new ChannelWrite("status", "synthesize")));
    }

    // Resume: IsResume is true even when Command.Approve() carries a null payload.
    if (context.IsResume)
    {
        return Task.FromResult(ResumeRiskGate(context.ResumePayload, context.ResumeKind));
    }

    var level = goal.Contains("purge", StringComparison.OrdinalIgnoreCase) ? "critical"
        : goal.Contains("deploy", StringComparison.OrdinalIgnoreCase) ? "high"
        : "medium";

    return Task.FromResult<NodeResult>(
        NodeResult.Interrupt(
            new
            {
                gate = "risk_gate",
                level,
                goal,
                requires = "dual_control",
                summary = level switch
                {
                    "critical" => "Bulk PII purge — legal hold may apply",
                    "high" => "Production deploy — confirm change window",
                    _ => "Payment above threshold — dual control required",
                },
            }));
}

static NodeResult ResumeRiskGate(object? resumePayload, string? resumeKind)
{
    var payloadText = resumePayload?.ToString() ?? "";
    var rejected = resumeKind == Command.Kinds.Reject
                   || payloadText.Contains("reject", StringComparison.OrdinalIgnoreCase)
                   || payloadText.Contains("deny", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(payloadText, "no", StringComparison.OrdinalIgnoreCase);
    return rejected
        ? NodeResult.Continue(
            new ChannelWrite("risk", "rejected"),
            new ChannelWrite("messages", $"risk_gate: rejected ({payloadText})"),
            new ChannelWrite("status", "blocked"),
            new ChannelWrite("verdict", "blocked by operator"))
        : NodeResult.Continue(
            new ChannelWrite("risk", "accepted"),
            new ChannelWrite("messages", "risk_gate: operator approved"),
            new ChannelWrite("status", "synthesize"));
}

static Task<NodeResult> SynthesizeAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var plan = context.Read<string>("plan") ?? "";
    var risk = context.Read<string>("risk") ?? "unknown";
    var verdict =
        $"OK · risk={risk} · executed plan length={plan.Length} chars · {DateTimeOffset.UtcNow:u}";
    return Task.FromResult<NodeResult>(
        NodeResult.Continue(
            new ChannelWrite("verdict", verdict),
            new ChannelWrite("messages", "synthesize: verdict drafted"),
            new ChannelWrite("status", "notify")));
}

static Task<NodeResult> NotifyAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var verdict = context.Read<string>("verdict") ?? "(no verdict)";
    return Task.FromResult<NodeResult>(
        NodeResult.Continue(
            new ChannelWrite("messages", $"notify: posted «{verdict}»"),
            new ChannelWrite("status", "done")));
}
