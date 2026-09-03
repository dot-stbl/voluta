using Microsoft.Extensions.AI;
using Voluta.Graph;
using Voluta.Graph.Builder;

namespace Voluta.Agents.AI;

/// <summary>
///     Instance-style registration helpers for <see cref="IChatClient" /> nodes (not extension methods).
/// </summary>
public static class ChatClientNodes
{
    /// <summary>
    ///     Adds a <see cref="ChatClientGraphNode" /> under <paramref name="name" />.
    /// </summary>
    /// <param name="graph">Graph builder.</param>
    /// <param name="name">Node name.</param>
    /// <param name="outputChannel">Channel for assistant text.</param>
    /// <param name="messages">Message factory.</param>
    /// <param name="chatClient">Optional client; when null, resolved from <see cref="GraphContext.Services" />.</param>
    /// <param name="stream">When true, stream tokens into the graph stream.</param>
    /// <param name="usageChannel">Optional channel for <see cref="ChatClientUsage" /> token counts.</param>
    /// <param name="toolCallsChannel">Optional channel for extracted <see cref="ChatClientToolCall" /> list.</param>
    /// <returns>The same <paramref name="graph" /> for chaining.</returns>
    public static StateGraph Add(
        StateGraph graph,
        string name,
        string outputChannel,
        Func<GraphContext, IEnumerable<ChatMessage>> messages,
        IChatClient? chatClient = null,
        bool stream = false,
        string? usageChannel = null,
        string? toolCallsChannel = null)
    {
        return graph.AddNode(
            name,
            new ChatClientGraphNode(
                new ChatClientNodeOptions
                {
                    OutputChannel = outputChannel,
                    Messages = messages,
                    Stream = stream,
                    UsageChannel = usageChannel,
                    ToolCallsChannel = toolCallsChannel,
                },
                chatClient));
    }
}
