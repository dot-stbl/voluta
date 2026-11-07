using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Exceptions.Run;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;
using Xunit;

namespace Voluta.Unit.Runtime;

public sealed class UnlimitedRecursionShould
{
    private static (StateGraph Graph, List<int> Seen) CountingCycleGraph(int exitAfter)
    {
        var seen = new List<int>();
        var graph = new StateGraph()
            .AddChannel("passes", ChannelKind.LastValue)
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode(
                "b",
                (context, _) =>
                {
                    seen.Add(context.Attempt);
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("passes", seen.Count)));
                })
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddConditionalEdges(
                "b",
                context => context.Read<int>("passes") >= exitAfter
                    ? GraphConstants.End
                    : "a");
        return (graph, seen);
    }

    [Fact(DisplayName = "Given WithoutRecursionLimit, when a cycle runs past the shipped 25-step cap, then it reaches END")]
    public async Task CycleRunsPastShippedCapToEnd()
    {
        const int passesNeeded = 30;
        var (builder, seenPasses) = CountingCycleGraph(passesNeeded);
        var graph = builder.Compile(new InMemoryCheckpointer(), CompileOptions.WithoutRecursionLimit());

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "unlimited", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        seenPasses.Count.ShouldBe(passesNeeded);
        var done = await graph.GetStateAsync("unlimited");
        done!.Status.ShouldBe(GraphRunStatus.Done);
    }

    [Fact(DisplayName = "Given the same cycle under the shipped default, when it passes step 25, then out-of-steps fails the run")]
    public async Task ShippedDefaultStillCaps()
    {
        const int passesNeeded = 30;
        var (builder, seenPasses) = CountingCycleGraph(passesNeeded);
        var graph = builder.Compile(new InMemoryCheckpointer());

        await Should.ThrowAsync<GraphOutOfStepsException>(
            async () => await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "limited", StreamMode = StreamMode.Events }));

        seenPasses.Count.ShouldBeLessThan(passesNeeded);
    }

    [Fact(DisplayName = "Given RecursionLimit.Unlimited directly, when compiled, then behaves like WithoutRecursionLimit")]
    public async Task UnlimitedConstEquivalent()
    {
        var (builder, seenPasses) = CountingCycleGraph(27);
        var graph = builder.Compile(
            new InMemoryCheckpointer(),
            new CompileOptions { RecursionLimit = RecursionLimit.Unlimited });

        var terminal = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "unlimited-const", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        seenPasses.Count.ShouldBe(27);
    }
}
