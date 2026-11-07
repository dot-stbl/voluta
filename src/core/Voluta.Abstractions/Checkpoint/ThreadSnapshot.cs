using Voluta.Abstractions.Runtime;

namespace Voluta.Abstractions.Checkpoint;

/// <summary>
///     Host/UI-facing projection of a checkpoint for time-travel read (current state or history step).
///     Omits engine-internal C-shape fields (versions, pending writes/sends) that ops rarely need.
/// </summary>
public sealed class ThreadSnapshot
{
    /// <summary>
    ///     Thread (conversation / run) identifier.
    /// </summary>
    public required string ThreadId { get; init; }

    /// <summary>
    ///     Superstep index of this snapshot.
    /// </summary>
    public long Step { get; init; }

    /// <summary>
    ///     Run status at this step.
    /// </summary>
    public GraphRunStatus Status { get; init; }

    /// <summary>
    ///     Channel name → value (last-good payload for Failed/Cancelled terminals).
    /// </summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; }
        = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    ///     Last node that completed before this snapshot, when known.
    /// </summary>
    public string? LastNode { get; init; }

    /// <summary>
    ///     Next node names scheduled or expected, when known.
    /// </summary>
    public IReadOnlyList<string> NextNodes { get; init; } = [];

    /// <summary>
    ///     HITL interrupt payload when <see cref="Status" /> is <see cref="GraphRunStatus.Interrupted" />.
    ///     For multi-interrupt, the first pending payload (compat); see <see cref="PendingInterrupts" />.
    /// </summary>
    public object? InterruptPayload { get; init; }

    /// <summary>
    ///     All pending HITL interrupts (empty when none or legacy single-payload snapshots).
    /// </summary>
    public IReadOnlyList<PendingInterrupt> PendingInterrupts { get; init; } = [];

    /// <summary>
    ///     Graph identity of the compiled graph that wrote this snapshot (null for legacy
    ///     snapshots). Additive field — null default, mirrors <see cref="CheckpointSnapshot" />.
    /// </summary>
    public string? GraphId { get; init; }

    /// <summary>
    ///     Graph version paired with <see cref="GraphId" /> (null for legacy snapshots).
    /// </summary>
    public string? GraphVersion { get; init; }

    /// <summary>
    ///     Host-supplied reason when this snapshot was written by an out-of-band cancellation
    ///     (null otherwise). Additive field — null default.
    /// </summary>
    public string? CancelReason { get; init; }
}
