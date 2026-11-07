using Shouldly;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Testing.Conformance;
using Xunit;

namespace Voluta.Checkpoints.Redis.Unit;

public sealed class RedisCheckpointerConformanceShould
{
    [Fact(DisplayName = "Given Redis checkpointer over the in-memory store, when conformance suite runs, then all mandatory scenarios pass")]
    public async Task PassesConformance()
    {
        await CheckpointerConformance.RunAllAsync(CreateCheckpointer());
    }

    [Fact(DisplayName = "Given puts across steps, when Get, then latest step returns")]
    public async Task GetReturnsLatestStep()
    {
        var checkpointer = CreateCheckpointer();

        await checkpointer.PutAsync(Snapshot("t1", step: 1));
        await checkpointer.PutAsync(Snapshot("t1", step: 4));
        await checkpointer.PutAsync(Snapshot("t1", step: 2));

        var latest = await checkpointer.GetAsync("t1");

        latest.ShouldNotBeNull();
        latest.Step.ShouldBe(4);
    }

    [Fact(DisplayName = "Given no checkpoints, when Get, then null")]
    public async Task GetMissingReturnsNull()
    {
        var checkpointer = CreateCheckpointer();

        (await checkpointer.GetAsync("missing")).ShouldBeNull();
    }

    [Fact(DisplayName = "Given puts across steps, when List, then ordered by step")]
    public async Task ListOrdersByStep()
    {
        var checkpointer = CreateCheckpointer();

        await checkpointer.PutAsync(Snapshot("t1", step: 3));
        await checkpointer.PutAsync(Snapshot("t1", step: 1));
        await checkpointer.PutAsync(Snapshot("t1", step: 2));

        var history = await checkpointer.ListAsync("t1");

        history.Select(static snapshot => snapshot.Step).ShouldBe([1, 2, 3]);
    }

    [Fact(DisplayName = "Given same step re-put, when List, then single entry survives")]
    public async Task SameStepReputKeepsSingleEntry()
    {
        var checkpointer = CreateCheckpointer();

        await checkpointer.PutAsync(Snapshot("t1", step: 2, lastNode: "a"));
        await checkpointer.PutAsync(Snapshot("t1", step: 2, lastNode: "b"));

        var history = await checkpointer.ListAsync("t1");

        history.Count.ShouldBe(1);
        history[0].LastNode.ShouldBe("b");
    }

    [Fact(DisplayName = "Given roundtrip with values, when Get, then channel values preserved")]
    public async Task RoundtripPreservesChannelValues()
    {
        var checkpointer = CreateCheckpointer();
        var snapshot = Snapshot(
            "t-rt",
            step: 7,
            status: GraphRunStatus.Interrupted,
            channelValues: new Dictionary<string, object?>
            {
                ["goal"] = "pay invoice",
                ["amount"] = 4200L,
                ["approved"] = false,
                ["tags"] = new List<object?> { "a", "b" },
            },
            interruptPayload: "dual-control required");

        await checkpointer.PutAsync(snapshot);
        var loaded = await checkpointer.GetAsync("t-rt");

        loaded.ShouldNotBeNull();
        loaded.Step.ShouldBe(7);
        loaded.Status.ShouldBe(GraphRunStatus.Interrupted);
        loaded.ChannelValues["goal"].ShouldBe("pay invoice");
        loaded.ChannelValues["amount"].ShouldBe(4200L);
        loaded.ChannelValues["approved"].ShouldBe(false);
        loaded.ChannelValues["tags"].ShouldNotBeNull();
        loaded.InterruptPayload.ShouldBe("dual-control required");
    }

    private static RedisCheckpointer CreateCheckpointer()
    {
        return new RedisCheckpointer(
            new InMemoryRedisCheckpointStore(),
            new RedisCheckpointerOptions());
    }

    private static CheckpointSnapshot Snapshot(
        string threadId,
        long step = 1,
        GraphRunStatus status = GraphRunStatus.Running,
        Dictionary<string, object?>? channelValues = null,
        object? interruptPayload = null,
        string? lastNode = null)
    {
        return new CheckpointSnapshot
        {
            ThreadId = threadId,
            Step = step,
            Status = status,
            ChannelValues = channelValues ?? new Dictionary<string, object?> { ["seed"] = "v" },
            InterruptPayload = interruptPayload,
            LastNode = lastNode,
            NextNodes = [],
        };
    }
}
