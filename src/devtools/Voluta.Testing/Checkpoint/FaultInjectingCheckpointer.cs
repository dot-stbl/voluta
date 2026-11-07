using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;

namespace Voluta.Testing.Checkpoint;

/// <summary>
///     <see cref="ICheckpointer" /> decorator that throws on the N-th <see cref="PutAsync(CheckpointSnapshot, long?, ICheckpointUnitOfWork?, CancellationToken)" />
///     (1-based) to simulate crash-between-supersteps.
/// </summary>
public sealed class FaultInjectingCheckpointer : ICheckpointer, IThreadDiscovery
{
    private readonly int failOnPutNumber;
    private readonly Exception fault;
    private readonly ICheckpointer inner;
    private int putCount;

    /// <summary>
    ///     Initializes a fault-injecting decorator.
    /// </summary>
    /// <param name="inner">Underlying checkpointer that receives successful Puts.</param>
    /// <param name="failOnPutNumber">1-based Put index that throws (must be ≥ 1).</param>
    /// <param name="fault">Optional exception; defaults to <see cref="InvalidOperationException" />.</param>
    public FaultInjectingCheckpointer(
        ICheckpointer inner,
        int failOnPutNumber,
        Exception? fault = null)
    {
        if (failOnPutNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failOnPutNumber),
                failOnPutNumber,
                "failOnPutNumber must be ≥ 1 (1-based Put index).");
        }

        this.inner = inner;
        this.failOnPutNumber = failOnPutNumber;
        this.fault = fault
                     ?? new InvalidOperationException(
                         $"FaultInjectingCheckpointer: configured to fail on Put #{failOnPutNumber}.");
    }

    /// <summary>
    ///     Number of Put attempts observed so far (including the failing one).
    /// </summary>
    public int PutAttempts => Volatile.Read(ref putCount);

    /// <inheritdoc />
    /// <remarks>
    ///     Blind passthrough; the attempt still counts toward <see cref="PutAttempts" /> via
    ///     the guarded overload.
    /// </remarks>
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        return PutAsync(snapshot, null, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        ICheckpointUnitOfWork? unitOfWork,
        CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref putCount);
        return attempt == failOnPutNumber
            ? throw fault
            : inner.PutAsync(snapshot, expectedParentStep, unitOfWork, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CheckpointSnapshot?> GetAsync(string threadId, CancellationToken cancellationToken = default)
    {
        return inner.GetAsync(threadId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        return inner.ListAsync(threadId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListThreadIdsAsync(CancellationToken cancellationToken = default)
    {
        return inner is IThreadDiscovery discovery
            ? discovery.ListThreadIdsAsync(cancellationToken)
            : Task.FromResult<IReadOnlyList<string>>([]);
    }
}
