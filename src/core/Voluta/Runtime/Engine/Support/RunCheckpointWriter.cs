using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;

namespace Voluta.Runtime.Engine.Support;

/// <summary>
///     Per-run checkpoint session wrapping the host <see cref="ICheckpointer" />:
///     <list type="bullet">
///         <item>guarded puts — every internal put carries <c>expectedParentStep</c> = the step
///         of the snapshot this run last loaded or wrote (null until the first one, which stays
///         a blind write exactly as before);</item>
///         <item>explicit unit-of-work handoff — <see cref="RunOptions.UnitOfWork" /> travels
///         with each put (no ambient flow);</item>
///         <item>graph identity — <see cref="GraphId" /> / <see cref="GraphVersion" /> of the
///         executing topology, passed to snapshot builds.</item>
///     </list>
///     A <c>checkpoint.conflict</c> from a guarded put escapes the run stream untouched — the
///     engine never synthesizes a terminal Failed snapshot for it.
/// </summary>
internal sealed class RunCheckpointWriter(ICheckpointer checkpointer, ICheckpointUnitOfWork? unitOfWork)
{
    /// <summary>Identity of the executing graph (topology compile options).</summary>
    public string? GraphId { get; init; }

    /// <summary>Identity version paired with <see cref="GraphId" />.</summary>
    public string? GraphVersion { get; init; }

    /// <summary>Step of the last snapshot this run loaded or wrote (null before the first).</summary>
    private long? lastStep;

    /// <summary>Identity pair for <see cref="RunEngineSnapshots.Build" />.</summary>
    public (string? GraphId, string? GraphVersion) Identity => (GraphId, GraphVersion);

    /// <summary>
    ///     Guarded put: the optimistic-concurrency guard is the last loaded / written step of
    ///     this run. Host unit of work, when present, travels with the put.
    /// </summary>
    public async Task PutAsync(CheckpointSnapshot snapshot, CancellationToken cancellationToken)
    {
        await checkpointer.PutAsync(snapshot, lastStep, unitOfWork, cancellationToken);
        lastStep = snapshot.Step;
    }

    /// <summary>Loads the latest snapshot, updating the guard step for subsequent puts.</summary>
    public async Task<CheckpointSnapshot?> GetAsync(string threadId, CancellationToken cancellationToken)
    {
        var snapshot = await checkpointer.GetAsync(threadId, cancellationToken);
        if (snapshot is not null)
        {
            lastStep = snapshot.Step;
        }

        return snapshot;
    }
}
