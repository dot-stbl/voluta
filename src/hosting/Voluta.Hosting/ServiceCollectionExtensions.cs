using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Voluta.Hosting.Wake;
using Voluta.Hosting.Worker;

namespace Voluta.Hosting;

/// <summary>
///     DI presets for Voluta worker hosting (wake bus + background runner).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the in-memory <see cref="IThreadWakeBus" />, <see cref="GraphThreadRunner" />,
    ///     and <see cref="GraphWorkerService" /> as a hosted service.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same collection for chaining.</returns>
    /// <remarks>
    ///     Requires a singleton <see cref="Voluta.Graph.IGraphResolver" /> and
    ///     <see cref="Voluta.Abstractions.Checkpoint.ICheckpointer" /> (typically via
    ///     <c>AddVoluta</c> — both <c>Graph(...)</c> and <c>Graphs(...)</c> register the resolver).
    ///     Multi-instance hosts should replace <see cref="InMemoryThreadWakeBus" /> with a
    ///     durable bus and use a shared checkpointer (File / EF / S3).
    /// </remarks>
    public static IServiceCollection AddVolutaWorkerHosting(this IServiceCollection services)
    {
        services.AddInMemoryThreadWakeBus();
        services.TryAddSingleton<GraphThreadRunner>();
        services.TryAddSingleton<GraphWorkerService>();
        services.AddHostedService(static provider => provider.GetRequiredService<GraphWorkerService>());
        return services;
    }

    /// <summary>
    ///     Registers <see cref="InMemoryThreadWakeBus" /> as the singleton <see cref="IThreadWakeBus" />.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddInMemoryThreadWakeBus(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryThreadWakeBus>();
        services.TryAddSingleton<IThreadWakeBus>(static provider =>
            provider.GetRequiredService<InMemoryThreadWakeBus>());
        return services;
    }
}
