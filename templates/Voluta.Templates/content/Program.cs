// VolutaAgent — HITL scaffold from `dotnet new voluta-agent`.
//
// Graph: START → intake → gate → chat → END
//
// - intake: records the user request
// - gate: interrupts for human approval (HITL)
// - chat: MEAI IChatClient node (offline stub by default — no API key required)
//
// Run:  dotnet run
// Env:  VOLUTA_USE_LIVE_CHAT=1 + your own IChatClient registration to go live

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Voluta;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Agents.AI;
using Voluta.DependencyInjection;
using Voluta.DependencyInjection.Checkpoints;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;

const string ThreadId = "voluta-agent-1";

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Offline by default: builds and runs without any API key.
// Swap OfflineChatClient for a real IChatClient (OpenAI, Azure, Ollama, …)
// when VOLUTA_USE_LIVE_CHAT is set and you register that client here.
builder.Services.AddSingleton<IChatClient, OfflineChatClient>();

builder.Services.AddVoluta(voluta =>
{
    // Swap UseInMemory() for UseFile("./.voluta/checkpoints") after adding
    // PackageReference Voluta.Checkpoints.File (see README).
    voluta.Checkpoints.UseInMemory();
    voluta.Graph((services, checkpointer) => new StateGraph()
        .AddChannel("messages", ChannelKind.Append)
        .AddChannel("request", ChannelKind.LastValue)
        .AddChannel("answer", ChannelKind.LastValue)
        .AddNode("intake", IntakeAsync)
        .AddNode("gate", GateAsync)
        .AddNode(
            "chat",
            ChatClientGraphNode.Create(
                "answer",
                static context =>
                {
                    var request = context.Read<string>("request") ?? "hello";
                    return
                    [
                        new ChatMessage(
                            ChatRole.System,
                            "You are a concise assistant for a Voluta agent sample."),
                        new ChatMessage(ChatRole.User, request),
                    ];
                }))
        .AddEdge(GraphConstants.Start, "intake")
        .AddEdge("intake", "gate")
        .AddEdge("gate", "chat")
        .AddEdge("chat", GraphConstants.End)
        .Compile(checkpointer, new CompileOptions { Services = services }));
});

using var host = builder.Build();
var graph = host.Services.GetRequiredService<CompiledGraph>();
var checkpointer = host.Services.GetRequiredService<ICheckpointer>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VolutaAgent");

logger.LogInformation("invoke · expect interrupt on gate (thread {ThreadId})", ThreadId);

await foreach (var item in graph.StreamAsync(
                   [
                       new ChannelWrite("request", "Summarize why HITL interrupts matter for agents."),
                       new ChannelWrite("messages", "user: start"),
                   ],
                   new RunOptions { ThreadId = ThreadId, StreamMode = StreamMode.Events }))
{
    LogStreamItem(logger, item);
}

var interrupted = await checkpointer.GetAsync(ThreadId);
if (interrupted?.Status != GraphRunStatus.Interrupted)
{
    logger.LogError("expected Interrupted, got {Status}", interrupted?.Status);
    return 1;
}

logger.LogInformation(
    "parked · payload={Payload} · resuming with Command.Approve",
    interrupted.InterruptPayload);

await foreach (var item in graph.ResumeAsync(
                   ThreadId,
                   Command.Approve("ok"),
                   StreamMode.Events))
{
    LogStreamItem(logger, item);
}

var done = await checkpointer.GetAsync(ThreadId);
logger.LogInformation("status={Status}", done?.Status);
if (done?.ChannelValues.TryGetValue("answer", out var answer) is true)
{
    logger.LogInformation("answer={Answer}", answer);
}

if (done?.ChannelValues.TryGetValue("messages", out var messages) is true)
{
    logger.LogInformation("messages={Messages}", messages);
}

return done?.Status == GraphRunStatus.Done ? 0 : 2;

static Task<NodeResult> IntakeAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var request = context.Read<string>("request") ?? "(empty)";
    return Task.FromResult<NodeResult>(
        NodeResult.Continue(new ChannelWrite("messages", $"intake: received «{request}»")));
}

static Task<NodeResult> GateAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    if (!context.IsResume)
    {
        return Task.FromResult<NodeResult>(
            NodeResult.Interrupt(new
            {
                action = "approve-chat",
                reason = "human approval before calling the model",
            }));
    }

    return Task.FromResult<NodeResult>(
        NodeResult.Continue(
            new ChannelWrite("messages", $"gate: approved · {context.ResumePayload}")));
}

static void LogStreamItem(ILogger logger, StreamEvent item)
{
    logger.LogInformation(
        "stream · {Kind} · nodes=[{Nodes}]",
        item.Kind,
        item.NodeNames is { Count: > 0 } names ? string.Join(',', names) : "—");
}

/// <summary>
///     Deterministic offline <see cref="IChatClient" /> so the template builds and runs
///     without cloud credentials. Replace with a real client for production.
/// </summary>
file sealed class OfflineChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lastUser = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text
            ?? "(no user message)";
        var text =
            $"[offline stub] HITL + checkpoints let you pause a graph, review «{Truncate(lastUser, 80)}», then resume safely.";
        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public void Dispose()
    {
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max] + "…";
    }
}
