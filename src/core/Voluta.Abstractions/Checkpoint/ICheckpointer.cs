namespace Voluta.Abstractions.Checkpoint;

/// <summary>
///     Pluggable durable store for C-shape checkpoints. Implementations live in core
///     (InMemory) or provider packages (EF, S3, File); this assembly has no storage deps.
/// </summary>
public interface ICheckpointer
{
    /// <summary>
    ///     Persists a checkpoint for the snapshot's thread (and step history when supported).
    /// </summary>
    /// <remarks>
    ///     Blind write: unconditional overwrite keyed (thread, step). This is the shipped
    ///     0.4.0 contract, frozen — 0.4.0 implementors and callers compile and behave
    ///     unchanged. Guarded / transactional puts go through
    ///     <see cref="PutAsync(CheckpointSnapshot, long?, Runtime.ICheckpointUnitOfWork?, CancellationToken)" />.
    /// </remarks>
    /// <param name="snapshot">Full C-shape snapshot to store.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>A task that completes when the snapshot is durable for the provider.</returns>
    /// <remarks>
    ///     RS0027: the guarded
    ///     <see cref="PutAsync(CheckpointSnapshot, long?, Runtime.ICheckpointUnitOfWork?, CancellationToken)" />
    ///     DIM deliberately takes ALL parameters as required (no optional-parameter shadowing of
    ///     this shipped form), so this shipped overload is no longer the widest one.
    /// </remarks>
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0027: API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "Shipped blind put is frozen; the guarded DIM takes all parameters as required.")]
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Guarded, transactional put (additive in 0.5.0). When
    ///     <paramref name="expectedParentStep" /> is non-null the provider MUST atomically
    ///     verify that the thread's current latest step equals this value before persisting:
    ///     a mismatch race and a same-step lost-update race both fail with
    ///     <see cref="CheckpointStoreException" /> code
    ///     <see cref="Diagnostics.VolutaErrorCodes.CheckpointConflict" />; rewriting the
    ///     expected step itself is allowed (protected by the provider's concurrency token).
    ///     Null <paramref name="expectedParentStep" /> degrades to the blind write.
    /// </summary>
    /// <remarks>
    ///     Default interface method delegating to the shipped blind
    ///     <see cref="PutAsync(CheckpointSnapshot, CancellationToken)" />: external 0.4.0
    ///     implementations that do not override it keep working (unguarded standalone
    ///     write). Deliberately has NO optional parameters — omitting the added arguments is
    ///     a compile error, so <c>PutAsync(snapshot)</c> / <c>PutAsync(snapshot, ct)</c>
    ///     always resolve to the shipped blind member without ambiguity.
    /// </remarks>
    /// <param name="snapshot">Full C-shape snapshot to store.</param>
    /// <param name="expectedParentStep">
    ///     Optimistic-concurrency guard; <see langword="null" /> keeps the blind write.
    /// </param>
    /// <param name="unitOfWork">
    ///     Host unit of work for transactional participation
    ///     (<see cref="Runtime.ICheckpointUnitOfWork" />) — explicit per-run handoff, no
    ///     ambient/<c>AsyncLocal</c> flow. <see langword="null" /> keeps the standalone write.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>A task that completes when the snapshot is durable for the provider.</returns>
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        Runtime.ICheckpointUnitOfWork? unitOfWork,
        CancellationToken cancellationToken)
    {
        return PutAsync(snapshot, cancellationToken);
    }

    /// <summary>
    ///     Loads the latest checkpoint for a thread, or null when the thread was never put.
    ///     A miss MUST NOT throw; storage outages may still throw.
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>The latest snapshot, or null if not found.</returns>
    /// <remarks>
    ///     <para>
    ///         <strong>Failure checkpoint policy:</strong> after a node/superstep failure the
    ///         runtime puts a terminal <see cref="Runtime.GraphRunStatus.Failed" /> (or
    ///         <see cref="Runtime.GraphRunStatus.Cancelled" />) snapshot at the failing
    ///         superstep index. That document's channel values are the last successfully
    ///         applied superstep — incomplete writes are never merged. Get therefore returns
    ///         status Failed/Cancelled with last-good payload, not a wiped or half-applied
    ///         state. Providers MUST NOT overwrite a prior step's document when putting a
    ///         higher step (keyed by thread + step).
    ///     </para>
    ///     <para>
    ///         Use <see cref="ListAsync" /> when supported to inspect earlier successful
    ///         Running/Interrupted/Done steps for recovery tooling.
    ///     </para>
    /// </remarks>
    public Task<CheckpointSnapshot?> GetAsync(string threadId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Optionally lists checkpoints for a thread (time-travel), ordered by step ascending.
    ///     Providers that do not support history MUST throw <see cref="NotSupportedException" />
    ///     rather than return partial silent data.
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Checkpoints for the thread, oldest step first when supported.</returns>
    public Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
        string threadId,
        CancellationToken cancellationToken = default);
}
