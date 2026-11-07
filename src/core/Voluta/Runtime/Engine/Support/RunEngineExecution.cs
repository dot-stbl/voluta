using System.Diagnostics;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Streaming;
using Voluta.Diagnostics;
using Voluta.Exceptions;
using Voluta.Exceptions.Run;
using Voluta.Graph;
using Voluta.Runtime.Engine.Support;
using Voluta.Runtime.Engine.Tasks;

namespace Voluta.Runtime.Engine.Support;

/// <summary>
///     Parallel node execution helpers.
/// </summary>
internal static class RunEngineExecution
{
    /// <summary>
    ///     Per-task resume map lookup: key present (even with a null payload) is a resume.
    /// </summary>
    internal readonly record struct ResumeResolution(object? Payload, bool IsResume);

    /// <summary>
    ///     Starts every ready task concurrently, then drains completions sequentially
    ///     (WhenEach-style, batch-merged): each drained batch issues one guarded progress
    ///     put (<see cref="SuperstepProgress" />) before the barrier. The first fault wins
    ///     exactly like the former <c>Task.WhenAll</c> semantics; remaining bodies still
    ///     run to completion before the outcome is produced.
    /// </summary>
    public static async Task<ReadyExecutionOutcome> TryExecuteReadyAsync(
        GraphTopology topology,
        IReadOnlyList<ReadyTask> orderedReady,
        IReadOnlyDictionary<string, object?> snapshot,
        IReadOnlyDictionary<string, object?>? resumeByTaskId,
        string? resumeKind,
        string threadId,
        Func<string, IStreamWriter>? streamWriterFactory,
        RunCheckpointWriter checkpointer,
        ChannelStore store,
        long step,
        string? lastNode,
        IReadOnlyDictionary<string, IReadOnlyList<string>> joinArrivals,
        CancellationToken cancellationToken)
    {
        var progress = new SuperstepProgress(
            threadId,
            step,
            lastNode,
            DistinctNodeNames(orderedReady),
            StepSends(orderedReady),
            checkpointer,
            store,
            joinArrivals);

        // Task bodies run concurrently (retry loop lives inside each body); completions
        // are collected by index so executions keep orderedReady order (barrier + routing).
        var tasks = new Task<NodeExecution>[orderedReady.Count];
        for (var index = 0; index < orderedReady.Count; index++)
        {
            var readyTask = orderedReady[index];
            var resume = ResolveResume(resumeByTaskId, readyTask.TaskId);
            tasks[index] = RunEngineExecutionHelpers.ExecuteOneAsync(
                topology,
                readyTask,
                snapshot,
                resume.Payload,
                resume.IsResume,
                resume.IsResume ? resumeKind : null,
                threadId,
                streamWriterFactory,
                progress,
                cancellationToken);
        }

        var executions = new NodeExecution[orderedReady.Count];
        Exception? firstFault = null;
        var pendingIndexes = new List<int>(orderedReady.Count);
        for (var index = 0; index < orderedReady.Count; index++)
        {
            pendingIndexes.Add(index);
        }

        while (pendingIndexes.Count > 0)
        {
            // Wait for at least one completion, then merge every completion that drained
            // between awaits into this batch (design: ≤ 1 extra put per batch).
            await Task.WhenAny(pendingIndexes.Select(index => tasks[index]));
            var batchCompletions = 0;
            for (var position = pendingIndexes.Count - 1; position >= 0; position--)
            {
                var index = pendingIndexes[position];
                var task = tasks[index];
                if (!task.IsCompleted)
                {
                    continue;
                }

                pendingIndexes.RemoveAt(position);
                NodeExecution execution;
                try
                {
                    execution = await task;
                }
                catch (Exception exception)
                {
                    // First fault decides the outcome (matches WhenAll semantics); the
                    // remaining bodies finish on their own and their writes are dropped.
                    firstFault ??= exception;
                    progress.Suppress();
                    continue;
                }

                executions[index] = execution;
                if (execution.Result is ContinueNodeResult)
                {
                    // Only successful continuations count as durable task completion —
                    // interrupted tasks are parked (they must re-run on resume).
                    progress.Complete(execution.TaskId, CollectWrites(execution));
                    batchCompletions++;
                }
            }

            if (batchCompletions > 0)
            {
                await progress.FlushAsync(cancellationToken);
            }
        }

        return firstFault is null
            ? new ReadyExecutionOutcome { Executions = executions, Progress = progress }
            : firstFault is OperationCanceledException
                ? new ReadyExecutionOutcome { Cancelled = true, Exception = firstFault }
                : new ReadyExecutionOutcome { Failure = AsFailure(firstFault) };
    }

