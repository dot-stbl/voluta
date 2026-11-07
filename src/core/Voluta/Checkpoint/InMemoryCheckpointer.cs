using System.Collections.Concurrent;
using System.Diagnostics;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Runtime;
using Voluta.Diagnostics;
using Voluta.Runtime.Engine.Support;

namespace Voluta.Checkpoint;

/// <summary>
///     Process-local C-shape checkpointer for tests and single-process samples.
/// </summary>
/// <remarks>
///     No JSON wire — values are held by reference (cloned on Get/List). Unlike File/EF/S3,
///     InMemory does <strong>not</strong> enforce the durable wire-format v1 value allow-list;
///     any CLR object may be stored within a single process.
/// </remarks>
public sealed class InMemoryCheckpointer : ICheckpointer, IThreadDiscovery
{
    private const string ProviderName = "inmemory";

    private readonly ConcurrentDictionary<string, List<CheckpointSnapshot>> history =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    /// <remarks>
    ///     Ownership: engine <see cref="CheckpointSnapshot" /> instances are treated as exclusive
    ///     after Put — maps are not re-cloned on store. Isolation is enforced on Get/List by cloning.
    ///     Blind write: unconditional append keyed (thread, step) — the shipped 0.4.0 behavior.
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
        return PutCoreAsync(snapshot, expectedParentStep: null, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Guarded put: single-threaded-state CAS under the per-thread lock — the latest step
    ///     is compared against <paramref name="expectedParentStep" /> (max over the stored
    ///     history, so an out-of-order blind put of a lower step cannot skew the guard), then
    ///     swapped. Rewriting the expected step itself is allowed.
    /// </remarks>
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        ICheckpointUnitOfWork? unitOfWork,
        CancellationToken cancellationToken)
    {
        if (unitOfWork is not null)
        {
            // Transactional participation: the host owns the commit boundary — route the
            // process-local write through its unit of work (explicit handoff, no ambient flow).
            return unitOfWork.ExecuteAsync(
                _ => PutCoreAsync(snapshot, expectedParentStep, CancellationToken.None),
                CancellationToken.None);
        }

        return PutCoreAsync(snapshot, expectedParentStep, cancellationToken);
    }

    private Task PutCoreAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = VolutaDiagnostics.ActivitySource.StartActivity(
            VolutaDiagnostics.CheckpointPutActivityName);
        activity?.SetTag(VolutaDiagnostics.TagProviderName, ProviderName);
        activity?.SetTag(VolutaDiagnostics.TagRunStatus, snapshot.Status.ToString());

        var list = history.GetOrAdd(snapshot.ThreadId, static _ => []);
        lock (list)
        {
            var latestStep = LatestStepLocked(list);
            if (expectedParentStep is { } expected
                && latestStep != expected)
            {
                throw new CheckpointStoreException(
                    VolutaErrorCodes.CheckpointConflict,
                    $"Checkpoint put for thread '{snapshot.ThreadId}' step {snapshot.Step} lost a " +
                    $"concurrency race: expected parent step {expected}, " +
                    $"latest step {(latestStep is null ? "<none>" : latestStep.Value.ToString())}.");
            }

            // Store without deep re-clone: RunEngineSnapshots.Build already allocated fresh maps.
            list.Add(snapshot);
        }

