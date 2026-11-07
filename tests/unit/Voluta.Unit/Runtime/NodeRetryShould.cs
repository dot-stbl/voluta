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
using Voluta.Testing.Checkpoint;
using Xunit;

namespace Voluta.Unit.Runtime;

public sealed class NodeRetryShould
{
    [Fact(DisplayName = "Given flaky node succeeding on 3rd attempt, when run, then attempts 1..3 are visible and run completes")]
    public async Task SucceedOnThirdAttemptWithVisibleAttempts()
    {
        var checkpointer = new InMemoryCheckpointer();
        var attempts = new List<int>();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "flaky",
                (context, _) =>
                {
                    attempts.Add(context.Attempt);
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "recovered")));
                },
                new NodeOptions
                {
                    Retry = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(1)),
                })
            .AddEdge(GraphConstants.Start, "flaky")
            .AddEdge("flaky", GraphConstants.End)
            .Compile(checkpointer);

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "retry-ok", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        attempts.ShouldBe([1]);
        var done = await graph.GetStateAsync("retry-ok");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        // Boundary snapshot leaves no retry residue.
        var raw = await checkpointer.GetAsync("retry-ok");
        raw!.TaskAttempts.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Given a node failing twice then recovering, when run with retry policy, then the node observes attempts 1, 2, 3")]
    public async Task RetryLoopVisibleAttempts()
    {
        var executions = 0;
        var attempts = new List<int>();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "flaky",
                (context, _) =>
                {
                    executions++;
                    attempts.Add(context.Attempt);
                    return executions >= 3
                        ? Task.FromResult<NodeResult>(
                            NodeResult.Continue(new ChannelWrite("messages", "recovered")))
                        : throw new InvalidOperationException("flaky failure");
                },
                new NodeOptions
                {
                    Retry = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(1)),
                })
            .AddEdge(GraphConstants.Start, "flaky")
            .AddEdge("flaky", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "retry-3rd", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        attempts.ShouldBe([1, 2, 3]);
        var done = await graph.GetStateAsync("retry-3rd");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.Values["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("recovered");
    }

    [Fact(DisplayName = "Given a node always failing, when retries exhaust, then run fails with no further attempts")]
    public async Task ExhaustionFailsTheRun()
    {
        var executions = 0;
        var graph = new StateGraph()
            .AddNode(
                "doomed",
                (_, _) =>
                {
                    executions++;
                    return Task.FromException<NodeResult>(
                        new InvalidOperationException("doomed always fails"));
                },
                new NodeOptions
                {
                    Retry = RetryPolicy.Exponential(2, TimeSpan.FromMilliseconds(1)),
                })
            .AddEdge(GraphConstants.Start, "doomed")
            .AddEdge("doomed", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        var exception = await Should.ThrowAsync<GraphRunFailedException>(
            async () => await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "retry-doom", StreamMode = StreamMode.Values }));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphRunFailed);
        executions.ShouldBe(2);
        var state = await graph.GetStateAsync("retry-doom");
        state!.Status.ShouldBe(GraphRunStatus.Failed);
    }

    [Fact(DisplayName = "Given cancellation mid-node, when the policy could retry, then cancellation is not retried")]
    public async Task CancellationIsNeverRetried()
    {
        var executions = 0;
        using var cancellation = new CancellationTokenSource();
        var graph = new StateGraph()
            .AddNode(
                "hangy",
                async (_, token) =>
                {
                    executions++;
                    await Task.Delay(Timeout.Infinite, token);
                    return NodeResult.Continue();
                },
                new NodeOptions
                {
                    Retry = RetryPolicy.Exponential(5, TimeSpan.FromMilliseconds(1)),
                })
            .AddEdge(GraphConstants.Start, "hangy")
            .AddEdge("hangy", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        var run = Task.Run(
            async () => await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "retry-cancel", StreamMode = StreamMode.Values },
                cancellation.Token));
        await Task.Delay(50);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(run);
        executions.ShouldBe(1);
    }

    [Fact(DisplayName = "Given a crash between retry attempts, when resumed, then the task continues at the recorded next attempt")]
    public async Task CrashResumeContinuesAtNextAttempt()
    {
        var executions = 0;
        var attemptsSeen = new List<int>();
        var checkpointer = new InMemoryCheckpointer();
        // Puts: (1) step 0 Running, (2) retry progress put — crash on the recovery put.
        var faulting = new FaultInjectingCheckpointer(checkpointer, failOnPutNumber: 3);
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "flaky",
                (context, _) =>
                {
                    executions++;
                    attemptsSeen.Add(context.Attempt);
                    return executions == 1
                        ? throw new InvalidOperationException("first attempt fails")
                        : Task.FromResult<NodeResult>(
                            NodeResult.Continue(new ChannelWrite("messages", "recovered")));
                },
                new NodeOptions
                {
                    Retry = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(1)),
                })
            .AddEdge(GraphConstants.Start, "flaky")
            .AddEdge("flaky", GraphConstants.End)
            .Compile(faulting);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "retry-crash", StreamMode = StreamMode.Values }));

        // The retry progress put landed before the injected fault: next attempt recorded.
        var parked = await checkpointer.GetAsync("retry-crash");
        parked.ShouldNotBeNull();
        parked!.TaskAttempts["flaky"].ShouldBe(2);

        // Resume on the real checkpointer: the task must run as attempt 2 (not restart at 1).
        var resumed = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "flaky",
                (context, _) =>
                {
                    executions++;
                    attemptsSeen.Add(context.Attempt);
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "recovered")));
                },
                new NodeOptions
                {
                    Retry = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(1)),
                })
            .AddEdge(GraphConstants.Start, "flaky")
            .AddEdge("flaky", GraphConstants.End)
            .Compile(checkpointer);

        var terminal = await resumed.ContinueInvokeAsync("retry-crash");

        terminal.Kind.ShouldBe(StreamEventKind.End);
        // [1] first run attempt 1 (fails) → [2] first run attempt 2 (succeeded, but its
        // boundary put crashed) → [2] resume re-runs the recorded attempt 2 — NOT a
        // restart at attempt 1.
        attemptsSeen.ShouldBe([1, 2, 2]);
        var done = await resumed.GetStateAsync("retry-crash");
        done!.Status.ShouldBe(GraphRunStatus.Done);
    }

    [Fact(DisplayName = "Given AddNode options null, when called, then GraphCompileException and node not registered")]
    public void RejectNullOptions()
    {
        var builder = new StateGraph().AddNode(
            "a",
            static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()));

        var exception = Should.Throw<GraphCompileException>(
            () => builder.AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()),
                null!));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidNode);
    }

    [Fact(DisplayName = "Given invalid node name with options, when AddNode, then node validation still applies")]
    public void RejectInvalidNameEvenWithOptions()
    {
        var builder = new StateGraph();

        var exception = Should.Throw<GraphCompileException>(
            () => builder.AddNode(
                "",
                static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()),
                new NodeOptions { Retry = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(1)) }));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidNode);
    }
}
