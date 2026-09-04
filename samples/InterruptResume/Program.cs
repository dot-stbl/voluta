// InterruptResume — HITL interrupt + Command approve resume.
//
// Run:
//   dotnet run --project samples/InterruptResume
//
// Graph:
//   START → gate → END
//
// First visit to `gate` interrupts with a payload. The host prints the
// interrupt, then ResumeAsync(Command.Approve(...)) continues the run.

using Voluta;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Samples.Shared;

const string ThreadId = "hitl-sample-1";

var checkpointer = new InMemoryCheckpointer();
var graph = new StateGraph()
    .AddChannel("messages", ChannelKind.Append)
    .AddNode("gate", GateNodeAsync)
    .AddEdge(GraphConstants.Start, "gate")
    .AddEdge("gate", GraphConstants.End)
    .Compile(checkpointer);

CliUi.Banner(
    "InterruptResume",
    "HITL interrupt · Command resume",
    ("thread", ThreadId),
    ("stream", "Events"));

CliUi.Section("invoke · expect interrupt");
await foreach (var item in graph.StreamAsync(
                   [new ChannelWrite("messages", "user: transfer $50")],
                   new RunOptions { ThreadId = ThreadId, StreamMode = StreamMode.Events }))
{
    CliUi.StreamEvent(item);
}

var interrupted = await checkpointer.GetAsync(ThreadId);
CliUi.KeyValue("checkpoint", interrupted?.Status.ToString());
CliUi.KeyValue("payload", CliUi.FormatValue(interrupted?.InterruptPayload));

if (interrupted?.Status != GraphRunStatus.Interrupted)
{
    CliUi.Error("expected Interrupted status — aborting");
    return 1;
}

CliUi.Section("resume · Command.Approve");
await foreach (var item in graph.ResumeAsync(
                   ThreadId,
                   Command.Approve("ok"),
                   StreamMode.Events))
{
    CliUi.StreamEvent(item);
}

var done = await checkpointer.GetAsync(ThreadId);
CliUi.Section("result");
CliUi.KeyValue("status", done?.Status.ToString());
if (done?.ChannelValues.TryGetValue("messages", out var messages) is true)
{
    CliUi.Messages(messages);
}

if (done?.Status == GraphRunStatus.Done)
{
    CliUi.Ok("done");
    return 0;
}

CliUi.Error($"unexpected status: {done?.Status}");
return 2;

static Task<NodeResult> GateNodeAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    if (!context.IsResume)
    {
        CliUi.Node("gate", "interrupting for human approval");
        return Task.FromResult<NodeResult>(
            NodeResult.Interrupt(new { action = "transfer", amount = 50, currency = "USD" }));
    }

    CliUi.Node("gate", $"resumed · payload={CliUi.FormatValue(context.ResumePayload)} · approving");
    return Task.FromResult<NodeResult>(
        NodeResult.Continue(new ChannelWrite("messages", "gate: transfer approved")));
}
