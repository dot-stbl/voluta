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
using Voluta.Testing.Checkpoint;
using Xunit;

namespace Voluta.Unit.Runtime;

public sealed class JoinBarrierShould
{
    [Fact(DisplayName = "Given a wait-for-all join over two branches, when both complete, then the target fires once with both writes applied")]
    public async Task WaitForAllFiresWhenBothSourcesComplete()
    {
        var checkpointer = new InMemoryCheckpointer();
        var gateRuns = 0;
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "left",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "left")))
            )
            .AddNode(
                "right",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "right")))
            )
            .AddNode(
                "gate",
                (_, _) =>
                {
                    Interlocked.Increment(ref gateRuns);
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "gate")));
                })
            .AddEdge(GraphConstants.Start, "left")
            .AddEdge(GraphConstants.Start, "right")
            .AddEdge("left", GraphConstants.End)
            .AddEdge("right", GraphConstants.End)
            .AddEdge(["left", "right"], "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "join-fire", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        gateRuns.ShouldBe(1);
        var done = await graph.GetStateAsync("join-fire");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.Values["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("left");
        messages.ShouldContain("right");
        messages.ShouldContain("gate");
        var raw = await checkpointer.GetAsync("join-fire");
        raw!.JoinArrivals.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Given a join whose sources complete in different supersteps, when the last one arrives, then the target fires")]
    public async Task FiresWhenSourcesArriveAcrossSupersteps()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "a")))
            )
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "b")))
            )
            .AddNode(
                "gate",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "gate")))
            )
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddEdge("b", GraphConstants.End)
            .AddEdge("gate", GraphConstants.End)
            .AddEdge(["a", "b"], "gate")
            .Compile(checkpointer);

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "join-staged", StreamMode = StreamMode.Values });

        // a (step 1) → b (step 2, join armed) → gate fires on b's arrival (step 3).
        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await graph.GetStateAsync("join-staged");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.Values["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("gate");
    }

    [Fact(DisplayName = "Given a join feeding a cycle, when it fires, then the epoch reopens and the join fires again")]
    public async Task EpochReopensInCycle()
    {
        var checkpointer = new InMemoryCheckpointer();
        var gateRuns = 0;
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "a")))
            )
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "b")))
            )
            .AddNode(
                "gate",
                (_, _) =>
                {
                    Interlocked.Increment(ref gateRuns);
                    return Task.FromResult<NodeResult>(NodeResult.Continue());
                })
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddEdge("b", "a")
            .AddEdge("gate", "a")
            .AddEdge(["a", "b"], "gate")
            .Compile(checkpointer);

        // The cycle re-arms the join every pass; the recursion limit stops the run.
        var exception = await Should.ThrowAsync<GraphOutOfStepsException>(
            async () => await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "join-cycle", StreamMode = StreamMode.Values }));

        exception.ShouldNotBeNull();
        gateRuns.ShouldBeGreaterThanOrEqualTo(2, "the join must fire again after each epoch resets");
    }

    [Fact(DisplayName = "Given a crash after both join sources completed, when resumed, then the firing is recovered and the gate runs")]
    public async Task CrashResumeRecoversArrivals()
    {
        var inner = new InMemoryCheckpointer();
        // Puts: (1) step 0, (2) progress put after c completes (all three sources done),
        // (3) boundary put — crash. The join firing only lives on the boundary put.
        var faulting = new FaultInjectingCheckpointer(inner, failOnPutNumber: 3);
        var gateRuns = 0;
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "a")))
            )
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "b")))
            )
            .AddNode(
                "c",
                async (_, _) =>
                {
                    await Task.Delay(50); // completes last, so its progress put is faulted
                    return NodeResult.Continue(new ChannelWrite("messages", "c"));
                })
            .AddNode(
                "gate",
                (_, _) =>
                {
                    Interlocked.Increment(ref gateRuns);
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "gate")));
                })
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge(GraphConstants.Start, "b")
            .AddEdge(GraphConstants.Start, "c")
            .AddEdge(["a", "b", "c"], "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(faulting);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "join-crash", StreamMode = StreamMode.Values }));

        var parked = await inner.GetAsync("join-crash");
        parked.ShouldNotBeNull();
        parked!.CompletedTaskIds.ShouldBe(["a", "b"], ignoreOrder: true);
        parked.JoinArrivals.ShouldBeEmpty("arrivals had not yet been routed when the boundary put crashed");

        var resumed = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "gate",
                (_, _) =>
                {
                    Interlocked.Increment(ref gateRuns);
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "gate")));
                })
            .AddEdge("gate", GraphConstants.End)
            .AddEdge(["a", "b", "c"], "gate")
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "a")))
            )
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "b")))
            )
            .AddNode(
                "c",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "c")))
            )
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge(GraphConstants.Start, "b")
            .AddEdge(GraphConstants.Start, "c")
            .Compile(inner);

        var terminal = await resumed.ContinueInvokeAsync("join-crash");

        terminal.Kind.ShouldBe(StreamEventKind.End);
        gateRuns.ShouldBe(1, "the join must fire exactly once on resume — recovered from completed marks");
        var done = await resumed.GetStateAsync("join-crash");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.Values["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("gate");
    }

    [Fact(DisplayName = "Given empty join sources, when AddEdge, then graph.invalid_edge")]
    public void RejectEmptyJoinSources()
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("gate", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()));

        var exception = Should.Throw<GraphCompileException>(() => builder.AddEdge([], "gate"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidEdge);
    }

    [Fact(DisplayName = "Given duplicate join sources, when AddEdge, then graph.invalid_edge")]
    public void RejectDuplicateJoinSources()
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("gate", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()));

        var exception = Should.Throw<GraphCompileException>(() => builder.AddEdge(["a", "a"], "gate"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidEdge);
    }

    [Theory(DisplayName = "Given a sentinel join endpoint, when AddEdge, then graph.unknown_endpoint immediately")]
    [InlineData(GraphConstants.Start, "gate")]
    [InlineData("a", GraphConstants.End)]
    public void RejectSentinelJoinEndpointsAtAddEdge(string source, string target)
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("gate", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End)
            .AddEdge("gate", GraphConstants.End);

        var exception = Should.Throw<GraphCompileException>(() => builder.AddEdge([source], target));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphUnknownEndpoint);
    }

    [Theory(DisplayName = "Given an unknown join endpoint, when Compile, then graph.unknown_endpoint")]
    [InlineData("a", "ghost")]
    [InlineData("ghost", "gate")]
    public void RejectUnknownJoinEndpointsAtCompile(string source, string target)
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("gate", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End)
            .AddEdge("gate", GraphConstants.End)
            .AddEdge([source], target);

        var exception = Should.Throw<GraphCompileException>(() => builder.Compile(new InMemoryCheckpointer()));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphUnknownEndpoint);
    }

    [Fact(DisplayName = "Given a join target that also has a regular incoming edge (either order), when AddEdge, then graph.invalid_edge")]
    public void RejectMixedInEdgesOnJoinTarget()
    {
        var joinFirst = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("b", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("c", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("gate", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(["a", "b"], "gate");

        var exception = Should.Throw<GraphCompileException>(() => joinFirst.AddEdge("c", "gate"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidEdge);

        var staticFirst = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("b", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("c", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("gate", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge("c", "gate");

        var exception2 = Should.Throw<GraphCompileException>(() => staticFirst.AddEdge(["a", "b"], "gate"));

        exception2.Code.ShouldBe(VolutaErrorCodes.GraphInvalidEdge);
    }

    [Fact(DisplayName = "Given two joins on the same target, when AddEdge, then graph.invalid_edge")]
    public void RejectDuplicateJoinTarget()
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("b", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("gate", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()));

        builder.AddEdge(["a"], "gate");

        var exception = Should.Throw<GraphCompileException>(() => builder.AddEdge(["b"], "gate"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidEdge);
    }
}
