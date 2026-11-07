using Shouldly;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Serialization;
using Voluta.Checkpoints.File;
using Xunit;

namespace Voluta.Checkpoints.File.Unit;

public sealed class FileRegistryAwareShould
{
    private sealed record HostOrder(string Sku, int Quantity);

    private static FileCheckpointer CreateCheckpointer()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "voluta-file-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new FileCheckpointer(root);
    }

    [Fact(DisplayName = "Given a registered type in channel values, when Put/Get, then the value restores typed")]
    public async Task RegisteredTypeRestoresTyped()
    {
        var checkpointer = CreateCheckpointer();
        checkpointer.TypeRegistry = new CheckpointTypeRegistry()
            .Register<HostOrder>("host:order");

        var threadId = "reg-" + Guid.NewGuid().ToString("N");
        var order = new HostOrder("SKU-7", 3);
        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = threadId,
                Step = 1,
                Status = GraphRunStatus.Running,
                ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["order"] = order,
                },
            });

        var loaded = await checkpointer.GetAsync(threadId);

        loaded.ShouldNotBeNull();
        loaded!.ChannelValues["order"].ShouldBeOfType<HostOrder>();
        loaded.ChannelValues["order"].ShouldBe(order);
    }

    [Fact(DisplayName = "Given a stamped payload, when read without a registry, then falls back to raw JSON")]
    public async Task StampedPayloadReadsRawWithoutRegistry()
    {
        var checkpointer = CreateCheckpointer();
        checkpointer.TypeRegistry = new CheckpointTypeRegistry()
            .Register<HostOrder>("host:order");
        var threadId = "raw-" + Guid.NewGuid().ToString("N");
        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = threadId,
                Step = 1,
                Status = GraphRunStatus.Running,
                ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["order"] = new HostOrder("SKU-8", 4),
                },
            });

        checkpointer.TypeRegistry = null;
        var loaded = await checkpointer.GetAsync(threadId);

        loaded.ShouldNotBeNull();
        loaded!.ChannelValues["order"].ShouldNotBeOfType<HostOrder>();
    }

    [Fact(DisplayName = "Given primitives, when registered or not, then they stay raw on the wire")]
    public async Task PrimitivesStayRaw()
    {
        var checkpointer = CreateCheckpointer();
        checkpointer.TypeRegistry = new CheckpointTypeRegistry()
            .Register<HostOrder>("host:order")
            .Register(typeof(int), "host:int");
        var threadId = "prim-" + Guid.NewGuid().ToString("N");
        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = threadId,
                Step = 1,
                Status = GraphRunStatus.Running,
                ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["count"] = 42,
                },
            });

        var loaded = await checkpointer.GetAsync(threadId);

        loaded.ShouldNotBeNull();
        loaded!.ChannelValues["count"].ShouldBe(42);
    }
}
