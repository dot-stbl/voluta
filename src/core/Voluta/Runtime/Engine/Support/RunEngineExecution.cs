using System.Diagnostics;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Streaming;
using Voluta.Diagnostics;
using Voluta.Exceptions;
using Voluta.Exceptions.Run;
using Voluta.Graph;
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

    public static async Task<ReadyExecutionOutcome> TryExecuteReadyAsync(
        GraphTopology topology,
        IReadOnlyList<ReadyTask> orderedReady,
        IReadOnlyDictionary<string, object?> snapshot,
        IReadOnlyDictionary<string, object?>? resumeByTaskId,
        string? resumeKind,
        string threadId,
        Func<string, IStreamWriter>? streamWriterFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            if (orderedReady.Count == 1)
            {
                var resume = ResolveResume(resumeByTaskId, orderedReady[0].TaskId);
                var single = await RunEngineExecutionHelpers.ExecuteOneAsync(
                    topology,
                    orderedReady[0],
                    snapshot,
                    resume.Payload,
                    resume.IsResume,
                    resume.IsResume ? resumeKind : null,
                    threadId,
                    streamWriterFactory,
                    cancellationToken);
                return new ReadyExecutionOutcome { Executions = [single] };
            }

            // WhenAll preserves input order — CollectWrites relies on that (no re-sort).
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
                    cancellationToken);
            }

            return new ReadyExecutionOutcome
            {
                Executions = await Task.WhenAll(tasks)
            };
        }
        catch (OperationCanceledException exception)
        {
            return new ReadyExecutionOutcome
            {
                Cancelled = true,
                Exception = exception
            };
        }
        catch (GraphException exception)
        {
            return new ReadyExecutionOutcome { Failure = exception };
        }
        catch (Exception exception)
        {
            return new ReadyExecutionOutcome
            {
                Failure = new GraphRunFailedException(
                    $"Node execution failed: {exception.Message}",
                    exception)
            };
        }
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
    ///     Collects writes in execution order. Callers must pass executions ordered by
    ///     (NodeName, TaskId) — matching <see cref="TryExecuteReadyAsync" /> / WhenAll order.
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
            var execution = executions[index];
            if (execution.Result is not ContinueNodeResult continueResult)
            {
                continue;
            }

            foreach (var write in continueResult.Writes)
            {
                writes.Add(new TaskChannelWrite(execution.TaskId, write));
            }
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
}

/// <summary>
///     Per-ready-task execution body (file-static to avoid private methods on the helper type).
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
            resumeKind);
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
        catch (Exception exception) when (exception is not GraphException)
        {
            scope.SetError(exception);
            throw new GraphRunFailedException(
                $"Node '{readyTask.NodeName}' threw: {exception.Message}",
                exception);
        }
        catch (GraphException graphException)
        {
            scope.SetError(graphException);
            throw;
        }
    }
}
