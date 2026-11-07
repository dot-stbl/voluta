using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Serialization;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoints.File;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;
using Xunit;

namespace Voluta.Checkpoints.File.Unit;

/// <summary>
///     v050 transport gate: CompileOptions.CheckpointTypes must reach the provider through
///     the engine (Compile → GraphTopology → CompiledGraph assignment). Typed restore only
///     works if the registry actually arrived — this test fails when the transport breaks.
/// </summary>
public sealed class FileCheckpointTypeTransportShould
{
    private sealed record HostOrder(string Sku, int Quantity);

    [Fact(DisplayName = "Given a graph compiled with CheckpointTypes, when a node writes the registered type, then the provider restores it typed")]
    public async Task RegistryReachesProviderThroughTheEngine()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "voluta-file-transport-" + Guid.NewGuid().ToString("N"));
        try
        {
            var checkpointer = new FileCheckpointer(root);
            var graph = new StateGraph()
                .AddChannel("orders", ChannelKind.LastValue)
                .AddNode(
                    "seller",
                    static (_, _) => Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("orders", new HostOrder("SKU-9", 2)))))
                .AddEdge(GraphConstants.Start, "seller")
                .AddEdge("seller", GraphConstants.End)
                .Compile(
                    checkpointer,
                    new CompileOptions
                    {
                        CheckpointTypes = new CheckpointTypeRegistry()
                            .Register<HostOrder>("host:order"),
                    });

            // The engine must have stamped the registry onto the provider at compile time.
            checkpointer.TypeRegistry.ShouldNotBeNull(
                "CompileOptions.CheckpointTypes must reach the IRegistryAwareCheckpointer provider");

            var terminal = await graph.InvokeAsync(
                [],
                new RunOptions { ThreadId = "transport", StreamMode = StreamMode.Values });

            terminal.Kind.ShouldBe(StreamEventKind.End);
            var state = await graph.GetStateAsync("transport");
            state!.Values["orders"].ShouldBeOfType<HostOrder>();
            state.Values["orders"].ShouldBe(new HostOrder("SKU-9", 2));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
