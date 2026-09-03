using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;
using Xunit;

namespace Voluta.Agents.AI.Unit;

public sealed class StreamingChatClientGraphNodeShould
{
    [Fact(DisplayName = "Given Stream=true and fake streaming client, when StreamAsync, then bridges tokens and writes full text")]
    public async Task BridgeTokensIntoGraphStream()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(static callInfo => FakeTokensAsync(
                ["Hel", "lo", "!", ""],
                callInfo.ArgAt<CancellationToken>(2)));

        var services = new ServiceCollection();
        services.AddSingleton(chatClient);
        await using var provider = services.BuildServiceProvider();

        var graph = new StateGraph()
            .AddChannel("answer", ChannelKind.LastValue)
            .AddNode(
                "chat",
                ChatClientGraphNode.Create(
                    "answer",
                    static _ => [new ChatMessage(ChatRole.User, "hi")],
                    stream: true))
            .AddEdge(GraphConstants.Start, "chat")
            .AddEdge("chat", GraphConstants.End)
            .Compile(
                new InMemoryCheckpointer(),
                new CompileOptions { Services = provider });

        var events = new List<StreamEvent>();
        await foreach (var item in graph.StreamAsync(
                           [],
                           new RunOptions { ThreadId = "stream-meai-1", StreamMode = StreamMode.Messages }))
        {
            events.Add(item);
        }

        var tokens = events
            .Where(static item => item.Kind == StreamEventKind.Messages)
            .Select(static item => item.Payload)
            .ToList();
        tokens.ShouldBe(["Hel", "lo", "!"]);
        events.Last().Kind.ShouldBe(StreamEventKind.End);

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "stream-meai-2", StreamMode = StreamMode.Values });
        terminal.Kind.ShouldBe(StreamEventKind.End);
        terminal.State.ShouldNotBeNull();
        terminal.State!["answer"].ShouldBe("Hello!");
    }

    [Fact(DisplayName = "Given Stream=true with usage and tool-call channels, when stream ends, then writes usage and calls after tokens")]
    public async Task WriteUsageAndToolCallsAfterStreamCompletes()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(static callInfo => FakeToolCallStreamAsync(callInfo.ArgAt<CancellationToken>(2)));

        var services = new ServiceCollection();
        services.AddSingleton(chatClient);
        await using var provider = services.BuildServiceProvider();

        var graph = new StateGraph()
            .AddChannel("answer", ChannelKind.LastValue)
            .AddChannel("usage", ChannelKind.LastValue)
            .AddChannel("tool_calls", ChannelKind.Append)
            .AddNode(
                "chat",
                new ChatClientGraphNode(
                    new ChatClientNodeOptions
                    {
                        OutputChannel = "answer",
                        UsageChannel = "usage",
                        ToolCallsChannel = "tool_calls",
                        Stream = true,
                        Messages = static _ => [new ChatMessage(ChatRole.User, "hi")],
                    }))
            .AddEdge(GraphConstants.Start, "chat")
            .AddEdge("chat", GraphConstants.End)
            .Compile(
                new InMemoryCheckpointer(),
                new CompileOptions { Services = provider });

        var events = new List<StreamEvent>();
        await foreach (var item in graph.StreamAsync(
                           [],
                           new RunOptions { ThreadId = "stream-usage-1", StreamMode = StreamMode.Messages }))
        {
            events.Add(item);
        }

        var tokens = events
            .Where(static item => item.Kind == StreamEventKind.Messages)
            .Select(static item => item.Payload)
            .ToList();
        tokens.ShouldBe(["Hel", "lo"]);
        events.Last().Kind.ShouldBe(StreamEventKind.End);

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "stream-usage-2", StreamMode = StreamMode.Values });
        terminal.Kind.ShouldBe(StreamEventKind.End);
        terminal.State.ShouldNotBeNull();
        terminal.State!["answer"].ShouldBe("Hello");

        var usage = terminal.State["usage"].ShouldBeOfType<ChatClientUsage>();
        usage.PromptTokens.ShouldBe(3);
        usage.CompletionTokens.ShouldBe(2);
        usage.TotalTokens.ShouldBe(5);

        var toolCalls = terminal.State["tool_calls"].ShouldBeOfType<List<object?>>();
        toolCalls.Count.ShouldBe(1);
        var call = toolCalls[0].ShouldBeOfType<ChatClientToolCall>();
        call.Name.ShouldBe("lookup");
        call.CallId.ShouldBe("call-stream");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FakeTokensAsync(
        IReadOnlyList<string> fragments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var fragment in fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, fragment);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FakeToolCallStreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Hel");

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "lo");

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-stream",
                    "lookup",
                    new Dictionary<string, object?> { ["q"] = "ams" }),
                new UsageContent(
                    new UsageDetails
                    {
                        InputTokenCount = 3,
                        OutputTokenCount = 2,
                        TotalTokenCount = 5,
                    }),
            ]);
    }
}
