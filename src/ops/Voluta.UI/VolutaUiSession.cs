using System.Collections.Concurrent;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Abstractions.Topology;
using Voluta.Graph;

namespace Voluta.UI;

/// <summary>
///     In-process session for the ops UI: resolves the executing graph per operation via
///     <see cref="IGraphResolver" /> and owns the shared checkpointer.
/// </summary>
/// <remarks>
///     Graph resolution follows the host resolver contract: null snapshot → default graph;
///     a snapshot carrying a graph identity pair → the matching graph. Thread checkpoint reads
///     go straight to the checkpointer (no resolution needed).
///     Constructed with <see cref="VolutaUiSession(CompiledGraph, ICheckpointer)" /> the session
///     keeps the shipped single-graph behavior: <see cref="Graph" /> / <see cref="Topology" />
///     work immediately. In resolver mode (<see cref="VolutaUiSession(IGraphResolver, ICheckpointer)" />)
///     those sync members surface the default graph only after the first resolution (cached;
///     any session operation or <see cref="GetTopologyAsync" /> populates it) — before that they
///     throw rather than block on sync-over-async.
/// </remarks>
/// <param name="graphs">Per-thread graph resolver.</param>
/// <param name="checkpointer">Same checkpointer the resolved graphs were compiled with.</param>
public sealed class VolutaUiSession(IGraphResolver graphs, ICheckpointer checkpointer)
{
    private readonly ConcurrentDictionary<string, byte> knownThreads = new(StringComparer.Ordinal);
    private readonly CompiledGraph? directGraph;
    private CompiledGraph? cachedDefaultGraph;

    /// <summary>
    ///     Shipped 0.4.0 constructor: binds the session to one compiled graph (wrapped in a
    ///     <see cref="SingleGraphResolver" />) — <see cref="Graph" /> / <see cref="Topology" />
    ///     work exactly as before.
    /// </summary>
    /// <param name="graph">Runnable graph.</param>
    /// <param name="checkpointer">Same checkpointer used at compile time.</param>
    public VolutaUiSession(CompiledGraph graph, ICheckpointer checkpointer)
        : this(new SingleGraphResolver(graph), checkpointer)
    {
        directGraph = graph;
        cachedDefaultGraph = graph;
    }

    /// <summary>
    ///     Compiled graph. On the single-graph path (shipped constructor) always available;
    ///     in resolver mode this is the default graph and requires a prior resolution (see the
    ///     type remarks) — it never blocks on synchronous resolution.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Resolver mode and no graph has been resolved yet.
    /// </exception>
    public CompiledGraph Graph
    {
        get
        {
            return directGraph
                ?? cachedDefaultGraph
                ?? throw new InvalidOperationException(
                    "VolutaUiSession was built with an IGraphResolver, so the default graph is only " +
                    "available after the first resolution. Call GetTopologyAsync (or any per-thread " +
                    "operation) first, or use the async session APIs.");
        }
    }

    /// <summary>
    ///     Checkpoint store.
    /// </summary>
    public ICheckpointer Checkpointer { get; } = checkpointer;

    /// <summary>
    ///     Topology export for the topology screen (description of <see cref="Graph" />).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Resolver mode and no graph has been resolved yet.
    /// </exception>
    public GraphDescription Topology => Graph.Describe();

    /// <summary>
    ///     Resolves the graph for a thread: by stored snapshot identity, or the default graph
    ///     when the thread has no checkpoint.
    /// </summary>
    private async Task<CompiledGraph> ResolveGraphAsync(string threadId, CancellationToken cancellationToken)
    {
        var snapshot = await Checkpointer.GetAsync(threadId, cancellationToken);
        return await graphs.ResolveAsync(threadId, snapshot, cancellationToken);
    }

    /// <summary>
    ///     Resolves the default graph (new thread / no snapshot) — used by topology export.
    ///     The result is cached so the sync <see cref="Graph" /> / <see cref="Topology" />
    ///     members can serve it afterwards without sync-over-async.
    /// </summary>
    private async Task<CompiledGraph> ResolveDefaultGraphAsync(CancellationToken cancellationToken)
    {
        if (directGraph is { } graph)
        {
            return graph;
        }

        if (cachedDefaultGraph is { } cached)
        {
            return cached;
        }

        var resolved = await graphs.ResolveAsync(string.Empty, null, cancellationToken);
        cachedDefaultGraph = resolved;
        return resolved;
    }

