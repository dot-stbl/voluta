using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Serialization;
using Xunit;

namespace Voluta.Checkpoints.EntityFrameworkCore.Unit;

public sealed class EfRegistryAwareShould
{
    private sealed record HostOrder(string Sku, int Quantity);

    [Fact(DisplayName = "Given a registered type in channel values, when Put/Get, then the value restores typed")]
    public async Task RegisteredTypeRestoresTyped()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var checkpointer = await CreateCheckpointerAsync(connection);
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
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var checkpointer = await CreateCheckpointerAsync(connection);
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

    private static async Task<EntityFrameworkCoreCheckpointer<VolutaCheckpointDbContext>> CreateCheckpointerAsync(
        SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<VolutaCheckpointDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setup = new VolutaCheckpointDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        return new EntityFrameworkCoreCheckpointer<VolutaCheckpointDbContext>(
            new SharedOptionsDbContextFactory(options));
    }
}

/// <summary>Factory sharing prepared options (pattern of the conformance test).</summary>
file sealed class SharedOptionsDbContextFactory(DbContextOptions<VolutaCheckpointDbContext> options)
    : IDbContextFactory<VolutaCheckpointDbContext>
{
    public VolutaCheckpointDbContext CreateDbContext()
    {
        return new VolutaCheckpointDbContext(options);
    }

    public async Task<VolutaCheckpointDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(CreateDbContext());
    }
}
