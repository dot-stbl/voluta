using Microsoft.EntityFrameworkCore;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Runtime;
using Voluta.Checkpoints.EntityFrameworkCore.Wire;

namespace Voluta.Checkpoints.EntityFrameworkCore;

/// <summary>
///     EF Core checkpointer over any <typeparamref name="TContext" /> that implements
///     <see cref="IVolutaCheckpointDbContext" />. Snapshot JSON conversion lives in the
///     EF model (internal entity configuration), not here.
/// </summary>
/// <typeparam name="TContext">Host DbContext type.</typeparam>
/// <remarks>
///     Host registration: <c>v.Checkpoints.UseEntityFrameworkCore&lt;TContext&gt;()</c>.
///     Direct construction is internal for conformance / unit tests only.
///     Channel values must be wire-format v1 allow-listed shapes; unsupported types fail Put with
///     <c>checkpoint.unsupported_value_type</c>.
/// </remarks>
public sealed class EntityFrameworkCoreCheckpointer<TContext> : ICheckpointer, IThreadDiscovery
    where TContext : DbContext, IVolutaCheckpointDbContext
{
    private readonly IDbContextFactory<TContext> factory;

    /// <summary>
    ///     Bound on SaveChanges attempts for a standalone blind put: the write is retried when an
    ///     overlapping guarded commit moved the row's concurrency token underneath the read.
    /// </summary>
    private const int MaxBlindSaveAttempts = 3;

    /// <summary>
    ///     Creates an EF Core checkpointer over <paramref name="factory" />.
    /// </summary>
    /// <param name="factory">Factory for host DbContext instances.</param>
    internal EntityFrameworkCoreCheckpointer(IDbContextFactory<TContext> factory)
    {
        this.factory = factory;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Guarded puts (<paramref name="expectedParentStep" /> non-null) execute as one
    ///         transaction on relational stores: read the thread's latest step, verify it equals
    ///         the expected parent, then insert / rewrite. Rewriting an existing row is protected
    ///         by the application-managed <see cref="CheckpointRecord.ConcurrencyToken" /> —
    ///         a same-step lost update surfaces as <c>DbUpdateConcurrencyException</c> and is
    ///         translated to code <c>checkpoint.conflict</c> (as is a primary-key collision from
    ///         a same-step insert race). Blind puts (null expected parent) keep the pre-existing
    ///         single-statement upsert without a transaction and MUST NOT change provider
    ///         behavior: the concurrency token is never bumped on that path, so a blind put
    ///         stays a silent last-writer-wins rewrite. Because the model marks the token as
    ///         an EF concurrency column, a token race with an overlapping guarded commit on
    ///         the same row is retried on a fresh context instead of surfacing as
    ///         <c>DbUpdateConcurrencyException</c> or <c>checkpoint.conflict</c>.
    ///     </para>
    ///     <para>
    ///         When <paramref name="unitOfWork" /> carries the host's own
    ///         <typeparamref name="TContext" /> (located via
    ///         <see cref="Voluta.Abstractions.Runtime.ICheckpointUnitOfWork.TryGetResource{T}(out T)" />), the put enlists on
    ///         the host context instead: the guard runs against the host's pending state and the
    ///         changes are staged but <em>not</em> saved — the host commits (or rolls back) its
    ///         transaction, making the checkpoint and its own writes atomic. Concurrency-token
    ///         violations on that path surface at the host's <c>SaveChanges</c>. A blind put
    ///         (null expected parent) enlists without touching the token, so it cannot conflict
    ///         at the host's <c>SaveChanges</c> either.
    ///     </para>
    /// </remarks>
    public async Task PutAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        ICheckpointUnitOfWork? unitOfWork,
        CancellationToken cancellationToken)
    {
        // Validate wire v1 allow-list before EF conversion so CheckpointStoreException is not wrapped.
        _ = EfCheckpointDocument.FromSnapshot(snapshot);
        var guarded = expectedParentStep is not null;

        if (unitOfWork is not null && unitOfWork.TryGetResource<TContext>(out var hostDb))
        {
            try
            {
                await EnlistPutAsync(hostDb, snapshot, expectedParentStep, cancellationToken);
            }
            catch (Exception exception) when (exception is DbUpdateException && guarded)
            {
                // DbUpdateConcurrencyException (same-step lost update) and PK-collision insert
                // races are both checkpoint.conflict — guarded puts only.
                throw Conflict(snapshot, exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                                              and not CheckpointStoreException)
            {
                throw new CheckpointStoreException(
                    VolutaErrorCodes.CheckpointPutFailed,
                    $"Failed to put checkpoint for thread '{snapshot.ThreadId}' step {snapshot.Step}.",
                    exception);
            }

            return;
        }

        try
        {
            if (guarded)
            {
                await GuardedStandalonePutAsync(
                    snapshot,
                    expectedParentStep.GetValueOrDefault(),
                    cancellationToken);
            }
            else
            {
                await BlindStandalonePutAsync(snapshot, cancellationToken);
            }
        }
        catch (Exception exception) when (guarded && exception is DbUpdateConcurrencyException)
        {
            // Same-step lost update on the application-managed token → checkpoint.conflict.
            // Guarded puts only: the blind path absorbs the token race internally
            // (see BlindStandalonePutAsync), so it never surfaces as a conflict.
            throw Conflict(snapshot, exception);
        }
        catch (Exception exception) when (guarded && exception is DbUpdateException)
        {
            // Same-step insert race (primary-key collision inside the guarded transaction).
            throw Conflict(snapshot, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not CheckpointStoreException)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointPutFailed,
                $"Failed to put checkpoint for thread '{snapshot.ThreadId}' step {snapshot.Step}.",
                exception);
        }
    }

    /// <summary>
    ///     Standalone guarded put: one transaction on relational stores, guard read, token-bumped
    ///     rewrite. Lost updates surface as <c>DbUpdateConcurrencyException</c> and are translated
    ///     to <c>checkpoint.conflict</c> by the caller.
    /// </summary>
    private async Task GuardedStandalonePutAsync(
        CheckpointSnapshot snapshot,
        long expectedParentStep,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var latestStep = await db.Checkpoints
            .Where(row => row.ThreadId == snapshot.ThreadId)
            .MaxAsync(row => (long?)row.Step, cancellationToken);
        if (latestStep != expectedParentStep)
        {
            throw Conflict(
                snapshot,
                new InvalidOperationException(
                    $"Expected parent step {expectedParentStep}, latest step " +
                    $"{latestStep?.ToString() ?? "<none>"}."));
        }

        await UpsertAsync(db, snapshot, guarded: true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    /// <summary>
    ///     Standalone blind put: an unconditional last-writer-wins rewrite that NEVER surfaces as
    ///     <c>checkpoint.conflict</c> (frozen 0.4.0 contract). The model marks
    ///     <see cref="CheckpointRecord.ConcurrencyToken" /> as an EF concurrency column, so the
    ///     tracked UPDATE carries the row's original token in its WHERE clause; if a concurrent
    ///     guarded commit bumps the token between this path's read and save, the UPDATE matches
    ///     zero rows and EF raises <c>DbUpdateConcurrencyException</c>. That race is absorbed,
    ///     not surfaced: the rewrite is retried on a fresh context until the write lands. A blind
    ///     put never bumps the token and never translates any failure to a conflict.
    /// </summary>
    private async Task BlindStandalonePutAsync(
        CheckpointSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await UpsertAsync(db, snapshot, guarded: false, cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxBlindSaveAttempts)
            {
                // The row's token moved underneath the read (e.g. an overlapping guarded commit
                // on the same (thread, step)): reload on a fresh context and rewrite anyway.
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Blind write: single-statement upsert with no transaction and no concurrency-token
    ///     bump — silent last-writer-wins, the shipped 0.4.0 behavior. It never raises
    ///     <c>checkpoint.conflict</c>, even when rewriting the latest step.
    ///     <para>
    ///         RS0027: the guarded 4-parameter override deliberately takes ALL parameters as
    ///         required (no optional-parameter shadowing of this shipped form), so this shipped
    ///         overload is no longer the widest.
    ///     </para>
    /// </remarks>
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0027: API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "Shipped blind put is frozen; the guarded override takes all parameters as required.")]
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0027: API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "Shipped blind put is frozen; the guarded override takes all parameters as required.")]
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        return PutAsync(snapshot, expectedParentStep: null, unitOfWork: null, cancellationToken);
    }

    /// <summary>
    ///     Applies the snapshot to the context's change tracker (insert or token-bumped
    ///     rewrite). On guarded puts the application-managed
    ///     <see cref="CheckpointRecord.ConcurrencyToken" /> is bumped so a concurrent
    ///     same-step rewrite loses at <c>SaveChanges</c>; on blind puts the token is left
    ///     untouched so the write stays an unconditional last-writer-wins.
    /// </summary>
    private static async Task UpsertAsync(
        TContext db,
        CheckpointSnapshot snapshot,
        bool guarded,
        CancellationToken cancellationToken)
    {
        var existing = await db.Checkpoints.FindAsync(
            [snapshot.ThreadId, snapshot.Step],
            cancellationToken);

        if (existing is null)
        {
            db.Checkpoints.Add(new CheckpointRecord
            {
                ThreadId = snapshot.ThreadId,
                Step = snapshot.Step,
                Status = snapshot.Status,
                Snapshot = snapshot,
                ConcurrencyToken = 1,
            });
        }
        else
        {
            existing.Status = snapshot.Status;
            existing.Snapshot = snapshot;
            if (guarded)
            {
                existing.ConcurrencyToken = existing.ConcurrencyToken + 1;
            }
        }
    }

    /// <summary>Enlists the put on the host's context; the host owns SaveChanges / commit.</summary>
    private static async Task EnlistPutAsync(
        TContext hostDb,
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        CancellationToken cancellationToken)
    {
        var guarded = expectedParentStep is not null;
        if (expectedParentStep is { } expected)
        {
            var latestStep = await hostDb.Checkpoints
                .Where(row => row.ThreadId == snapshot.ThreadId)
                .MaxAsync(row => (long?)row.Step, cancellationToken);
            if (latestStep != expected)
            {
                throw Conflict(
                    snapshot,
                    new InvalidOperationException(
                        $"Expected parent step {expected}, latest step " +
                        $"{latestStep?.ToString() ?? "<none>"}."));
            }
        }

        await UpsertAsync(hostDb, snapshot, guarded, cancellationToken);
    }

    private static CheckpointStoreException Conflict(CheckpointSnapshot snapshot, Exception exception)
    {
        return new CheckpointStoreException(
            VolutaErrorCodes.CheckpointConflict,
            $"Checkpoint put for thread '{snapshot.ThreadId}' step {snapshot.Step} lost a " +
            "concurrency race (parent-step mismatch or same-step lost update).",
            exception);
    }

    /// <inheritdoc />
    public async Task<CheckpointSnapshot?> GetAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var record = await db.Checkpoints
                .AsNoTracking()
                .Where(row => row.ThreadId == threadId)
                .OrderByDescending(row => row.Step)
                .FirstOrDefaultAsync(cancellationToken);

            return record?.Snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not CheckpointStoreException)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointGetFailed,
                $"Failed to get checkpoint for thread '{threadId}'.",
                exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            return await db.Checkpoints
                .AsNoTracking()
                .Where(row => row.ThreadId == threadId)
                .OrderBy(row => row.Step)
                .Select(row => row.Snapshot)
                .ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not CheckpointStoreException)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointListFailed,
                $"Failed to list checkpoints for thread '{threadId}'.",
                exception);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Distinct <c>ThreadId</c> values from the checkpoints table (SQL
    ///     <c>SELECT DISTINCT thread_id … ORDER BY thread_id</c>).
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListThreadIdsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            return await db.Checkpoints
                .AsNoTracking()
                .Select(row => row.ThreadId)
                .Distinct()
                .OrderBy(threadId => threadId)
                .ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not CheckpointStoreException)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointListFailed,
                "Failed to list thread identifiers from the checkpoint store.",
                exception);
        }
    }
}

