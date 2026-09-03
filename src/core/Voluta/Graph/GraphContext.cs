using Voluta.Abstractions.Streaming;

namespace Voluta.Graph;

/// <summary>
///     Frozen superstep view passed to a node handler (pre-barrier channel values).
/// </summary>
/// <remarks>
///     Initializes a graph context for one node task.
///     HITL: <c>NodeResult.Interrupt(ask)</c> → <c>Command.Approve(answer)</c> →
///     <see cref="IsResume"/> is <see langword="true"/> and
///     <see cref="ResumePayload"/> equals <c>answer</c> (never the interrupt ask).
///     Gate on <see cref="IsResume"/>, not on <see cref="ResumePayload"/> is null —
///     <c>Command.Approve()</c> carries a null payload.
/// </remarks>
/// <param name="nodeName">Node being executed.</param>
/// <param name="channelValues">Snapshot of channel values before this superstep's apply.</param>
/// <param name="resumePayload">Resume command payload (the answer) when continuing after interrupt.</param>
/// <param name="taskPayload">Send/PUSH task payload when this invocation was scheduled via Send.</param>
/// <param name="services">Optional host <see cref="IServiceProvider" /> (from <see cref="Options.CompileOptions.Services" />).</param>
/// <param name="threadId">Parent run thread id (for nested checkpoint namespaces).</param>
/// <param name="taskId">Stable task id (node name for pull tasks; Send task id for PUSH).</param>
/// <param name="stream">Optional stream writer for custom / message events during the node body.</param>
/// <param name="isResume">
///     <see langword="true"/> when this task is in the resume map on the first superstep
///     after Resume (even if <paramref name="resumePayload"/> is null).
/// </param>
/// <param name="resumeKind">
///     <c>Command.Kind</c> (<c>approve</c> / <c>reject</c> / <c>update</c>) on that first
///     resume superstep; otherwise <see langword="null"/>.
/// </param>
public sealed class GraphContext(
    string nodeName,
    IReadOnlyDictionary<string, object?> channelValues,
    object? resumePayload = null,
    object? taskPayload = null,
    IServiceProvider? services = null,
    string? threadId = null,
    string? taskId = null,
    IStreamWriter? stream = null,
    bool isResume = false,
    string? resumeKind = null)
{
    private readonly IReadOnlyDictionary<string, object?> channelValues = channelValues;

    /// <summary>
    ///     Name of the node currently executing.
    /// </summary>
    public string NodeName { get; } = nodeName;

    /// <summary>
    ///     Stable task id for this invocation (defaults to <see cref="NodeName" /> when omitted).
    /// </summary>
    public string TaskId { get; } = taskId is { Length: > 0 } ? taskId : nodeName;

    /// <summary>
    ///     Resume command payload (<c>Command.Payload</c>) when this invocation is a resume
    ///     of an interrupted node. This is the answer, never the interrupt ask.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>NodeResult.Interrupt(ask)</c> → <c>Command.Approve(answer)</c> →
    ///         <see cref="IsResume"/> is <see langword="true"/> and
    ///         <see cref="ResumePayload"/> equals <c>answer</c> (never <c>== ask</c>).
    ///     </para>
    ///     <para>
    ///         Do not use <c>ResumePayload is null</c> as the interrupt gate:
    ///         <c>Command.Approve()</c> carries a null payload and would silently
    ///         re-interrupt. Use <see cref="IsResume"/> instead.
    ///     </para>
    /// </remarks>
    public object? ResumePayload { get; } = resumePayload;

    /// <summary>
    ///     <see langword="true"/> on the first superstep after
    ///     <c>ResumeAsync</c> / <c>ResumeInvokeAsync</c> when this task is in the
    ///     resume map, even if <see cref="ResumePayload"/> is null.
    /// </summary>
    /// <remarks>
    ///     Canonical HITL gate:
    ///     <c>if (!context.IsResume) return NodeResult.Interrupt(ask);</c>
    /// </remarks>
    public bool IsResume { get; } = isResume;

    /// <summary>
    ///     <c>Command.Kind</c> (<c>approve</c> / <c>reject</c> / <c>update</c>) on the
    ///     first resume superstep for this task; otherwise <see langword="null"/>.
    /// </summary>
    public string? ResumeKind { get; } = resumeKind;

    /// <summary>
    ///     Payload from a <see cref="Abstractions.Runtime.Send" /> that scheduled this task.
    /// </summary>
    public object? TaskPayload { get; } = taskPayload;

    /// <summary>
    ///     Host service provider when the graph was compiled with
    ///     <see cref="Options.CompileOptions.Services" />; otherwise <see langword="null" />.
    /// </summary>
    public IServiceProvider? Services { get; } = services;

    /// <summary>
    ///     Thread id of the parent run. Used by <see cref="Subgraph.AsNode" /> to build a stable
    ///     nested child thread id (default: <c>{ThreadId}/{NodeName}</c>).
    /// </summary>
    public string? ThreadId { get; } = threadId;

    /// <summary>
    ///     Writer for custom progress and LLM token fragments into the live graph stream.
    ///     No-op when the runtime did not attach a writer (e.g. unit tests constructing a bare context).
    /// </summary>
    public IStreamWriter Stream { get; } = stream ?? GraphContextNullStream.Instance;

    /// <summary>
    ///     Reads a channel value cast to <typeparamref name="T" />, or default when missing/null.
    /// </summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="channelName">Channel name.</param>
    /// <returns>Channel value or default.</returns>
    public T? Read<T>(string channelName)
    {
        return !channelValues.TryGetValue(channelName, out var value) || value is null
            ? default
            : value is T typed
                ? typed
                : (T)value;
    }

    /// <summary>
    ///     Returns the full frozen channel map for this superstep.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        return channelValues;
    }

    /// <summary>
    ///     Resolves <typeparamref name="T" /> from <see cref="Services" /> or throws when missing.
    /// </summary>
    /// <typeparam name="T">Service type.</typeparam>
    /// <returns>Resolved service.</returns>
    /// <exception cref="InvalidOperationException">When services were not compiled in or type is unregistered.</exception>
    public T GetRequiredService<T>()
        where T : notnull
    {
        return Services is null
            ? throw new InvalidOperationException(
                "GraphContext.Services is null. Pass CompileOptions.Services when compiling the graph (e.g. from AddVoluta factory sp).")
            : Services.GetService(typeof(T)) is T service
                ? service
                : throw new InvalidOperationException(
                    $"Service '{typeof(T).FullName}' is not registered in GraphContext.Services.");
    }
}

/// <summary>
///     File-local no-op stream for bare <see cref="GraphContext" /> construction without a runtime writer.
/// </summary>
file sealed class GraphContextNullStream : IStreamWriter
{
    public static GraphContextNullStream Instance { get; } = new();

    public ValueTask WriteCustomAsync(object? payload, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}
