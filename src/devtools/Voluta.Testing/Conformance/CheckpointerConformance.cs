using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Serialization;
using Voluta.Abstractions.Streaming;
using Voluta.Graph;
using Voluta.Testing.Fixtures;

namespace Voluta.Testing.Conformance;

/// <summary>
///     Behavioral contract for <see cref="ICheckpointer" />. Call from provider unit tests
///     (InMemory now; EF/S3 later) without rewriting scenarios.
/// </summary>
public static class CheckpointerConformance
{
    /// <summary>How many times the repeated-invoke scenario runs the graph on one thread.</summary>
    private const int RepeatedInvocationCount = 3;

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

    /// <summary>
    ///     Repeated invokes on one thread MUST keep the thread's history append-only: no
    ///     invocation fails, the listed history grows with every invocation, its steps never go
    ///     backwards, and every checkpoint a later invocation appends sits strictly above the
    ///     previous invocation's last step.
    /// </summary>
    /// <remarks>
    ///     The scenario drives the runtime over the provider under test, so it pins the store
    ///     contract every provider owes the engine: a thread's step numbering continues instead
    ///     of rewinding to zero (a blind start on a thread that already holds history made the
    ///     second invoke fail with <c>checkpoint.conflict</c>).
    /// </remarks>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task RepeatedInvokesGrowThreadHistoryAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-repeat-{Guid.NewGuid():N}";
        var graph = GraphFixtures.Linear().Compile(checkpointer);

        var previousCount = 0;
        long? previousLastStep = null;

