using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Testing.Checkpoint;
using Xunit;

namespace Voluta.Unit.Runtime;

/// <summary>
///     Per-task durable writes (v050): the engine puts a guarded progress snapshot after
///     every completed task of a superstep; a crash mid-superstep resumes applying the
///     recorded writes and re-executes only the unfinished tasks (A2 semantics intact).
/// </summary>
public sealed class PerTaskWritesShould
{
    [Fact(DisplayName = "Given two parallel tasks and a crash after the first progress put, when continued, then recorded writes apply and only the unfinished task re-runs")]
    public async Task CrashAfterFirstTaskResumeAppliesWritesAndSkipsCompleted()
    {
        var inner = new InMemoryCheckpointer();
        // Puts: (1) step 0 Running, (2) progress put for "fast" (lands), (3) progress put for "slow" → crash.
        var faulting = new FaultInjectingCheckpointer(inner, failOnPutNumber: 3);
        var fastExecutions = 0;
        var slowExecutions = 0;

        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "fast",
                (_, _) =>
                {
                    Interlocked.Increment(ref fastExecutions);
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "fast")));
                })
            .AddNode(
                "slow",
                async (_, _) =>
                {
                    Interlocked.Increment(ref slowExecutions);
                    // Long enough that "fast"'s progress put is flushed first.
                    await Task.Delay(50);
                    return NodeResult.Continue(new ChannelWrite("messages", "slow"));
                })
            .AddEdge(GraphConstants.Start, "fast")
            .AddEdge(GraphConstants.Start, "slow")
            .AddEdge("fast", GraphConstants.End)
            .AddEdge("slow", GraphConstants.End)
            .Compile(faulting);

        // "fast" completes and flushes its progress put; "slow"'s progress put faults.
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "ptw-crash", StreamMode = StreamMode.Values }));

        var parked = await inner.GetAsync("ptw-crash");
        parked.ShouldNotBeNull();
        parked!.CompletedTaskIds.ShouldBe(["fast"]);
        parked.PendingWrites.Select(static write => write.Value).ShouldBe(["fast"]);
        parked.NextNodes.ShouldBe(["fast", "slow"]);

        // Continue on the surviving store: recorded writes apply, only "slow" re-runs.
        var resumed = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "fast",
                (_, _) =>
                {
                    Interlocked.Increment(ref fastExecutions);
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "fast")));
                })
            .AddNode(
                "slow",
                (_, _) =>
                {
                    Interlocked.Increment(ref slowExecutions);
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "slow")));
                })
            .AddEdge(GraphConstants.Start, "fast")
            .AddEdge(GraphConstants.Start, "slow")
            .AddEdge("fast", GraphConstants.End)
            .AddEdge("slow", GraphConstants.End)
            .Compile(inner);

        var terminal = await resumed.ContinueInvokeAsync("ptw-crash");

        terminal.Kind.ShouldBe(StreamEventKind.End);
        fastExecutions.ShouldBe(1, "the completed task must not re-execute");
        slowExecutions.ShouldBe(2, "the unfinished task re-executes once on resume");

        var done = await resumed.GetStateAsync("ptw-crash");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.Values["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("fast");
        messages.ShouldContain("slow");
    }

    [Fact(DisplayName = "Given a clean superstep, when it completes, then the boundary snapshot leaves no per-task residue")]
    public async Task CleanSuperstepLeavesNoResidue()
    {
        var inner = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "a"))))
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "b"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge(GraphConstants.Start, "b")
            .AddEdge("a", GraphConstants.End)
            .AddEdge("b", GraphConstants.End)
            .Compile(inner);

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "ptw-clean", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await inner.GetAsync("ptw-clean");
        done.ShouldNotBeNull();
        done!.Status.ShouldBe(GraphRunStatus.Done);
        done.CompletedTaskIds.ShouldBeEmpty();
        done.TaskAttempts.ShouldBeEmpty();
        done.JoinArrivals.ShouldBeEmpty();
        done.PendingWrites.ShouldBeEmpty();
        done.StopReason.ShouldBe(Voluta.Abstractions.Runtime.RunStopReason.None);
    }

    [Fact(DisplayName = "Given a mid-superstep interrupt with a completed sibling, when resumed, then sibling writes apply and only the interrupter re-runs")]
    public async Task InterruptSnapshotCarriesProgressBuffer()
    {
        var inner = new InMemoryCheckpointer();
        var siblingExecutions = 0;
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "sibling",
                (_, _) =>
                {
                    Interlocked.Increment(ref siblingExecutions);
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "sibling")));
                })
            .AddNode(
                "gate",
                static (context, _) => context.IsResume
                    ? Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "resumed")))
                    : Task.FromResult<NodeResult>(NodeResult.Interrupt("need-answer")))
            .AddEdge(GraphConstants.Start, "sibling")
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("sibling", GraphConstants.End)
            .AddEdge("gate", GraphConstants.End)
            .Compile(inner);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "ptw-int", StreamMode = StreamMode.Events });

        var interrupted = await inner.GetAsync("ptw-int");
        interrupted.ShouldNotBeNull();
        interrupted!.Status.ShouldBe(Voluta.Abstractions.Runtime.GraphRunStatus.Interrupted);
        interrupted.CompletedTaskIds.ShouldBe(["sibling"]);
        interrupted.PendingWrites.Select(static write => write.Value).ShouldBe(["sibling"]);

        var terminal = await graph.ResumeInvokeAsync("ptw-int", Voluta.Abstractions.Runtime.Command.Approve("ok"));

        terminal.Kind.ShouldBe(StreamEventKind.End);
        siblingExecutions.ShouldBe(1, "the completed sibling must not re-execute on resume");
        var done = await graph.GetStateAsync("ptw-int");
        done!.Status.ShouldBe(Voluta.Abstractions.Runtime.GraphRunStatus.Done);
        var messages = done.Values["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("sibling");
        messages.ShouldContain("resumed");
    }
}
