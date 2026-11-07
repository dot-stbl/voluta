using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.DependencyInjection;
using Voluta.Exceptions.Run;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;
using Xunit;

namespace Voluta.DependencyInjection.Unit;

public sealed class GraphResolverRegistrationShould
{
    [Fact(DisplayName = "Given Graphs(resolver), when the container is built, then IGraphResolver resolves and no CompiledGraph is registered")]
    public void GraphsRegistersResolverOnly()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildLinearGraph(checkpointer);

        var services = new ServiceCollection();
        _ = services.AddVoluta(builder => builder.Graphs(new SingleGraphResolver(graph)));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IGraphResolver>().ShouldNotBeNull();
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<CompiledGraph>());
    }

    [Fact(DisplayName = "Given Graphs(default, versions), when resolving, then a matching identity pair resolves its variant and null snapshot resolves the default")]
    public async Task GraphsOverloadResolvesByIdentityPair()
    {
        var checkpointer = new InMemoryCheckpointer();
        var v1 = BuildLinearGraph(
            checkpointer,
            "identity-v1",
            new CompileOptions { GraphId = "comuki-graph", GraphVersion = "v1" });
        var v2 = BuildLinearGraph(
            checkpointer,
            "identity-v2",
            new CompileOptions { GraphId = "comuki-graph", GraphVersion = "v2" });

        var services = new ServiceCollection();
        _ = services.AddVoluta(builder => builder.Graphs(v1, v2));

        await using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IGraphResolver>();

        (await resolver.ResolveAsync("t", null)).ShouldBeSameAs(v1);
        (await resolver.ResolveAsync("t", SnapshotWith("comuki-graph", "v2"))).ShouldBeSameAs(v2);
        // Foreign pair → default graph; the engine turns it into graph.version_mismatch on resume.
        (await resolver.ResolveAsync("t", SnapshotWith("unknown-graph", "v1"))).ShouldBeSameAs(v1);
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<CompiledGraph>());
    }

    [Fact(DisplayName = "Given Graph(...), when the container is built, then both the graph and a default resolver resolve")]
    public async Task GraphRegistersGraphAndDefaultResolver()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildLinearGraph(
            checkpointer,
            "single-graph",
            new CompileOptions { GraphId = "comuki-graph", GraphVersion = "v1" });

        var services = new ServiceCollection();
        _ = services.AddVoluta(builder => builder.Graph(graph));

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<CompiledGraph>().ShouldBeSameAs(graph);
        var resolver = provider.GetRequiredService<IGraphResolver>();
        (await resolver.ResolveAsync("t", null)).ShouldBeSameAs(graph);
        resolver.ShouldBeOfType<SingleGraphResolver>();
    }

    [Fact(DisplayName = "Given Graph(...) then Graphs(...), when Complete runs, then the explicit resolver wins over the auto-registered default")]
    public void GraphsWinsOverGraphDefaultResolver()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildLinearGraph(checkpointer);
        var customResolver = new CustomResolver();

        var services = new ServiceCollection();
        _ = services.AddVoluta(builder =>
        {
            builder.Graph(graph);
            builder.Graphs(customResolver);
        });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IGraphResolver>().ShouldBeSameAs(customResolver);
        provider.GetRequiredService<CompiledGraph>().ShouldBeSameAs(graph);
    }

    [Fact(DisplayName = "Given Graphs(...) then Graph(...), when Complete runs, then the explicit resolver still wins")]
    public void GraphsWinsRegardlessOfRegistrationOrder()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildLinearGraph(checkpointer);
        var customResolver = new CustomResolver();

        var services = new ServiceCollection();
        _ = services.AddVoluta(builder =>
        {
            builder.Graphs(customResolver);
            builder.Graph(graph);
        });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IGraphResolver>().ShouldBeSameAs(customResolver);
    }

    [Fact(DisplayName = "Given a default resolver over a single graph, when a thread resumes with a foreign identity pair, then resume fails graph.version_mismatch")]
    public async Task DefaultResolverForeignPairFailsOnResume()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddNode(
                "gate",
                static (context, _) => context.ResumePayload is null
                    ? Task.FromResult<NodeResult>(NodeResult.Interrupt("need-approve"))
                    : Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(
                checkpointer,
                new CompileOptions { GraphId = "comuki-graph", GraphVersion = "v2" });

        var services = new ServiceCollection();
        _ = services.AddVoluta(builder => builder.Graph(graph));
        using var provider = services.BuildServiceProvider();

        // Foreign pair parked in storage (deployed graph version no longer registered).
        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "foreign-resume",
                Step = 1,
                Status = GraphRunStatus.Interrupted,
                LastNode = "gate",
                NextNodes = ["gate"],
                InterruptPayload = new Dictionary<string, object?> { ["amount"] = 1 },
                GraphId = "comuki-graph",
                GraphVersion = "v1",
            });

        var resolvedGraph = await provider.GetRequiredService<IGraphResolver>()
            .ResolveAsync("foreign-resume", await checkpointer.GetAsync("foreign-resume"));
        resolvedGraph.ShouldBeSameAs(graph); // resolver returns its graph; the engine validates

        var exception = await Should.ThrowAsync<GraphVersionMismatchException>(async () =>
        {
            await resolvedGraph.ResumeInvokeAsync("foreign-resume", Command.Approve("ok"));
        });

        exception.Code.ShouldBe(VolutaErrorCodes.GraphVersionMismatch);
    }

    private static CheckpointSnapshot SnapshotWith(string graphId, string graphVersion)
    {
        return new CheckpointSnapshot
        {
            ThreadId = "t",
            Step = 1,
            Status = GraphRunStatus.Running,
            GraphId = graphId,
            GraphVersion = graphVersion,
        };
    }

    private static CompiledGraph BuildLinearGraph(
        ICheckpointer checkpointer,
        string? threadId = null,
        CompileOptions? options = null)
    {
        var builder = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "from-a"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End);
        return options is null
            ? builder.Compile(checkpointer)
            : builder.Compile(checkpointer, options);
    }

    private sealed class CustomResolver : IGraphResolver
    {
        public Task<CompiledGraph> ResolveAsync(
            string threadId,
            CheckpointSnapshot? snapshot,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Custom resolver should be the registered one.");
        }
    }
}
