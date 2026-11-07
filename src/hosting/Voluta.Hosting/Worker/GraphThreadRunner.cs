using Microsoft.Extensions.Logging;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Graph;
using Voluta.Hosting.Wake;

namespace Voluta.Hosting.Worker;

/// <summary>
///     Runs one wake against a graph resolved per thread: invoke or resume until interrupt/done/fail.
/// </summary>
/// <remarks>
///     Resolution goes through <see cref="IGraphResolver" /> at run entry: a wake with a
///     command resumes the parked thread using its stored snapshot (the resolver sees that
///     snapshot and picks the matching graph), a plain start resolves the default graph.
/// </remarks>
/// <param name="graphs">Per-thread graph resolver (host default + versioned graphs).</param>
/// <param name="checkpointer">Checkpoint store, read to resolve by snapshot identity.</param>
/// <param name="logger">Logger.</param>
public sealed class GraphThreadRunner(IGraphResolver graphs, ICheckpointer checkpointer, ILogger<GraphThreadRunner> logger)
{
    /// <summary>
    ///     Processes a single wake to a terminal disposition.
    /// </summary>
    /// <param name="wake">Start or resume signal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GraphThreadOutcome> RunAsync(ThreadWake wake, CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await checkpointer.GetAsync(wake.ThreadId, cancellationToken);
            var graph = await graphs.ResolveAsync(wake.ThreadId, snapshot, cancellationToken);
            var terminal = wake.Command is { } command
                ? await graph.ResumeInvokeAsync(wake.ThreadId, command, cancellationToken)
                : await graph.InvokeAsync(
                    wake.Input ?? [],
                    new RunOptions { ThreadId = wake.ThreadId, StreamMode = StreamMode.Events },
                    cancellationToken);

            return GraphThreadOutcomeMapper.FromTerminal(wake.ThreadId, terminal, logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Thread {ThreadId} turn cancelled", wake.ThreadId);
            return new GraphThreadOutcome
            {
                ThreadId = wake.ThreadId,
                Disposition = GraphThreadDisposition.Cancelled,
            };
        }
        catch (Exception exception)
        {
            // Invoke/Resume rethrow graph faults after writing Failed checkpoint (last-good policy).
            logger.LogError(
                exception,
                "Thread {ThreadId} failed — checkpoint is terminal Failed; do not HITL-resume",
                wake.ThreadId);
            return new GraphThreadOutcome
            {
                ThreadId = wake.ThreadId,
                Disposition = GraphThreadDisposition.Failed,
                Exception = exception,
            };
        }
    }
}

/// <summary>
///     Maps a terminal stream event to a worker disposition.
/// </summary>
file static class GraphThreadOutcomeMapper
{
    public static GraphThreadOutcome FromTerminal(
        string threadId,
        StreamEvent terminal,
        ILogger logger)
    {
        return terminal.Kind switch
        {
            StreamEventKind.Interrupt => Park(threadId, terminal, logger),
            StreamEventKind.End => Complete(threadId, terminal, logger),
            StreamEventKind.Failed => Fail(threadId, terminal, terminal.Payload as Exception, logger),
            StreamEventKind.Cancelled => new GraphThreadOutcome
            {
                ThreadId = threadId,
                Disposition = GraphThreadDisposition.Cancelled,
                Terminal = terminal,
            },
            _ => Fail(
                threadId,
                terminal,
                new InvalidOperationException($"Unexpected terminal kind {terminal.Kind} for thread {threadId}."),
                logger),
        };
    }

    private static GraphThreadOutcome Park(string threadId, StreamEvent terminal, ILogger logger)
    {
        logger.LogInformation(
            "Thread {ThreadId} parked at interrupt (step {Step})",
            threadId,
            terminal.Step);
        return new GraphThreadOutcome
        {
            ThreadId = threadId,
            Disposition = GraphThreadDisposition.Parked,
            Terminal = terminal,
        };
    }

    private static GraphThreadOutcome Complete(string threadId, StreamEvent terminal, ILogger logger)
    {
        logger.LogInformation("Thread {ThreadId} completed", threadId);
        return new GraphThreadOutcome
        {
            ThreadId = threadId,
            Disposition = GraphThreadDisposition.Completed,
            Terminal = terminal,
        };
    }

    private static GraphThreadOutcome Fail(
        string threadId,
        StreamEvent? terminal,
        Exception? exception,
        ILogger logger)
    {
        logger.LogError(
            exception,
            "Thread {ThreadId} failed at stream terminal — apply dead-letter / alert policy here",
            threadId);
        return new GraphThreadOutcome
        {
            ThreadId = threadId,
            Disposition = GraphThreadDisposition.Failed,
            Terminal = terminal,
            Exception = exception,
        };
    }
}