    /// <summary>
    ///     Topology export for the topology screen (resolved default graph).
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Topology description of the host default graph.</returns>
    public async Task<GraphDescription> GetTopologyAsync(CancellationToken cancellationToken = default)
    {
        var graph = await ResolveDefaultGraphAsync(cancellationToken);
        return graph.Describe();
    }

    /// <summary>
    ///     Remembers a thread id after an invoke/stream so the HITL queue can list it.
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    public void TrackThread(string threadId)
    {
        knownThreads[threadId] = 0;
    }

    /// <summary>
    ///     Lists known threads with latest checkpoint status (for the inspector list).
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Thread summaries ordered by id.</returns>
    /// <remarks>
    ///     Merges in-process <see cref="TrackThread" /> ids with
    ///     <see cref="IThreadDiscovery.ListThreadIdsAsync" /> when the checkpointer implements
    ///     discovery (File / EF / S3 / InMemory). After a process restart, durable stores still
    ///     surface threads without re-tracking.
    /// </remarks>
    public async Task<IReadOnlyList<ThreadSummary>> ListThreadsAsync(
        CancellationToken cancellationToken = default)
    {
        var threadIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var threadId in knownThreads.Keys)
        {
            threadIds.Add(threadId);
        }

        if (Checkpointer is IThreadDiscovery discovery)
        {
            foreach (var threadId in await discovery.ListThreadIdsAsync(cancellationToken))
            {
                threadIds.Add(threadId);
            }
        }

        var result = new List<ThreadSummary>(threadIds.Count);
        foreach (var threadId in threadIds)
        {
            var snapshot = await Checkpointer.GetAsync(threadId, cancellationToken);
            if (snapshot is null)
            {
                result.Add(
                    new ThreadSummary
                    {
                        ThreadId = threadId,
                        Status = "Unknown",
                        Step = 0,
                    });
                continue;
            }

            string? goal = null;
            if (snapshot.ChannelValues.TryGetValue("goal", out var goalValue) && goalValue is not null)
            {
                goal = goalValue.ToString();
            }

            result.Add(
                new ThreadSummary
                {
                    ThreadId = threadId,
                    Status = snapshot.Status.ToString(),
                    Step = snapshot.Step,
                    LastNode = snapshot.LastNode,
                    Goal = goal,
                });
        }