    public static GraphException? TryApplyWrites(
        ChannelStore store,
        IReadOnlyList<TaskChannelWrite> writes)
    {
        try
        {
            store.ApplyWrites(writes);
            return null;
        }
        catch (GraphException exception)
        {
            return exception;
        }
    }

    /// <summary>
    ///     Converts persisted pending writes (progress buffer on a crashed step's snapshot)
    ///     back to task-attributed writes for the restore-time apply.
    /// </summary>
    public static IReadOnlyList<TaskChannelWrite> FromPendingWrites(
        IReadOnlyList<PendingWrite> pendingWrites)
    {
        return pendingWrites
            .Select(static pending => new TaskChannelWrite(
                pending.TaskId,
                new ChannelWrite(pending.ChannelName, pending.Value)))
            .ToArray();
    }

    /// <summary>
    ///     Collects one task's writes in execution order.
    /// </summary>
    public static IReadOnlyList<TaskChannelWrite> CollectWrites(NodeExecution execution)
    {
        return execution.Result is not ContinueNodeResult continueResult
            ? []
            : [.. continueResult.Writes.Select(write => new TaskChannelWrite(execution.TaskId, write))];
    }

    /// <summary>
    ///     Collects writes in execution order. Callers must pass executions ordered by
    ///     (NodeName, TaskId) — matching the index-ordered drain in
    ///     <see cref="TryExecuteReadyAsync" />.
    /// </summary>
    public static IReadOnlyList<TaskChannelWrite> CollectWrites(IReadOnlyList<NodeExecution> executions)
    {
        var capacity = 0;
        for (var index = 0; index < executions.Count; index++)
        {
            if (executions[index].Result is ContinueNodeResult continueResult)
            {
                capacity += continueResult.Writes.Count;
            }
        }

        if (capacity == 0)
        {
            return [];
        }

        var writes = new List<TaskChannelWrite>(capacity);
        for (var index = 0; index < executions.Count; index++)
        {
            writes.AddRange(CollectWrites(executions[index]));
        }

        return writes;
    }

    /// <summary>
    ///     Per-task resume map lookup: key present (even with a null payload) is a resume.
    /// </summary>
    public static ResumeResolution ResolveResume(
        IReadOnlyDictionary<string, object?>? resumeByTaskId,
        string taskId)
    {
        return resumeByTaskId is not null && resumeByTaskId.TryGetValue(taskId, out var payload)
            ? new ResumeResolution(payload, IsResume: true)
            : new ResumeResolution(null, IsResume: false);
    }

