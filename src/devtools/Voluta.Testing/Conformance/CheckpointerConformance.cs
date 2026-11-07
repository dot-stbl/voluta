using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Serialization;

namespace Voluta.Testing.Conformance;

/// <summary>
///     Behavioral contract for <see cref="ICheckpointer" />. Call from provider unit tests
///     (InMemory now; EF/S3 later) without rewriting scenarios.
/// </summary>
public static class CheckpointerConformance
{
    /// <summary>
    ///     Put then Get MUST roundtrip C-shape fields for the same thread.
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task PutGetRoundtripAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-roundtrip-{Guid.NewGuid():N}";
        var original = CheckpointerConformanceFixtures.CreateSampleSnapshot(
            threadId,
            step: 3,
            status: GraphRunStatus.Running);

        await checkpointer.PutAsync(original, cancellationToken: cancellationToken);
        var loaded = await checkpointer.GetAsync(threadId, cancellationToken) ?? throw new InvalidOperationException(
            "Conformance Put/Get: Get returned null after Put for the same thread.");
        CheckpointerConformanceFixtures.AssertEqual(original, loaded);
    }

    /// <summary>
    ///     Get for an unknown thread MUST return null (not throw).
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task GetMissingReturnsNullAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-missing-{Guid.NewGuid():N}";
        var loaded = await checkpointer.GetAsync(threadId, cancellationToken);
        if (loaded is not null)
        {
            throw new InvalidOperationException(
                "Conformance Get missing: expected null for unknown thread, got a snapshot.");
        }
    }

    /// <summary>
    ///     When List is supported, multiple Puts MUST appear ordered by step ascending.
    ///     Providers that throw <see cref="NotSupportedException" /> skip this scenario.
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task ListOrderedByStepWhenSupportedAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-list-{Guid.NewGuid():N}";
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 1, status: GraphRunStatus.Running),
            cancellationToken: cancellationToken);
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 2, status: GraphRunStatus.Done),
            cancellationToken: cancellationToken);

        IReadOnlyList<CheckpointSnapshot> list;
        try
        {
            list = await checkpointer.ListAsync(threadId, cancellationToken);
        }
        catch (NotSupportedException)
        {
            return;
        }

        if (list.Count < 2)
        {
            throw new InvalidOperationException(
                $"Conformance List: expected ≥ 2 snapshots, got {list.Count}.");
        }

        for (var index = 1; index < list.Count; index++)
        {
            if (list[index].Step < list[index - 1].Step)
            {
                throw new InvalidOperationException(
                    "Conformance List: snapshots must be ordered by step ascending.");
            }
        }
    }

    /// <summary>
    ///     Interrupted status and interrupt payload MUST roundtrip.
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task InterruptFieldsRoundtripAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-interrupt-{Guid.NewGuid():N}";
        var payload = new Dictionary<string, object?> { ["amount"] = 50 };
        var original = new CheckpointSnapshot
        {
            ThreadId = threadId,
            Step = 1,
            Status = GraphRunStatus.Interrupted,
            LastNode = "gate",
            NextNodes = ["gate"],
            InterruptPayload = payload,
            ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(StringComparer.Ordinal),
            VersionsSeen = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal),
            PendingWrites = []
        };

        await checkpointer.PutAsync(original, cancellationToken: cancellationToken);
        var loaded = await checkpointer.GetAsync(threadId, cancellationToken) ??
                     throw new InvalidOperationException("Conformance interrupt: Get returned null after Put.");
        if (loaded.Status != GraphRunStatus.Interrupted)
        {
            throw new InvalidOperationException(
                $"Conformance interrupt: expected Interrupted, got {loaded.Status}.");
        }

        if (loaded.InterruptPayload is null)
        {
            throw new InvalidOperationException("Conformance interrupt: InterruptPayload was null.");
        }
    }

    /// <summary>
    ///     Pending writes MUST roundtrip when present.
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task PendingWritesRoundtripAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-pending-{Guid.NewGuid():N}";
        var original = CheckpointerConformanceFixtures.CreateSampleSnapshot(
            threadId,
            step: 4,
            status: GraphRunStatus.Running);
        await checkpointer.PutAsync(original, cancellationToken: cancellationToken);
        var loaded = await checkpointer.GetAsync(threadId, cancellationToken) ??
                     throw new InvalidOperationException("Conformance pending writes: Get returned null after Put.");
        if (loaded.PendingWrites.Count != original.PendingWrites.Count)
        {
            throw new InvalidOperationException(
                $"Conformance pending writes: expected {original.PendingWrites.Count}, got {loaded.PendingWrites.Count}.");
        }

        if (loaded.PendingWrites[0].TaskId != original.PendingWrites[0].TaskId
            || loaded.PendingWrites[0].ChannelName != original.PendingWrites[0].ChannelName)
        {
            throw new InvalidOperationException(
                "Conformance pending writes: TaskId/ChannelName mismatch after roundtrip.");
        }
    }

    /// <summary>
    ///     A guarded put whose expected parent step matches the thread's latest step MUST succeed.
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task GuardedPutAcceptsMatchingParentAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-guard-accept-{Guid.NewGuid():N}";
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 6, status: GraphRunStatus.Running),
            cancellationToken: cancellationToken);
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 7, status: GraphRunStatus.Running),
            expectedParentStep: 6,
            unitOfWork: null,
            cancellationToken: cancellationToken);

        var loaded = await checkpointer.GetAsync(threadId, cancellationToken) ??
                     throw new InvalidOperationException("Conformance guarded put: Get returned null after Put.");
        if (loaded.Step != 7)
        {
            throw new InvalidOperationException(
                $"Conformance guarded put: expected latest step 7 after accepted put, got {loaded.Step}.");
        }
    }

    /// <summary>
    ///     A guarded put whose expected parent step no longer matches MUST throw
    ///     <see cref="CheckpointStoreException" /> with code <c>checkpoint.conflict</c> and MUST
    ///     NOT persist the snapshot.
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task GuardedPutConflictOnStaleParentAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-guard-conflict-{Guid.NewGuid():N}";
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 1, status: GraphRunStatus.Running),
            cancellationToken: cancellationToken);
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 2, status: GraphRunStatus.Running),
            cancellationToken: cancellationToken);

        // Two writers raced for parent 1; the second lands after step 2 already exists.
        var conflict = await AssertConflictAsync(
            () => checkpointer.PutAsync(
                CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 2, status: GraphRunStatus.Running),
                expectedParentStep: 1,
                unitOfWork: null,
                cancellationToken: cancellationToken));
        if (conflict.Code != Voluta.Abstractions.Diagnostics.VolutaErrorCodes.CheckpointConflict)
        {
            throw new InvalidOperationException(
                $"Conformance guarded put: expected conflict code, got '{conflict.Code}'.");
        }

        var loaded = await checkpointer.GetAsync(threadId, cancellationToken) ??
                     throw new InvalidOperationException("Conformance guarded put: Get returned null.");
        if (loaded.Step != 2)
        {
            throw new InvalidOperationException(
                $"Conformance guarded put: losing put must not persist (latest step {loaded.Step}).");
        }
    }

    /// <summary>
    ///     Rewriting the SAME step with a guarded put MUST be allowed (the provider's concurrency
    ///     token protects true concurrent rewrites — sequential rewrite is accepted).
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task GuardedPutSameStepRewriteAllowedAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-guard-rewrite-{Guid.NewGuid():N}";
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 5, status: GraphRunStatus.Running),
            cancellationToken: cancellationToken);
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 5, status: GraphRunStatus.Interrupted),
            expectedParentStep: 5,
            unitOfWork: null,
            cancellationToken: cancellationToken);

        var loaded = await checkpointer.GetAsync(threadId, cancellationToken) ??
                     throw new InvalidOperationException("Conformance guarded rewrite: Get returned null.");
        if (loaded.Step != 5 || loaded.Status != GraphRunStatus.Interrupted)
        {
            throw new InvalidOperationException(
                "Conformance guarded rewrite: same-step rewrite must replace the step's snapshot.");
        }
    }

    /// <summary>
    ///     A put WITHOUT an expected parent step MUST write unconditionally (pre-change blind
    ///     behavior), even when the thread's latest step moved on.
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task BlindPutUnchangedAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-blind-{Guid.NewGuid():N}";
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 9, status: GraphRunStatus.Running),
            cancellationToken: cancellationToken);
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 9, status: GraphRunStatus.Done),
            cancellationToken: cancellationToken);

        var loaded = await checkpointer.GetAsync(threadId, cancellationToken) ??
                     throw new InvalidOperationException("Conformance blind put: Get returned null.");
        if (loaded.Step != 9 || loaded.Status != GraphRunStatus.Done)
        {
            throw new InvalidOperationException(
                "Conformance blind put: unguarded put must overwrite unconditionally.");
        }
    }

    /// <summary>
    ///     Additive-field compatibility: snapshots WITH graph identity / cancel reason and WITHOUT
    ///     them (legacy shape) MUST both roundtrip without loss, wire format version unchanged.
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task AdditiveFieldsRoundtripAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var withIdentityThread = $"conformance-additive-{Guid.NewGuid():N}";
        var withIdentity = CheckpointerConformanceFixtures.CreateSampleSnapshot(
            withIdentityThread,
            step: 2,
            status: GraphRunStatus.Cancelled);
        withIdentity = new CheckpointSnapshot
        {
            FormatVersion = withIdentity.FormatVersion,
            ThreadId = withIdentity.ThreadId,
            Step = withIdentity.Step,
            Status = withIdentity.Status,
            ChannelValues = withIdentity.ChannelValues,
            ChannelVersions = withIdentity.ChannelVersions,
            VersionsSeen = withIdentity.VersionsSeen,
            PendingWrites = withIdentity.PendingWrites,
            PendingSends = withIdentity.PendingSends,
            LastNode = withIdentity.LastNode,
            NextNodes = withIdentity.NextNodes,
            InterruptPayload = withIdentity.InterruptPayload,
            PendingInterrupts = withIdentity.PendingInterrupts,
            GraphId = "comuki-graph",
            GraphVersion = "2026.11",
            CancelReason = "operator-cancelled",
            CompletedTaskIds = ["agent", "tools"],
            TaskAttempts = new Dictionary<string, int>(StringComparer.Ordinal) { ["agent"] = 3 },
            JoinArrivals = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["gate"] = ["left"],
            },
            StopReason = RunStopReason.BudgetExhausted,
        };

        await checkpointer.PutAsync(withIdentity, cancellationToken: cancellationToken);
        var loaded = await checkpointer.GetAsync(withIdentityThread, cancellationToken) ??
                     throw new InvalidOperationException("Conformance additive fields: Get returned null.");
        CheckpointerConformanceFixtures.AssertEqual(withIdentity, loaded);
        if (loaded.GraphId != "comuki-graph"
            || loaded.GraphVersion != "2026.11"
            || loaded.CancelReason != "operator-cancelled")
        {
            throw new InvalidOperationException(
                "Conformance additive fields: identity/cancel-reason lost on roundtrip.");
        }

        if (!loaded.CompletedTaskIds.SequenceEqual(["agent", "tools"], StringComparer.Ordinal)
            || loaded.TaskAttempts["agent"] != 3
            || !loaded.JoinArrivals["gate"].SequenceEqual(["left"], StringComparer.Ordinal)
            || loaded.StopReason != RunStopReason.BudgetExhausted)
        {
            throw new InvalidOperationException(
                "Conformance additive fields: v050 progress fields lost on roundtrip.");
        }

        if (loaded.FormatVersion != 1)
        {
            throw new InvalidOperationException(
                $"Conformance additive fields: wire version must stay 1, got {loaded.FormatVersion}.");
        }

        // Legacy shape (no identity / reason fields) still roundtrips with nulls and
        // empty v050 progress fields.
        var legacyThread = $"conformance-legacy-{Guid.NewGuid():N}";
        var legacy = CheckpointerConformanceFixtures.CreateSampleSnapshot(
            legacyThread,
            step: 1,
            status: GraphRunStatus.Running);
        await checkpointer.PutAsync(legacy, cancellationToken: cancellationToken);
        var legacyLoaded = await checkpointer.GetAsync(legacyThread, cancellationToken) ??
                           throw new InvalidOperationException("Conformance legacy fields: Get returned null.");
        CheckpointerConformanceFixtures.AssertEqual(legacy, legacyLoaded);
        if (legacyLoaded.GraphId is not null
            || legacyLoaded.GraphVersion is not null
            || legacyLoaded.CancelReason is not null)
        {
            throw new InvalidOperationException(
                "Conformance legacy fields: legacy snapshot must roundtrip with null additive fields.");
        }

        if (legacyLoaded.CompletedTaskIds.Count != 0
            || legacyLoaded.TaskAttempts.Count != 0
            || legacyLoaded.JoinArrivals.Count != 0
            || legacyLoaded.StopReason != RunStopReason.None)
        {
            throw new InvalidOperationException(
                "Conformance legacy fields: legacy snapshot must restore with empty v050 progress fields.");
        }
    }

    /// <summary>
    ///     v050 typed restore (capability-gated): when the provider implements
    ///     <see cref="IRegistryAwareCheckpointer" />, a registered runtime type stamps on
    ///     encode and materializes back typed on restore; a second get with the registry
    ///     detached falls back to raw JSON. Providers without the capability skip this
    ///     scenario (InMemory — unchanged behavior).
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task StampedTypeRoundtripAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        if (checkpointer is not IRegistryAwareCheckpointer registryAware)
        {
            return;
        }

        var previousRegistry = registryAware.TypeRegistry;
        try
        {
            var registry = new CheckpointTypeRegistry()
                .Register<OrderRecord>("conformance:order");
            registryAware.TypeRegistry = registry;

            var threadId = $"conformance-stamp-{Guid.NewGuid():N}";
            var order = new OrderRecord("SKU-1", 5);
            var original = new CheckpointSnapshot
            {
                ThreadId = threadId,
                Step = 1,
                Status = GraphRunStatus.Interrupted,
                ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["order"] = order,
                },
                ChannelVersions = new Dictionary<string, long>(StringComparer.Ordinal) { ["order"] = 1 },
                VersionsSeen = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal),
                LastNode = "agent",
                NextNodes = ["tools"],
                InterruptPayload = order,
            };

            await checkpointer.PutAsync(original, cancellationToken: cancellationToken);
            var loaded = await checkpointer.GetAsync(threadId, cancellationToken) ??
                         throw new InvalidOperationException("Conformance stamped type: Get returned null.");
            var restoredOrder = loaded.ChannelValues["order"] as OrderRecord
                                ?? throw new InvalidOperationException(
                                    $"Conformance stamped type: expected typed OrderRecord, got " +
                                    $"'{loaded.ChannelValues["order"]?.GetType().FullName ?? "null"}'.");
            if (restoredOrder != order)
            {
                throw new InvalidOperationException("Conformance stamped type: typed value lost on restore.");
            }

            var restoredPayload = loaded.InterruptPayload as OrderRecord
                                  ?? throw new InvalidOperationException(
                                      "Conformance stamped type: interrupt payload did not restore typed.");
            if (restoredPayload != order)
            {
                throw new InvalidOperationException("Conformance stamped type: payload value lost on restore.");
            }

            // Registry-less consumer: the stamped payload reads back as raw JSON.
            registryAware.TypeRegistry = null;
            var rawLoaded = await checkpointer.GetAsync(threadId, cancellationToken) ??
                            throw new InvalidOperationException("Conformance stamped type fallback: Get returned null.");
            var rawValue = rawLoaded.ChannelValues["order"];
            if (rawValue is OrderRecord or null)
            {
                throw new InvalidOperationException(
                    "Conformance stamped type fallback: expected raw JSON fallback without a registry, " +
                    $"got '{rawValue?.GetType().FullName ?? "null"}'.");
            }
        }
        finally
        {
            registryAware.TypeRegistry = previousRegistry;
        }
    }

    private sealed record OrderRecord(string Sku, int Quantity);

    /// <summary>
    ///     Two overlapping guarded puts racing for the SAME (thread, step, expectedParentStep)
    ///     MUST resolve to exactly one success and one <c>checkpoint.conflict</c> — the loser
    ///     MUST NOT persist. Determinism per provider: head-marker CAS (File), head-key Lua CAS
    ///     (Redis), pointer conditional writes (S3), transactional latest-step re-check +
    ///     primary-key collision (EF), per-thread lock (InMemory).
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task ConcurrentGuardedPutsExactlyOneWinnerAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-race-{Guid.NewGuid():N}";
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 1, status: GraphRunStatus.Running),
            cancellationToken: cancellationToken);
        await checkpointer.PutAsync(
            CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 2, status: GraphRunStatus.Running),
            expectedParentStep: 1,
            unitOfWork: null,
            cancellationToken: cancellationToken);

        // Both writers target step 3 with expected parent 2 — identical guard, distinct payload
        // (Running vs Interrupted) so the persisted snapshot identifies the winner.
        var winner = CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 3, status: GraphRunStatus.Running);
        var loser = CheckpointerConformanceFixtures.CreateSampleSnapshot(threadId, step: 3, status: GraphRunStatus.Interrupted);

        var first = RacePutAsync(checkpointer, winner, cancellationToken);
        var second = RacePutAsync(checkpointer, loser, cancellationToken);
        var outcomes = await Task.WhenAll(first, second);

        var successes = outcomes.Count(static outcome => outcome.Success);
        if (successes != 1)
        {
            throw new InvalidOperationException(
                $"Conformance concurrent guarded puts: expected exactly one successful put, got {successes}.");
        }

        foreach (var outcome in outcomes)
        {
            if (!outcome.Success
                && (outcome.Conflict is null
                    || outcome.Conflict.Code != Voluta.Abstractions.Diagnostics.VolutaErrorCodes.CheckpointConflict))
            {
                throw new InvalidOperationException(
                    "Conformance concurrent guarded puts: losing put must fail with code 'checkpoint.conflict', " +
                    $"got {(outcome.Conflict is null ? "<no exception>" : outcome.Conflict.Code)}.");
            }
        }

        var winningSnapshot = outcomes[0].Success ? winner : loser;
        var loaded = await checkpointer.GetAsync(threadId, cancellationToken)
                     ?? throw new InvalidOperationException(
                         "Conformance concurrent guarded puts: Get returned null after the race.");
        if (loaded.Step != 3 || loaded.Status != winningSnapshot.Status)
        {
            throw new InvalidOperationException(
                "Conformance concurrent guarded puts: the losing put must not persist — " +
                $"expected step 3 ({winningSnapshot.Status}), got step {loaded.Step} ({loaded.Status}).");
        }
    }

    /// <summary>Runs one racing guarded put, capturing the conflict outcome instead of throwing.</summary>
    private static async Task<GuardedPutOutcome> RacePutAsync(
        ICheckpointer checkpointer,
        CheckpointSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await checkpointer.PutAsync(
                snapshot,
                expectedParentStep: snapshot.Step - 1,
                unitOfWork: null,
                cancellationToken: cancellationToken);
            return new GuardedPutOutcome(true, null);
        }
        catch (CheckpointStoreException conflict)
        {
            return new GuardedPutOutcome(false, conflict);
        }
    }

    /// <summary>Outcome of one racing guarded put: success flag or the captured conflict.</summary>
    private sealed record GuardedPutOutcome(bool Success, CheckpointStoreException? Conflict);

    /// <summary>
    ///     Runs all mandatory conformance scenarios against <paramref name="checkpointer" />.
    /// </summary>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task RunAllAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        await GetMissingReturnsNullAsync(checkpointer, cancellationToken);
        await PutGetRoundtripAsync(checkpointer, cancellationToken);
        await ListOrderedByStepWhenSupportedAsync(checkpointer, cancellationToken);
        await InterruptFieldsRoundtripAsync(checkpointer, cancellationToken);
        await PendingWritesRoundtripAsync(checkpointer, cancellationToken);
        await GuardedPutAcceptsMatchingParentAsync(checkpointer, cancellationToken);
        await GuardedPutConflictOnStaleParentAsync(checkpointer, cancellationToken);
        await GuardedPutSameStepRewriteAllowedAsync(checkpointer, cancellationToken);
        await BlindPutUnchangedAsync(checkpointer, cancellationToken);
        await AdditiveFieldsRoundtripAsync(checkpointer, cancellationToken);
        await StampedTypeRoundtripAsync(checkpointer, cancellationToken);
        await ConcurrentGuardedPutsExactlyOneWinnerAsync(checkpointer, cancellationToken);
    }

    /// <summary>Asserts the call throws <see cref="CheckpointStoreException" /> and returns it.</summary>
    private static async Task<CheckpointStoreException> AssertConflictAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (CheckpointStoreException conflict)
        {
            return conflict;
        }

        throw new InvalidOperationException(
            "Conformance guarded put: expected CheckpointStoreException, but the put succeeded.");
    }
}
