using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.State;
using Voluta.Checkpoint;
using Voluta.Exceptions;
using Voluta.Graph.Options;

namespace Voluta.Graph.Builder;

/// <summary>
///     Fluent builder for named channels, nodes, and edges. Compile produces an immutable runnable graph.
/// </summary>
public sealed class StateGraph
{
    private readonly Dictionary<string, GraphChannelDeclaration> channels = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Func<GraphContext, IReadOnlyList<string>>> conditionalEdges =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, NodeHandler> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> staticEdges = new(StringComparer.Ordinal);

    /// <summary>
    ///     Registers a named channel with the given merge kind.
    /// </summary>
    /// <param name="name">Channel name.</param>
    /// <param name="kind">LastValue or Append.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddChannel(string name, ChannelKind kind)
    {
        return AddChannel(new GraphChannelDeclaration(name, kind));
    }

    /// <summary>
    ///     Registers a named channel with a restore type used to coerce durable checkpoints.
    /// </summary>
    /// <typeparam name="T">Declared channel value type.</typeparam>
    /// <param name="name">Channel name.</param>
    /// <param name="kind">LastValue or Append.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddChannel<T>(string name, ChannelKind kind)
    {
        return AddChannel(new GraphChannelDeclaration(name, kind, typeof(T)));
    }

    /// <summary>
    ///     Registers a named channel with a custom reducer.
    /// </summary>
    /// <param name="name">Channel name.</param>
    /// <param name="reducer">Custom merge reducer.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddChannel(string name, IChannelReducer reducer)
    {
        return AddChannel(new GraphChannelDeclaration(name, reducer));
    }

    /// <summary>
    ///     Registers a named channel with a custom reducer and restore type.
    /// </summary>
    /// <typeparam name="T">Declared channel value type.</typeparam>
    /// <param name="name">Channel name.</param>
    /// <param name="reducer">Custom merge reducer.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddChannel<T>(string name, IChannelReducer reducer)
    {
        return AddChannel(new GraphChannelDeclaration(name, reducer, typeof(T)));
    }

    /// <summary>
    ///     Registers a compiled channel declaration (schema / generator path).
    /// </summary>
    /// <param name="declaration">Name, kind, optional reducer and restore type.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddChannel(GraphChannelDeclaration declaration)
    {
        return string.IsNullOrWhiteSpace(declaration.Name)
            ? throw new GraphCompileException(VolutaErrorCodes.GraphInvalidChannel, "Channel name must be non-empty.")
            : channels.TryAdd(declaration.Name, declaration)
                ? this
                : throw new GraphCompileException(
                    VolutaErrorCodes.GraphDuplicateChannel,
                    $"Channel '{declaration.Name}' is already registered.");
    }

    /// <summary>
    ///     Registers a unique node handler.
    /// </summary>
    /// <param name="name">Node name (not START/END).</param>
    /// <param name="handler">Async node body.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddNode(string name, NodeHandler handler)
    {
        return string.IsNullOrWhiteSpace(name)
            ? throw new GraphCompileException(VolutaErrorCodes.GraphInvalidNode, "Node name must be non-empty.")
            : StateGraphValidation.IsSentinel(name)
                ? throw new GraphCompileException(
                    VolutaErrorCodes.GraphInvalidNode,
                    $"Node name '{name}' is reserved for START/END sentinels.")
                : nodes.TryAdd(name, handler)
                    ? this
                    : throw new GraphCompileException(
                        VolutaErrorCodes.GraphDuplicateNode,
                        $"Node '{name}' is already registered.");
    }

    /// <summary>
    ///     Registers a DI-resolved <see cref="IGraphNode" />. The type must be registered in
    ///     <see cref="CompileOptions.Services" /> (or the host provider passed at compile).
    /// </summary>
    /// <typeparam name="TNode">Node implementation type.</typeparam>
    /// <param name="name">Node name (not START/END).</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddNode<TNode>(string name)
        where TNode : class, IGraphNode
    {
        return AddNode(
            name,
            static async (context, cancellationToken) =>
            {
                var node = context.GetRequiredService<TNode>();
                return await node.InvokeAsync(context, cancellationToken);
            });
    }

    /// <summary>
    ///     Registers a pre-built <see cref="IGraphNode" /> instance (singleton for the graph lifetime).
    /// </summary>
    /// <param name="name">Node name (not START/END).</param>
    /// <param name="node">Node implementation.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddNode(string name, IGraphNode node)
    {
        return AddNode(
            name,
            node.InvokeAsync);
    }

    /// <summary>
    ///     Registers a node created from the host <see cref="IServiceProvider" /> on each invocation.
    /// </summary>
    /// <param name="name">Node name (not START/END).</param>
    /// <param name="nodeFactory">Factory receiving <see cref="GraphContext.Services" />.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddNode(string name, Func<IServiceProvider, IGraphNode> nodeFactory)
    {
        return AddNode(
            name,
            async (context, cancellationToken) =>
            {
                if (context.Services is null)
                {
                    throw new InvalidOperationException(
                        "AddNode(factory) requires CompileOptions.Services.");
                }

                var node = nodeFactory(context.Services);
                return await node.InvokeAsync(context, cancellationToken);
            });
    }

