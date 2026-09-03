using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoints.File;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Xunit;

namespace Voluta.Checkpoints.File.Unit;

public sealed class FileCheckpointerRuntimeShould
{
    [Fact(DisplayName = "Given FileCheckpointer, when interrupt then new FileCheckpointer on same root Resume, then continues to End")]
    public async Task RehydrateAndResumeHitl()
    {
        var root = Path.Combine(Path.GetTempPath(), "voluta-file-rt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new FileCheckpointer(root);
            var graph = BuildInterruptGraph(first);

            var interrupted = await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "file-hitl-1", StreamMode = StreamMode.Events });
            interrupted.Kind.ShouldBe(StreamEventKind.Interrupt);

            var reloaded = new FileCheckpointer(root);
            var resumedGraph = BuildInterruptGraph(reloaded);
            var terminal = await resumedGraph.ResumeInvokeAsync(
                "file-hitl-1",
                Command.Approve("ok"));

            terminal.Kind.ShouldBe(StreamEventKind.End);
            var snapshot = await reloaded.GetAsync("file-hitl-1");
            snapshot!.Status.ShouldBe(GraphRunStatus.Done);
            var messages = snapshot.ChannelValues["messages"].ShouldBeOfType<List<object?>>();
            messages.ShouldContain("approved");

            var state = await resumedGraph.GetStateAsync("file-hitl-1");
            state.ShouldNotBeNull();
            state.Status.ShouldBe(GraphRunStatus.Done);
            state.Values["messages"].ShouldBeOfType<List<object?>>().ShouldContain("approved");

            var history = await resumedGraph.GetHistoryAsync("file-hitl-1");
            history.Count.ShouldBeGreaterThanOrEqualTo(1);
            history[^1].Step.ShouldBe(state.Step);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Given typed LastValue int + Append string + LastValue dict, when FileCheckpointer rehydrates, then Read<T> restores CLR types")]
    public async Task RehydrateTypedChannelsViaDeclaredValueType()
    {
        var root = Path.Combine(Path.GetTempPath(), "voluta-file-typed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new FileCheckpointer(root);
            var graph = BuildTypedInterruptGraph(first, capture: null);

            var interrupted = await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "file-typed-1", StreamMode = StreamMode.Events });
            interrupted.Kind.ShouldBe(StreamEventKind.Interrupt);

            var reloaded = new FileCheckpointer(root);
            var restoredScore = 0;
            List<object?>? restoredMessages = null;
            Dictionary<string, string>? restoredArtifacts = null;
            var resumedGraph = BuildTypedInterruptGraph(
                reloaded,
                (score, messages, artifacts) =>
                {
                    restoredScore = score;
                    restoredMessages = messages;
                    restoredArtifacts = artifacts;
                });
            var terminal = await resumedGraph.ResumeInvokeAsync(
                "file-typed-1",
                Command.Approve("ok"));

            terminal.Kind.ShouldBe(StreamEventKind.End);
            restoredScore.ShouldBe(7);
            restoredMessages.ShouldNotBeNull();
            restoredMessages.ShouldBe(["a"]);
            restoredMessages[0].ShouldBeOfType<string>();
            restoredArtifacts.ShouldNotBeNull();
            restoredArtifacts["k"].ShouldBe("v");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CompiledGraph BuildInterruptGraph(FileCheckpointer checkpointer)
    {
        return new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "gate",
                static (context, _) => context.ResumePayload is null
                    ? Task.FromResult<NodeResult>(NodeResult.Interrupt("need-approve"))
                    : Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "approved"))))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);
    }

    private static CompiledGraph BuildTypedInterruptGraph(
        FileCheckpointer checkpointer,
        Action<int, List<object?>?, Dictionary<string, string>?>? capture)
    {
        return new StateGraph()
            .AddChannel<int>("score", ChannelKind.LastValue)
            .AddChannel<string>("messages", ChannelKind.Append)
            .AddChannel<Dictionary<string, string>>("artifacts", ChannelKind.LastValue)
            .AddNode(
                "write",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(
                        new ChannelWrite("score", 7),
                        new ChannelWrite("messages", "a"),
                        new ChannelWrite(
                            "artifacts",
                            new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" }))))
            .AddNode(
                "gate",
                static (context, _) => context.ResumePayload is null
                    ? Task.FromResult<NodeResult>(NodeResult.Interrupt("need-approve"))
                    : Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode(
                "read",
                (context, _) =>
                {
                    capture?.Invoke(
                        context.Read<int>("score"),
                        context.Read<List<object?>>("messages"),
                        context.Read<Dictionary<string, string>>("artifacts"));
                    return Task.FromResult<NodeResult>(NodeResult.Continue());
                })
            .AddEdge(GraphConstants.Start, "write")
            .AddEdge("write", "gate")
            .AddEdge("gate", "read")
            .AddEdge("read", GraphConstants.End)
            .Compile(checkpointer);
    }
}
