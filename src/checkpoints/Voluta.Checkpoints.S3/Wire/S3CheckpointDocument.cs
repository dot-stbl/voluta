using System.Text.Json;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;

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

    public static S3CheckpointDocument FromSnapshot(CheckpointSnapshot snapshot)
    {
        return new S3CheckpointDocument
        {
            FormatVersion = CheckpointWireFormat.Version,
            ThreadId = snapshot.ThreadId,
            Step = snapshot.Step,
            Status = snapshot.Status.ToString(),
            ChannelValues = snapshot.ChannelValues.ToDictionary(
                static pair => pair.Key,
                static pair => S3CheckpointJson.ToElement(pair.Value),
                StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(snapshot.ChannelVersions, StringComparer.Ordinal),
            VersionsSeen = snapshot.VersionsSeen.ToDictionary(
                static pair => pair.Key,
                static pair => new Dictionary<string, long>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            PendingWrites =
            [
                .. snapshot.PendingWrites.Select(static write => new S3PendingWriteDocument
                {
                    TaskId = write.TaskId,
                    ChannelName = write.ChannelName,
                    Value = S3CheckpointJson.ToElement(write.Value),
                })
            ],
            PendingSends =
            [
                .. snapshot.PendingSends.Select(static send => new S3PendingSendDocument
                {
                    NodeName = send.NodeName,
                    TaskId = send.TaskId,
                    Payload = S3CheckpointJson.ToElement(send.Payload),
                })
            ],
            PendingInterrupts =
            [
                .. snapshot.PendingInterrupts.Select(static item => new S3PendingInterruptDocument
                {
                    TaskId = item.TaskId,
                    NodeName = item.NodeName,
                    Payload = S3CheckpointJson.ToElement(item.Payload),
                    TaskPayload = S3CheckpointJson.ToElement(item.TaskPayload),
                })
            ],
            LastNode = snapshot.LastNode,
            NextNodes = [.. snapshot.NextNodes],
            InterruptPayload = S3CheckpointJson.ToElement(snapshot.InterruptPayload),
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
                static pair => S3CheckpointJson.FromElement(pair.Value),
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
                    Value = S3CheckpointJson.FromElement(write.Value),
                })
            ],
            PendingSends =
            [
                .. PendingSends.Select(static send => new PendingSend
                {
                    NodeName = send.NodeName,
                    TaskId = send.TaskId,
                    Payload = S3CheckpointJson.FromElement(send.Payload),
                })
            ],
            PendingInterrupts =
            [
                .. PendingInterrupts.Select(static item => new PendingInterrupt
                {
                    TaskId = item.TaskId,
                    NodeName = item.NodeName,
                    Payload = S3CheckpointJson.FromElement(item.Payload),
                    TaskPayload = S3CheckpointJson.FromElement(item.TaskPayload),
                })
            ],
            LastNode = LastNode,
            NextNodes = [.. NextNodes],
            InterruptPayload = S3CheckpointJson.FromElement(InterruptPayload),
            GraphId = GraphId,
            GraphVersion = GraphVersion,
            CancelReason = CancelReason,
        };
    }
}
