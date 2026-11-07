using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Results;
using Voluta.Checkpoint;
using Voluta.Exceptions;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;
using Xunit;

namespace Voluta.Unit.Graph;

public sealed class StateGraphCompileShould
{
    [Fact(DisplayName = "Given compile options with graph identity, when Compile is called, then the pair lands on the compiled graph")]
    public void CarryIdentityFromCompileOptions()
    {
        var graph = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End)
            .Compile(
                new InMemoryCheckpointer(),
                new CompileOptions { GraphId = "comuki-graph", GraphVersion = "2026.11.0" });

        graph.GraphId.ShouldBe("comuki-graph");
        graph.GraphVersion.ShouldBe("2026.11.0");
    }

    [Fact(DisplayName = "Given compile options without identity, when Compile is called, then the graph stays anonymous")]
    public void StayAnonymousWithoutIdentity()
    {
        var graph = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End)
            .Compile(new InMemoryCheckpointer());

        graph.GraphId.ShouldBeNull();
        graph.GraphVersion.ShouldBeNull();
    }

    [Fact(DisplayName = "Given missing START edge, when Compile is called, then fails")]
    public void FailWhenStartMissing()
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()));

        var exception = Should.Throw<GraphCompileException>(
            () => builder.Compile(new InMemoryCheckpointer()));

        exception.Code.ShouldBe("graph.missing_start");
    }

    [Fact(DisplayName = "Given duplicate node name, when AddNode is called, then fails")]
    public void FailOnDuplicateNode()
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()));

        var exception = Should.Throw<GraphCompileException>(
            () => builder.AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue())));

        exception.Code.ShouldBe("graph.duplicate_node");
    }

    [Fact(DisplayName = "Given edge to unknown node, when Compile is called, then fails")]
    public void FailOnUnknownEndpoint()
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "missing");

        var exception = Should.Throw<GraphCompileException>(
            () => builder.Compile(new InMemoryCheckpointer()));

        exception.Code.ShouldBe("graph.unknown_endpoint");
    }

    [Fact(DisplayName = "Given a cycle, when Compile is called, then succeeds")]
    public void AllowCycles()
    {
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode("agent", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddNode("tools", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "agent")
            .AddEdge("agent", "tools")
            .AddEdge("tools", "agent")
            .Compile(new InMemoryCheckpointer());

        graph.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Given empty graph, when Compile is called, then fails with graph.no_nodes")]
    public void FailWhenNoNodes()
    {
        var exception = Should.Throw<GraphCompileException>(
            () => new StateGraph().Compile(new InMemoryCheckpointer()));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphNoNodes);
    }

    [Fact(DisplayName = "Given END as edge source, when Compile is called, then fails with graph.invalid_edge")]
    public void FailWhenEndIsSource()
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.End)
            .AddEdge(GraphConstants.End, "a");

        var exception = Should.Throw<GraphCompileException>(
            () => builder.Compile(new InMemoryCheckpointer()));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidEdge);
    }

    [Fact(DisplayName = "Given START as edge target, when Compile is called, then fails with graph.invalid_edge")]
    public void FailWhenStartIsTarget()
    {
        var builder = new StateGraph()
            .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", GraphConstants.Start);

        var exception = Should.Throw<GraphCompileException>(
            () => builder.Compile(new InMemoryCheckpointer()));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidEdge);
    }

    [Fact(DisplayName = "Given reserved START node name, when AddNode is called, then fails with graph.invalid_node")]
    public void FailOnReservedNodeName()
    {
        var exception = Should.Throw<GraphCompileException>(
            () => new StateGraph().AddNode(
                GraphConstants.Start,
                static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue())));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidNode);
    }

    [Fact(DisplayName = "Given empty channel name, when AddChannel is called, then fails with graph.invalid_channel")]
    public void FailOnEmptyChannelName()
    {
        var exception = Should.Throw<GraphCompileException>(
            () => new StateGraph().AddChannel("  ", ChannelKind.LastValue));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidChannel);
    }

    [Fact(DisplayName = "Given duplicate channel, when AddChannel is called, then fails with graph.duplicate_channel")]
    public void FailOnDuplicateChannel()
    {
        var builder = new StateGraph().AddChannel("status", ChannelKind.LastValue);

        var exception = Should.Throw<GraphCompileException>(
            () => builder.AddChannel("status", ChannelKind.Append));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphDuplicateChannel);
    }

    [Fact(DisplayName = "Given empty edge endpoints, when AddEdge is called, then fails with graph.invalid_edge")]
    public void FailOnEmptyEdgeEndpoints()
    {
        var exception = Should.Throw<GraphCompileException>(
            () => new StateGraph().AddEdge("", "a"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidEdge);
    }

    [Fact(DisplayName = "Given unknown endpoint codes, when Compile fails, then codes match catalog constants")]
    public void CompileFailuresUseCatalogCodes()
    {
        Should.Throw<GraphCompileException>(() =>
                new StateGraph()
                    .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
                    .Compile(new InMemoryCheckpointer()))
            .Code.ShouldBe(VolutaErrorCodes.GraphMissingStart);

        Should.Throw<GraphCompileException>(() =>
                new StateGraph()
                    .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
                    .AddEdge(GraphConstants.Start, "a")
                    .AddEdge("a", "missing")
                    .Compile(new InMemoryCheckpointer()))
            .Code.ShouldBe(VolutaErrorCodes.GraphUnknownEndpoint);

        Should.Throw<GraphCompileException>(() =>
                new StateGraph()
                    .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
                    .AddNode("a", static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue())))
            .Code.ShouldBe(VolutaErrorCodes.GraphDuplicateNode);
    }
}
