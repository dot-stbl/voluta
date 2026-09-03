using Voluta.Abstractions.State;

namespace Voluta.Graph.Builder;

/// <summary>
///     Applies generated or hand-built channel schemas to a <see cref="StateGraph" />.
/// </summary>
public static class StateGraphChannelExtensions
{
    /// <summary>
    ///     Registers every channel from <paramref name="schema" /> on the builder.
    /// </summary>
    /// <param name="graph">Graph builder.</param>
    /// <param name="schema">Channel schema (often from source-generated <c>CreateSchema</c>).</param>
    /// <returns>The same builder for chaining.</returns>
    public static StateGraph AddChannels(this StateGraph graph, GraphChannelSchema schema)
    {
        foreach (var channel in schema.Channels)
        {
            _ = graph.AddChannel(channel);
        }

        return graph;
    }
}
