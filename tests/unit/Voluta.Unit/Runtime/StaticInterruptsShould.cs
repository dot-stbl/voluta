using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Exceptions;
using Voluta.Exceptions.Run;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;
using Xunit;

namespace Voluta.Unit.Runtime;

public sealed class StaticInterruptsShould
{
    [Fact(DisplayName = "Given InterruptBefore node, when run, then parks before entry; continue runs it and completes")]
    public async Task PauseBeforeAndContinue()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(
                NodeResult.Continue(new ChannelWrite("messages", "a"))))
            .AddNode("b", static (_, _) => Task.FromResult<NodeResult>(
                NodeResult.Continue(new ChannelWrite("messages", "b"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddEdge("b", GraphConstants.End)
            .Compile(
                checkpointer,
                new CompileOptions { InterruptBefore = ["b"] });

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "si-before", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.Interrupt);
        var parked = await graph.GetStateAsync("si-before");
        parked!.Status.ShouldBe(GraphRunStatus.Interrupted);
        parked.StopReason.ShouldBe(RunStopReason.StaticBefore);
        parked.PendingInterrupts.ShouldBeEmpty();
        parked.NextNodes.ShouldBe(["b"]);
        parked.Step.ShouldBe(1, "the paused step is not consumed");
        parked.Values["messages"].ShouldBeOfType<List<object?>>().ShouldContain("a");

        var afterContinue = await graph.ContinueInvokeAsync("si-before");
        afterContinue.Kind.ShouldBe(StreamEventKind.End);
        var done = await graph.GetStateAsync("si-before");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.Values["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("a");
        messages.ShouldContain("b");
    }

    [Fact(DisplayName = "Given InterruptAfter node, when run, then parks after its output applied with the next ready set")]
    public async Task PauseAfter()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(
                NodeResult.Continue(new ChannelWrite("messages", "a"))))
            .AddNode("b", static (_, _) => Task.FromResult<NodeResult>(
                NodeResult.Continue(new ChannelWrite("messages", "b"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddEdge("b", GraphConstants.End)
            .Compile(
                checkpointer,
                new CompileOptions { InterruptAfter = ["a"] });

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "si-after", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.Interrupt);
        var parked = await graph.GetStateAsync("si-after");
        parked!.Status.ShouldBe(GraphRunStatus.Interrupted);
        parked.StopReason.ShouldBe(RunStopReason.StaticAfter);
        parked.PendingInterrupts.ShouldBeEmpty();
        parked.NextNodes.ShouldBe(["b"]);
        // a's output was already applied and is visible.
        parked.Values["messages"].ShouldBeOfType<List<object?>>().ShouldContain("a");

        var resumed = await graph.ContinueInvokeAsync("si-after");
        resumed.Kind.ShouldBe(StreamEventKind.End);
        var done = await graph.GetStateAsync("si-after");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        done.Values["messages"].ShouldBeOfType<List<object?>>().ShouldContain("b");
    }

    [Fact(DisplayName = "Given a cycle through the parked node, when continued, then every pass re-parks (LangGraph behavior)")]
    public async Task CycleReparksEveryPass()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode("loop", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "loop")
            .AddEdge("loop", "loop")
            .Compile(
                checkpointer,
                new CompileOptions { InterruptBefore = ["loop"] });

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "si-cycle", StreamMode = StreamMode.Events });

        for (var pass = 0; pass < 3; pass++)
        {
            var terminal = await graph.ContinueInvokeAsync("si-cycle");
            terminal.Kind.ShouldBe(StreamEventKind.Interrupt);
            var parked = await graph.GetStateAsync("si-cycle");
            parked!.StopReason.ShouldBe(RunStopReason.StaticBefore);
            parked.NextNodes.ShouldBe(["loop"]);
        }
    }

    [Fact(DisplayName = "Given static vs HITL parks, when inspected, then the (PendingInterrupts, StopReason) pair distinguishes them; wrong API rejects")]
    public async Task DistinguishesStaticFromHitl()
    {
        var checkpointer = new InMemoryCheckpointer();
        var hitlGraph = new StateGraph()
            .AddNode(
                "gate",
                static (context, _) => context.IsResume
                    ? Task.FromResult<NodeResult>(NodeResult.Continue())
                    : Task.FromResult<NodeResult>(NodeResult.Interrupt("ask")))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);
        await hitlGraph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "si-hitl", StreamMode = StreamMode.Events });
        var hitl = await hitlGraph.GetStateAsync("si-hitl");
        hitl!.PendingInterrupts.ShouldNotBeEmpty();
        hitl.StopReason.ShouldBe(RunStopReason.None);
        // Continue on a HITL interrupt is still rejected.
        await Should.ThrowAsync<GraphInvalidContinueException>(
            async () => await hitlGraph.ContinueInvokeAsync("si-hitl"));

        var staticGraph = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("b", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddEdge("b", GraphConstants.End)
            .Compile(
                checkpointer,
                new CompileOptions { InterruptBefore = ["b"] });
        await staticGraph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "si-static", StreamMode = StreamMode.Events });
        var staticPark = await staticGraph.GetStateAsync("si-static");
        staticPark!.PendingInterrupts.ShouldBeEmpty();
        staticPark.StopReason.ShouldBe(RunStopReason.StaticBefore);
        // Resume (with a command) on a static pause is rejected before any legacy fallback.
        var resumeException = await Should.ThrowAsync<GraphInvalidResumeException>(
            async () => await staticGraph.ResumeInvokeAsync("si-static", Command.Approve("ok")));
        resumeException.Code.ShouldBe(VolutaErrorCodes.GraphInvalidResume);

        var terminal = await staticGraph.ContinueInvokeAsync("si-static");
        terminal.Kind.ShouldBe(StreamEventKind.End);
    }

    [Fact(DisplayName = "Given a Failed thread, when ContinueAsync, then still GraphInvalidContinueException")]
    public async Task FailedThreadStillRejectsContinue()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddNode(
                "boom",
                static (_, _) => throw new InvalidOperationException("boom"))
            .AddEdge(GraphConstants.Start, "boom")
            .AddEdge("boom", GraphConstants.End)
            .Compile(checkpointer);

        await Should.ThrowAsync<GraphRunFailedException>(
            async () => await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "si-failed", StreamMode = StreamMode.Events }));

        var exception = await Should.ThrowAsync<GraphInvalidContinueException>(
            async () => await graph.ContinueInvokeAsync("si-failed"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidContinue);
    }

    [Fact(DisplayName = "Given unknown interrupt node, when Compile, then graph.unknown_endpoint")]
    public void RejectUnknownInterruptNode()
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End);

        var exception = Should.Throw<Exceptions.GraphCompileException>(
            () => builder.Compile(
                new InMemoryCheckpointer(),
                new CompileOptions { InterruptBefore = ["ghost"] }));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphUnknownEndpoint);
    }
}
