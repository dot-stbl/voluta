using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Exceptions.Run;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Xunit;

namespace Voluta.Unit.Runtime;

public sealed class InterruptResumeShould
{
    [Fact(DisplayName = "Given interrupt node, when Invoke then Resume, then continues to End")]
    public async Task InterruptThenResumeContinues()
    {
        var checkpointer = new InMemoryCheckpointer();
        var phase = 0;

        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "gate",
                (context, _) =>
                {
                    phase++;
                    return !context.IsResume && phase == 1
                        ? Task.FromResult<NodeResult>(NodeResult.Interrupt(new { amount = 50 }))
                        : Task.FromResult<NodeResult>(
                            NodeResult.Continue(new ChannelWrite("messages", "approved")));
                })
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);

        var interrupted = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "hitl-1", StreamMode = StreamMode.Events });

        interrupted.Kind.ShouldBe(StreamEventKind.Interrupt);
        var snapshot = await checkpointer.GetAsync("hitl-1");
        snapshot!.Status.ShouldBe(GraphRunStatus.Interrupted);

        var terminal = await graph.ResumeInvokeAsync(
            "hitl-1",
            Command.Approve("ok"));

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await checkpointer.GetAsync("hitl-1");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.ChannelValues["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("approved");
    }

    [Fact(DisplayName = "Given done thread, when ResumeAsync is called, then fails invalid resume")]
    public async Task ResumeWhenDoneFails()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End)
            .Compile(checkpointer);

        var terminal = await graph.InvokeAsync([], new RunOptions { ThreadId = "done-1" });
        terminal.Kind.ShouldBe(StreamEventKind.End);

        await Should.ThrowAsync<GraphInvalidResumeException>(async () =>
        {
            await foreach (var _ in graph.ResumeAsync("done-1", Command.Approve()))
            {
            }
        });
    }

    [Fact(DisplayName = "Given unknown thread, when ResumeAsync is called, then fails invalid resume")]
    public async Task ResumeUnknownThreadFails()
    {
        var graph = new StateGraph()
            .AddNode(
                "gate",
                static (_, _) => Task.FromResult<NodeResult>(NodeResult.Interrupt("wait")))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        await Should.ThrowAsync<GraphInvalidResumeException>(async () =>
        {
            await graph.ResumeInvokeAsync("missing-thread", Command.Approve());
        });
    }

    [Fact(DisplayName = "Given interrupted gate, when Resume with Payload, then gate sees ResumePayload")]
    public async Task ResumePayloadReachesNode()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "gate",
                static (context, _) => !context.IsResume
                    ? Task.FromResult<NodeResult>(NodeResult.Interrupt(new { need = "signoff" }))
                    : Task.FromResult<NodeResult>(
                        NodeResult.Continue(
                            new ChannelWrite("messages", $"payload={context.ResumePayload}"))))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);

        var interrupted = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "payload-1", StreamMode = StreamMode.Events });
        interrupted.Kind.ShouldBe(StreamEventKind.Interrupt);

        var terminal = await graph.ResumeInvokeAsync(
            "payload-1",
            Command.Approve("signed-off"));

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await checkpointer.GetAsync("payload-1");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.ChannelValues["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("payload=signed-off");
    }

    [Fact(DisplayName = "Given interrupted gate, when Resume with Command.Values, then channel values apply before gate re-runs")]
    public async Task ResumeAppliesCommandValues()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddChannel("decision", ChannelKind.LastValue)
            .AddNode(
                "gate",
                static (context, _) => !context.IsResume
                    ? Task.FromResult<NodeResult>(NodeResult.Interrupt("need-decision"))
                    : Task.FromResult<NodeResult>(
                        NodeResult.Continue(
                            new ChannelWrite(
                                "messages",
                                $"decision={context.Read<string>("decision") ?? "(none)"}"))))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);

        var interrupted = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "values-1", StreamMode = StreamMode.Events });
        interrupted.Kind.ShouldBe(StreamEventKind.Interrupt);

        var terminal = await graph.ResumeInvokeAsync(
            "values-1",
            Command.Approve(
                "ok",
                new Dictionary<string, object?> { ["decision"] = "go" }));

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await checkpointer.GetAsync("values-1");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.ChannelValues["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("decision=go");
        done.ChannelValues["decision"].ShouldBe("go");
    }

    [Fact(DisplayName = "Given interrupt node, when Invoke with Events, then Interrupt event carries payload")]
    public async Task InterruptEventCarriesPayload()
    {
        var graph = new StateGraph()
            .AddNode(
                "gate",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Interrupt(new { amount = 50, currency = "USD" })))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "evt-int-1", StreamMode = StreamMode.Events });

        terminal.Kind.ShouldBe(StreamEventKind.Interrupt);
        terminal.NodeNames.ShouldBe(["gate"]);
        terminal.Payload.ShouldNotBeNull();
        var payloadText = terminal.Payload!.ToString();
        payloadText.ShouldNotBeNull();
        payloadText.ShouldContain("amount");
    }

    [Fact(DisplayName = "Given interrupted gate, when Resume with Approve() null payload, then IsResume is true and gate does not interrupt again")]
    public async Task NullPayloadApproveIsResumeAndDoesNotReinterrupt()
    {
        var checkpointer = new InMemoryCheckpointer();
        GraphContext? resumeContext = null;
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "gate",
                (context, _) =>
                {
                    if (context.IsResume)
                    {
                        resumeContext = context;
                        return Task.FromResult<NodeResult>(
                            NodeResult.Continue(new ChannelWrite("messages", "approved-null")));
                    }

                    return Task.FromResult<NodeResult>(NodeResult.Interrupt(new { need = "signoff" }));
                })
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);

        var interrupted = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "null-payload-1", StreamMode = StreamMode.Events });
        interrupted.Kind.ShouldBe(StreamEventKind.Interrupt);

        var terminal = await graph.ResumeInvokeAsync("null-payload-1", Command.Approve());

        terminal.Kind.ShouldBe(StreamEventKind.End);
        resumeContext.ShouldNotBeNull();
        resumeContext.IsResume.ShouldBeTrue();
        resumeContext.ResumePayload.ShouldBeNull();
        resumeContext.ResumeKind.ShouldBe(Command.Kinds.Approve);
        var done = await checkpointer.GetAsync("null-payload-1");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.ChannelValues["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("approved-null");
    }

    [Theory(DisplayName = "Given interrupted gate, when Resume with Command, then ResumeKind matches Command.Kind")]
    [InlineData(Command.Kinds.Approve)]
    [InlineData(Command.Kinds.Reject)]
    public async Task ResumeKindMatchesCommand(string kind)
    {
        var checkpointer = new InMemoryCheckpointer();
        string? seenKind = null;
        var graph = new StateGraph()
            .AddNode(
                "gate",
                (context, _) =>
                {
                    if (!context.IsResume)
                    {
                        return Task.FromResult<NodeResult>(NodeResult.Interrupt("wait"));
                    }

                    seenKind = context.ResumeKind;
                    return Task.FromResult<NodeResult>(NodeResult.Continue());
                })
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = $"kind-{kind}", StreamMode = StreamMode.Events });

        var command = kind == Command.Kinds.Reject
            ? Command.Reject("no")
            : Command.Approve("ok");
        var terminal = await graph.ResumeInvokeAsync($"kind-{kind}", command);

        terminal.Kind.ShouldBe(StreamEventKind.End);
        seenKind.ShouldBe(kind);
    }

    [Fact(DisplayName = "Given first Invoke, when gate runs, then IsResume is false")]
    public async Task FirstInvokeIsNotResume()
    {
        var checkpointer = new InMemoryCheckpointer();
        var sawResume = false;
        var graph = new StateGraph()
            .AddNode(
                "gate",
                (context, _) =>
                {
                    sawResume = context.IsResume;
                    return Task.FromResult<NodeResult>(
                        context.IsResume
                            ? NodeResult.Continue()
                            : NodeResult.Interrupt("wait"));
                })
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);

        var interrupted = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "first-invoke-1", StreamMode = StreamMode.Events });

        interrupted.Kind.ShouldBe(StreamEventKind.Interrupt);
        sawResume.ShouldBeFalse();
    }
}
