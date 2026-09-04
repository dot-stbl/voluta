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
    ///     Runs from input (or empty) until terminal, yielding stream events for the selected mode.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        IEnumerable<ChannelWrite> input,
        RunOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
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

        await checkpointer.PutAsync(
            RunEngineSnapshots.Build(
                options.ThreadId,
                step,
                GraphRunStatus.Running,
                store,
                lastNode,
                nextNodes,
                [],
                null),
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
                           options,
                           store,
                           RunEngineRouting.ToPullTasks(topology, nextNodes),
                           step,
                           lastNode,
                           resumeByTaskId: null,
                           resumeKind: null,
                           cancellationToken))
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
        StreamMode streamMode,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        CommandTaxonomy.EnsureValid(command);

        var checkpoint = await checkpointer.GetAsync(threadId, cancellationToken) ??
                         throw new GraphInvalidResumeException(
                             $"No checkpoint found for thread '{threadId}'.");
        if (checkpoint.Status != GraphRunStatus.Interrupted)
        {
            throw new GraphInvalidResumeException(
                $"Thread '{threadId}' is not interrupted (status={checkpoint.Status}).");
        }

        var pendingInterrupts = RunEngineLoopHelpers.ResolvePendingInterrupts(checkpoint);
        CommandTaxonomy.EnsureMultiInterruptResumes(command, pendingInterrupts);

        var store = new ChannelStore(topology.Channels);
        store.Restore(checkpoint.ChannelValues, checkpoint.ChannelVersions, checkpoint.VersionsSeen);

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

        var options = new RunOptions { ThreadId = threadId, StreamMode = streamMode };

        if (RunEngineStreaming.EmitsLifecycle(streamMode))
        {
            yield return new StreamEvent
            {
                Mode = streamMode,
                Kind = StreamEventKind.Start,
                Step = checkpoint.Step
            };
        }

        // Progressive multi-interrupt: re-run only resumed task ids; keep remaining pending.
        if (remainingAfterResume.Count > 0)
        {
            await foreach (var item in RunPartialMultiInterruptResumeAsync(
                               options,
                               store,
                               readyTasks,
                               remainingAfterResume,
                               checkpoint,
                               resumeByTaskId,
                               command.Kind,
                               cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        await foreach (var item in RunLoopAsync(
                           options,
                           store,
                           readyTasks,
                           checkpoint.Step,
                           checkpoint.LastNode,
                           resumeByTaskId,
                           command.Kind,
                           cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    ///     Continues a Running thread from the latest checkpoint (after UpdateState / Fork).
    ///     Does not re-inject HITL resume payload; use <see cref="ResumeAsync" /> for Interrupted.
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
        StreamMode streamMode,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await checkpointer.GetAsync(threadId, cancellationToken)
                         ?? throw new GraphThreadNotFoundException(
                             $"No checkpoint found for thread '{threadId}'.");
        if (checkpoint.Status != GraphRunStatus.Running)
        {
            throw new GraphInvalidContinueException(
                $"Thread '{threadId}' is not Running (status={checkpoint.Status}); " +
                "use ResumeAsync for Interrupted or UpdateStateAsync after Failed/Cancelled.");
        }

        var store = new ChannelStore(topology.Channels);
        store.Restore(checkpoint.ChannelValues, checkpoint.ChannelVersions, checkpoint.VersionsSeen);

        var nextNodes = checkpoint.NextNodes;
        if (nextNodes.Count == 0 && checkpoint.PendingSends.Count == 0)
        {
            throw new GraphInvalidContinueException(
                $"Thread '{threadId}' has no next nodes or pending sends to continue.");
        }

        var options = new RunOptions { ThreadId = threadId, StreamMode = streamMode };

        if (RunEngineStreaming.EmitsLifecycle(streamMode))
        {
            yield return new StreamEvent
            {
                Mode = streamMode,
                Kind = StreamEventKind.Start,
                Step = checkpoint.Step
            };
        }

        // A2: pending sends only — do not re-pull NextNodes alongside (avoids re-exec map/side-effect).
        if (checkpoint.PendingSends.Count > 0)
        {
            await foreach (var item in ContinueWithPendingSendsAsync(
                               options,
                               store,
                               nextNodes: [],
                               checkpoint,
                               cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        await foreach (var item in RunLoopAsync(
                           options,
                           store,
                           RunEngineRouting.ToPullTasks(topology, nextNodes),
                           checkpoint.Step,
                           checkpoint.LastNode,
                           resumeByTaskId: null,
                           resumeKind: null,
                           cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    ///     Progressive multi-interrupt: run only resumed tasks for one superstep, then park
    ///     with remaining <see cref="PendingInterrupt" /> list (status Interrupted).
    /// </summary>
    private async IAsyncEnumerable<StreamEvent> RunPartialMultiInterruptResumeAsync(
        RunOptions options,
        ChannelStore store,
        IReadOnlyList<ReadyTask> readyTasks,
        IReadOnlyList<PendingInterrupt> remainingInterrupts,
        CheckpointSnapshot checkpoint,
        IReadOnlyDictionary<string, object?> resumeByTaskId,
        string? resumeKind,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var step = checkpoint.Step + 1;
        if (step > topology.RecursionLimit)
        {
            var outOfSteps = new GraphOutOfStepsException(topology.RecursionLimit, step);
            await checkpointer.PutAsync(
                RunEngineSnapshots.Build(
                    options.ThreadId,
                    step,
                    GraphRunStatus.Failed,
                    store,
                    checkpoint.LastNode,
                    RunEngineLoopHelpers.DistinctNodeNames(readyTasks),
                    [],
                    null),
                cancellationToken);
            yield return RunEngineStreaming.Terminal(
                options.StreamMode,
                StreamEventKind.Failed,
                step,
                store,
                outOfSteps);
            throw outOfSteps;
        }

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
                pendingInterrupts: remainingInterrupts),
            cancellationToken);

        yield return RunEngineStreaming.Terminal(
            options.StreamMode,
            StreamEventKind.Interrupt,
            step,
            store,
            payload: firstPayload);
    }

    private async IAsyncEnumerable<StreamEvent> ContinueWithPendingSendsAsync(
        RunOptions options,
        ChannelStore store,
        IReadOnlyList<string> nextNodes,
        CheckpointSnapshot checkpoint,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var pull = RunEngineRouting.ToPullTasks(topology, nextNodes);
        var ready = new List<ReadyTask>(pull.Count + checkpoint.PendingSends.Count);
        ready.AddRange(pull);
        foreach (var send in checkpoint.PendingSends)
        {
            ready.Add(new ReadyTask(send.NodeName, send.TaskId, send.Payload));
        }

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
                        interruptPayload: null),
                    cancellationToken);

                yield return RunEngineStreaming.Terminal(
                    streamMode,
                    StreamEventKind.End,
                    step,
                    store);
                yield break;
            }

            step++;
            if (step > topology.RecursionLimit)
            {
                var outOfSteps = new GraphOutOfStepsException(topology.RecursionLimit, step);
                var failedNodeNames = RunEngineLoopHelpers.DistinctNodeNames(readyTasks);
                await checkpointer.PutAsync(
                    RunEngineSnapshots.Build(
                        options.ThreadId,
                        step,
                        GraphRunStatus.Failed,
                        store,
                        lastNode,
                        failedNodeNames,
                        [],
                        null),
                    cancellationToken);

                yield return RunEngineStreaming.Terminal(
                    streamMode,
                    StreamEventKind.Failed,
                    step,
                    store,
                    outOfSteps);
                throw outOfSteps;
            }

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
        RunOptions options,
        ChannelStore store,
        IReadOnlyList<ReadyTask> initialReady,
        long step,
        string? lastNode,
        IReadOnlyDictionary<string, object?>? resumeByTaskId,
        string? resumeKind,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var readyTasks = initialReady;
        var isFirstResumeStep = resumeByTaskId is not null;

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
                        interruptPayload: null),
                    cancellationToken);

                yield return RunEngineStreaming.Terminal(
                    options.StreamMode,
                    StreamEventKind.End,
                    step,
                    store);
                yield break;
            }

            step++;
            if (step > topology.RecursionLimit)
            {
                var outOfSteps = new GraphOutOfStepsException(topology.RecursionLimit, step);
                var failedNodeNames = RunEngineLoopHelpers.DistinctNodeNames(readyTasks);
                await checkpointer.PutAsync(
                    RunEngineSnapshots.Build(
                        options.ThreadId,
                        step,
                        GraphRunStatus.Failed,
                        store,
                        lastNode,
                        failedNodeNames,
                        [],
                        null),
                    cancellationToken);

                yield return RunEngineStreaming.Terminal(
                    options.StreamMode,
                    StreamEventKind.Failed,
                    step,
                    store,
                    outOfSteps);
                throw outOfSteps;
            }

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
                               cancellationToken))
            {
                if (item.Commit is { } commit)
                {
                    lastNode = commit.LastNode;
                    readyTasks = commit.ReadyTasks;

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
///     Hot-path helpers for ready-set node name extraction and superstep body (file-static).
/// </summary>
file static class RunEngineLoopHelpers
{
    public static async IAsyncEnumerable<SuperstepStreamItem> RunSuperstepStreamingAsync(
        GraphTopology topology,
        ICheckpointer checkpointer,
        RunOptions options,
        ChannelStore store,
        IReadOnlyList<ReadyTask> orderedReady,
        long step,
        string? lastNode,
        IReadOnlyDictionary<string, object?>? resumeByTaskId,
        string? resumeKind,
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
        ICheckpointer checkpointer,
        RunOptions options,
        ChannelStore store,
        IReadOnlyList<ReadyTask> orderedReady,
        long step,
        string? lastNode,
        IReadOnlyDictionary<string, object?>? resumeByTaskId,
        string? resumeKind,
        string threadId,
        Func<string, IStreamWriter>? streamWriterFactory,
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
                    null),
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
                    null),
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
            // (barrier holds until all pending interrupts resume).
            lastNode = pendingInterrupts[^1].NodeName;
            var nextNodeNames = DistinctNames(
                [.. pendingInterrupts.Select(static item => item.NodeName)]);
            var primaryPayload = pendingInterrupts[0].Payload;
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
                    pendingInterrupts: pendingInterrupts),
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
                    null),
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

        var nextPull = RunEngineRouting.ToPullTasks(topology, DistinctNames(scheduled));
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
            foreach (var send in pendingSends)
            {
                merged.Add(new ReadyTask(send.NodeName, send.TaskId, send.Payload));
            }

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
                postApplySnapshot),
            cancellationToken);

        superstep.SetTag(VolutaDiagnostics.TagRunStatus, nameof(GraphRunStatus.Running));
        return new SuperstepCommit
        {
            LastNode = lastNode,
            ReadyTasks = readyTasks,
            StreamItems = [.. RunEngineStreaming.EmitCommit(
                options.StreamMode,
                step,
                nodeNames,
                writes,
                store,
                postApplySnapshot)],
        };
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

    public static IReadOnlyList<string> DistinctNames(List<string> names)
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

        var tasks = new List<ReadyTask>(pendingInterrupts.Count);
        foreach (var pending in pendingInterrupts)
        {
            tasks.Add(new ReadyTask(pending.NodeName, pending.TaskId, pending.TaskPayload));
        }

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
