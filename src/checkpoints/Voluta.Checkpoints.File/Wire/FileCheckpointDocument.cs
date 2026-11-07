using System.Text.Json;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Serialization;

namespace Voluta.Checkpoints.File.Wire;

/// <summary>
///     Wire shape for a single checkpoint JSON file.
/// </summary>
internal sealed class FileCheckpointDocument
{
    public int FormatVersion { get; set; } = 1;

    public string ThreadId { get; set; } = "";

    public long Step { get; set; }

    public string Status { get; set; } = nameof(GraphRunStatus.Running);

    public Dictionary<string, JsonElement?> ChannelValues { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, long> ChannelVersions { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, Dictionary<string, long>> VersionsSeen { get; set; } = new(StringComparer.Ordinal);

    public List<FilePendingWriteDocument> PendingWrites { get; set; } = [];

    public List<FilePendingSendDocument> PendingSends { get; set; } = [];

    public List<FilePendingInterruptDocument> PendingInterrupts { get; set; } = [];

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

    public static FileCheckpointDocument FromSnapshot(
        CheckpointSnapshot snapshot,
        CheckpointTypeRegistry? registry = null)
    {
        return new FileCheckpointDocument
        {
            FormatVersion = CheckpointWireFormat.Version,
            ThreadId = snapshot.ThreadId,
            Step = snapshot.Step,
            Status = snapshot.Status.ToString(),
            ChannelValues = snapshot.ChannelValues.ToDictionary(
                static pair => pair.Key,
                pair => FileCheckpointJson.ToElement(pair.Value, registry),
                StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(snapshot.ChannelVersions, StringComparer.Ordinal),
            VersionsSeen = snapshot.VersionsSeen.ToDictionary(
                static pair => pair.Key,
                static pair => new Dictionary<string, long>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            PendingWrites =
            [
                .. snapshot.PendingWrites.Select(write => new FilePendingWriteDocument
                {
                    TaskId = write.TaskId,
                    ChannelName = write.ChannelName,
                    Value = FileCheckpointJson.ToElement(write.Value, registry),
                })
            ],
            PendingSends =
            [
                .. snapshot.PendingSends.Select(send => new FilePendingSendDocument
                {
                    NodeName = send.NodeName,
                    TaskId = send.TaskId,
                    Payload = FileCheckpointJson.ToElement(send.Payload, registry),
                })
            ],
            PendingInterrupts =
            [
                .. snapshot.PendingInterrupts.Select(item => new FilePendingInterruptDocument
                {
                    TaskId = item.TaskId,
                    NodeName = item.NodeName,
                    Payload = FileCheckpointJson.ToElement(item.Payload, registry),
                    TaskPayload = FileCheckpointJson.ToElement(item.TaskPayload, registry),
                })
            ],
            LastNode = snapshot.LastNode,
            NextNodes = [.. snapshot.NextNodes],
            InterruptPayload = FileCheckpointJson.ToElement(snapshot.InterruptPayload, registry),
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
                pair => FileCheckpointJson.FromElement(pair.Value, registry),
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
                    Value = FileCheckpointJson.FromElement(write.Value, registry),
                })
            ],
            PendingSends =
            [
                .. PendingSends.Select(send => new PendingSend
                {
                    NodeName = send.NodeName,
                    TaskId = send.TaskId,
                    Payload = FileCheckpointJson.FromElement(send.Payload, registry),
                })
            ],
            PendingInterrupts =
            [
                .. PendingInterrupts.Select(item => new PendingInterrupt
                {
                    TaskId = item.TaskId,
                    NodeName = item.NodeName,
                    Payload = FileCheckpointJson.FromElement(item.Payload, registry),
                    TaskPayload = FileCheckpointJson.FromElement(item.TaskPayload, registry),
                })
            ],
            LastNode = LastNode,
            NextNodes = [.. NextNodes],
            InterruptPayload = FileCheckpointJson.FromElement(InterruptPayload, registry),
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
