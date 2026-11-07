using Voluta.Abstractions.Runtime;

namespace Voluta.Abstractions.Checkpoint;

/// <summary>
///     Full C-shape checkpoint: channel values, versions, versions_seen, pending writes,
///     step, status, and optional interrupt payload.
/// </summary>
public sealed class CheckpointSnapshot
{
    /// <summary>
    ///     Schema version for checkpoint evolution / conformance.
    /// </summary>
    public int FormatVersion { get; init; } = 1;

    /// <summary>
    ///     Thread (conversation / run) identifier isolating mutable state.
    /// </summary>
    public required string ThreadId { get; init; }

    /// <summary>
    ///     Superstep index associated with this snapshot.
    /// </summary>
    public long Step { get; init; }

    /// <summary>
    ///     Run status at the time of the snapshot.
    /// </summary>
    public GraphRunStatus Status { get; init; }

    /// <summary>
    ///     Channel name → current value.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ChannelValues { get; init; }
        = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    ///     Channel name → monotonic version after last successful apply.
    /// </summary>
    public IReadOnlyDictionary<string, long> ChannelVersions { get; init; }
        = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>
    ///     Per-node map of channel → last version seen by that node (for ready-set prep).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> VersionsSeen { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);

    /// <summary>
    ///     Incomplete-task writes retained for mid-superstep recovery.
    /// </summary>
    public IReadOnlyList<PendingWrite> PendingWrites { get; init; } = [];

    /// <summary>
    ///     Scheduled PUSH/Send tasks not yet executed (empty when none).
    /// </summary>
    public IReadOnlyList<PendingSend> PendingSends { get; init; } = [];

    /// <summary>
    ///     Last node (or task) that completed before this snapshot, when known.
    /// </summary>
    public string? LastNode { get; init; }

    /// <summary>
    ///     Next node names scheduled or expected, when known.
    /// </summary>
    public IReadOnlyList<string> NextNodes { get; init; } = [];

    /// <summary>
    ///     HITL interrupt payload when <see cref="Status" /> is <see cref="GraphRunStatus.Interrupted" />.
    ///     For a single interrupt this is the node payload; for multi-interrupt it is the first pending
    ///     interrupt's payload (compat). Prefer <see cref="PendingInterrupts" /> for the full set.
    /// </summary>
    public object? InterruptPayload { get; init; }

    /// <summary>
    ///     All pending HITL interrupts for this step (empty when not interrupted or legacy single-path
    ///     snapshots that only set <see cref="InterruptPayload" />). Additive wire field — empty default.
    /// </summary>
    public IReadOnlyList<PendingInterrupt> PendingInterrupts { get; init; } = [];

    /// <summary>
    ///     Host-assigned graph identifier of the compiled graph that wrote this snapshot
    ///     (null for legacy snapshots written before graph identity existed). Additive wire
    ///     field — null default, same precedent as <see cref="PendingInterrupts" />.
    /// </summary>
    public string? GraphId { get; init; }

    /// <summary>
    ///     Host-assigned graph version paired with <see cref="GraphId" /> (null for legacy
    ///     snapshots). Additive wire field — null default, same precedent as
    ///     <see cref="PendingInterrupts" />.
    /// </summary>
    public string? GraphVersion { get; init; }

    /// <summary>
    ///     Host-supplied reason recorded when this snapshot was written by an out-of-band
    ///     cancellation (null otherwise). Additive wire field — null default, same precedent
    ///     as <see cref="PendingInterrupts" />.
    /// </summary>
    public string? CancelReason { get; init; }
}
