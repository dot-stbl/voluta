using System.Collections.Concurrent;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;

namespace Voluta.Testing.Checkpoint;

/// <summary>
///     Recorded <see cref="ICheckpointer.PutAsync(CheckpointSnapshot, long?, ICheckpointUnitOfWork?, System.Threading.CancellationToken)" /> invocation (snapshot + guard arguments).
/// </summary>
/// <param name="Snapshot">Snapshot passed to Put.</param>
/// <param name="ExpectedParentStep">Optimistic-concurrency guard passed to Put.</param>
/// <param name="UnitOfWork">Host unit of work passed to Put (null when standalone).</param>
public sealed record CheckpointPutRecord(
    CheckpointSnapshot Snapshot,
    long? ExpectedParentStep,
    ICheckpointUnitOfWork? UnitOfWork);

/// <summary>
///     <see cref="ICheckpointer" /> decorator that records Put/Get/List calls for assertions.
///     Forwards <see cref="IThreadDiscovery" /> when the inner store implements it.
/// </summary>
public sealed class RecordingCheckpointer(ICheckpointer inner) : ICheckpointer, IThreadDiscovery
{
    private readonly ConcurrentQueue<CheckpointGetRecord> gets = new();
    private readonly ConcurrentQueue<CheckpointListRecord> lists = new();
    private readonly ConcurrentQueue<CheckpointPutRecord> puts = new();

    /// <summary>
    ///     Snapshots passed to <see cref="PutAsync(CheckpointSnapshot, long?, ICheckpointUnitOfWork?, System.Threading.CancellationToken)" />, in call order.
    /// </summary>
    public IReadOnlyList<CheckpointSnapshot> Puts => [.. puts.Select(static put => put.Snapshot)];

    /// <summary>
    ///     Full put records (snapshot + expected parent step + unit of work), in call order.
    /// </summary>
    public IReadOnlyList<CheckpointPutRecord> PutRecords => [.. puts];

    /// <summary>
    ///     Get calls (thread id + result), in call order.
    /// </summary>
    public IReadOnlyList<CheckpointGetRecord> Gets => [.. gets];

    /// <summary>
    ///     List calls (thread id + result), in call order.
    /// </summary>
    public IReadOnlyList<CheckpointListRecord> Lists => [.. lists];

    /// <inheritdoc />
    /// <remarks>Blind passthrough with recording.</remarks>
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        return PutAsync(snapshot, null, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PutAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        ICheckpointUnitOfWork? unitOfWork,
        CancellationToken cancellationToken)
    {
        await inner.PutAsync(snapshot, expectedParentStep, unitOfWork, cancellationToken);
        puts.Enqueue(new CheckpointPutRecord(snapshot, expectedParentStep, unitOfWork));
    }

    /// <inheritdoc />
    public async Task<CheckpointSnapshot?> GetAsync(string threadId, CancellationToken cancellationToken = default)
    {
        var result = await inner.GetAsync(threadId, cancellationToken);
        gets.Enqueue(new CheckpointGetRecord(threadId, result));
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.ListAsync(threadId, cancellationToken);
        lists.Enqueue(new CheckpointListRecord(threadId, result));
        return result;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListThreadIdsAsync(CancellationToken cancellationToken = default)
    {
        return inner is IThreadDiscovery discovery
            ? discovery.ListThreadIdsAsync(cancellationToken)
            : Task.FromResult<IReadOnlyList<string>>([]);
    }
}

/// <summary>
///     Recorded <see cref="ICheckpointer.GetAsync" /> invocation.
/// </summary>
/// <param name="ThreadId">Thread identifier passed to Get.</param>
/// <param name="Result">Snapshot returned by the inner checkpointer.</param>
public sealed record CheckpointGetRecord(string ThreadId, CheckpointSnapshot? Result);

/// <summary>
///     Recorded <see cref="ICheckpointer.ListAsync" /> invocation.
/// </summary>
/// <param name="ThreadId">Thread identifier passed to List.</param>
/// <param name="Result">Snapshots returned by the inner checkpointer.</param>
public sealed record CheckpointListRecord(string ThreadId, IReadOnlyList<CheckpointSnapshot> Result);
