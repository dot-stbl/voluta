using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Diagnostics;
using Voluta.Exceptions.Run;
using Voluta.Graph;
using Voluta.Runtime.Engine.Streaming;
using Voluta.Runtime.Engine.Support;
using Voluta.Runtime.Engine.Tasks;

// PendingInterrupt lives in Voluta.Abstractions.Checkpoint.

// GraphConstants lives in root Voluta namespace.
// CommandTaxonomy lives in Voluta.Runtime (parent of this namespace).

namespace Voluta.Runtime.Engine;

/// <summary>
///     Pregel superstep loop for a single stream/invoke/resume session.
/// </summary>
internal sealed class RunEngine(GraphTopology topology, ICheckpointer checkpointer)
{
    /// <summary>
    ///     Creates the per-run checkpoint session (guarded puts, UoW handoff, graph identity).
    /// </summary>
    private RunCheckpointWriter CreateWriter(RunOptions options)
    {
        return new RunCheckpointWriter(checkpointer, options.UnitOfWork)
        {
            GraphId = topology.GraphId,
            GraphVersion = topology.GraphVersion,
        };
    }

    /// <summary>
    ///     Resume / continue identity validation (D2): a snapshot without a graph version
    ///     (legacy snapshot from earlier versions) is accepted unchecked; a snapshot whose
    ///     identity pair differs from the executing graph fails
    ///     <see cref="GraphVersionMismatchException" /> before any node executes or put lands.
    /// </summary>
    private void ValidateGraphIdentity(CheckpointSnapshot checkpoint, string threadId)
    {
        if (checkpoint.GraphVersion is null)
        {
            return;
        }

        if (!string.Equals(checkpoint.GraphId, topology.GraphId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.GraphVersion, topology.GraphVersion, StringComparison.Ordinal))
        {
            throw new GraphVersionMismatchException(
                $"Thread '{threadId}' checkpoint identity " +
                $"({checkpoint.GraphId ?? "<null>"}, {checkpoint.GraphVersion}) does not match the " +
                $"executing graph ({topology.GraphId ?? "<null>"}, {topology.GraphVersion ?? "<null>"}).");
        }
    }

    /// <summary>
    ///     Runs from input (or empty) until terminal, yielding stream events for the selected mode.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        IEnumerable<ChannelWrite> input,
        RunOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var checkpointer = CreateWriter(options);
        var store = new ChannelStore(topology.Channels);
        var inputList = input as IList<ChannelWrite> ?? [.. input];
        if (inputList.Count > 0)
        {
            store.ApplyInputWrites(inputList);
        }

        var nextNodes = RunEngineRouting.ResolveNextNodes(
            topology,
            GraphConstants.Start,
            store.SnapshotValues(),
            null);
        long step = 0;
        string? lastNode = null;
        var startedAt = DateTimeOffset.UtcNow;

        // First put of a fresh run: no loaded/written snapshot yet → guard is null (blind), as before.
        await checkpointer.PutAsync(
            RunEngineSnapshots.Build(
                options.ThreadId,
                step,
                GraphRunStatus.Running,
                store,
                lastNode,
                nextNodes,
                [],
                null,
                identity: checkpointer.Identity),
            cancellationToken);

        if (RunEngineStreaming.EmitsLifecycle(options.StreamMode))
        {
            yield return new StreamEvent
            {
                Mode = options.StreamMode,
                Kind = StreamEventKind.Start,
                Step = step
            };
        }