    /// <summary>
    ///     Registers a static edge from source (node or START) to target (node or END).
    /// </summary>
    /// <param name="source">Source node name or <see cref="GraphConstants.Start" />.</param>
    /// <param name="target">Target node name or <see cref="GraphConstants.End" />.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddEdge(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            throw new GraphCompileException(VolutaErrorCodes.GraphInvalidEdge, "Edge endpoints must be non-empty.");
        }

        if (!staticEdges.TryGetValue(source, out var targets))
        {
            targets = [];
            staticEdges[source] = targets;
        }

        targets.Add(target);
        return this;
    }

    /// <summary>
    ///     Registers a conditional edge that selects next node name(s) after <paramref name="source" /> completes.
    /// </summary>
    /// <param name="source">Source node name.</param>
    /// <param name="router">Routing function over the post-node context snapshot.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddConditionalEdges(string source, Func<GraphContext, IReadOnlyList<string>> router)
    {
        return string.IsNullOrWhiteSpace(source)
            ? throw new GraphCompileException(
                VolutaErrorCodes.GraphInvalidEdge,
                "Conditional edge source must be non-empty.")
            : conditionalEdges.TryAdd(source, router)
                ? this
                : throw new GraphCompileException(
                    VolutaErrorCodes.GraphDuplicateConditional,
                    $"Conditional edges for '{source}' are already registered.");
    }

    /// <summary>
    ///     Registers a conditional edge with a single next-node selector.
    /// </summary>
    /// <param name="source">Source node name.</param>
    /// <param name="router">Routing function returning one node name or END.</param>
    /// <returns>This builder for chaining.</returns>
    public StateGraph AddConditionalEdges(string source, Func<GraphContext, string> router)
    {
        return AddConditionalEdges(
            source,
            context =>
            {
                var next = router(context);
                return string.IsNullOrEmpty(next) ? [] : [next];
            });
    }

    /// <summary>
    ///     Validates topology and returns an immutable compiled graph.
    /// </summary>
    /// <param name="checkpointer">Checkpoint store (typically <see cref="InMemoryCheckpointer" />).</param>
    /// <param name="options">Optional compile options (recursion limit).</param>
    /// <returns>Immutable runnable graph.</returns>
    public CompiledGraph Compile(ICheckpointer checkpointer, CompileOptions? options = null)
    {
        options ??= new CompileOptions();
        StateGraphValidation.Validate(nodes, staticEdges, conditionalEdges);

        var topology = new GraphTopology(
            new Dictionary<string, NodeHandler>(nodes, StringComparer.Ordinal),
            new Dictionary<string, GraphChannelDeclaration>(channels, StringComparer.Ordinal),
            staticEdges.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<string>)[.. pair.Value],
                StringComparer.Ordinal),
            new Dictionary<string, Func<GraphContext, IReadOnlyList<string>>>(
                conditionalEdges,
                StringComparer.Ordinal),
            options.RecursionLimit,
            options.Services);

        return new CompiledGraph(topology, checkpointer);
    }
}

/// <summary>
///     Compile-time topology validation for <see cref="StateGraph" />.
/// </summary>
file static class StateGraphValidation
{
    public static void Validate(
        IReadOnlyDictionary<string, NodeHandler> nodes,
        IReadOnlyDictionary<string, List<string>> staticEdges,
        IReadOnlyDictionary<string, Func<GraphContext, IReadOnlyList<string>>> conditionalEdges)
    {
        if (nodes.Count == 0)
        {
            throw new GraphCompileException(VolutaErrorCodes.GraphNoNodes, "Graph must register at least one node.");
        }

        if (!staticEdges.ContainsKey(GraphConstants.Start)
            && !conditionalEdges.ContainsKey(GraphConstants.Start))
        {
            throw new GraphCompileException(
                VolutaErrorCodes.GraphMissingStart,
                "Compile requires at least one edge originating from START.");
        }

        foreach (var (source, targets) in staticEdges)
        {
            ValidateEndpoint(nodes, source, true);
            foreach (var target in targets)
            {
                ValidateEndpoint(nodes, target, false);
            }
        }

        foreach (var source in conditionalEdges.Keys)
        {
            ValidateEndpoint(nodes, source, true);
        }
    }

    public static bool IsSentinel(string name)
    {
        return name is GraphConstants.Start or GraphConstants.End;
    }

    private static void ValidateEndpoint(
        IReadOnlyDictionary<string, NodeHandler> nodes,
        string name,
        bool isSource)
    {
        if (IsSentinel(name))
        {
            if (isSource && name == GraphConstants.End)
            {
                throw new GraphCompileException(
                    VolutaErrorCodes.GraphInvalidEdge,
                    "END cannot be an edge source.");
            }

            if (!isSource && name == GraphConstants.Start)
            {
                throw new GraphCompileException(
                    VolutaErrorCodes.GraphInvalidEdge,
                    "START cannot be an edge target.");
            }

            return;
        }

        if (!nodes.ContainsKey(name))
        {
            throw new GraphCompileException(
                VolutaErrorCodes.GraphUnknownEndpoint,
                $"Edge references unknown node '{name}'.");
        }
    }
}
