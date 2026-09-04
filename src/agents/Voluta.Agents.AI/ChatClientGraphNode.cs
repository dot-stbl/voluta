using System.Text;
using Microsoft.Extensions.AI;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Graph;

namespace Voluta.Agents.AI;

/// <summary>
///     Runs <see cref="IChatClient" /> as an <see cref="IGraphNode" />.
///     Prefer constructor injection of <see cref="IChatClient" />; alternatively resolve from
///     <see cref="GraphContext.Services" /> when <paramref name="chatClient" /> is null.
///     When <see cref="ChatClientNodeOptions.Stream" /> is true, uses
///     <see cref="IChatClient.GetStreamingResponseAsync" /> and bridges token fragments via
///     <see cref="GraphContext.Stream" />.
///     Optional <see cref="ChatClientNodeOptions.UsageChannel" /> and
///     <see cref="ChatClientNodeOptions.ToolCallsChannel" /> receive token counts and parsed
///     function calls after the completion (or after the stream ends).
/// </summary>
public sealed class ChatClientGraphNode(
    ChatClientNodeOptions options,
    IChatClient? chatClient = null) : IGraphNode
{
    /// <summary>
    ///     Creates a node that completes chat and writes assistant text to a channel.
    ///     Usage and tool-call channel writes are configured via
    ///     <see cref="ChatClientNodeOptions" /> — keep this factory thin.
    /// </summary>
    /// <param name="outputChannel">Target channel.</param>
    /// <param name="messagesFactory">Builds the message list from the graph context.</param>
    /// <param name="chatClient">Optional client; when null, resolved from <see cref="GraphContext.Services" />.</param>
    /// <param name="stream">When true, stream tokens into the graph stream (default false).</param>
    /// <returns>Graph node instance.</returns>
    public static ChatClientGraphNode Create(
        string outputChannel,
        Func<GraphContext, IEnumerable<ChatMessage>> messagesFactory,
        IChatClient? chatClient = null,
        bool stream = false)
    {
        return new ChatClientGraphNode(
            new ChatClientNodeOptions
            {
                OutputChannel = outputChannel,
                Messages = messagesFactory,
                Stream = stream,
            },
            chatClient);
    }

    /// <inheritdoc />
    public async Task<NodeResult> InvokeAsync(GraphContext context, CancellationToken cancellationToken = default)
    {
        var client = chatClient ?? context.GetRequiredService<IChatClient>();
        var messages = options.Messages(context);
        var completion = options.Stream
            ? await ChatClientCompletion.StreamAndBridgeAsync(
                client,
                messages,
                options.ChatOptions,
                context,
                cancellationToken)
            : await ChatClientCompletion.CompleteAsync(
                client,
                messages,
                options.ChatOptions,
                cancellationToken);
        return NodeResult.Continue(ChatClientChannelWrites.Collect(options, completion));
    }
}

file sealed record ChatClientTurn(
    string Text,
    UsageDetails? Usage,
    IReadOnlyList<ChatClientToolCall> ToolCalls);

file static class ChatClientCompletion
{
    public static async Task<ChatClientTurn> CompleteAsync(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions,
        CancellationToken cancellationToken)
    {
        var response = await client.GetResponseAsync(messages, chatOptions, cancellationToken);
        return new ChatClientTurn(
            response.Text ?? string.Empty,
            response.Usage,
            ChatClientResponseExtraction.ToolCalls(response));
    }

    public static async Task<ChatClientTurn> StreamAndBridgeAsync(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions,
        GraphContext context,
        CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           messages,
                           chatOptions,
                           cancellationToken))
        {
            updates.Add(update);
            var fragment = update.Text;
            if (string.IsNullOrEmpty(fragment))
            {
                continue;
            }

            buffer.Append(fragment);
            await context.Stream.WriteMessageAsync(fragment, cancellationToken);
        }

        var reconstructed = updates.ToChatResponse();
        var text = buffer.Length > 0 ? buffer.ToString() : reconstructed.Text ?? string.Empty;
        return new ChatClientTurn(
            text,
            reconstructed.Usage ?? ChatClientResponseExtraction.UsageFromUpdates(updates),
            ChatClientResponseExtraction.ToolCalls(reconstructed, updates));
    }
}

file static class ChatClientResponseExtraction
{
    public static IReadOnlyList<ChatClientToolCall> ToolCalls(
        ChatResponse response,
        IReadOnlyList<ChatResponseUpdate>? updates = null)
    {
        var calls = new List<ChatClientToolCall>();
        foreach (var message in response.Messages)
        {
            AddFromContents(calls, message.Contents);
        }

        if (calls.Count == 0 && updates is not null)
        {
            foreach (var update in updates)
            {
                AddFromContents(calls, update.Contents);
            }
        }

        return calls;
    }

    public static UsageDetails? UsageFromUpdates(IReadOnlyList<ChatResponseUpdate> updates)
    {
        UsageDetails? last = null;
        foreach (var update in updates)
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usageContent)
                {
                    last = usageContent.Details;
                }
            }
        }

        return last;
    }

    public static void AddFromContents(List<ChatClientToolCall> calls, IList<AIContent>? contents)
    {
        if (contents is null)
        {
            return;
        }

        foreach (var content in contents)
        {
            if (content is not FunctionCallContent functionCall)
            {
                continue;
            }

            calls.Add(
                new ChatClientToolCall
                {
                    Name = functionCall.Name,
                    CallId = functionCall.CallId,
                    Arguments = functionCall.Arguments is { } arguments
                        ? arguments.ToDictionary(
                            static pair => pair.Key,
                            static pair => pair.Value)
                        : null,
                });
        }
    }
}

file static class ChatClientChannelWrites
{
    public static IReadOnlyList<ChannelWrite> Collect(ChatClientNodeOptions options, ChatClientTurn completion)
    {
        var writes = new List<ChannelWrite>
        {
            new(options.OutputChannel, completion.Text),
        };

        if (options.UsageChannel is { Length: > 0 } usageChannel)
        {
            writes.Add(new ChannelWrite(usageChannel, ToUsage(completion.Usage)));
        }

        if (options.ToolCallsChannel is { Length: > 0 } toolCallsChannel)
        {
            writes.Add(
                new ChannelWrite(
                    toolCallsChannel,
                    completion.ToolCalls.Cast<object?>().ToList()));
        }

        return writes;
    }

    public static ChatClientUsage ToUsage(UsageDetails? details)
    {
        return new ChatClientUsage
        {
            PromptTokens = details?.InputTokenCount,
            CompletionTokens = details?.OutputTokenCount,
            TotalTokens = details?.TotalTokenCount,
        };
    }
}
