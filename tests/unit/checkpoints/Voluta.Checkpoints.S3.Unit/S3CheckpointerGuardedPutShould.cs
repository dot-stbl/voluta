using Amazon.S3;
using Shouldly;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Runtime;
using Voluta.Checkpoints.S3;
using Xunit;

namespace Voluta.Checkpoints.S3.Unit;

public sealed class S3CheckpointerGuardedPutShould
{
    private static S3Checkpointer CreateCheckpointer(IAmazonS3 client)
    {
        return new S3Checkpointer(
            client,
            new S3CheckpointerOptions
            {
                BucketName = "voluta-guard",
                KeyPrefix = "checkpoints",
            });
    }

    private static CheckpointSnapshot Snapshot(string threadId, long step, GraphRunStatus? status = null)
    {
        return new CheckpointSnapshot
        {
            ThreadId = threadId,
            Step = step,
            Status = status ?? GraphRunStatus.Running,
            GraphId = "comuki-graph",
            GraphVersion = "v1",
            ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(StringComparer.Ordinal),
            VersionsSeen = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal),
        };
    }

    [Fact(DisplayName = "Given a guarded put whose expected parent no longer matches, when put, then throws checkpoint.conflict and the losing snapshot is not persisted")]
    public async Task StaleParentConflicts()
    {
        var checkpointer = CreateCheckpointer(InMemoryAmazonS3.Create());
        await checkpointer.PutAsync(Snapshot("s3-guard-1", step: 1));
        await checkpointer.PutAsync(Snapshot("s3-guard-1", step: 2));

        var exception = await Should.ThrowAsync<CheckpointStoreException>(async () =>
        {
            await checkpointer.PutAsync(
                Snapshot("s3-guard-1", step: 2),
                expectedParentStep: 1,
                unitOfWork: null,
                cancellationToken: default);
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);
        var latest = await checkpointer.GetAsync("s3-guard-1");
        latest!.Step.ShouldBe(2);
        latest.Status.ShouldBe(GraphRunStatus.Running);
    }

    [Fact(DisplayName = "Given a guarded put with the matching parent, when put, then succeeds and the pointer tracks the latest step")]
    public async Task MatchingParentAccepts()
    {
        var checkpointer = CreateCheckpointer(InMemoryAmazonS3.Create());
        await checkpointer.PutAsync(Snapshot("s3-guard-2", step: 4));

        await checkpointer.PutAsync(
            Snapshot("s3-guard-2", step: 5),
            expectedParentStep: 4,
            unitOfWork: null,
            cancellationToken: default);

        var latest = await checkpointer.GetAsync("s3-guard-2");
        latest!.Step.ShouldBe(5);
        latest.GraphId.ShouldBe("comuki-graph");
    }

    [Fact(DisplayName = "Given two sequential guarded puts racing for the same parent, when the first lands, then the second loses the pointer race with checkpoint.conflict")]
    public async Task PointerRaceLosesSecondWriter()
    {
        var checkpointer = CreateCheckpointer(InMemoryAmazonS3.Create());
        await checkpointer.PutAsync(Snapshot("s3-guard-3", step: 10));

        // Both writers observed parent step 10; the first lands step 11.
        await checkpointer.PutAsync(
            Snapshot("s3-guard-3", step: 11),
            expectedParentStep: 10,
            unitOfWork: null,
            cancellationToken: default);

        var exception = await Should.ThrowAsync<CheckpointStoreException>(async () =>
        {
            await checkpointer.PutAsync(
                Snapshot("s3-guard-3", step: 11),
                expectedParentStep: 10,
                unitOfWork: null,
                cancellationToken: default);
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);
        var latest = await checkpointer.GetAsync("s3-guard-3");
        latest!.Step.ShouldBe(11);
        latest.Status.ShouldBe(GraphRunStatus.Running);
    }

    [Fact(DisplayName = "Given a guarded same-step rewrite with the matching parent, when put, then succeeds and replaces the step's status")]
    public async Task GuardedSameStepRewriteAccepts()
    {
        var checkpointer = CreateCheckpointer(InMemoryAmazonS3.Create());
        await checkpointer.PutAsync(Snapshot("s3-guard-4", step: 1));
        await checkpointer.PutAsync(
            Snapshot("s3-guard-4", step: 2),
            expectedParentStep: 1,
            unitOfWork: null,
            cancellationToken: default);

        await checkpointer.PutAsync(
            Snapshot("s3-guard-4", step: 2, status: GraphRunStatus.Done),
            expectedParentStep: 2,
            unitOfWork: null,
            cancellationToken: default);

        var latest = await checkpointer.GetAsync("s3-guard-4");
        latest!.Status.ShouldBe(GraphRunStatus.Done);
        latest.Step.ShouldBe(2);
    }

    [Fact(DisplayName = "Given a guarded put on an empty thread with a non-null expected parent, when put, then conflicts (fail closed)")]
    public async Task GuardedPutOnMissingThreadConflicts()
    {
        var checkpointer = CreateCheckpointer(InMemoryAmazonS3.Create());

        var exception = await Should.ThrowAsync<CheckpointStoreException>(async () =>
        {
            await checkpointer.PutAsync(
                Snapshot("s3-guard-5", step: 0),
                expectedParentStep: 0,
                unitOfWork: null,
                cancellationToken: default);
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);
    }
}
