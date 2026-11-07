using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Serialization;
using Voluta.Abstractions.State;
using Voluta.Graph.Options;

namespace Voluta.Graph;

/// <summary>
///     Immutable topology produced by <see cref="Builder.StateGraph.Compile" />.
/// </summary>
/// <remarks>
///     Initializes topology maps.
/// </remarks>
internal sealed class GraphTopology(
    IReadOnlyDictionary<string, NodeHandler> nodes,
    IReadOnlyDictionary<string, GraphChannelDeclaration> channels,
    IReadOnlyDictionary<string, IReadOnlyList<string>> staticEdges,
    IReadOnlyDictionary<string, Func<GraphContext, IReadOnlyList<string>>> conditionalEdges,
    int recursionLimit,
    string? graphId = null,
    string? graphVersion = null,
    IServiceProvider? services = null,
    IReadOnlyDictionary<string, RetryPolicy?>? nodeRetries = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? joinSources = null,
    IReadOnlyList<string>? interruptBefore = null,
    IReadOnlyList<string>? interruptAfter = null,
    IReadOnlyList<Voluta.Abstractions.Runtime.IRunGuard>? runGuards = null,
    CheckpointTypeRegistry? checkpointTypes = null)
{
    /// <summary>
    ///     Node name → handler.
    /// </summary>
    public IReadOnlyDictionary<string, NodeHandler> Nodes { get; } = nodes;

    /// <summary>
    ///     Channel name → compiled declaration (kind, optional reducer and restore type).
    /// </summary>
    public IReadOnlyDictionary<string, GraphChannelDeclaration> Channels { get; } = channels;

    /// <summary>
    ///     Source node → static target names (may include END).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> StaticEdges { get; } = staticEdges;

    /// <summary>
    ///     Source node → routing function selecting next node names (or END).
    /// </summary>
    public IReadOnlyDictionary<string, Func<GraphContext, IReadOnlyList<string>>> ConditionalEdges { get; } =
        conditionalEdges;

    /// <summary>
    ///     Maximum supersteps before out-of-steps failure.
    /// </summary>
    public int RecursionLimit { get; } = recursionLimit;

    /// <summary>
    ///     Host-assigned graph identifier (compile options), stamped onto every snapshot this
    ///     topology's runs write. Null when the graph compiled without identity.
    /// </summary>
    public string? GraphId { get; } = graphId;

    /// <summary>
    ///     Host-assigned graph version paired with <see cref="GraphId" />. Null when the graph
    ///     compiled without identity.
    /// </summary>
    public string? GraphVersion { get; } = graphVersion;

    /// <summary>
    ///     Host services snapshotted at compile time (same instance for the process).
    /// </summary>
    public IServiceProvider? Services { get; } = services;

    /// <summary>
    ///     Node name → retry policy (only nodes registered with <see cref="Options.NodeOptions.Retry" />;
    ///     absent key = no retry).
    /// </summary>
    public IReadOnlyDictionary<string, RetryPolicy?> NodeRetries { get; } =
        nodeRetries ?? new Dictionary<string, RetryPolicy?>(StringComparer.Ordinal);

    /// <summary>
    ///     Wait-for-all join targets → their sources (stable order). StaticEdges still maps
    ///     every join source → the target, so downstream routing sees the target normally.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> JoinSources { get; } =
        joinSources ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    ///     Nodes parked BEFORE entry (CompileOptions.InterruptBefore); empty by default.
    /// </summary>
    public IReadOnlyList<string> InterruptBefore { get; } =
        interruptBefore ?? [];

    /// <summary>
    ///     Nodes parked AFTER output applied (CompileOptions.InterruptAfter); empty by default.
    /// </summary>
    public IReadOnlyList<string> InterruptAfter { get; } =
        interruptAfter ?? [];

    /// <summary>
    ///     Policy hooks evaluated before each superstep (CompileOptions.RunGuards); empty by default.
    /// </summary>
    public IReadOnlyList<Voluta.Abstractions.Runtime.IRunGuard> RunGuards { get; } =
        runGuards ?? [];

    /// <summary>
    ///     Host type registry for provider wire stamping / typed restore
    ///     (CompileOptions.CheckpointTypes); null when the graph compiled without one.
    ///     Engine assigns it to <c>IRegistryAwareCheckpointer</c> providers at run start.
    /// </summary>
    public CheckpointTypeRegistry? CheckpointTypes { get; } = checkpointTypes;
}