        VolutaDiagnostics.CheckpointPutCount.Add(
            1,
            new TagList
            {
                { VolutaDiagnostics.TagProviderName, ProviderName },
                { VolutaDiagnostics.TagRunStatus, snapshot.Status.ToString() },
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<CheckpointSnapshot?> GetAsync(string threadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = VolutaDiagnostics.ActivitySource.StartActivity(
            VolutaDiagnostics.CheckpointGetActivityName);
        activity?.SetTag(VolutaDiagnostics.TagProviderName, ProviderName);

        if (!history.TryGetValue(threadId, out var list))
        {
            VolutaDiagnostics.CheckpointGetCount.Add(
                1,
                new TagList { { VolutaDiagnostics.TagProviderName, ProviderName } });
            return Task.FromResult<CheckpointSnapshot?>(null);
        }

        CheckpointSnapshot? result;
        lock (list)
        {
            result = list.Count == 0
                ? null
                : InMemoryCheckpointClone.Clone(list[^1]);
        }

        if (result is not null)
        {
            activity?.SetTag(VolutaDiagnostics.TagRunStatus, result.Status.ToString());
        }

        VolutaDiagnostics.CheckpointGetCount.Add(
            1,
            new TagList { { VolutaDiagnostics.TagProviderName, ProviderName } });
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = VolutaDiagnostics.ActivitySource.StartActivity(
            VolutaDiagnostics.CheckpointListActivityName);
        activity?.SetTag(VolutaDiagnostics.TagProviderName, ProviderName);

        if (!history.TryGetValue(threadId, out var list))
        {
            VolutaDiagnostics.CheckpointListCount.Add(
                1,
                new TagList { { VolutaDiagnostics.TagProviderName, ProviderName } });
            return Task.FromResult<IReadOnlyList<CheckpointSnapshot>>([]);
        }

        IReadOnlyList<CheckpointSnapshot> ordered;
        lock (list)
        {
            var snapshots = new List<CheckpointSnapshot>(list.Count);
            foreach (var snapshot in list.OrderBy(static item => item.Step))
            {
                snapshots.Add(InMemoryCheckpointClone.Clone(snapshot));
            }

            ordered = snapshots;
        }

        VolutaDiagnostics.CheckpointListCount.Add(
            1,
            new TagList { { VolutaDiagnostics.TagProviderName, ProviderName } });
        return Task.FromResult(ordered);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListThreadIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = history.Keys
            .OrderBy(static threadId => threadId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    /// <summary>Highest step in the thread's history (callers must hold the list lock).</summary>
    /// <remarks>
    ///     Max — not the last append — so an out-of-order blind put of a lower step cannot
    ///     make the following guarded puts conflict.
    /// </remarks>
    private static long? LatestStepLocked(List<CheckpointSnapshot> list)
    {
        long? latest = null;
        foreach (var item in list)
        {
            if (latest is null || item.Step > latest.Value)
            {
                latest = item.Step;
            }
        }

        return latest;
    }
}

/// <summary>
///     Deep-enough clone of C-shape snapshots so callers cannot mutate stored history.
///     Dictionary copying delegates to the shared <see cref="CheckpointStateMutation" />
///     clone helpers (the same semantics as fork / cancel-park copies).
/// </summary>
file static class InMemoryCheckpointClone
{
    public static CheckpointSnapshot Clone(CheckpointSnapshot snapshot)
    {
        return new CheckpointSnapshot
        {
            FormatVersion = snapshot.FormatVersion,
            ThreadId = snapshot.ThreadId,
            Step = snapshot.Step,
            Status = snapshot.Status,
            ChannelValues = CheckpointStateMutation.CloneValues(snapshot.ChannelValues),
            ChannelVersions = CheckpointStateMutation.CloneVersions(snapshot.ChannelVersions),
            VersionsSeen = CheckpointStateMutation.CloneVersionsSeen(snapshot.VersionsSeen),
            PendingWrites = [.. snapshot.PendingWrites],
            PendingSends = [.. snapshot.PendingSends],
            CompletedTaskIds = [.. snapshot.CompletedTaskIds],
            TaskAttempts = CheckpointStateMutation.CloneTaskAttempts(snapshot.TaskAttempts),
            JoinArrivals = CheckpointStateMutation.CloneJoinArrivals(snapshot.JoinArrivals),
            StopReason = snapshot.StopReason,
            LastNode = snapshot.LastNode,
            NextNodes = [.. snapshot.NextNodes],
            InterruptPayload = snapshot.InterruptPayload,
            PendingInterrupts = [.. snapshot.PendingInterrupts],
            GraphId = snapshot.GraphId,
            GraphVersion = snapshot.GraphVersion,
            CancelReason = snapshot.CancelReason,
        };
    }
}
