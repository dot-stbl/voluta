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

public sealed class ChatClientGraphNodeShould
{
    [Fact(DisplayName = "Given ChatClientGraphNode, when invoked, then writes assistant text to output channel")]
    public async Task WriteAssistantTextToChannel()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "hello-from-meai")]));

        var terminal = await InvokeAsync(
            chatClient,
            ChatClientGraphNode.Create(
                "answer",
                static _ => [new ChatMessage(ChatRole.User, "hi")]),
            ["answer"]);

        terminal.Kind.ShouldBe(StreamEventKind.End);
        terminal.State.ShouldNotBeNull();
        terminal.State!["answer"].ShouldBe("hello-from-meai");
        terminal.State.ContainsKey("usage").ShouldBeFalse();
        terminal.State.ContainsKey("tool_calls").ShouldBeFalse();
    }

    [Fact(DisplayName = "Given usage and tool-call channels, when response has Usage and FunctionCallContent, then writes counts and calls")]
    public async Task WriteUsageAndToolCallsWhenChannelsConfigured()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(
                [
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new TextContent("calling lookup"),
                            new FunctionCallContent(
                                "call-1",
                                "lookup",
                                new Dictionary<string, object?> { ["city"] = "ams" }),
                        ]),
                ])
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 11,
                    OutputTokenCount = 7,
                    TotalTokenCount = 18,
                },
            });

        var terminal = await InvokeAsync(
            chatClient,
            new ChatClientGraphNode(
                new ChatClientNodeOptions
                {
                    OutputChannel = "answer",
                    UsageChannel = "usage",
                    ToolCallsChannel = "tool_calls",
                    Messages = static _ => [new ChatMessage(ChatRole.User, "hi")],
                }),
            ["answer", "usage"],
            appendChannels: ["tool_calls"]);

        terminal.Kind.ShouldBe(StreamEventKind.End);
        terminal.State.ShouldNotBeNull();
        terminal.State!["answer"].ShouldBe("calling lookup");

        var usage = terminal.State["usage"].ShouldBeOfType<ChatClientUsage>();
        usage.PromptTokens.ShouldBe(11);
        usage.CompletionTokens.ShouldBe(7);
        usage.TotalTokens.ShouldBe(18);

        var toolCalls = terminal.State["tool_calls"].ShouldBeOfType<List<object?>>();
        toolCalls.Count.ShouldBe(1);
        var call = toolCalls[0].ShouldBeOfType<ChatClientToolCall>();
        call.Name.ShouldBe("lookup");
        call.CallId.ShouldBe("call-1");
        call.Arguments.ShouldNotBeNull();
        call.Arguments!["city"].ShouldBe("ams");
    }

    [Fact(DisplayName = "Given tool-calls channel and no FunctionCallContent, when invoked, then writes an empty list")]
    public async Task WriteEmptyToolCallsWhenNonePresent()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "plain")]));

        var terminal = await InvokeAsync(
            chatClient,
            new ChatClientGraphNode(
                new ChatClientNodeOptions
                {
                    OutputChannel = "answer",
                    ToolCallsChannel = "tool_calls",
                    Messages = static _ => [new ChatMessage(ChatRole.User, "hi")],
                }),
            ["answer"],
            appendChannels: ["tool_calls"]);

        terminal.Kind.ShouldBe(StreamEventKind.End);
        terminal.State.ShouldNotBeNull();
        terminal.State!["answer"].ShouldBe("plain");
        var toolCalls = terminal.State["tool_calls"].ShouldBeOfType<List<object?>>();
        toolCalls.ShouldBeEmpty();
    }

    private static async Task<StreamEvent> InvokeAsync(
        IChatClient chatClient,
        ChatClientGraphNode node,
        IReadOnlyList<string> lastValueChannels,
        IReadOnlyList<string>? appendChannels = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(chatClient);
        await using var provider = services.BuildServiceProvider();

        var graph = new StateGraph();
        foreach (var channelName in lastValueChannels)
        {
            graph.AddChannel(channelName, ChannelKind.LastValue);
        }

        if (appendChannels is not null)
        {
            foreach (var channelName in appendChannels)
            {
                graph.AddChannel(channelName, ChannelKind.Append);
            }
        }

        return await graph
            .AddNode("chat", node)
            .AddEdge(GraphConstants.Start, "chat")
            .AddEdge("chat", GraphConstants.End)
            .Compile(
                new InMemoryCheckpointer(),
                new CompileOptions { Services = provider })
            .InvokeAsync(
                [],
                new RunOptions { ThreadId = "t1", StreamMode = StreamMode.Values });
    }
}
