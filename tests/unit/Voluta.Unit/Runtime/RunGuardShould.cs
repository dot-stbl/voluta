using Microsoft.Extensions.Time.Testing;
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

    [Fact(DisplayName = "Given an absolute deadline guard on a FakeTimeProvider, when time advances to exactly the deadline, then the guard trips precisely at the boundary")]
    public void AbsoluteDeadlineTripsExactlyOnDeadline()
    {
        var clock = new FakeTimeProvider();
        var deadline = clock.GetUtcNow().AddMinutes(10);
        var guard = RunGuards.Deadline(deadline, clock);
        var context = new RunGuardContext { NextStep = 1, CompletedSteps = 0, StartedAt = clock.GetUtcNow() };

        guard.Evaluate(context).Action.ShouldBe(RunGuardAction.Continue, "time has not reached the deadline yet");

        clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        guard.Evaluate(context).Action.ShouldBe(RunGuardAction.Continue, "one tick before the deadline is still green");

        clock.Advance(TimeSpan.FromTicks(1));
        var verdict = guard.Evaluate(context);
        verdict.Action.ShouldBe(RunGuardAction.Stop, "the guard fires exactly at the deadline");
        verdict.Reason.ShouldBe(RunGuards.DeadlineExceeded);
    }

    [Fact(DisplayName = "Given a relative deadline on a FakeTimeProvider, when time advances from guard creation, then the guard stays green until the anchored deadline elapses")]
    public void RelativeDeadlineAnchorsAtCreationAndTripsAfterAdvance()
    {
        var clock = new FakeTimeProvider();
        var createdAt = clock.GetUtcNow();
        var guard = RunGuards.Deadline(TimeSpan.FromMinutes(5), clock);
        var context = new RunGuardContext { NextStep = 1, CompletedSteps = 0, StartedAt = createdAt };

        clock.Advance(TimeSpan.FromMinutes(4));
        guard.Evaluate(context).Action.ShouldBe(RunGuardAction.Continue, "relative deadline is measured from creation, not from evaluation");

        clock.Advance(TimeSpan.FromMinutes(1));
        var verdict = guard.Evaluate(context);
        verdict.Action.ShouldBe(RunGuardAction.Stop, "creation + 5 minutes has elapsed");
        verdict.Reason.ShouldBe(RunGuards.DeadlineExceeded);
    }

    [Fact(DisplayName = "Given a null time provider, when creating a deadline guard overload, then a domain ArgumentException names the parameter")]
    public void NullTimeProviderThrows()
    {
        var absoluteEx = Should.Throw<ArgumentException>(() => RunGuards.Deadline(DateTimeOffset.UtcNow, null!));
        absoluteEx.ParamName.ShouldBe("timeProvider");

        var relativeEx = Should.Throw<ArgumentException>(() => RunGuards.Deadline(TimeSpan.FromMinutes(1), null!));
        relativeEx.ParamName.ShouldBe("timeProvider");
    }

    [Fact(DisplayName = "Given a compiled graph whose guard reads a FakeTimeProvider, when time moves past the deadline between runs, then the next run parks Interrupted deterministically")]
    public async Task EngineHonorsInjectedTimeProvider()
    {
        var clock = new FakeTimeProvider();
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
                    RunGuards = [RunGuards.Deadline(clock.GetUtcNow().AddMinutes(1), clock)],
                });

        var first = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "guard-frozen", StreamMode = StreamMode.Values });
        first.Kind.ShouldBe(StreamEventKind.End, "the injected clock has not reached the deadline");
        var done = await graph.GetStateAsync("guard-frozen");
        done!.Status.ShouldBe(GraphRunStatus.Done);

        clock.Advance(TimeSpan.FromMinutes(2));

        var second = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "guard-moved", StreamMode = StreamMode.Values });
        second.Kind.ShouldBe(StreamEventKind.Interrupt, "the same guard instance now reads a past deadline");
        var parked = await graph.GetStateAsync("guard-moved");
        parked!.Status.ShouldBe(GraphRunStatus.Interrupted);
        parked.StopReason.ShouldBe(RunStopReason.DeadlineExceeded);
        parked.PendingInterrupts.ShouldBeEmpty();
        parked.NextNodes.ShouldBe(["a"]);
    }
}
