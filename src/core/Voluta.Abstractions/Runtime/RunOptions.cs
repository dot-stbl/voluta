using Voluta.Abstractions.Streaming;

namespace Voluta.Abstractions.Runtime;

/// <summary>
///     Options for invoke / stream of a compiled graph. Cancellation is a method parameter, not options.
/// </summary>
public sealed class RunOptions
{
    /// <summary>
    ///     Thread (conversation / run) id isolating checkpoints and channel state.
    /// </summary>
    public required string ThreadId { get; init; }

    /// <summary>
    ///     Preferred stream observation mode when streaming (default updates).
    /// </summary>
    public StreamMode StreamMode { get; init; } = StreamMode.Updates;

    /// <summary>
    ///     Optional host unit of work for transactional participation: checkpoint puts of this
    ///     run enlist in the host's own transaction instead of writing standalone. Explicit
    ///     per-run handoff — no ambient / <c>AsyncLocal</c> flow. Null (default) keeps the
    ///     standalone write.
    /// </summary>
    public ICheckpointUnitOfWork? UnitOfWork { get; init; }
}
