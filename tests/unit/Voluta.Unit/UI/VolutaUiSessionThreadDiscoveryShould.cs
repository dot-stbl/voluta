using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.UI;
using Xunit;

namespace Voluta.Unit.UI;

public sealed class VolutaUiSessionThreadDiscoveryShould
{
    [Fact(DisplayName = "Given durable put without TrackThread, when ListThreadsAsync, then includes discovered thread")]
    public async Task ListThreadsFromDiscoveryWithoutTrack()
    {
        var checkpointer = new InMemoryCheckpointer();
        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "discovered-1",
                Step = 3,
                Status = GraphRunStatus.Interrupted,
                ChannelValues = new Dictionary<string, object?> { ["goal"] = "ship it" },
                LastNode = "review",
            });

        var session = new VolutaUiSession(new SingleGraphResolver(BuildGraph(checkpointer)), checkpointer);

        var threads = await session.ListThreadsAsync();

        threads.Count.ShouldBe(1);
        threads[0].ThreadId.ShouldBe("discovered-1");
        threads[0].Status.ShouldBe(GraphRunStatus.Interrupted.ToString());
        threads[0].Step.ShouldBe(3);
        threads[0].Goal.ShouldBe("ship it");
        threads[0].LastNode.ShouldBe("review");
    }

    [Fact(DisplayName = "Given tracked id without checkpoint and discovered id, when ListThreadsAsync, then merges both")]
    public async Task MergeTrackedAndDiscovered()
    {
        var checkpointer = new InMemoryCheckpointer();
        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "from-store",
                Step = 1,
                Status = GraphRunStatus.Running,
            });

        var session = new VolutaUiSession(new SingleGraphResolver(BuildGraph(checkpointer)), checkpointer);
        session.TrackThread("tracked-only");

        var threads = await session.ListThreadsAsync();

        threads.Select(static thread => thread.ThreadId).ShouldBe(["from-store", "tracked-only"]);
        threads.Single(static thread => thread.ThreadId == "tracked-only").Status.ShouldBe("Unknown");
        threads.Single(static thread => thread.ThreadId == "from-store").Status
            .ShouldBe(GraphRunStatus.Running.ToString());
    }

    private static CompiledGraph BuildGraph(ICheckpointer checkpointer)
    {
        return new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "noop",
                static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "noop")
            .AddEdge("noop", GraphConstants.End)
            .Compile(checkpointer);
    }
}
