using Shouldly;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Checkpoint;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Xunit;

namespace Voluta.Unit.Checkpoint;

/// <summary>
///     v050 additive snapshot fields (CompletedTaskIds / TaskAttempts / JoinArrivals /
///     StopReason) must survive every snapshot copy site — InMemory clone, cancel park,
///     and fork — otherwise InMemory Get silently drops them and crash-resume tests of
///     retry/join/guards break.
/// </summary>
public sealed class SnapshotCopierRoundtripShould
{
    private static CheckpointSnapshot BuildProgressSnapshot(string threadId)
    {
        return new CheckpointSnapshot
        {
            ThreadId = threadId,
            Step = 2,
            Status = GraphRunStatus.Interrupted,
            ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(StringComparer.Ordinal),
            VersionsSeen = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal),
            CompletedTaskIds = ["tools", "agent"],
            TaskAttempts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["agent"] = 3,
                ["tools"] = 1,
            },
            JoinArrivals = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["gate"] = ["left"],
            },
            StopReason = RunStopReason.BudgetExhausted,
            PendingInterrupts = [],
        };
    }

    private static void AssertCarried(CheckpointSnapshot snapshot)
    {
        snapshot.CompletedTaskIds.ShouldBe(["tools", "agent"]);
        snapshot.TaskAttempts["agent"].ShouldBe(3);
        snapshot.TaskAttempts["tools"].ShouldBe(1);
        snapshot.JoinArrivals["gate"].ShouldBe(["left"]);
        snapshot.StopReason.ShouldBe(RunStopReason.BudgetExhausted);
    }

    [Fact(DisplayName = "Given progress snapshot stored, when InMemory Get, then clone carries all new fields")]
    public async Task InMemoryCloneCarriesFields()
    {
        var checkpointer = new InMemoryCheckpointer();
        await checkpointer.PutAsync(BuildProgressSnapshot("clone-thread"));

        var loaded = await checkpointer.GetAsync("clone-thread");

        loaded.ShouldNotBeNull();
        AssertCarried(loaded!);
    }

    [Fact(DisplayName = "Given progress snapshot stored, when CancelAsync parks the thread, then cancel copy carries all new fields")]
    public async Task CancelCarriesFields()
    {
        var checkpointer = new InMemoryCheckpointer();
        await checkpointer.PutAsync(BuildProgressSnapshot("cancel-thread"));
        var graph = new StateGraph()
            .AddNode(
                "noop",
                static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "noop")
            .AddEdge("noop", GraphConstants.End)
            .Compile(checkpointer);

        var cancelled = await graph.CancelAsync("cancel-thread", "ops stop");

        cancelled.StopReason.ShouldBe(RunStopReason.BudgetExhausted);
        var loaded = await checkpointer.GetAsync("cancel-thread");
        loaded.ShouldNotBeNull();
        AssertCarried(loaded!);
    }

    [Fact(DisplayName = "Given progress snapshot stored, when ForkAsync copies the step, then fork carries all new fields")]
    public async Task ForkCarriesFields()
    {
        var checkpointer = new InMemoryCheckpointer();
        await checkpointer.PutAsync(BuildProgressSnapshot("fork-src"));

        var graph = new StateGraph()
            .AddNode(
                "noop",
                static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "noop")
            .AddEdge("noop", GraphConstants.End)
            .Compile(checkpointer);
        await graph.ForkAsync("fork-src", 2, "fork-child");

        var loaded = await checkpointer.GetAsync("fork-child");
        loaded.ShouldNotBeNull();
        AssertCarried(loaded!);
    }
}