        await foreach (var item in RunLoopAsync(
                           checkpointer,
                           options,
                           store,
                           RunEngineRouting.ToPullTasks(topology, nextNodes),
                           step,
                           lastNode,
                           resumeByTaskId: null,
                           resumeKind: null,
                           joinArrivals: null,
                           startedAt,
                           cancellationToken: cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    ///     Resumes an interrupted thread with a command payload.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> ResumeAsync(
        string threadId,
        Command command,
        RunOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        CommandTaxonomy.EnsureValid(command);

        var checkpointer = CreateWriter(options);
        var checkpoint = await checkpointer.GetAsync(threadId, cancellationToken) ??
                         throw new GraphInvalidResumeException(
                             $"No checkpoint found for thread '{threadId}'.");
        if (checkpoint.Status != GraphRunStatus.Interrupted)
        {
            throw new GraphInvalidResumeException(
                $"Thread '{threadId}' is not interrupted (status={checkpoint.Status}).");
        }

        ValidateGraphIdentity(checkpoint, threadId);

        // Static pauses / guard stops (v050) are not HITL interrupts: there is no pending
        // interrupt to answer — hosts continue them (ContinueAsync) without a command.
        // This check MUST precede the legacy single-path fallback below, which would
        // otherwise synthesize a phantom interrupt from NextNodes[0].
        if (checkpoint.StopReason is not RunStopReason.None)
        {
            throw new GraphInvalidResumeException(
                $"Thread '{threadId}' is parked by a static pause or stop guard (StopReason={checkpoint.StopReason}); " +
                "use ContinueAsync — resume commands answer HITL interrupts.");
        }

        var pendingInterrupts = RunEngineLoopHelpers.ResolvePendingInterrupts(checkpoint);
        CommandTaxonomy.EnsureMultiInterruptResumes(command, pendingInterrupts);

        var store = new ChannelStore(topology.Channels);
        store.Restore(checkpoint.ChannelValues, checkpoint.ChannelVersions, checkpoint.VersionsSeen);

        // Per-task progress (v050): a superstep that crashed mid-flight recorded completed
        // tasks + their writes; apply the recorded writes so the resume step sees them.
        RunEngineExecution.RestorePendingWrites(store, checkpoint);

        if (command.Values is { Count: > 0 } values)
        {
            store.ApplyInputWrites(values.Select(pair => new ChannelWrite(pair.Key, pair.Value)));
        }

        var resumeByTaskId = RunEngineLoopHelpers.BuildResumeMap(command, pendingInterrupts);
        var remainingAfterResume = RunEngineLoopHelpers.RemainingPendingInterrupts(
            pendingInterrupts,
            resumeByTaskId);
        var readyTasks = RunEngineLoopHelpers.ToResumeReadyTasks(
            RunEngineLoopHelpers.SelectPendingInterrupts(pendingInterrupts, resumeByTaskId));
        var resumeArrivals = new Dictionary<string, IReadOnlyList<string>>(
            checkpoint.JoinArrivals,
            StringComparer.Ordinal);
        readyTasks = RunEngineRouting.RehydrateJoins(
            topology,
            resumeArrivals,
            RunEngineRouting.ExcludeCompleted(readyTasks, checkpoint),
            checkpoint.CompletedTaskIds);
        if (readyTasks.Count == 0)
        {
            var nextNodes = checkpoint.NextNodes.Count > 0
                ? checkpoint.NextNodes
                : RunEngineRouting.ResolveNextNodes(
                    topology,
                    GraphConstants.Start,
                    store.SnapshotValues(),
                    command.Payload,
                    isResume: true,
                    resumeKind: command.Kind);
            readyTasks = RunEngineRouting.ToPullTasks(topology, nextNodes);
        }

        if (RunEngineStreaming.EmitsLifecycle(options.StreamMode))
        {
            yield return new StreamEvent
            {
                Mode = options.StreamMode,
                Kind = StreamEventKind.Start,
                Step = checkpoint.Step
            };
        }

        var startedAt = DateTimeOffset.UtcNow;

        // Progressive multi-interrupt: re-run only resumed task ids; keep remaining pending.
        if (remainingAfterResume.Count > 0)
        {
            await foreach (var item in RunPartialMultiInterruptResumeAsync(
                               checkpointer,
                               options,
                               store,
                               readyTasks,
                               remainingAfterResume,
                               checkpoint,
                               resumeByTaskId,
                               command.Kind,
                               resumeArrivals,
                               startedAt,
                               cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        await foreach (var item in RunLoopAsync(
                           checkpointer,
                           options,
                           store,
                           readyTasks,
                           checkpoint.Step,
                           checkpoint.LastNode,
                           resumeByTaskId,
                           command.Kind,
                           resumeArrivals,
                           startedAt,
                           cancellationToken: cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    ///     Resumes an interrupted thread with a command and stream mode.
    /// </summary>
    public IAsyncEnumerable<StreamEvent> ResumeAsync(
        string threadId,
        Command command,
        StreamMode streamMode,
        CancellationToken cancellationToken = default)
    {
        return ResumeAsync(
            threadId,
            command,
            new RunOptions { ThreadId = threadId, StreamMode = streamMode },
            cancellationToken);
    }

    /// <summary>
    ///     Continues a Running thread from the latest checkpoint (after UpdateState / Fork).
    ///     Does not re-inject HITL resume payload; use
    ///     <see cref="ResumeAsync(string, Command, Voluta.Abstractions.Runtime.RunOptions, System.Threading.CancellationToken)" />
    ///     for Interrupted.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Incomplete-only ready set (A2):</strong> when the checkpoint has
    ///         <see cref="CheckpointSnapshot.PendingSends" />, Continue schedules those push tasks
    ///         only — it does <em>not</em> re-drive <see cref="CheckpointSnapshot.NextNodes" /> as
    ///         fresh pull tasks. That avoids double-firing side-effect nodes that already completed
    ///         and only scheduled Sends. When there are no pending sends, Continue falls back to
    ///         pull from NextNodes (fork/update of a mid-run pull barrier).
    ///     </para>
    /// </remarks>
    public async IAsyncEnumerable<StreamEvent> ContinueAsync(
        string threadId,
        RunOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var checkpointer = CreateWriter(options);
        var startedAt = DateTimeOffset.UtcNow;
        var checkpoint = await checkpointer.GetAsync(threadId, cancellationToken)
                         ?? throw new GraphThreadNotFoundException(
                             $"No checkpoint found for thread '{threadId}'.");
        // v050: the gate widens for policy/static parks only — Interrupted snapshots that
        // carry a StopReason (StaticBefore/StaticAfter/BudgetExhausted/DeadlineExceeded,
        // empty PendingInterrupts) resume like Running snapshots. Bare HITL interrupts,
        // Failed/Cancelled/Done keep throwing.
        if (checkpoint.Status != GraphRunStatus.Running
            && (checkpoint.Status != GraphRunStatus.Interrupted
                || checkpoint.StopReason is RunStopReason.None))
        {
            throw new GraphInvalidContinueException(
                $"Thread '{threadId}' is not Running (status={checkpoint.Status}, stopReason={checkpoint.StopReason}); " +
                "use ResumeAsync for HITL interrupts or UpdateStateAsync after Failed/Cancelled.");
        }

        ValidateGraphIdentity(checkpoint, threadId);

        var store = new ChannelStore(topology.Channels);
        store.Restore(checkpoint.ChannelValues, checkpoint.ChannelVersions, checkpoint.VersionsSeen);

        // Per-task progress (v050): apply writes recorded for completed tasks of the
        // interrupted (crashed) step, then continue only with unfinished tasks.
        RunEngineExecution.RestorePendingWrites(store, checkpoint);

        var nextNodes = checkpoint.NextNodes;
        if (nextNodes.Count == 0 && checkpoint.PendingSends.Count == 0)
        {
            throw new GraphInvalidContinueException(
                $"Thread '{threadId}' has no next nodes or pending sends to continue.");
        }

        if (RunEngineStreaming.EmitsLifecycle(options.StreamMode))
        {
            yield return new StreamEvent
            {
                Mode = options.StreamMode,
                Kind = StreamEventKind.Start,
                Step = checkpoint.Step
            };
        }

        // Static / guard parks (v050) carry the full next ready set in NextNodes — drive it
        // alongside pending sends. Legacy Running-with-sends snapshots keep A2 semantics
        // (sends only, NextNodes not re-pulled).
        var parkedByPolicy = checkpoint.StopReason is not RunStopReason.None;
        if (checkpoint.PendingSends.Count > 0)
        {
            await foreach (var item in ContinueWithPendingSendsAsync(
                               checkpointer,
                               options,
                               store,
                               nextNodes: parkedByPolicy ? nextNodes : [],
                               checkpoint,
                               RehydrateArrivals(checkpoint),
                               startedAt,
                               skipStaticBeforeOnce: checkpoint.StopReason == RunStopReason.StaticBefore,
                               cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        var resumedArrivals = RehydrateArrivals(checkpoint);
        var resumedReady = RunEngineRouting.RehydrateJoins(
            topology,
            resumedArrivals,
            RunEngineRouting.ExcludeCompleted(
                RunEngineRouting.ToPullTasks(topology, nextNodes),
                checkpoint),
            checkpoint.CompletedTaskIds);
        await foreach (var item in RunLoopAsync(
                           checkpointer,
                           options,
                           store,
                           resumedReady,
                           checkpoint.Step,
                           checkpoint.LastNode,
                           resumeByTaskId: null,
                           resumeKind: null,
                           resumedArrivals,
                           startedAt,
                           skipStaticBeforeOnce: checkpoint.StopReason == RunStopReason.StaticBefore,
                           cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    ///     Restores the join epoch map from a snapshot and re-derives the crashed step's
    ///     arrivals from <see cref="CheckpointSnapshot.CompletedTaskIds" /> (join firing
    ///     must survive a boundary-put crash).
    /// </summary>
    private Dictionary<string, IReadOnlyList<string>> RehydrateArrivals(CheckpointSnapshot checkpoint)
    {
        return new Dictionary<string, IReadOnlyList<string>>(
            checkpoint.JoinArrivals,
            StringComparer.Ordinal);
    }

    /// <summary>
    ///     Continues a Running thread with a stream mode (builds default run options).
    /// </summary>
    public IAsyncEnumerable<StreamEvent> ContinueAsync(
        string threadId,
        StreamMode streamMode,
        CancellationToken cancellationToken = default)
    {
        return ContinueAsync(
            threadId,
            new RunOptions { ThreadId = threadId, StreamMode = streamMode },
            cancellationToken);
    }

    /// <summary>
    ///     Progressive multi-interrupt: run only resumed tasks for one superstep, then park
    ///     with remaining <see cref="PendingInterrupt" /> list (status Interrupted).
    /// </summary>
    private async IAsyncEnumerable<StreamEvent> RunPartialMultiInterruptResumeAsync(
        RunCheckpointWriter checkpointer,
        RunOptions options,
        ChannelStore store,
        IReadOnlyList<ReadyTask> readyTasks,
        IReadOnlyList<PendingInterrupt> remainingInterrupts,
        CheckpointSnapshot checkpoint,
        IReadOnlyDictionary<string, object?> resumeByTaskId,
        string? resumeKind,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? joinArrivals = null,
        DateTimeOffset startedAt = default,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var arrivals = new Dictionary<string, IReadOnlyList<string>>(
            joinArrivals ?? new Dictionary<string, IReadOnlyList<string>>(),
            StringComparer.Ordinal);

        // Superstep entry order (v050): guards → static-before → limit → execute.
        var park = await RunEngineLoopHelpers.TryEnterSuperstepAsync(
            topology,
            checkpointer,
            options,
            store,
            checkpoint.Step,
            checkpoint.LastNode,
            readyTasks,
            [],
            arrivals,
            startedAt,
            checkStaticBefore: true,
            cancellationToken);
        if (park is not null)
        {
            yield return park.Event;
            if (park.OutOfSteps is { } outOfSteps)
            {
                throw outOfSteps;
            }

            yield break;
        }

        var step = checkpoint.Step + 1;

        SuperstepCommit? commit = null;
        await foreach (var item in RunEngineLoopHelpers.RunSuperstepStreamingAsync(
                           topology,
                           checkpointer,
                           options,
                           store,
                           readyTasks,
                           step,
                           checkpoint.LastNode,
                           resumeByTaskId,
                           resumeKind,
                           arrivals,
                           cancellationToken))
        {
            if (item.LiveEvent is { } live)
            {
                yield return live;
            }

            if (item.Commit is { } done)
            {
                commit = done;
            }
        }

        if (commit is null)
        {
            throw new InvalidOperationException("Superstep completed without a commit.");
        }

        arrivals = new Dictionary<string, IReadOnlyList<string>>(
            commit.JoinArrivals,
            StringComparer.Ordinal);

        if (commit.TerminalEvent is { } terminal
            && terminal.Kind is StreamEventKind.Failed or StreamEventKind.Cancelled)
        {
            yield return terminal;
            if (commit.Exception is not null)
            {
                throw commit.Exception;
            }

            yield break;
        }

        // Force park with remaining interrupts even if the superstep would have continued.
        var nextNames = remainingInterrupts
            .Select(static item => item.NodeName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var firstPayload = remainingInterrupts[0].Payload;
        await checkpointer.PutAsync(
            RunEngineSnapshots.Build(
                options.ThreadId,
                step,
                GraphRunStatus.Interrupted,
                store,
                commit.LastNode,
                nextNames,
                [],
                firstPayload,
                channelValues: null,
                pendingInterrupts: remainingInterrupts,
                joinArrivals: arrivals,
                identity: checkpointer.Identity),
            cancellationToken);

        yield return RunEngineStreaming.Terminal(
            options.StreamMode,
            StreamEventKind.Interrupt,
            step,
            store,
            payload: firstPayload);
    }

    private async IAsyncEnumerable<StreamEvent> ContinueWithPendingSendsAsync(
        RunCheckpointWriter checkpointer,
        RunOptions options,
        ChannelStore store,
        IReadOnlyList<string> nextNodes,
        CheckpointSnapshot checkpoint,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? joinArrivals = null,
        DateTimeOffset startedAt = default,
        bool skipStaticBeforeOnce = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var arrivals = new Dictionary<string, IReadOnlyList<string>>(
            joinArrivals ?? new Dictionary<string, IReadOnlyList<string>>(),
            StringComparer.Ordinal);
        var skipStaticBefore = skipStaticBeforeOnce;
        var pull = RunEngineRouting.RehydrateJoins(
            topology,
            arrivals,
            RunEngineRouting.ExcludeCompleted(
                RunEngineRouting.ToPullTasks(topology, nextNodes),
                checkpoint),
            checkpoint.CompletedTaskIds);
        var ready = new List<ReadyTask>(pull.Count + checkpoint.PendingSends.Count);
        ready.AddRange(pull);
        foreach (var send in checkpoint.PendingSends)
        {
            if (checkpoint.CompletedTaskIds.Contains(send.TaskId))
            {
                continue;
            }

            ready.Add(
                checkpoint.TaskAttempts.TryGetValue(send.TaskId, out var attempt) && attempt > 0
                    ? new ReadyTask(send.NodeName, send.TaskId, send.Payload) { StartAttempt = attempt }
                    : new ReadyTask(send.NodeName, send.TaskId, send.Payload));        }

        ready.Sort(static (left, right) =>
        {
            var nodeCompare = string.CompareOrdinal(left.NodeName, right.NodeName);
            return nodeCompare != 0
                ? nodeCompare
                : string.CompareOrdinal(left.TaskId, right.TaskId);
        });

        var readyTasks = (IReadOnlyList<ReadyTask>)ready;
        var step = checkpoint.Step;
        var lastNode = checkpoint.LastNode;
        var streamMode = options.StreamMode;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (readyTasks.Count == 0)
            {
                await checkpointer.PutAsync(
                    RunEngineSnapshots.Build(
                        options.ThreadId,
                        step,
                        GraphRunStatus.Done,
                        store,
                        lastNode,
                        [],
                        [],
                        interruptPayload: null,
                        joinArrivals: arrivals,
                        identity: checkpointer.Identity),
                    cancellationToken);

                yield return RunEngineStreaming.Terminal(
                    streamMode,
                    StreamEventKind.End,
                    step,
                    store);
                yield break;
            }

            // Superstep entry order (v050): guards → static-before → limit → execute.
            var park = await RunEngineLoopHelpers.TryEnterSuperstepAsync(
                topology,
                checkpointer,
                options,
                store,
                step,
                lastNode,
                readyTasks,
                RunEngineExecution.StepSends(readyTasks),
                arrivals,
                startedAt,
                checkStaticBefore: !skipStaticBefore,
                cancellationToken);
            if (park is not null)
            {
                yield return park.Event;
                if (park.OutOfSteps is { } outOfSteps)
                {
                    throw outOfSteps;
                }

                yield break;
            }

            skipStaticBefore = false;
            step++;

            SuperstepCommit? commit = null;
            await foreach (var item in RunEngineLoopHelpers.RunSuperstepStreamingAsync(
                               topology,
                               checkpointer,
                               options,
                               store,
                               readyTasks,
                               step,
                               lastNode,
                            resumeByTaskId: null,
                            resumeKind: null,
                            arrivals,
                            cancellationToken))
            {
                if (item.LiveEvent is { } live)
                {
                    yield return live;
                }

                if (item.Commit is { } done)
                {
                    commit = done;
                }
            }

            if (commit is null)
            {
                throw new InvalidOperationException("Superstep completed without a commit.");
            }

            lastNode = commit.LastNode;
            readyTasks = commit.ReadyTasks;
            arrivals = new Dictionary<string, IReadOnlyList<string>>(
                commit.JoinArrivals,
                StringComparer.Ordinal);

            if (commit.TerminalEvent is { } terminal)
            {
                yield return terminal;
                if (commit.Exception is not null)
                {
                    throw commit.Exception;
                }

                yield break;
            }

            foreach (var streamItem in commit.StreamItems)
            {
                yield return streamItem;
            }
        }
    }

    private async IAsyncEnumerable<StreamEvent> RunLoopAsync(
        RunCheckpointWriter checkpointer,
        RunOptions options,
        ChannelStore store,
        IReadOnlyList<ReadyTask> initialReady,
        long step,
        string? lastNode,
        IReadOnlyDictionary<string, object?>? resumeByTaskId,
        string? resumeKind,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? joinArrivals = null,
        DateTimeOffset startedAt = default,
        bool skipStaticBeforeOnce = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var readyTasks = initialReady;
        var isFirstResumeStep = resumeByTaskId is not null;
        var arrivals = new Dictionary<string, IReadOnlyList<string>>(
            joinArrivals ?? new Dictionary<string, IReadOnlyList<string>>(),
            StringComparer.Ordinal);
        // v050: resuming a StaticBefore park must drive the parked node through — the
        // before-check re-arms only from the NEXT entry (cycles re-park again, per spec).
        var skipStaticBefore = skipStaticBeforeOnce;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (readyTasks.Count == 0)
            {
                await checkpointer.PutAsync(
                    RunEngineSnapshots.Build(
                        options.ThreadId,
                        step,
                        GraphRunStatus.Done,
                        store,
                        lastNode,
                        [],
                        [],
                        interruptPayload: null,
                        joinArrivals: arrivals,
                        identity: checkpointer.Identity),
                    cancellationToken);

                yield return RunEngineStreaming.Terminal(
                    options.StreamMode,
                    StreamEventKind.End,
                    step,
                    store);
                yield break;
            }

            // Superstep entry order (v050, deliberate): guards → static-before → limit →
            // execute. The parked snapshot keeps the current step (the step is not consumed).
            var park = await RunEngineLoopHelpers.TryEnterSuperstepAsync(
                topology,
                checkpointer,
                options,
                store,
                step,
                lastNode,
                readyTasks,
                RunEngineExecution.StepSends(readyTasks),
                arrivals,
                startedAt,
                checkStaticBefore: !skipStaticBefore,
                cancellationToken);
            if (park is not null)
            {
                yield return park.Event;
                if (park.OutOfSteps is { } outOfSteps)
                {
                    throw outOfSteps;
                }

                yield break;
            }

            skipStaticBefore = false;
            step++;

            // readyTasks from ToPullTasks are already sorted; re-sort only when sends merge in.
            var orderedReady = readyTasks;
            var payloadsForStep = isFirstResumeStep ? resumeByTaskId : null;
            var kindForStep = isFirstResumeStep ? resumeKind : null;
            isFirstResumeStep = false;

            // Superstep body runs concurrently with live Custom/Messages drain so tokens
            // surface before the superstep commit (async iterator cannot yield inside using).
            await foreach (var item in RunEngineLoopHelpers.RunSuperstepStreamingAsync(
                               topology,
                               checkpointer,
                               options,
                               store,
                               orderedReady,
                               step,
                               lastNode,
                               payloadsForStep,
                               kindForStep,
                               arrivals,
                               cancellationToken))
            {
                if (item.Commit is { } commit)
                {
                    lastNode = commit.LastNode;
                    readyTasks = commit.ReadyTasks;
                    arrivals = new Dictionary<string, IReadOnlyList<string>>(
                        commit.JoinArrivals,
                        StringComparer.Ordinal);

                    if (commit.TerminalEvent is { } terminal)
                    {
                        yield return terminal;
                        if (commit.Exception is not null)
                        {
                            throw commit.Exception;
                        }

                        yield break;
                    }

                    foreach (var streamItem in commit.StreamItems)
                    {
                        yield return streamItem;
                    }
                }
                else if (item.LiveEvent is { } live)
                {
                    yield return live;
                }
            }
        }
    }
}

/// <summary>
///     Result of one superstep body (after activity disposed, before stream yields).
/// </summary>
file sealed class SuperstepCommit
{
    public required string? LastNode { get; init; }

    public required IReadOnlyList<ReadyTask> ReadyTasks { get; init; }

    public StreamEvent? TerminalEvent { get; init; }

    public Exception? Exception { get; init; }

    public IReadOnlyList<StreamEvent> StreamItems { get; init; } = [];

    /// <summary>Join epoch map after this superstep (v050 wait-for-all joins).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> JoinArrivals { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>
///     Live node stream item or final superstep commit (mutually exclusive).
/// </summary>
file sealed class SuperstepStreamItem
{
    public StreamEvent? LiveEvent { get; init; }

    public SuperstepCommit? Commit { get; init; }
}

/// <summary>
///     Park outcome of the superstep entry prelude: the terminal event to replay and, on
///     the recursion cap, the failure to rethrow after the event.
/// </summary>
file sealed record SuperstepEntryPark(StreamEvent Event, GraphOutOfStepsException? OutOfSteps = null);

/// <summary>
///     Hot-path helpers for ready-set node name extraction and superstep body (file-static).
/// </summary>
file static class RunEngineLoopHelpers
{
    /// <summary>
    ///     Explicit superstep-cap check (v050): <see cref="Voluta.Graph.Options.RecursionLimit.Unlimited" />
    ///     disables the cap without relying on overflow; <paramref name="step" /> is long.
    /// </summary>
    public static bool IsOutOfSteps(long step, int limit)
    {
        return limit != Voluta.Graph.Options.RecursionLimit.Unlimited && step > limit;
    }

    /// <summary>
    ///     Superstep entry prelude (v050): guards → static-before → recursion limit, checked in
    ///     exactly this order (the caller owns the cancellation check and the empty-ready Done
    ///     check). On a stop it parks the run — <c>Interrupted</c> snapshot at the current step
    ///     (the step is not consumed) for guard/static stops, <c>Failed</c> at the incremented
    ///     step on the cap — and returns the terminal event to replay plus the failure to
    ///     throw; null means execution proceeds at <paramref name="step" /> + 1.
    /// </summary>
    public static async Task<SuperstepEntryPark?> TryEnterSuperstepAsync(
        GraphTopology topology,
        RunCheckpointWriter checkpointer,
        RunOptions options,
        ChannelStore store,
        long step,
        string? lastNode,
        IReadOnlyList<ReadyTask> readyTasks,
        IReadOnlyList<PendingSend> pendingSends,
        IReadOnlyDictionary<string, IReadOnlyList<string>> joinArrivals,
        DateTimeOffset startedAt,
        bool checkStaticBefore,
        CancellationToken cancellationToken)
    {
        var guardStop = EvaluateGuards(topology.RunGuards, step + 1, startedAt);
        if (guardStop is { } guardReason)
        {
            await ParkPolicyStopAsync(
                checkpointer,
                options,
                store,
                step,
                lastNode,
                readyTasks,
                pendingSends,
                joinArrivals,
                guardReason,
                cancellationToken);
            return new SuperstepEntryPark(
                RunEngineStreaming.Terminal(
                    options.StreamMode,
                    StreamEventKind.Interrupt,
                    step,
                    store,
                    payload: StopReasonPayload(guardReason)));
        }

        if (checkStaticBefore && StaticInterrupts.ShouldPauseBefore(topology, readyTasks))
        {
            await ParkPolicyStopAsync(
                checkpointer,
                options,
                store,
                step,
                lastNode,
                readyTasks,
                pendingSends,
                joinArrivals,
                RunStopReason.StaticBefore,
                cancellationToken);
            return new SuperstepEntryPark(
                RunEngineStreaming.Terminal(options.StreamMode, StreamEventKind.Interrupt, step, store));
        }

        var nextStep = step + 1;
        if (IsOutOfSteps(nextStep, topology.RecursionLimit))
        {
            var outOfSteps = new GraphOutOfStepsException(topology.RecursionLimit, nextStep);
            await checkpointer.PutAsync(
                RunEngineSnapshots.Build(
                    options.ThreadId,
                    nextStep,
                    GraphRunStatus.Failed,
                    store,
                    lastNode,
                    DistinctNodeNames(readyTasks),
                    [],
                    null,
                    joinArrivals: joinArrivals,
                    identity: checkpointer.Identity),
                cancellationToken);
            return new SuperstepEntryPark(
                RunEngineStreaming.Terminal(options.StreamMode, StreamEventKind.Failed, nextStep, store, outOfSteps),
                outOfSteps);
        }

        return null;
    }

    /// <summary>
    ///     Guard evaluation before superstep <paramref name="nextStep" /> (v050): first Stop
    ///     wins; the reason string maps to a typed <see cref="RunStopReason" />
    ///     (<c>deadline_exceeded</c> → DeadlineExceeded, everything else → BudgetExhausted —
    ///     the typed enum stays closed). Null = proceed.
    /// </summary>
    private static RunStopReason? EvaluateGuards(
        IReadOnlyList<IRunGuard> guards,
        long nextStep,
        DateTimeOffset startedAt)
    {
        if (guards.Count == 0)
        {
            return null;
        }

        var context = new RunGuardContext
        {
            NextStep = nextStep,
            CompletedSteps = nextStep - 1,
            StartedAt = startedAt,
        };
        foreach (var guard in guards)
        {
            var decision = guard.Evaluate(context);
            if (decision.Action == RunGuardAction.Stop)
            {
                return string.Equals(decision.Reason, RunGuards.DeadlineExceeded, StringComparison.Ordinal)
                    ? RunStopReason.DeadlineExceeded
                    : RunStopReason.BudgetExhausted;
            }
        }

        return null;
    }

    /// <summary>
    ///     Parks the run with an <c>Interrupted</c> snapshot at <paramref name="step" />
    ///     (unchanged — the step is not consumed): ready set in NextNodes, pending sends
    ///     preserved, empty PendingInterrupts, typed StopReason. Serves static pauses
    ///     (StaticBefore) and guard stops (Budget/Deadline) alike.
    /// </summary>
    private static async Task ParkPolicyStopAsync(
        RunCheckpointWriter checkpointer,
        RunOptions options,
        ChannelStore store,
        long step,
        string? lastNode,
        IReadOnlyList<ReadyTask> readyTasks,
        IReadOnlyList<PendingSend> pendingSends,
        IReadOnlyDictionary<string, IReadOnlyList<string>> joinArrivals,
        RunStopReason reason,
        CancellationToken cancellationToken)
    {
        await checkpointer.PutAsync(
            RunEngineSnapshots.Build(
                options.ThreadId,
                step,
                GraphRunStatus.Interrupted,
                store,
                lastNode,
                DistinctNodeNames(readyTasks),
                pendingSends,
                null,
                pendingInterrupts: [],
                joinArrivals: joinArrivals,
                stopReason: reason,
                identity: checkpointer.Identity),
            cancellationToken);
    }

    private static string StopReasonPayload(RunStopReason reason)
    {
        return reason == RunStopReason.DeadlineExceeded
            ? RunGuards.DeadlineExceeded
            : RunGuards.BudgetExhausted;
    }

    public static async IAsyncEnumerable<SuperstepStreamItem> RunSuperstepStreamingAsync(
        GraphTopology topology,
        RunCheckpointWriter checkpointer,
        RunOptions options,
        ChannelStore store,
        IReadOnlyList<ReadyTask> orderedReady,
        long step,
        string? lastNode,
        IReadOnlyDictionary<string, object?>? resumeByTaskId,
        string? resumeKind,
        Dictionary<string, IReadOnlyList<string>> joinArrivals,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        // C2: bounded live buffer — TryWrite false when full (Wait mode); writer drops + metric.
        const int LiveStreamCapacity = 256;
        var liveChannel = System.Threading.Channels.Channel.CreateBounded<StreamEvent>(
            new System.Threading.Channels.BoundedChannelOptions(LiveStreamCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });

        Func<string, IStreamWriter>? streamWriterFactory = null;
        if (RunEngineStreaming.ForwardsNodeStreamItems(options.StreamMode))
        {
            streamWriterFactory = nodeName =>
                new ChannelStreamWriter(liveChannel.Writer, nodeName, step);
        }

        var executeTask = ExecuteSuperstepAsync(
            topology,
            checkpointer,
            options,
            store,
            orderedReady,
            step,
            lastNode,
            resumeByTaskId,
            resumeKind,
            options.ThreadId,
            streamWriterFactory,
            joinArrivals,
            cancellationToken);

        while (!executeTask.IsCompleted)
        {
            while (liveChannel.Reader.TryRead(out var live))
            {
                yield return new SuperstepStreamItem { LiveEvent = live };
            }

            if (executeTask.IsCompleted)
            {
                break;
            }

            var waitRead = liveChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(executeTask, waitRead);
            if (completed == waitRead && await waitRead)
            {
                while (liveChannel.Reader.TryRead(out var live))
                {
                    yield return new SuperstepStreamItem { LiveEvent = live };
                }
            }
        }

        var commit = await executeTask;
        liveChannel.Writer.TryComplete();
        while (liveChannel.Reader.TryRead(out var remaining))
        {
            yield return new SuperstepStreamItem { LiveEvent = remaining };
        }

        yield return new SuperstepStreamItem { Commit = commit };
    }

    public static async Task<SuperstepCommit> ExecuteSuperstepAsync(
        GraphTopology topology,
        RunCheckpointWriter checkpointer,
        RunOptions options,
        ChannelStore store,
        IReadOnlyList<ReadyTask> orderedReady,
        long step,
        string? lastNode,
        IReadOnlyDictionary<string, object?>? resumeByTaskId,
        string? resumeKind,
        string threadId,
        Func<string, IStreamWriter>? streamWriterFactory,
        Dictionary<string, IReadOnlyList<string>> joinArrivals,
        CancellationToken cancellationToken)
    {
        using var superstep = ActivityScope.Start(
            VolutaDiagnostics.SuperstepActivityName,
            VolutaDiagnostics.SuperstepDuration);

        var preApplySnapshot = store.SnapshotValues();
        var executionOutcome = await RunEngineExecution.TryExecuteReadyAsync(
            topology,
            orderedReady,
            preApplySnapshot,
            resumeByTaskId,
            resumeKind,
            threadId,
            streamWriterFactory,
            checkpointer,
            store,
            step,
            lastNode,
            joinArrivals,
            cancellationToken);

        if (executionOutcome.Cancelled)
        {
            // Terminal marker at the failing superstep (not step-1): never clobber last-good.
            var cancelledNodes = DistinctNodeNames(orderedReady);
            await checkpointer.PutAsync(
                RunEngineSnapshots.Build(
                    options.ThreadId,
                    step,
                    GraphRunStatus.Cancelled,
                    store,
                    lastNode,
                    cancelledNodes,
                    [],
                    null,
                    identity: checkpointer.Identity),
                cancellationToken);

            superstep.SetTag(VolutaDiagnostics.TagRunStatus, nameof(GraphRunStatus.Cancelled));
            return new SuperstepCommit
            {
                LastNode = lastNode,
                ReadyTasks = orderedReady,
                TerminalEvent = RunEngineStreaming.Terminal(
                    options.StreamMode,
                    StreamEventKind.Cancelled,
                    step,
                    store),
                Exception = executionOutcome.Exception,
            };
        }

        if (executionOutcome.Failure is { } failure)
        {
            // Last-good payload: Failed at this superstep with store from last successful apply.
            var failedNodes = DistinctNodeNames(orderedReady);
            await checkpointer.PutAsync(
                RunEngineSnapshots.Build(
                    options.ThreadId,
                    step,
                    GraphRunStatus.Failed,
                    store,
                    lastNode,
                    failedNodes,
                    [],
                    null,
                    identity: checkpointer.Identity),
                cancellationToken);

            superstep.SetError(failure);
            superstep.SetTag(VolutaDiagnostics.TagRunStatus, nameof(GraphRunStatus.Failed));
            return new SuperstepCommit
            {
                LastNode = lastNode,
                ReadyTasks = orderedReady,
                TerminalEvent = RunEngineStreaming.Terminal(
                    options.StreamMode,
                    StreamEventKind.Failed,
                    step,
                    store,
                    failure),
                Exception = failure,
            };
        }

        var executions = executionOutcome.Executions!;
        var pendingInterrupts = CollectPendingInterrupts(orderedReady, executions);
        if (pendingInterrupts.Count > 0)
        {
            // When any task interrupts, continue results from the same superstep are not applied
            // (barrier holds until all pending interrupts resume). The drain-side progress
            // buffer rides along: resume applies the completed siblings' writes and re-runs
            // only the interrupted tasks.
            lastNode = pendingInterrupts[^1].NodeName;
            var nextNodeNames = DistinctNames(
                [.. pendingInterrupts.Select(static item => item.NodeName)]);
            var primaryPayload = pendingInterrupts[0].Payload;
            var progressCurrent = executionOutcome.Progress?.Current();
            var bufferedWrites = progressCurrent?.Writes ?? (IReadOnlyList<PendingWrite>)[];
            var bufferedCompleted = progressCurrent?.Completed ?? (IReadOnlyList<string>)[];
            var bufferedAttempts = progressCurrent?.Attempts
                ?? new Dictionary<string, int>(StringComparer.Ordinal);
            await checkpointer.PutAsync(
                RunEngineSnapshots.Build(
                    options.ThreadId,
                    step,
                    GraphRunStatus.Interrupted,
                    store,
                    lastNode,
                    nextNodeNames,
                    [],
                    primaryPayload,
                    pendingInterrupts: pendingInterrupts,
                    pendingWrites: bufferedWrites,
                    completedTaskIds: bufferedCompleted,
                    taskAttempts: bufferedAttempts,
                    joinArrivals: joinArrivals,
                    identity: checkpointer.Identity),
                cancellationToken);

            superstep.SetTag(VolutaDiagnostics.TagRunStatus, nameof(GraphRunStatus.Interrupted));
            superstep.SetTag(VolutaDiagnostics.TagNodeName, lastNode);
            return new SuperstepCommit
            {
                LastNode = lastNode,
                ReadyTasks = orderedReady,
                TerminalEvent = new StreamEvent
                {
                    Mode = options.StreamMode,
                    Kind = StreamEventKind.Interrupt,
                    Step = step,
                    NodeNames = nextNodeNames,
                    Payload = pendingInterrupts.Count == 1
                        ? primaryPayload
                        : pendingInterrupts,
                    State = options.StreamMode == StreamMode.Values ? store.SnapshotValues() : null,
                },
            };
        }

        var writes = RunEngineExecution.CollectWrites(executions);
        var applyError = RunEngineExecution.TryApplyWrites(store, writes);
        if (applyError is not null)
        {
            // Merge never applied — store still last-good; Failed at this step keeps history.
            var applyFailedNodes = DistinctNodeNames(orderedReady);
            await checkpointer.PutAsync(
                RunEngineSnapshots.Build(
                    options.ThreadId,
                    step,
                    GraphRunStatus.Failed,
                    store,
                    lastNode,
                    applyFailedNodes,
                    [],
                    null,
                    identity: checkpointer.Identity),
                cancellationToken);

            superstep.SetError(applyError);
            superstep.SetTag(VolutaDiagnostics.TagRunStatus, nameof(GraphRunStatus.Failed));
            return new SuperstepCommit
            {
                LastNode = lastNode,
                ReadyTasks = orderedReady,
                TerminalEvent = RunEngineStreaming.Terminal(
                    options.StreamMode,
                    StreamEventKind.Failed,
                    step,
                    store,
                    applyError),
                Exception = applyError,
            };
        }

        // One post-apply snapshot shared by routing, checkpoint, and Values stream.
        var postApplySnapshot = store.SnapshotValues();
        var nodeNames = DistinctNodeNames(orderedReady);
        foreach (var nodeName in nodeNames)
        {
            store.MarkSeen(nodeName);
        }

        lastNode = orderedReady[^1].NodeName;
        var scheduled = new List<string>(executions.Count);
        var pendingSends = new List<PendingSend>();
        foreach (var execution in executions)
        {
            var resume = RunEngineExecution.ResolveResume(resumeByTaskId, execution.TaskId);
            scheduled.AddRange(
                RunEngineRouting.ResolveNextNodes(
                    topology,
                    execution.NodeName,
                    postApplySnapshot,
                    resume.Payload,
                    resume.IsResume,
                    resume.IsResume ? resumeKind : null));

            if (execution.Result is ContinueNodeResult continueResult)
            {
                foreach (var send in continueResult.Sends)
                {
                    if (!topology.Nodes.ContainsKey(send.Node))
                    {
                        var sendError = new GraphRunFailedException(
                            $"Send targets unknown node '{send.Node}' from '{execution.NodeName}'.");
                        superstep.SetError(sendError);
                        throw sendError;
                    }

                    pendingSends.Add(
                        new PendingSend
                        {
                            NodeName = send.Node,
                            Payload = send.Payload,
                            TaskId = $"{execution.NodeName}->{send.Node}:{pendingSends.Count}",
                        });
                }
            }
        }

        var nextPull = RunEngineRouting.ToPullTasks(
            topology,
            DistinctNames(ResolveJoins(topology, joinArrivals, nodeNames, scheduled)));
        IReadOnlyList<ReadyTask> readyTasks;
        if (pendingSends.Count == 0)
        {
            readyTasks = nextPull;
        }
        else
        {
            // Merge pull (already sorted) with sends; sort once by node then task id.
            var merged = new List<ReadyTask>(nextPull.Count + pendingSends.Count);
            merged.AddRange(nextPull);
            merged.AddRange(pendingSends.Select(
                static send => new ReadyTask(send.NodeName, send.TaskId, send.Payload)));

            merged.Sort(static (left, right) =>
            {
                var nodeCompare = string.CompareOrdinal(left.NodeName, right.NodeName);
                return nodeCompare != 0
                    ? nodeCompare
                    : string.CompareOrdinal(left.TaskId, right.TaskId);
            });
            readyTasks = merged;
        }

        var checkpointNextNodes = DistinctNodeNames(readyTasks);

        // Static pause AFTER output (v050): routing already produced the next ready set —
        // park with it (nothing re-runs); the step's own sends stay preserved for resume.
        if (StaticInterrupts.ShouldPauseAfter(topology, nodeNames))
        {
            await checkpointer.PutAsync(
                RunEngineSnapshots.Build(
                    options.ThreadId,
                    step,
                    GraphRunStatus.Interrupted,
                    store,
                    lastNode,
                    checkpointNextNodes,
                    pendingSends,
                    null,
                    postApplySnapshot,
                    joinArrivals: joinArrivals,
                    stopReason: RunStopReason.StaticAfter,
                    identity: checkpointer.Identity),
                cancellationToken);

            superstep.SetTag(VolutaDiagnostics.TagRunStatus, nameof(GraphRunStatus.Interrupted));
            return new SuperstepCommit
            {
                LastNode = lastNode,
                ReadyTasks = readyTasks,
                JoinArrivals = new Dictionary<string, IReadOnlyList<string>>(joinArrivals, StringComparer.Ordinal),
                TerminalEvent = RunEngineStreaming.Terminal(
                    options.StreamMode,
                    StreamEventKind.Interrupt,
                    step,
                    store),
            };
        }

        await checkpointer.PutAsync(
            RunEngineSnapshots.Build(
                options.ThreadId,
                step,
                GraphRunStatus.Running,
                store,
                lastNode,
                checkpointNextNodes,
                pendingSends,
                null,
                postApplySnapshot,
                joinArrivals: joinArrivals,
                identity: checkpointer.Identity),
            cancellationToken);

        superstep.SetTag(VolutaDiagnostics.TagRunStatus, nameof(GraphRunStatus.Running));
        return new SuperstepCommit
        {
            LastNode = lastNode,
            ReadyTasks = readyTasks,
            JoinArrivals = new Dictionary<string, IReadOnlyList<string>>(joinArrivals, StringComparer.Ordinal),
            StreamItems = [.. RunEngineStreaming.EmitCommit(
                options.StreamMode,
                step,
                nodeNames,
                writes,
                store,
                postApplySnapshot)],
        };
    }

    /// <summary>
    ///     Wait-for-all join resolution (v050): join targets reach <paramref name="scheduled" />
    ///     through their sources' static edges, but the barrier holds them until ALL sources
    ///     completed in the current epoch. Completed sources arrive in the epoch map; a fully
    ///     armed join fires (epoch resets, target stays scheduled); an armed-but-incomplete
    ///     join parks its target until the remaining sources arrive. Cycle-compatible: the
    ///     epoch resets on fire, so the next full pass of sources re-triggers the target.
    ///     Mutates <paramref name="joinArrivals" /> (engine-owned live epoch map, carried
    ///     on every commit put).
    /// </summary>
    private static IReadOnlyList<string> ResolveJoins(
        GraphTopology topology,
        Dictionary<string, IReadOnlyList<string>> joinArrivals,
        IReadOnlyList<string> completedNodes,
        List<string> scheduled)
    {
        if (topology.JoinSources.Count == 0)
        {
            return scheduled;
        }

        var fired = new List<string>();
        foreach (var nodeName in completedNodes)
        {
            foreach (var (target, sources) in topology.JoinSources)
            {
                if (!sources.Contains(nodeName))
                {
                    continue;
                }

                var arrived = joinArrivals.TryGetValue(target, out var list)
                    ? new List<string>(list)
                    : [];
                if (!arrived.Contains(nodeName))
                {
                    arrived.Add(nodeName);
                }

                if (sources.All(arrived.Contains))
                {
                    joinArrivals.Remove(target);
                    if (!fired.Contains(target))
                    {
                        fired.Add(target);
                    }
                }
                else
                {
                    joinArrivals[target] = arrived;
                }
            }
        }

        // Parked join targets leave the scheduled set; fired ones stay/enter exactly once.
        var result = new List<string>(scheduled.Count + fired.Count);
        foreach (var name in scheduled)
        {
            if (topology.JoinSources.ContainsKey(name) && !fired.Contains(name))
            {
                continue;
            }

            result.Add(name);
        }

        foreach (var target in fired)
        {
            if (!result.Contains(target))
            {
                result.Add(target);
            }
        }

        return result;
    }

    public static IReadOnlyList<string> DistinctNodeNames(IReadOnlyList<ReadyTask> tasks)
    {
        if (tasks.Count == 0)
        {
            return [];
        }

        if (tasks.Count == 1)
        {
            return [tasks[0].NodeName];
        }

        // Ready tasks are ordered by NodeName — adjacent de-dupe is O(n).
        var names = new List<string>(tasks.Count);
        string? previous = null;
        for (var index = 0; index < tasks.Count; index++)
        {
            var name = tasks[index].NodeName;
            if (previous is null || !string.Equals(previous, name, StringComparison.Ordinal))
            {
                names.Add(name);
                previous = name;
            }
        }

        return names;
    }

    public static IReadOnlyList<string> DistinctNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return [];
        }

        if (names.Count == 1)
        {
            return names;
        }

        var seen = new HashSet<string>(names.Count, StringComparer.Ordinal);
        var distinct = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (seen.Add(name))
            {
                distinct.Add(name);
            }
        }

        return distinct;
    }

    public static IReadOnlyList<PendingInterrupt> ResolvePendingInterrupts(CheckpointSnapshot checkpoint)
    {
        if (checkpoint.PendingInterrupts is { Count: > 0 } pending)
        {
            return pending;
        }

        // Legacy single-path: only InterruptPayload + NextNodes/LastNode.
        if (checkpoint.InterruptPayload is null && checkpoint.NextNodes.Count == 0)
        {
            return [];
        }

        var nodeName = checkpoint.NextNodes.Count > 0
            ? checkpoint.NextNodes[0]
            : checkpoint.LastNode ?? GraphConstants.Start;
        return
        [
            new PendingInterrupt
            {
                TaskId = nodeName,
                NodeName = nodeName,
                Payload = checkpoint.InterruptPayload,
            },
        ];
    }

    public static Dictionary<string, object?> BuildResumeMap(
        Command command,
        IReadOnlyList<PendingInterrupt> pendingInterrupts)
    {
        if (command.Resumes is { Count: > 0 } resumes)
        {
            // Progressive: only include keys that are still pending.
            var map = new Dictionary<string, object?>(resumes.Count, StringComparer.Ordinal);
            var known = new HashSet<string>(
                pendingInterrupts.Select(static item => item.TaskId),
                StringComparer.Ordinal);
            foreach (var pair in resumes)
            {
                if (known.Contains(pair.Key))
                {
                    map[pair.Key] = pair.Value;
                }
            }

            return map;
        }

        // Single-path: same payload for every pending interrupt (typically one).
        var broadcast = new Dictionary<string, object?>(pendingInterrupts.Count, StringComparer.Ordinal);
        foreach (var pending in pendingInterrupts)
        {
            broadcast[pending.TaskId] = command.Payload;
        }

        return broadcast;
    }

    public static IReadOnlyList<PendingInterrupt> SelectPendingInterrupts(
        IReadOnlyList<PendingInterrupt> pendingInterrupts,
        IReadOnlyDictionary<string, object?> resumeByTaskId)
    {
        if (pendingInterrupts.Count == 0 || resumeByTaskId.Count == 0)
        {
            return [];
        }

        var selected = new List<PendingInterrupt>(resumeByTaskId.Count);
        foreach (var pending in pendingInterrupts)
        {
            if (resumeByTaskId.ContainsKey(pending.TaskId))
            {
                selected.Add(pending);
            }
        }

        return selected;
    }

    public static IReadOnlyList<PendingInterrupt> RemainingPendingInterrupts(
        IReadOnlyList<PendingInterrupt> pendingInterrupts,
        IReadOnlyDictionary<string, object?> resumeByTaskId)
    {
        if (pendingInterrupts.Count == 0)
        {
            return [];
        }

        if (resumeByTaskId.Count == 0)
        {
            return pendingInterrupts;
        }

        var remaining = new List<PendingInterrupt>();
        foreach (var pending in pendingInterrupts)
        {
            if (!resumeByTaskId.ContainsKey(pending.TaskId))
            {
                remaining.Add(pending);
            }
        }

        return remaining;
    }

    public static IReadOnlyList<ReadyTask> ToResumeReadyTasks(IReadOnlyList<PendingInterrupt> pendingInterrupts)
    {
        if (pendingInterrupts.Count == 0)
        {
            return [];
        }

        var tasks = pendingInterrupts
            .Select(static pending => new ReadyTask(pending.NodeName, pending.TaskId, pending.TaskPayload))
            .ToList();
        tasks.Sort(static (left, right) =>
        {
            var nodeCompare = string.CompareOrdinal(left.NodeName, right.NodeName);
            return nodeCompare != 0
                ? nodeCompare
                : string.CompareOrdinal(left.TaskId, right.TaskId);
        });
        return tasks;
    }

    public static IReadOnlyList<PendingInterrupt> CollectPendingInterrupts(
        IReadOnlyList<ReadyTask> orderedReady,
        IReadOnlyList<NodeExecution> executions)
    {
        // Executions preserve orderedReady order (WhenAll / single path).
        List<PendingInterrupt>? pending = null;
        for (var index = 0; index < executions.Count; index++)
        {
            if (executions[index].Result is not InterruptNodeResult interruptResult)
            {
                continue;
            }

            pending ??= [];
            var ready = orderedReady[index];
            pending.Add(
                new PendingInterrupt
                {
                    TaskId = ready.TaskId,
                    NodeName = ready.NodeName,
                    Payload = interruptResult.Payload,
                    TaskPayload = ready.TaskPayload,
                });
        }

        return pending is null ? [] : pending;
    }
}
