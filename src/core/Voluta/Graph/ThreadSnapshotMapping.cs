using Voluta.Abstractions.Checkpoint;

namespace Voluta.Graph;

/// <summary>
///     Maps C-shape checkpoints to host-facing <see cref="ThreadSnapshot" />.
/// </summary>
internal static class ThreadSnapshotMapping
{
    public static ThreadSnapshot FromSnapshot(CheckpointSnapshot snapshot)
    {
        return new ThreadSnapshot
        {
            ThreadId = snapshot.ThreadId,
            Step = snapshot.Step,
            Status = snapshot.Status,
            Values = snapshot.ChannelValues,
            LastNode = snapshot.LastNode,
            NextNodes = snapshot.NextNodes,
            InterruptPayload = snapshot.InterruptPayload,
            PendingInterrupts = snapshot.PendingInterrupts,
            GraphId = snapshot.GraphId,
            GraphVersion = snapshot.GraphVersion,
            CancelReason = snapshot.CancelReason,
        };
    }
}