        for (var invocation = 0; invocation < RepeatedInvocationCount; invocation++)
        {
            var terminal = await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = threadId },
                cancellationToken);
            if (terminal.Kind != StreamEventKind.End)
            {
                throw new InvalidOperationException(
                    $"Conformance repeated invokes: invocation {invocation} ended with '{terminal.Kind}' instead of End.");
            }

            IReadOnlyList<CheckpointSnapshot> history;
            try
            {
                history = await checkpointer.ListAsync(threadId, cancellationToken);
            }
            catch (NotSupportedException)
            {
                // A provider without history support cannot express the growth contract.
                return;
            }

            for (var index = 1; index < history.Count; index++)
            {
                if (history[index].Step < history[index - 1].Step)
                {
                    throw new InvalidOperationException(
                        "Conformance repeated invokes: the listed steps must not go backwards.");
                }
            }

            if (history.Count <= previousCount)
            {
                throw new InvalidOperationException(
                    $"Conformance repeated invokes: the history must grow with every invocation " +
                    $"({previousCount} → {history.Count}).");
            }

            // The history is ordered by step, so the checkpoints this invocation appended come
            // last — they must all sit above the previous invocation's last step. The terminal
            // Done reuses the last commit's step, hence the non-strict comparison inside a run.
            var appendedSteps = history
                .Skip(previousCount)
                .Select(static snapshot => snapshot.Step)
                .ToArray();
            if (appendedSteps.Length == 0)
            {
                throw new InvalidOperationException(
                    "Conformance repeated invokes: an invocation must append at least one checkpoint.");
            }

            if (previousLastStep is { } previousStep)
            {
                foreach (var step in appendedSteps)
                {
                    if (step <= previousStep)
                    {
                        throw new InvalidOperationException(
                            $"Conformance repeated invokes: step {step} of a later invocation must exceed " +
                            $"the previous invocation's last step {previousStep}.");
                    }
                }
            }

            previousLastStep = history[^1].Step;
            previousCount = history.Count;
        }
    }

    /// <summary>
    ///     Two concurrent invokes on one thread that already holds a checkpoint MUST resolve to
    ///     exactly one winner: one run completes, the other fails with
    ///     <see cref="CheckpointStoreException" /> code <c>checkpoint.conflict</c> and persists
    ///     nothing.
    /// </summary>
    /// <remarks>
    ///     The race is made deterministic through the <see cref="ICheckpointer" /> contract alone
    ///     (see <see cref="RacingCheckpointer" />): both runs read the thread's latest snapshot
    ///     before either of them writes, so both target the same next step, and the store calls
    ///     are serialized so that the provider's <em>guard</em> — not the provider's transaction
    ///     mechanics — decides the winner. The losing run's guarded put then sees the advanced
    ///     latest step and MUST fail with <c>checkpoint.conflict</c> on every provider.
    /// </remarks>
    /// <param name="checkpointer">Provider under test.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static async Task ConcurrentInvokesHaveExactlyOneWinnerAsync(
        ICheckpointer checkpointer,
        CancellationToken cancellationToken = default)
    {
        var threadId = $"conformance-invoke-race-{Guid.NewGuid():N}";
        var racer = new RacingCheckpointer(checkpointer, threadId);
        var graph = GraphFixtures.Linear().Compile(racer);

        // Seed the thread with one completed run, so the race is decided by the guarded marker
        // put (a blind start on an empty thread would let both runs in).
        var seeded = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = threadId },
            cancellationToken);
        if (seeded.Kind != StreamEventKind.End)
        {
            throw new InvalidOperationException(
                $"Conformance concurrent invokes: the seeding run ended with '{seeded.Kind}' instead of End.");
        }

        racer.Arm();

        var outcomes = await Task.WhenAll(
            InvokeCatchingConflictAsync(graph, threadId, cancellationToken),
            InvokeCatchingConflictAsync(graph, threadId, cancellationToken));

        var successes = outcomes.Count(static outcome => outcome.TerminalKind == StreamEventKind.End);
        if (successes != 1)
        {
            throw new InvalidOperationException(
                $"Conformance concurrent invokes: expected exactly one successful invocation, got {successes} " +
                $"({string.Join(", ", outcomes.Select(static outcome => outcome.TerminalKind?.ToString() ?? "conflict"))}).");
        }

        var conflicts = outcomes.Where(static outcome => outcome.Conflict is not null).ToArray();
        if (conflicts.Length != 1)
        {
            throw new InvalidOperationException(
                $"Conformance concurrent invokes: expected exactly one losing invocation, got {conflicts.Length}.");
        }

        var conflict = conflicts[0].Conflict;
        if (conflict is null
            || conflict.Code != Voluta.Abstractions.Diagnostics.VolutaErrorCodes.CheckpointConflict)
        {
            throw new InvalidOperationException(
                "Conformance concurrent invokes: the losing invocation must fail with code 'checkpoint.conflict', " +
                $"got {(conflict is null ? "<no exception>" : conflict.Code)}.");
        }

        // The loser persisted nothing: the winner's history is intact and ends with Done.
        var history = await checkpointer.ListAsync(threadId, cancellationToken);
        if (history.Count == 0)
        {
            throw new InvalidOperationException(
                "Conformance concurrent invokes: the thread's history is empty after the race.");
        }

        if (history.Any(static snapshot => snapshot.Status == GraphRunStatus.Failed))
        {
            throw new InvalidOperationException(
                "Conformance concurrent invokes: the losing invocation must not persist a Failed snapshot.");
        }

        if (history[^1].Status != GraphRunStatus.Done)
        {
            throw new InvalidOperationException(
                "Conformance concurrent invokes: the winning invocation must leave a Done snapshot.");
        }
    }

    /// <summary>Runs one invoke, capturing the store conflict instead of throwing it.</summary>
    private static async Task<InvokeOutcome> InvokeCatchingConflictAsync(
        CompiledGraph graph,
        string threadId,
        CancellationToken cancellationToken)
    {
        try
        {
            var terminal = await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = threadId },
                cancellationToken);
            return new InvokeOutcome(terminal.Kind, null);
        }
        catch (CheckpointStoreException conflict)
        {
            return new InvokeOutcome(null, conflict);
        }
    }

    /// <summary>
    ///     Outcome of one racing invoke: the terminal stream kind when the run completed, or the
    ///     captured store conflict when it lost the race.
    /// </summary>
    private sealed record InvokeOutcome(StreamEventKind? TerminalKind, CheckpointStoreException? Conflict);

    /// <summary>
    ///     <see cref="ICheckpointer" /> decorator that makes an engine-level race deterministic
    ///     and provider-agnostic:
    ///     <list type="bullet">
    ///         <item>a load barrier — the loads of the watched thread block until two of them
    ///         arrived, so both racing runs observe the SAME latest snapshot and therefore target
    ///         the SAME next step;</item>
    ///         <item>a store gate — the store calls are serialized, so the provider's guard (not
    ///         the provider's transaction mechanics) decides the winner: the second racer's
    ///         guarded put sees the advanced latest step and fails with
    ///         <c>checkpoint.conflict</c>.</item>
    ///     </list>
    /// </summary>
    /// <remarks>
    ///     Only the <see cref="ICheckpointer" /> contract is used — no provider internals. The
    ///     gate also keeps providers that cannot run two concurrent transactions on one
    ///     connection (SQLite over a shared connection) from failing with a transport error
    ///     instead of the contractual conflict.
    /// </remarks>
    private sealed class RacingCheckpointer(ICheckpointer inner, string threadId) : ICheckpointer
    {
        private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int loads;
        private volatile bool armed;

        /// <summary>Starts blocking the loads of the watched thread at the barrier.</summary>
        public void Arm()
        {
            armed = true;
        }

        /// <inheritdoc />
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
            await gate.WaitAsync(cancellationToken);
            try
            {
                await inner.PutAsync(snapshot, expectedParentStep, unitOfWork, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<CheckpointSnapshot?> GetAsync(
            string thread,
            CancellationToken cancellationToken = default)
        {
            CheckpointSnapshot? snapshot;
            await gate.WaitAsync(cancellationToken);
            try
            {
                snapshot = await inner.GetAsync(thread, cancellationToken);
            }
            finally
            {
                gate.Release();
            }

            if (armed && string.Equals(thread, threadId, StringComparison.Ordinal))
            {
                // The snapshot is read before the barrier — both racers must see the same one.
                if (Interlocked.Increment(ref loads) >= 2)
                {
                    barrier.TrySetResult();
                }

                try
                {
                    await barrier.Task.WaitAsync(BarrierTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    // The other racer never reached the barrier — race as it is.
                }
            }

            return snapshot;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
            string thread,
            CancellationToken cancellationToken = default)
        {
            return inner.ListAsync(thread, cancellationToken);
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
        await RepeatedInvokesGrowThreadHistoryAsync(checkpointer, cancellationToken);
        await ConcurrentInvokesHaveExactlyOneWinnerAsync(checkpointer, cancellationToken);
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
