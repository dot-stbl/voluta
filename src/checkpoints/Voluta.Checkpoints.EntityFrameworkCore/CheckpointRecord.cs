using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;

namespace Voluta.Checkpoints.EntityFrameworkCore;

/// <summary>
///     EF entity for one C-shape checkpoint row (composite key thread + step).
///     <see cref="Snapshot" /> is mapped with a JSON value converter (no hand-serialize in the checkpointer).
/// </summary>
public sealed class CheckpointRecord
{
    /// <summary>Thread identifier (part of composite key).</summary>
    public required string ThreadId { get; set; }

    /// <summary>Superstep number (part of composite key).</summary>
    public long Step { get; set; }

    /// <summary>Run status at this step.</summary>
    public GraphRunStatus Status { get; set; }

    /// <summary>
    ///     Full C-shape snapshot (EF converts to/from JSON column).
    /// </summary>
    public required CheckpointSnapshot Snapshot { get; set; }

    /// <summary>
    ///     Application-managed concurrency token for guarded puts (same-step rewrite races fail
    ///     as <see cref="CheckpointStoreException" /> code <c>checkpoint.conflict</c>).
    ///     Provider-agnostic by design: store-generated tokens (rowversion / <c>xmin</c>) are not
    ///     produced by portable test backends (SQLite in-memory, EF InMemory). Existing stores
    ///     need a column addition when upgrading to 0.5.0.
    /// </summary>
    public long ConcurrencyToken { get; set; }
}
