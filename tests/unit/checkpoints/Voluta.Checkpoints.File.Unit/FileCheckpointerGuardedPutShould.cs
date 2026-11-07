using Shouldly;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Runtime;
using Voluta.Checkpoints.File;
using Xunit;

namespace Voluta.Checkpoints.File.Unit;

public sealed class FileCheckpointerGuardedPutShould
{
    [Fact(DisplayName = "Given a guarded put whose expected parent no longer matches, when put, then throws checkpoint.conflict and no document is written")]
    public async Task StaleParentConflicts()
    {
        using var temp = new TempRoot();
        var checkpointer = new FileCheckpointer(temp.Root);
        await checkpointer.PutAsync(Snapshot("file-guard-1", step: 1));
        await checkpointer.PutAsync(Snapshot("file-guard-1", step: 2));

        var exception = await Should.ThrowAsync<CheckpointStoreException>(async () =>
        {
            await checkpointer.PutAsync(
                Snapshot("file-guard-1", step: 2),
                expectedParentStep: 1,
                unitOfWork: null,
                cancellationToken: default);
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);
        var latest = await checkpointer.GetAsync("file-guard-1");
        latest!.Step.ShouldBe(2);
    }

    [Fact(DisplayName = "Given a guarded put with the matching parent, when put, then succeeds including same-step rewrite")]
    public async Task MatchingParentAccepts()
    {
        using var temp = new TempRoot();
        var checkpointer = new FileCheckpointer(temp.Root);
        await checkpointer.PutAsync(Snapshot("file-guard-2", step: 4));

        await checkpointer.PutAsync(
            Snapshot("file-guard-2", step: 5),
            expectedParentStep: 4,
            unitOfWork: null,
            cancellationToken: default);
        await checkpointer.PutAsync(
            Snapshot("file-guard-2", step: 5, status: GraphRunStatus.Interrupted),
            expectedParentStep: 5,
            unitOfWork: null,
            cancellationToken: default);

        var latest = await checkpointer.GetAsync("file-guard-2");
        latest!.Step.ShouldBe(5);
        latest.Status.ShouldBe(GraphRunStatus.Interrupted);
    }

    private static CheckpointSnapshot Snapshot(string threadId, long step, GraphRunStatus? status = null)
    {
        return new CheckpointSnapshot
        {
            ThreadId = threadId,
            Step = step,
            Status = status ?? GraphRunStatus.Running,
            ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(StringComparer.Ordinal),
            VersionsSeen = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal),
        };
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "voluta-file-guard-" + Guid.NewGuid().ToString("N"));
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
