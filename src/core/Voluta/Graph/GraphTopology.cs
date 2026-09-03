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
    IServiceProvider? services = null)
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
    ///     Host services snapshotted at compile time (same instance for the process).
    /// </summary>
    public IServiceProvider? Services { get; } = services;
}