    /// <summary>
    ///     Distinct node names of the ready set (progress put NextNodes — the pull tasks
    ///     scheduled in this step).
    /// </summary>
    public static IReadOnlyList<string> DistinctNodeNames(IReadOnlyList<ReadyTask> tasks)
    {
        if (tasks.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(tasks.Count, StringComparer.Ordinal);
        var names = new List<string>(tasks.Count);
        foreach (var task in tasks)
        {
            if (seen.Add(task.NodeName))
            {
                names.Add(task.NodeName);
            }
        }

        return names;
    }

    /// <summary>
    ///     Send tasks of the ready set (TaskId differs from the node name) as PendingSend,
    ///     so progress puts can reconstruct the step's unfinished send tasks on crash.
    /// </summary>
    public static IReadOnlyList<PendingSend> StepSends(IReadOnlyList<ReadyTask> tasks)
    {
        List<PendingSend>? sends = null;
        foreach (var task in tasks)
        {
            if (string.Equals(task.TaskId, task.NodeName, StringComparison.Ordinal))
            {
                continue;
            }

            sends ??= [];
            sends.Add(
                new PendingSend
                {
                    NodeName = task.NodeName,
                    Payload = task.TaskPayload,
                    TaskId = task.TaskId,
                });
        }

        return sends ?? [];
    }

    private static GraphException AsFailure(Exception fault)
    {
        return fault as GraphException
               ?? new GraphRunFailedException(
                   $"Node execution failed: {fault.Message}",
                   fault);
    }
}

/// <summary>
///     Per-ready-task execution body (file-static to avoid private methods on the helper type).
///     Carries the node retry loop (item 1): attempts up to the node's
///     <see cref="Voluta.Abstractions.Runtime.RetryPolicy.MaxAttempts" />, attempt number visible on the context,
///     next attempt persisted via a guarded progress put before the backoff delay.
/// </summary>
file static class RunEngineExecutionHelpers
{
    public static async Task<NodeExecution> ExecuteOneAsync(
        GraphTopology topology,
        ReadyTask readyTask,
        IReadOnlyDictionary<string, object?> snapshot,
        object? resumePayload,
        bool isResume,
        string? resumeKind,
        string threadId,
        Func<string, IStreamWriter>? streamWriterFactory,
        SuperstepProgress progress,
        CancellationToken cancellationToken)
    {
        if (!topology.Nodes.TryGetValue(readyTask.NodeName, out var handler))
        {
            throw new GraphRunFailedException($"Unknown ready node '{readyTask.NodeName}'.");
        }

        var tags = new TagList { { VolutaDiagnostics.TagNodeName, readyTask.NodeName } };
        using var scope = ActivityScope.Start(
            VolutaDiagnostics.NodeExecuteActivityName,
            VolutaDiagnostics.NodeDuration,
            tags);

        var streamWriter = streamWriterFactory?.Invoke(readyTask.NodeName);
        var policy = topology.NodeRetries.TryGetValue(readyTask.NodeName, out var retry) && retry is not null
            ? retry
            : null;
        var attempt = Math.Max(readyTask.StartAttempt, 0);
        attempt = attempt > 0 ? attempt : 1;
        while (true)
        {
            var context = new GraphContext(
                readyTask.NodeName,
                snapshot,
                resumePayload,
                readyTask.TaskPayload,
                topology.Services,
                threadId,
                readyTask.TaskId,
                streamWriter,
                isResume,
                resumeKind,
                attempt);
            try
            {
                var result = await handler(context, cancellationToken);
                if (result is InterruptNodeResult)
                {
                    VolutaDiagnostics.InterruptCount.Add(1, tags);
                }

                return new NodeExecution(readyTask.NodeName, readyTask.TaskId, result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                scope.SetError(exception);
                var wrapped = exception as GraphException
                    ?? new GraphRunFailedException(
                        $"Node '{readyTask.NodeName}' threw: {exception.Message}",
                        exception);
                if (policy is null
                    || attempt >= policy.MaxAttempts
                    || !policy.ShouldRetry(exception)
                    || progress.Suppressed)
                {
                    throw wrapped;
                }

                // Persist the next attempt BEFORE the backoff delay: a crash during the
                // delay restores the snapshot carrying TaskAttempts[taskId] = attempt + 1.
                await progress.RecordRetryAsync(readyTask.TaskId, attempt + 1, cancellationToken);
                await Task.Delay(policy.DelayFor(attempt), cancellationToken);
                attempt++;
            }
        }
    }
}
