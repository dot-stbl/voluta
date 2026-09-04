using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Channels.Reducers;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Exceptions;
using Voluta.Graph.Builder;
using Xunit;

namespace Voluta.Unit.State;

public sealed class ChannelReducerShould
{
    [Fact(DisplayName = "Given Append channel, when node writes a list then a string, then list is flattened and string is one element")]
    public async Task AppendFlattensEnumerableNotString()
    {
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", new List<object?> { "a", "b" }))))
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "c"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddEdge("b", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "append-1", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var messages = terminal.State!["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldBe(["a", "b", "c"]);
    }

    [Fact(DisplayName = "Given LastValue channel, when two sequential nodes write, then final value is the later write")]
    public async Task LastValueSequentialOverwrite()
    {
        var graph = new StateGraph()
            .AddChannel("status", ChannelKind.LastValue)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("status", "from-a"))))
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("status", "from-b"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddEdge("b", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "lv-seq-1", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        terminal.State!["status"].ShouldBe("from-b");
    }

    [Fact(DisplayName = "Given AddChannel<T> and untyped AddChannel, when both register, then they coexist")]
    public void TypedAndUntypedAddChannelCoexist()
    {
        var graph = new StateGraph()
            .AddChannel<int>("score", ChannelKind.LastValue)
            .AddChannel("notes", ChannelKind.Append);

        Should.Throw<GraphCompileException>(() => graph.AddChannel("score", ChannelKind.LastValue));
        Should.Throw<GraphCompileException>(() => graph.AddChannel<string>("notes", ChannelKind.Append));
    }

    [Fact(DisplayName = "Given DictMergeReducer, when two parallel nodes write different keys, then dict merges")]
    public async Task DictMergeCombinesParallelKeys()
    {
        var graph = new StateGraph()
            .AddChannel<Dictionary<string, object?>>("artifacts", new DictMergeReducer())
            .AddNode(
                "left",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(
                        new ChannelWrite(
                            "artifacts",
                            new Dictionary<string, object?>(StringComparer.Ordinal) { ["a"] = 1 }))))
            .AddNode(
                "right",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(
                        new ChannelWrite(
                            "artifacts",
                            new Dictionary<string, object?>(StringComparer.Ordinal) { ["b"] = 2 }))))
            .AddEdge(GraphConstants.Start, "left")
            .AddEdge(GraphConstants.Start, "right")
            .AddEdge("left", GraphConstants.End)
            .AddEdge("right", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "dict-merge-1", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var artifacts = terminal.State!["artifacts"].ShouldBeOfType<Dictionary<string, object?>>();
        artifacts["a"].ShouldBe(1);
        artifacts["b"].ShouldBe(2);
    }

    [Fact(DisplayName = "Given LastValueReducer, when two parallel writers collide, then concurrent update still fails")]
    public async Task LastValueReducerConcurrentStillFails()
    {
        var graph = new StateGraph()
            .AddChannel("status", new LastValueReducer())
            .AddNode(
                "left",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("status", "L"))))
            .AddNode(
                "right",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("status", "R"))))
            .AddEdge(GraphConstants.Start, "left")
            .AddEdge(GraphConstants.Start, "right")
            .AddEdge("left", GraphConstants.End)
            .AddEdge("right", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        await Should.ThrowAsync<GraphConcurrentUpdateException>(async () =>
        {
            await graph.InvokeAsync([], new RunOptions { ThreadId = "lv-reducer-1" });
        });
    }

    [Fact(DisplayName = "Given DictMergeReducer, when a write is not a dictionary, then fails with channel.invalid_write")]
    public async Task DictMergeRejectsNonDictionaryWrite()
    {
        var graph = new StateGraph()
            .AddChannel("artifacts", new DictMergeReducer())
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("artifacts", "not-a-dict"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        var exception = await Should.ThrowAsync<GraphException>(async () =>
        {
            await graph.InvokeAsync([], new RunOptions { ThreadId = "dict-merge-bad" });
        });
        exception.Code.ShouldBe(VolutaErrorCodes.ChannelInvalidWrite);
    }
}
