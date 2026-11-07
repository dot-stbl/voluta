using System.Text.Json;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;

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

    public static FileCheckpointDocument FromSnapshot(CheckpointSnapshot snapshot)
    {
        return new FileCheckpointDocument
        {
            FormatVersion = CheckpointWireFormat.Version,
            ThreadId = snapshot.ThreadId,
            Step = snapshot.Step,
            Status = snapshot.Status.ToString(),
            ChannelValues = snapshot.ChannelValues.ToDictionary(
                static pair => pair.Key,
                static pair => FileCheckpointJson.ToElement(pair.Value),
                StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(snapshot.ChannelVersions, StringComparer.Ordinal),
            VersionsSeen = snapshot.VersionsSeen.ToDictionary(
                static pair => pair.Key,
                static pair => new Dictionary<string, long>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            PendingWrites =
            [
                .. snapshot.PendingWrites.Select(static write => new FilePendingWriteDocument
                {
                    TaskId = write.TaskId,
                    ChannelName = write.ChannelName,
                    Value = FileCheckpointJson.ToElement(write.Value),
                })
            ],
            PendingSends =
            [
                .. snapshot.PendingSends.Select(static send => new FilePendingSendDocument
                {
                    NodeName = send.NodeName,
                    TaskId = send.TaskId,
                    Payload = FileCheckpointJson.ToElement(send.Payload),
                })
            ],
            PendingInterrupts =
            [
                .. snapshot.PendingInterrupts.Select(static item => new FilePendingInterruptDocument
                {
                    TaskId = item.TaskId,
                    NodeName = item.NodeName,
                    Payload = FileCheckpointJson.ToElement(item.Payload),
                    TaskPayload = FileCheckpointJson.ToElement(item.TaskPayload),
                })
            ],
            LastNode = snapshot.LastNode,
            NextNodes = [.. snapshot.NextNodes],
            InterruptPayload = FileCheckpointJson.ToElement(snapshot.InterruptPayload),
            GraphId = snapshot.GraphId,
            GraphVersion = snapshot.GraphVersion,
            CancelReason = snapshot.CancelReason,
        };
    }

    public CheckpointSnapshot ToSnapshot()
    {
        CheckpointWireFormat.EnsureSupported(FormatVersion);

        var status = Enum.TryParse<GraphRunStatus>(Status, ignoreCase: true, out var parsed)
            ? parsed
            : GraphRunStatus.Running;

        return new CheckpointSnapshot
        {
            FormatVersion = FormatVersion,
            ThreadId = ThreadId,
            Step = Step,
            Status = status,
            ChannelValues = ChannelValues.ToDictionary(
                static pair => pair.Key,
                static pair => FileCheckpointJson.FromElement(pair.Value),
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
                .. PendingWrites.Select(static write => new PendingWrite
                {
                    TaskId = write.TaskId,
                    ChannelName = write.ChannelName,
                    Value = FileCheckpointJson.FromElement(write.Value),
                })
            ],
            PendingSends =
            [
                .. PendingSends.Select(static send => new PendingSend
                {
                    NodeName = send.NodeName,
                    TaskId = send.TaskId,
                    Payload = FileCheckpointJson.FromElement(send.Payload),
                })
            ],
            PendingInterrupts =
            [
                .. PendingInterrupts.Select(static item => new PendingInterrupt
                {
                    TaskId = item.TaskId,
                    NodeName = item.NodeName,
                    Payload = FileCheckpointJson.FromElement(item.Payload),
                    TaskPayload = FileCheckpointJson.FromElement(item.TaskPayload),
                })
            ],
            LastNode = LastNode,
            NextNodes = [.. NextNodes],
            InterruptPayload = FileCheckpointJson.FromElement(InterruptPayload),
            GraphId = GraphId,
            GraphVersion = GraphVersion,
            CancelReason = CancelReason,
        };
    }
}
