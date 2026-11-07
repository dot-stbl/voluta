using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;
using Xunit;

namespace Voluta.Unit.Runtime;

public sealed class RunGuardShould
{
    [Fact(DisplayName = "Given a past absolute deadline, when run, then parks Interrupted with DeadlineExceeded; continue completes")]
    public async Task DeadlineGuardParksAndContinueWorks()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(
                NodeResult.Continue(new ChannelWrite("messages", "a"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End)
            .Compile(
                checkpointer,
                new CompileOptions
                {
                    RunGuards = [RunGuards.Deadline(DateTimeOffset.UtcNow.AddMinutes(-1))],
                });

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "guard-deadline", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.Interrupt);
        var parked = await graph.GetStateAsync("guard-deadline");
        parked!.Status.ShouldBe(GraphRunStatus.Interrupted, "a guard stop must never become Failed");
        parked.StopReason.ShouldBe(RunStopReason.DeadlineExceeded);
        parked.PendingInterrupts.ShouldBeEmpty();
        parked.NextNodes.ShouldBe(["a"]);

        var afterContinue = await graph.ContinueInvokeAsync("guard-deadline");
        afterContinue.Kind.ShouldBe(StreamEventKind.Interrupt, "the deadline still holds on continue");
        var parkedAgain = await graph.GetStateAsync("guard-deadline");
        parkedAgain!.StopReason.ShouldBe(RunStopReason.DeadlineExceeded);
    }

    [Fact(DisplayName = "Given a budget predicate that trips from the start, when run, then parks Interrupted with BudgetExhausted — never Failed")]
    public async Task BudgetGuardParks()
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
                new CompileOptions
                {
                    RunGuards =
                    [
                        RunGuards.Budget(static _ => true),
                    ],
                });

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "guard-budget", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.Interrupt);
        var parked = await graph.GetStateAsync("guard-budget");
        parked!.Status.ShouldBe(GraphRunStatus.Interrupted, "a guard stop must never become Failed");
        parked.StopReason.ShouldBe(RunStopReason.BudgetExhausted);
        parked.PendingInterrupts.ShouldBeEmpty();
        parked.NextNodes.ShouldBe(["a"], "the guard preserves the would-be ready set");
    }

    [Fact(DisplayName = "Given guards that never trip, when run, then completes normally (guards are transparent)")]
    public async Task TransparentWhenNotTripped()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(
                NodeResult.Continue(new ChannelWrite("messages", "a"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End)
            .Compile(
                checkpointer,
                new CompileOptions
                {
                    RunGuards =
                    [
                        RunGuards.Deadline(DateTimeOffset.UtcNow.AddHours(1)),
                        RunGuards.Budget(static _ => false),
                    ],
                });

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "guard-clear", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await graph.GetStateAsync("guard-clear");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        done.StopReason.ShouldBe(RunStopReason.None);
    }

    [Fact(DisplayName = "Given a guard stop on a mid-run thread, when continued after the guard passes, then StopReason clears and the run completes")]
    public async Task ContinueClearsStopReason()
    {
        var exhausted = true;
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
                new CompileOptions
                {
                    RunGuards = [RunGuards.Budget(_ => exhausted)],
                });

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "guard-resume", StreamMode = StreamMode.Values });
        terminal.Kind.ShouldBe(StreamEventKind.Interrupt);
        var parked = await graph.GetStateAsync("guard-resume");
        parked!.StopReason.ShouldBe(RunStopReason.BudgetExhausted);
        parked.NextNodes.ShouldBe(["a"]);

        exhausted = false;
        var resumed = await graph.ContinueInvokeAsync("guard-resume");
        resumed.Kind.ShouldBe(StreamEventKind.End);
        var done = await graph.GetStateAsync("guard-resume");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        done.StopReason.ShouldBe(RunStopReason.None, "the next commit clears the stop reason");
        var messages = done.Values["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("a");
        messages.ShouldContain("b");
    }
}
