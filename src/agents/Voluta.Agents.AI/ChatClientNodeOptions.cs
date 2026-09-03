using Microsoft.Extensions.AI;
using Voluta.Graph;

namespace Voluta.Agents.AI;

/// <summary>
///     Options for <see cref="ChatClientGraphNode" />.
/// </summary>
public sealed class ChatClientNodeOptions()
{
    /// <summary>Channel receiving assistant text.</summary>
    public required string OutputChannel { get; init; }

    /// <summary>Builds chat messages for the completion call.</summary>
    public required Func<GraphContext, IEnumerable<ChatMessage>> Messages { get; init; }

    /// <summary>Optional MEAI chat options.</summary>
    public ChatOptions? ChatOptions { get; init; }

    /// <summary>
    ///     When true, uses streaming MEAI API and bridges each text delta via
    ///     <see cref="GraphContext.Stream" /> as <c>StreamEventKind.Messages</c>.
    /// </summary>
    public bool Stream { get; init; }

    /// <summary>
    ///     Optional channel for <see cref="ChatClientUsage" /> token counts.
    ///     Always populated for non-stream completions from <see cref="ChatResponse.Usage" />
    ///     (null counts when the provider omitted usage). On <see cref="Stream" />, populated
    ///     best-effort from <see cref="UsageContent" /> on streaming updates — many providers
    ///     only send usage on the last chunk.
    /// </summary>
    public string? UsageChannel { get; init; }

    /// <summary>
    ///     Optional channel for extracted tool calls (<see cref="ChatClientToolCall" />).
    ///     Writes an Append-friendly <c>List&lt;object?&gt;</c> (empty when the model made
    ///     no calls). The host graph still routes agent ⇄ tools; this node does not run a ReAct loop.
    /// </summary>
    public string? ToolCallsChannel { get; init; }
}
