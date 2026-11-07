using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Abstractions.Topology;
using Voluta.Exceptions.Run;
using Voluta.Runtime.Engine;
using Voluta.Runtime.Engine.Support;

namespace Voluta.Graph;

/// <summary>
///     Immutable compiled graph: stream, invoke, and resume against a checkpointer.
/// </summary>
public sealed class CompiledGraph
{
    private readonly ICheckpointer checkpointer;
    private readonly GraphTopology topology;

    /// <summary>
    ///     Initializes a compiled graph from validated topology.
    /// </summary>
    /// <param name="topology">Immutable topology.</param>
    /// <param name="checkpointer">Checkpoint provider for this runnable.</param>
    internal CompiledGraph(GraphTopology topology, ICheckpointer checkpointer)
    {
        this.topology = topology;
        this.checkpointer = checkpointer;
    }

    /// <summary>
    ///     Read-only topology export for UI / tooling (no handlers).
    /// </summary>
    public GraphDescription Describe()
    {
        var staticEdges = new List<GraphEdgeDescription>();
        foreach (var (source, targets) in topology.StaticEdges.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var target in targets)
            {
                staticEdges.Add(new GraphEdgeDescription { Source = source, Target = target });
            }
        }

        return new GraphDescription
        {
            Nodes = [.. topology.Nodes.Keys.OrderBy(static name => name, StringComparer.Ordinal)],
            Channels = topology.Channels.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Kind,
                StringComparer.Ordinal),
            StaticEdges = staticEdges,
            ConditionalSources =
            [
                .. topology.ConditionalEdges.Keys.OrderBy(static name => name, StringComparer.Ordinal)
            ],
            RecursionLimit = topology.RecursionLimit,
        };
    }

    /// <summary>
    ///     Host-assigned graph identifier from compile options (null for anonymous graphs).
    ///     Stamped onto every snapshot this graph's runs write and validated on resume / continue.
    /// </summary>
    public string? GraphId => topology.GraphId;

    /// <summary>
    ///     Host-assigned graph version paired with <see cref="GraphId" /> (null for anonymous graphs).
    /// </summary>
    public string? GraphVersion => topology.GraphVersion;

    /// <summary>
    ///     Streams multi-mode observation items until the run reaches a terminal status.
    /// </summary>
    /// <param name="input">Initial channel writes (seed).</param>
    /// <param name="options">Thread id and stream mode.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Async sequence of stream events.</returns>
    public IAsyncEnumerable<StreamEvent> StreamAsync(
        IEnumerable<ChannelWrite> input,
        RunOptions options,
        CancellationToken cancellationToken = default)
    {
        var engine = new RunEngine(topology, checkpointer);
        return engine.StreamAsync(input, options, cancellationToken);
    }

    /// <summary>
    ///     Drains the stream to completion and returns the last terminal event.
    /// </summary>
    /// <param name="input">Initial channel writes (seed).</param>
    /// <param name="options">Thread id and preferred observation mode.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Final stream event (end, interrupt, failed, or cancelled).</returns>
    public async Task<StreamEvent> InvokeAsync(
        IEnumerable<ChannelWrite> input,
        RunOptions options,
        CancellationToken cancellationToken = default)
    {
        return await DrainToTerminalAsync(
            StreamAsync(input, options, cancellationToken),
            options.StreamMode);
    }

    /// <summary>
    ///     Resumes an interrupted thread with a command (Updates stream mode).
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="command">Resume payload / values.</param>
    /// <returns>Async sequence continuing from the interrupted checkpoint.</returns>
    public IAsyncEnumerable<StreamEvent> ResumeAsync(
        string threadId,
        Command command)
    {
        return ResumeAsync(threadId, command, StreamMode.Updates, default);
    }

    /// <summary>
    ///     Resumes an interrupted thread with a command, carrying host run options (stream mode,
    ///     optional unit of work for transactional checkpoint commits). Existing overloads keep
    ///     their behavior (no unit of work — standalone puts).
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="command">Resume payload / values.</param>
    /// <param name="options">Run options (ThreadId is taken from <paramref name="threadId" />).</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Async sequence continuing from the interrupted checkpoint.</returns>
    public IAsyncEnumerable<StreamEvent> ResumeAsync(
        string threadId,
        Command command,
        RunOptions options,
        CancellationToken cancellationToken)
    {
        var engine = new RunEngine(topology, checkpointer);
        return engine.ResumeAsync(
            threadId,
            command,
            new RunOptions
            {
                ThreadId = threadId,
                StreamMode = options.StreamMode,
                UnitOfWork = options.UnitOfWork,
            },
            cancellationToken);
    }

    /// <summary>
    ///     Resumes an interrupted thread with a command and stream mode.
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="command">Resume payload / values.</param>
    /// <param name="streamMode">Observation mode for the resumed stream.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Async sequence continuing from the interrupted checkpoint.</returns>
    /// <remarks>
    ///     RS0027: the <see cref="ResumeAsync(string, Command, RunOptions, CancellationToken)" />
    ///     overload keeps <paramref name="cancellationToken" /> required (no optional-parameter
    ///     overload may shadow it), so this shipped overload is no longer the widest one.
    /// </remarks>
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0027: API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "RunOptions overloads intentionally require the CancellationToken.")]
    public IAsyncEnumerable<StreamEvent> ResumeAsync(
        string threadId,
        Command command,
        StreamMode streamMode,
        CancellationToken cancellationToken = default)
    {
        var engine = new RunEngine(topology, checkpointer);
        return engine.ResumeAsync(threadId, command, streamMode, cancellationToken);
    }

    /// <summary>
    ///     Drains a resume stream to the next terminal event.
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="command">Resume payload / values.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Final stream event after resume.</returns>
    public async Task<StreamEvent> ResumeInvokeAsync(
        string threadId,
        Command command,
        CancellationToken cancellationToken = default)
    {
        return await DrainToTerminalAsync(
            ResumeAsync(threadId, command, StreamMode.Updates, cancellationToken),
            StreamMode.Updates);
    }

    /// <summary>
    ///     Loads the latest host-facing state for a thread (time-travel read).
    ///     Returns null when the thread was never checkpointed.
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Latest <see cref="ThreadSnapshot" />, or null if not found.</returns>
    public async Task<ThreadSnapshot?> GetStateAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread id is required.", nameof(threadId));
        }

        var snapshot = await checkpointer.GetAsync(threadId, cancellationToken);
        return snapshot is null ? null : ThreadSnapshotMapping.FromSnapshot(snapshot);
    }

    /// <summary>
    ///     Lists host-facing states for a thread ordered by step ascending (time-travel history).
    ///     Providers that do not support list throw <see cref="NotSupportedException" />.
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Steps oldest-first; empty when the thread has no checkpoints.</returns>
    public async Task<IReadOnlyList<ThreadSnapshot>> GetHistoryAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread id is required.", nameof(threadId));
        }

        var list = await checkpointer.ListAsync(threadId, cancellationToken);
        if (list.Count == 0)
        {
            return [];
        }

        var result = new List<ThreadSnapshot>(list.Count);
        foreach (var snapshot in list)
        {
            result.Add(ThreadSnapshotMapping.FromSnapshot(snapshot));
        }

        return result;
    }

    /// <summary>
    ///     Applies channel writes to the latest checkpoint and puts a new history step.
    ///     Uses channel reducers (LastValue / Append). Failed/Cancelled become Running
    ///     so <see cref="ContinueAsync(string, Voluta.Abstractions.Streaming.StreamMode, System.Threading.CancellationToken)" />
    ///     can re-drive; Interrupted stays Interrupted
    ///     (resume via <see cref="ResumeInvokeAsync" />); Done stays Done.
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="writes">Channel writes to merge.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Host-facing snapshot after the edit.</returns>
    /// <exception cref="Exceptions.Run.GraphThreadNotFoundException">Thread never checkpointed.</exception>
    public Task<ThreadSnapshot> UpdateStateAsync(
        string threadId,
        IEnumerable<ChannelWrite> writes,
        CancellationToken cancellationToken = default)
    {
        return string.IsNullOrWhiteSpace(threadId)
            ? throw new ArgumentException("Thread id is required.", nameof(threadId))
            : CheckpointStateMutation.UpdateStateAsync(
                topology,
                checkpointer,
                threadId,
                writes,
                cancellationToken);
    }

    /// <summary>
    ///     Copies the checkpoint at <paramref name="step" /> from
    ///     <paramref name="sourceThreadId" /> onto <paramref name="newThreadId" />,
    ///     keeping the same step index as the fork root.
    ///     Source thread is unchanged. Requires list-capable checkpointer.
    ///     Host-initiated put outside a run, guarded like <see cref="UpdateStateAsync" />:
    ///     when the destination thread already has history the put carries its latest step as
    ///     the expected parent, so a competing fork or run write on that thread fails with
    ///     <c>checkpoint.conflict</c> instead of silently overwriting; a fresh destination
    ///     keeps the blind first write.
    /// </summary>
    /// <param name="sourceThreadId">Thread to copy from.</param>
    /// <param name="step">History step to copy.</param>
    /// <param name="newThreadId">Destination thread id (must be unused or append history).</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Host-facing snapshot on the new thread.</returns>
    /// <exception cref="Exceptions.Run.GraphThreadNotFoundException">Source has no history.</exception>
    /// <exception cref="Exceptions.Run.GraphStepNotFoundException">Step missing on source.</exception>
    /// <exception cref="Voluta.Abstractions.Checkpoint.CheckpointStoreException">
    ///     Code <c>checkpoint.conflict</c> — the destination thread moved under the fork
    ///     (guarded put lost the concurrency race).
    /// </exception>
    public Task<ThreadSnapshot> ForkAsync(
        string sourceThreadId,
        long step,
        string newThreadId,
        CancellationToken cancellationToken = default)
    {
        return string.IsNullOrWhiteSpace(sourceThreadId)
            ? throw new ArgumentException("Source thread id is required.", nameof(sourceThreadId))
            : string.IsNullOrWhiteSpace(newThreadId)
                ? throw new ArgumentException("New thread id is required.", nameof(newThreadId))
                : CheckpointStateMutation.ForkAsync(
                    checkpointer,
                    sourceThreadId,
                    step,
                    newThreadId,
                    cancellationToken);
    }

    /// <summary>
    ///     Continues a Running thread from the latest checkpoint (after update or fork).
    ///     For Interrupted use <see cref="ResumeInvokeAsync" />. Nodes in NextNodes re-execute —
    ///     side effects may run again; make node work idempotent when continuing after edit.
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="streamMode">Observation mode.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Async sequence continuing the run.</returns>
    /// <remarks>
    ///     RS0027: the <see cref="ContinueAsync(string, RunOptions, CancellationToken)" />
    ///     overload keeps <paramref name="cancellationToken" /> required (no optional-parameter
    ///     overload may shadow it), so this shipped overload is no longer the widest one.
    /// </remarks>
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0027: API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "RunOptions overloads intentionally require the CancellationToken.")]
    public IAsyncEnumerable<StreamEvent> ContinueAsync(
        string threadId,
        StreamMode streamMode = StreamMode.Updates,
        CancellationToken cancellationToken = default)
    {
        return string.IsNullOrWhiteSpace(threadId)
            ? throw new ArgumentException("Thread id is required.", nameof(threadId))
            : new RunEngine(topology, checkpointer).ContinueAsync(threadId, streamMode, cancellationToken);
    }

    /// <summary>
    ///     Continues a Running thread from the latest checkpoint carrying host run options
    ///     (stream mode, optional unit of work). Existing overloads keep their behavior.
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="options">Run options (ThreadId is taken from <paramref name="threadId" />).</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Async sequence continuing the run.</returns>
    public IAsyncEnumerable<StreamEvent> ContinueAsync(
        string threadId,
        RunOptions options,
        CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(threadId)
            ? throw new ArgumentException("Thread id is required.", nameof(threadId))
            : new RunEngine(topology, checkpointer).ContinueAsync(
                threadId,
                new RunOptions
                {
                    ThreadId = threadId,
                    StreamMode = options.StreamMode,
                    UnitOfWork = options.UnitOfWork,
                },
                cancellationToken);
    }

    /// <summary>
    ///     Drains
    ///     <see cref="ContinueAsync(string, Voluta.Abstractions.Streaming.StreamMode, System.Threading.CancellationToken)" />
    ///     to the next terminal event.
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Final stream event after continue.</returns>
    public async Task<StreamEvent> ContinueInvokeAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        return await DrainToTerminalAsync(
            ContinueAsync(threadId, StreamMode.Updates, cancellationToken),
            StreamMode.Updates);
    }

    /// <summary>
    ///     Cancels a parked thread out-of-band by writing a <see cref="GraphRunStatus.Cancelled" />
    ///     snapshot from the thread's last state via a guarded put (expected parent step = the
    ///     thread's latest step; reason recorded in <see cref="CheckpointSnapshot.CancelReason" />).
    /// </summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="reason">Optional host-supplied cancel reason (stored on the snapshot).</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>The host-facing snapshot recorded for the cancellation.</returns>
    /// <exception cref="Exceptions.Run.GraphThreadNotFoundException">Thread never checkpointed.</exception>
    /// <exception cref="Exceptions.Run.GraphInvalidCancelException">
    ///     Latest snapshot is Done / Failed (terminal) — cancellation is not applicable.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         Cancelling an already-Cancelled thread succeeds idempotently (no duplicate write).
    ///         Allowed origin statuses: Running and Interrupted (a thread parked mid-run or left
    ///         Running by a dead process).
    ///     </para>
    ///     <para>
    ///         <strong>Live streams are NOT killed by this call.</strong> A run currently streaming
    ///         in this process terminates only via its own cancellation token. That live run's next
    ///         guarded checkpoint put loses the durable race with this cancel and fails with
    ///         <c>checkpoint.conflict</c> — the parked-cancel is the durable winner.
    ///     </para>
    /// </remarks>
    public async Task<ThreadSnapshot> CancelAsync(
        string threadId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread id is required.", nameof(threadId));
        }

        var loaded = await checkpointer.GetAsync(threadId, cancellationToken)
                     ?? throw new GraphThreadNotFoundException(
                         $"No checkpoint found for thread '{threadId}'.");

        switch (loaded.Status)
        {
            case GraphRunStatus.Cancelled:
                // Idempotent: already cancelled — no duplicate snapshot.
                return ThreadSnapshotMapping.FromSnapshot(loaded);
            case GraphRunStatus.Done:
            case GraphRunStatus.Failed:
                throw new GraphInvalidCancelException(
                    $"Thread '{threadId}' is {loaded.Status} and cannot be cancelled.");
        }

        var cancelled = new CheckpointSnapshot
        {
            FormatVersion = loaded.FormatVersion,
            ThreadId = loaded.ThreadId,
            Step = loaded.Step,
            Status = GraphRunStatus.Cancelled,
            ChannelValues = CheckpointStateMutation.CloneValues(loaded.ChannelValues),
            ChannelVersions = CheckpointStateMutation.CloneVersions(loaded.ChannelVersions),
            VersionsSeen = CheckpointStateMutation.CloneVersionsSeen(loaded.VersionsSeen),
            PendingWrites = [.. loaded.PendingWrites],
            PendingSends = [.. loaded.PendingSends],
            LastNode = loaded.LastNode,
            NextNodes = [.. loaded.NextNodes],
            InterruptPayload = loaded.InterruptPayload,
            PendingInterrupts = [.. loaded.PendingInterrupts],
            GraphId = loaded.GraphId,
            GraphVersion = loaded.GraphVersion,
            CancelReason = reason,
        };

        // Guarded put without a host unit of work (out-of-band host operation): the loaded
        // step is the expected parent, so a live run's concurrent put loses with conflict.
        await checkpointer.PutAsync(cancelled, loaded.Step, unitOfWork: null, cancellationToken);
        return ThreadSnapshotMapping.FromSnapshot(cancelled);
    }

    private static async Task<StreamEvent> DrainToTerminalAsync(
        IAsyncEnumerable<StreamEvent> stream,
        StreamMode streamMode)
    {
        StreamEvent? last = null;
        await foreach (var item in stream)
        {
            last = item;
            if (item.Kind is StreamEventKind.Failed && item.Payload is Exception exception)
            {
                throw exception;
            }

            if (item.Kind is StreamEventKind.End
                or StreamEventKind.Interrupt
                or StreamEventKind.Failed
                or StreamEventKind.Cancelled)
            {
                return item;
            }
        }

        return last ?? new StreamEvent
        {
            Mode = streamMode,
            Kind = StreamEventKind.End
        };
    }
}