        return result;
    }

    /// <summary>
    ///     Lists tracked thread ids that currently have an interrupted checkpoint.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Interrupted thread summaries.</returns>
    public async Task<IReadOnlyList<HitlThreadSummary>> ListInterruptedAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<HitlThreadSummary>();
        foreach (var thread in await ListThreadsAsync(cancellationToken))
        {
            if (thread.Status != GraphRunStatus.Interrupted.ToString())
            {
                continue;
            }

            var snapshot = await Checkpointer.GetAsync(thread.ThreadId, cancellationToken);
            if (snapshot is null)
            {
                continue;
            }

            result.Add(
                new HitlThreadSummary
                {
                    ThreadId = thread.ThreadId,
                    Step = snapshot.Step,
                    InterruptPayload = snapshot.InterruptPayload?.ToString(),
                    LastNode = snapshot.LastNode,
                });
        }

        return result;
    }

    /// <summary>
    ///     Loads the latest checkpoint for a thread.
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Checkpoint or null when missing.</returns>
    public Task<CheckpointSnapshot?> GetCheckpointAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        return Checkpointer.GetAsync(threadId, cancellationToken);
    }

    /// <summary>
    ///     Loads the latest host-facing thread state (time-travel read).
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>State or null when missing.</returns>
    public async Task<ThreadSnapshot?> GetStateAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        var graph = await ResolveGraphAsync(threadId, cancellationToken);
        return await graph.GetStateAsync(threadId, cancellationToken);
    }

    /// <summary>
    ///     Lists host-facing states ordered by step (time-travel history).
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Steps oldest-first.</returns>
    public async Task<IReadOnlyList<ThreadSnapshot>> GetHistoryAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        var graph = await ResolveGraphAsync(threadId, cancellationToken);
        return await graph.GetHistoryAsync(threadId, cancellationToken);
    }

    /// <summary>
    ///     Resumes an interrupted thread with a command (drains to terminal event).
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    /// <param name="command">Resume command.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Terminal stream event.</returns>
    public async Task<StreamEvent> ResumeAsync(
        string threadId,
        Command command,
        CancellationToken cancellationToken = default)
    {
        TrackThread(threadId);
        var graph = await ResolveGraphAsync(threadId, cancellationToken);
        return await graph.ResumeInvokeAsync(threadId, command, cancellationToken);
    }

    /// <summary>
    ///     Streams resume events for an interrupted thread (SSE source).
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    /// <param name="command">Resume command.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Live stream of graph events.</returns>
    public async IAsyncEnumerable<StreamEvent> StreamResumeAsync(
        string threadId,
        Command command,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        TrackThread(threadId);
        var graph = await ResolveGraphAsync(threadId, cancellationToken);
        await foreach (var item in graph.ResumeAsync(threadId, command, StreamMode.Events, cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    ///     Streams a new invoke for a thread (SSE source).
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    /// <param name="input">Seed channel writes.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Live stream of graph events.</returns>
    public async IAsyncEnumerable<StreamEvent> StreamInvokeAsync(
        string threadId,
        IEnumerable<ChannelWrite> input,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        TrackThread(threadId);
        var graph = await ResolveGraphAsync(threadId, cancellationToken);
        await foreach (var item in graph.StreamAsync(
                           input,
                           new RunOptions { ThreadId = threadId, StreamMode = StreamMode.Events },
                           cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    ///     Continues a Running thread from the latest checkpoint (drains to terminal event).
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Terminal stream event.</returns>
    public async Task<StreamEvent> ContinueAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        TrackThread(threadId);
        var graph = await ResolveGraphAsync(threadId, cancellationToken);
        return await graph.ContinueInvokeAsync(threadId, cancellationToken);
    }

    /// <summary>
    ///     Streams continue events for a Running thread (SSE source).
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Live stream of graph events.</returns>
    public async IAsyncEnumerable<StreamEvent> StreamContinueAsync(
        string threadId,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        TrackThread(threadId);
        var graph = await ResolveGraphAsync(threadId, cancellationToken);
        await foreach (var item in graph.ContinueAsync(threadId, StreamMode.Events, cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    ///     Applies channel writes to the latest checkpoint (time-travel edit).
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    /// <param name="writes">Channel writes to merge.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Host-facing snapshot after the edit.</returns>
    public async Task<ThreadSnapshot> UpdateStateAsync(
        string threadId,
        IEnumerable<ChannelWrite> writes,
        CancellationToken cancellationToken = default)
    {
        TrackThread(threadId);
        var graph = await ResolveGraphAsync(threadId, cancellationToken);
        return await graph.UpdateStateAsync(threadId, writes, cancellationToken);
    }

    /// <summary>
    ///     Forks a history step onto a new thread id.
    /// </summary>
    /// <param name="sourceThreadId">Source thread.</param>
    /// <param name="step">History step to copy.</param>
    /// <param name="newThreadId">Destination thread id.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Host-facing snapshot on the new thread.</returns>
    public async Task<ThreadSnapshot> ForkAsync(
        string sourceThreadId,
        long step,
        string newThreadId,
        CancellationToken cancellationToken = default)
    {
        TrackThread(newThreadId);
        var graph = await ResolveGraphAsync(sourceThreadId, cancellationToken);
        return await graph.ForkAsync(sourceThreadId, step, newThreadId, cancellationToken);
    }

    /// <summary>
    ///     Cancels a parked thread out-of-band (writes a Cancelled snapshot with the reason).
    /// </summary>
    /// <param name="threadId">Thread id.</param>
    /// <param name="reason">Optional host-supplied cancel reason.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>The host-facing snapshot recorded for the cancellation.</returns>
    public async Task<ThreadSnapshot> CancelAsync(
        string threadId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        TrackThread(threadId);
        var graph = await ResolveGraphAsync(threadId, cancellationToken);
        return await graph.CancelAsync(threadId, reason, cancellationToken);
    }
}
