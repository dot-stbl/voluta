using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;

namespace Voluta.Runtime.Engine.Support;

/// <summary>
///     Checkpoint snapshot builders for the run engine.
/// </summary>
internal static class RunEngineSnapshots
{
    public static CheckpointSnapshot Build(
        string threadId,
        long step,
        GraphRunStatus status,
        ChannelStore store,
        string? lastNode,
        IReadOnlyList<string> nextNodes,
        IReadOnlyList<PendingSend> pendingSends,
        object? interruptPayload,
        IReadOnlyDictionary<string, object?>? channelValues = null,
        IReadOnlyList<PendingInterrupt>? pendingInterrupts = null,
        (string? GraphId, string? GraphVersion) identity = default,
        IReadOnlyList<PendingWrite>? pendingWrites = null,
        IReadOnlyList<string>? completedTaskIds = null,
        IReadOnlyDictionary<string, int>? taskAttempts = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? joinArrivals = null,
        RunStopReason stopReason = RunStopReason.None)
    {
        var versions = store.Versions;
        var channelVersions = new Dictionary<string, long>(versions.Count, StringComparer.Ordinal);
        foreach (var (name, version) in versions)
        {
            channelVersions[name] = version;
        }

        return new CheckpointSnapshot
        {
            ThreadId = threadId,
            Step = step,
            Status = status,
            ChannelValues = channelValues ?? store.SnapshotValues(),
            ChannelVersions = channelVersions,
            VersionsSeen = store.CloneVersionsSeen(),
            PendingWrites = pendingWrites is { Count: > 0 } ? [.. pendingWrites] : [],
            PendingSends = [.. pendingSends],
            LastNode = lastNode,
            NextNodes = [.. nextNodes],
            InterruptPayload = interruptPayload,
            PendingInterrupts = pendingInterrupts is { Count: > 0 }
                ? [.. pendingInterrupts]
                : [],
            CompletedTaskIds = completedTaskIds is { Count: > 0 } ? [.. completedTaskIds] : [],
            TaskAttempts = taskAttempts is { Count: > 0 }
                ? new Dictionary<string, int>(taskAttempts, StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal),
            JoinArrivals = joinArrivals is { Count: > 0 }
                ? new Dictionary<string, IReadOnlyList<string>>(joinArrivals, StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            StopReason = stopReason,
            GraphId = identity.GraphId,
            GraphVersion = identity.GraphVersion,
        };
    }
}
