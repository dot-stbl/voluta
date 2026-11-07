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
        (string? GraphId, string? GraphVersion) identity = default)
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
            PendingWrites = [],
            PendingSends = [.. pendingSends],
            LastNode = lastNode,
            NextNodes = [.. nextNodes],
            InterruptPayload = interruptPayload,
            PendingInterrupts = pendingInterrupts is { Count: > 0 }
                ? [.. pendingInterrupts]
                : [],
            GraphId = identity.GraphId,
            GraphVersion = identity.GraphVersion,
        };
    }
}
