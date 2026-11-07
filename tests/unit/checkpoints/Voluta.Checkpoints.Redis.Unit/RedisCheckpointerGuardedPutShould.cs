using Shouldly;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Runtime;
using Voluta.Checkpoints.Redis;
using Xunit;

namespace Voluta.Checkpoints.Redis.Unit;

public sealed class RedisCheckpointerGuardedPutShould
{
    private static readonly RedisCheckpointerOptions Options = new();

    private static CheckpointSnapshot Snapshot(string threadId, long step)
    {
        return new CheckpointSnapshot
        {
            ThreadId = threadId,
            Step = step,
            Status = GraphRunStatus.Running,
            ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(StringComparer.Ordinal),
            VersionsSeen = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal),
        };
    }

    [Fact(DisplayName = "Given a guarded put whose expected parent no longer matches, when put, then throws checkpoint.conflict without persisting")]
    public async Task StaleParentConflicts()
    {
        var store = new InMemoryRedisCheckpointStore();
        var checkpointer = new RedisCheckpointer(store, Options);
        await checkpointer.PutAsync(Snapshot("redis-guard-1", step: 1));
        await checkpointer.PutAsync(Snapshot("redis-guard-1", step: 2));

        var exception = await Should.ThrowAsync<CheckpointStoreException>(async () =>
        {
            await checkpointer.PutAsync(
                Snapshot("redis-guard-1", step: 2),
                expectedParentStep: 1,
                unitOfWork: null,
                cancellationToken: default);
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);
        var latest = await checkpointer.GetAsync("redis-guard-1");
        latest!.Step.ShouldBe(2);
        latest.Status.ShouldBe(GraphRunStatus.Running);
    }

    [Fact(DisplayName = "Given a guarded put with the matching parent, when put, then succeeds (including same-step rewrite)")]
    public async Task MatchingParentAccepts()
    {
        var store = new InMemoryRedisCheckpointStore();
        var checkpointer = new RedisCheckpointer(store, Options);
        await checkpointer.PutAsync(Snapshot("redis-guard-2", step: 5));

        await checkpointer.PutAsync(
            Snapshot("redis-guard-2", step: 6),
            expectedParentStep: 5,
            unitOfWork: null,
            cancellationToken: default);
        await checkpointer.PutAsync(
            Snapshot("redis-guard-2", step: 6),
            expectedParentStep: 6,
            unitOfWork: null,
            cancellationToken: default);

        var latest = await checkpointer.GetAsync("redis-guard-2");
        latest!.Step.ShouldBe(6);
        (await store.ListAsync(RedisCheckpointKeys.ThreadKey(Options.KeyPrefix, "redis-guard-2"), default))
            .Count.ShouldBe(2); // steps 5 and 6; no same-score leftovers
    }

    [Fact(DisplayName = "Given a guarded put on an empty thread with a non-null expected parent, when put, then conflicts (fail closed)")]
    public async Task GuardedPutOnMissingThreadConflicts()
    {
        var store = new InMemoryRedisCheckpointStore();
        var checkpointer = new RedisCheckpointer(store, Options);

        var exception = await Should.ThrowAsync<CheckpointStoreException>(async () =>
        {
            await checkpointer.PutAsync(
                Snapshot("redis-guard-3", step: 0),
                expectedParentStep: 0,
                unitOfWork: null,
                cancellationToken: default);
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);
    }
}
