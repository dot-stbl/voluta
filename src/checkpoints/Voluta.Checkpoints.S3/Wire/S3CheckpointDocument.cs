using System.Text.Json;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Serialization;

namespace Voluta.Checkpoints.S3.Wire;

/// <summary>
///     Wire shape for a single checkpoint JSON object (same C-shape as the file provider).
/// </summary>
internal sealed class S3CheckpointDocument
{
    public int FormatVersion { get; set; } = 1;

    public string ThreadId { get; set; } = "";

    public long Step { get; set; }

    public string Status { get; set; } = nameof(GraphRunStatus.Running);

    public Dictionary<string, JsonElement?> ChannelValues { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, long> ChannelVersions { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, Dictionary<string, long>> VersionsSeen { get; set; } = new(StringComparer.Ordinal);

    public List<S3PendingWriteDocument> PendingWrites { get; set; } = [];

    public List<S3PendingSendDocument> PendingSends { get; set; } = [];

    public List<S3PendingInterruptDocument> PendingInterrupts { get; set; } = [];

    public string? LastNode { get; set; }

    public List<string> NextNodes { get; set; } = [];

    public JsonElement? InterruptPayload { get; set; }

    public string? GraphId { get; set; }

    public string? GraphVersion { get; set; }

    public string? CancelReason { get; set; }

    /// <summary>v050 additive: tasks of the current step that completed (empty at boundaries).</summary>
    public List<string>? CompletedTaskIds { get; set; }

    /// <summary>v050 additive: in-progress retry attempt per task id.</summary>
    public Dictionary<string, int>? TaskAttempts { get; set; }

    /// <summary>v050 additive: join target → sources arrived in the current epoch.</summary>
    public Dictionary<string, List<string>>? JoinArrivals { get; set; }

    /// <summary>v050 additive: typed stop reason enum name behind an Interrupted snapshot.</summary>
    public string? StopReason { get; set; }

    public static S3CheckpointDocument FromSnapshot(
        CheckpointSnapshot snapshot,
        CheckpointTypeRegistry? registry = null)
    {
        return new S3CheckpointDocument
        {
            FormatVersion = CheckpointWireFormat.Version,
            ThreadId = snapshot.ThreadId,
            Step = snapshot.Step,
            Status = snapshot.Status.ToString(),
            ChannelValues = snapshot.ChannelValues.ToDictionary(
                static pair => pair.Key,
                pair => S3CheckpointJson.ToElement(pair.Value, registry),
                StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(snapshot.ChannelVersions, StringComparer.Ordinal),
            VersionsSeen = snapshot.VersionsSeen.ToDictionary(
                static pair => pair.Key,
                static pair => new Dictionary<string, long>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            PendingWrites =
            [
                .. snapshot.PendingWrites.Select(write => new S3PendingWriteDocument
                {
                    TaskId = write.TaskId,
                    ChannelName = write.ChannelName,
                    Value = S3CheckpointJson.ToElement(write.Value, registry),
                })
            ],
            PendingSends =
            [
                .. snapshot.PendingSends.Select(send => new S3PendingSendDocument
                {
                    NodeName = send.NodeName,
                    TaskId = send.TaskId,
                    Payload = S3CheckpointJson.ToElement(send.Payload, registry),
                })
            ],
            PendingInterrupts =
            [
                .. snapshot.PendingInterrupts.Select(item => new S3PendingInterruptDocument
                {
                    TaskId = item.TaskId,
                    NodeName = item.NodeName,
                    Payload = S3CheckpointJson.ToElement(item.Payload, registry),
                    TaskPayload = S3CheckpointJson.ToElement(item.TaskPayload, registry),
                })
            ],
            LastNode = snapshot.LastNode,
            NextNodes = [.. snapshot.NextNodes],
            InterruptPayload = S3CheckpointJson.ToElement(snapshot.InterruptPayload, registry),
            GraphId = snapshot.GraphId,
            GraphVersion = snapshot.GraphVersion,
            CancelReason = snapshot.CancelReason,
            CompletedTaskIds = snapshot.CompletedTaskIds is { Count: > 0 }
                ? [.. snapshot.CompletedTaskIds]
                : null,
            TaskAttempts = snapshot.TaskAttempts is { Count: > 0 }
                ? new Dictionary<string, int>(snapshot.TaskAttempts, StringComparer.Ordinal)
                : null,
            JoinArrivals = snapshot.JoinArrivals is { Count: > 0 }
                ? snapshot.JoinArrivals.ToDictionary(
                    static pair => pair.Key,
                    static pair => new List<string>(pair.Value),
                    StringComparer.Ordinal)
                : null,
            StopReason = snapshot.StopReason == RunStopReason.None
                ? null
                : snapshot.StopReason.ToString(),
        };
    }

    public CheckpointSnapshot ToSnapshot(CheckpointTypeRegistry? registry = null)
    {
        CheckpointWireFormat.EnsureSupported(FormatVersion);

        var status = Enum.TryParse<GraphRunStatus>(Status, ignoreCase: true, out var parsed)
            ? parsed
            : GraphRunStatus.Running;
        var stopReason = Enum.TryParse<RunStopReason>(StopReason, ignoreCase: true, out var parsedReason)
            ? parsedReason
            : RunStopReason.None;

        return new CheckpointSnapshot
        {
            FormatVersion = FormatVersion,
            ThreadId = ThreadId,
            Step = Step,
            Status = status,
            ChannelValues = ChannelValues.ToDictionary(
                static pair => pair.Key,
                pair => S3CheckpointJson.FromElement(pair.Value, registry),
                StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(ChannelVersions, StringComparer.Ordinal),
            VersionsSeen = VersionsSeen.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyDictionary<string, long>)new Dictionary<string, long>(
                    pair.Value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal),
            PendingWrites =
            [
                .. PendingWrites.Select(write => new PendingWrite
                {
                    TaskId = write.TaskId,
                    ChannelName = write.ChannelName,
                    Value = S3CheckpointJson.FromElement(write.Value, registry),
                })
            ],
            PendingSends =
            [
                .. PendingSends.Select(send => new PendingSend
                {
                    NodeName = send.NodeName,
                    TaskId = send.TaskId,
                    Payload = S3CheckpointJson.FromElement(send.Payload, registry),
                })
            ],
            PendingInterrupts =
            [
                .. PendingInterrupts.Select(item => new PendingInterrupt
                {
                    TaskId = item.TaskId,
                    NodeName = item.NodeName,
                    Payload = S3CheckpointJson.FromElement(item.Payload, registry),
                    TaskPayload = S3CheckpointJson.FromElement(item.TaskPayload, registry),
                })
            ],
            LastNode = LastNode,
            NextNodes = [.. NextNodes],
            InterruptPayload = S3CheckpointJson.FromElement(InterruptPayload, registry),
            GraphId = GraphId,
            GraphVersion = GraphVersion,
            CancelReason = CancelReason,
            CompletedTaskIds = CompletedTaskIds ?? [],
            TaskAttempts = new Dictionary<string, int>(TaskAttempts ?? [], StringComparer.Ordinal),
            JoinArrivals = ToSnapshotArrivals(JoinArrivals),
            StopReason = stopReason,
        };
    }

    private static Dictionary<string, IReadOnlyList<string>> ToSnapshotArrivals(
        Dictionary<string, List<string>>? arrivals)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var pair in arrivals ?? [])
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }
}