/// <summary>
///     Non-generic façade over dedicated <see cref="VolutaCheckpointDbContext" />.
/// </summary>
/// <remarks>
///     Host registration: <c>v.Checkpoints.UseEntityFrameworkCore()</c>.
///     Direct construction is internal for conformance / unit tests only.
/// </remarks>
public sealed class EntityFrameworkCoreCheckpointer : ICheckpointer, IThreadDiscovery
{
    private readonly EntityFrameworkCoreCheckpointer<VolutaCheckpointDbContext> inner;

    /// <summary>
    ///     Creates a checkpointer over dedicated <see cref="VolutaCheckpointDbContext" />.
    /// </summary>
    /// <param name="factory">Factory for <see cref="VolutaCheckpointDbContext" />.</param>
    internal EntityFrameworkCoreCheckpointer(IDbContextFactory<VolutaCheckpointDbContext> factory)
    {
        inner = new EntityFrameworkCoreCheckpointer<VolutaCheckpointDbContext>(factory);
    }

    /// <inheritdoc />
    /// <remarks>Guarded/transactional put — see <see cref="EntityFrameworkCoreCheckpointer{TContext}" />.</remarks>
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        ICheckpointUnitOfWork? unitOfWork,
        CancellationToken cancellationToken)
    {
        return inner.PutAsync(snapshot, expectedParentStep, unitOfWork, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Blind write: single-statement upsert without a transaction or token bump — silent
    ///     last-writer-wins, never <c>checkpoint.conflict</c> (the shipped 0.4.0 behavior).
    ///     <para>
    ///         RS0027: the guarded 4-parameter override deliberately takes ALL parameters as
    ///         required (no optional-parameter shadowing of this shipped form), so this shipped
    ///         overload is no longer the widest.
    ///     </para>
    /// </remarks>
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0027: API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "Shipped blind put is frozen; the guarded override takes all parameters as required.")]
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0027: API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "Shipped blind put is frozen; the guarded override takes all parameters as required.")]
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        return inner.PutAsync(snapshot, expectedParentStep: null, unitOfWork: null, cancellationToken);
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
        return inner.ListThreadIdsAsync(cancellationToken);
    }
}
