using Shouldly;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Xunit;

namespace Voluta.Abstractions.Unit.Checkpoint;

public sealed class CheckpointSnapshotShould
{
    [Fact(DisplayName = "Given only ThreadId, when constructed, then defaults match C-shape empty snapshot")]
    public void UseCShapeDefaults()
    {
        var snapshot = new CheckpointSnapshot { ThreadId = "thread-1" };

        snapshot.FormatVersion.ShouldBe(1);
        snapshot.ThreadId.ShouldBe("thread-1");
        snapshot.Step.ShouldBe(0);
        snapshot.Status.ShouldBe(GraphRunStatus.Running);
        snapshot.ChannelValues.ShouldBeEmpty();
        snapshot.ChannelVersions.ShouldBeEmpty();
        snapshot.VersionsSeen.ShouldBeEmpty();
        snapshot.PendingWrites.ShouldBeEmpty();
        snapshot.LastNode.ShouldBeNull();
        snapshot.NextNodes.ShouldBeEmpty();
        snapshot.InterruptPayload.ShouldBeNull();
    }

    [Fact(DisplayName = "Given an empty snapshot, when constructed, then additive v1 fields default to null")]
    public void DefaultAdditiveIdentityAndCancelFieldsToNull()
    {
        var snapshot = new CheckpointSnapshot { ThreadId = "thread-1" };

        snapshot.GraphId.ShouldBeNull();
        snapshot.GraphVersion.ShouldBeNull();
        snapshot.CancelReason.ShouldBeNull();
        snapshot.FormatVersion.ShouldBe(1);
    }

    [Fact(DisplayName = "Given an empty snapshot, when constructed, then v050 progress fields default to empty / None")]
    public void DefaultProgressFieldsToEmpty()
    {
        var snapshot = new CheckpointSnapshot { ThreadId = "thread-1" };

        snapshot.CompletedTaskIds.ShouldBeEmpty();
        snapshot.TaskAttempts.ShouldBeEmpty();
        snapshot.JoinArrivals.ShouldBeEmpty();
        snapshot.StopReason.ShouldBe(RunStopReason.None);
    }

    [Fact(DisplayName = "Given v050 progress fields, when set, then init-only preserved")]
    public void PreserveProgressFields()
    {
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal) { ["agent"] = 3 };
        var arrivals = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["gate"] = ["left", "right"],
        };

        var snapshot = new CheckpointSnapshot
        {
            ThreadId = "thread-50",
            Status = GraphRunStatus.Interrupted,
            CompletedTaskIds = ["tools"],
            TaskAttempts = attempts,
            JoinArrivals = arrivals,
            StopReason = RunStopReason.BudgetExhausted,
        };

        snapshot.CompletedTaskIds.ShouldBe(["tools"]);
        snapshot.TaskAttempts.ShouldBeSameAs(attempts);
        snapshot.JoinArrivals.ShouldBeSameAs(arrivals);
        snapshot.StopReason.ShouldBe(RunStopReason.BudgetExhausted);
    }

    [Fact(DisplayName = "Given graph identity and cancel reason, when set, then fields are init-only preserved")]
    public void PreserveAdditiveIdentityAndCancelFields()
    {
        var snapshot = new CheckpointSnapshot
        {
            ThreadId = "thread-9",
            Status = GraphRunStatus.Cancelled,
            GraphId = "comuki-graph",
            GraphVersion = "2026.11.0",
            CancelReason = "operator requested",
        };

        snapshot.GraphId.ShouldBe("comuki-graph");
        snapshot.GraphVersion.ShouldBe("2026.11.0");
        snapshot.CancelReason.ShouldBe("operator requested");
    }

    [Fact(DisplayName = "Given full C-shape fields, when constructed, then all fields roundtrip by identity")]
    public void PreserveFullCShapeFields()
    {
        var channelValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["messages"] = new[] { "a", "b" },
            ["status"] = "ok",
        };
        var channelVersions = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["messages"] = 3,
            ["status"] = 1,
        };
        var versionsSeen = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal)
        {
            ["agent"] = new Dictionary<string, long>(StringComparer.Ordinal) { ["messages"] = 2 },
        };
        var pending = new List<PendingWrite>
        {
            new() { TaskId = "t1", ChannelName = "messages", Value = "c" },
        };
        var interrupt = new Dictionary<string, object?> { ["amount"] = 50 };

        var snapshot = new CheckpointSnapshot
        {
            FormatVersion = 1,
            ThreadId = "thread-42",
            Step = 7,
            Status = GraphRunStatus.Interrupted,
            ChannelValues = channelValues,
            ChannelVersions = channelVersions,
            VersionsSeen = versionsSeen,
            PendingWrites = pending,
            LastNode = "tools",
            NextNodes = ["agent"],
            InterruptPayload = interrupt,
        };

        snapshot.ThreadId.ShouldBe("thread-42");
        snapshot.Step.ShouldBe(7);
        snapshot.Status.ShouldBe(GraphRunStatus.Interrupted);
        snapshot.ChannelValues.ShouldBeSameAs(channelValues);
        snapshot.ChannelVersions.ShouldBeSameAs(channelVersions);
        snapshot.VersionsSeen.ShouldBeSameAs(versionsSeen);
        snapshot.PendingWrites.ShouldBeSameAs(pending);
        snapshot.LastNode.ShouldBe("tools");
        snapshot.NextNodes.ShouldBe(["agent"]);
        snapshot.InterruptPayload.ShouldBeSameAs(interrupt);
    }

    [Fact(DisplayName = "When GraphRunStatus is enumerated, then includes Running, Interrupted, Done, Failed, Cancelled")]
    public void ExposeTerminalAndActiveStatuses()
    {
        var names = Enum.GetNames<GraphRunStatus>();

        names.ShouldContain(nameof(GraphRunStatus.Running));
        names.ShouldContain(nameof(GraphRunStatus.Interrupted));
        names.ShouldContain(nameof(GraphRunStatus.Done));
        names.ShouldContain(nameof(GraphRunStatus.Failed));
        names.ShouldContain(nameof(GraphRunStatus.Cancelled));
    }
}
