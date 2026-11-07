using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Voluta.Abstractions.Checkpoint;
using Voluta.DependencyInjection.Checkpoints;
using Voluta.DependencyInjection.Store;
using Voluta.Graph;

namespace Voluta.DependencyInjection;

/// <summary>
///     Fluent composition root for Voluta host registration: checkpoints, cross-thread store, graph.
/// </summary>
public sealed class VolutaBuilder(IServiceCollection services)
{
    private Func<IServiceProvider, CompiledGraph>? graphFactory;
    private bool graphRequiresCheckpointer;
    private IGraphResolver? resolverOverride;

    /// <summary>Service collection being configured.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>
    ///     Checkpoint store registration (<c>UseInMemory</c> / <c>UseFile</c> / Sqlite / EF / S3 / Postgres).
    /// </summary>
    public VolutaCheckpointBuilder Checkpoints { get; } = new(services);

    /// <summary>
    ///     Cross-thread key-value store registration (<c>UseInMemory</c>). Optional — independent of checkpoints.
    /// </summary>
    public VolutaStoreBuilder Store { get; } = new(services);

    /// <summary>
    ///     Registers a pre-built <see cref="CompiledGraph" /> as a singleton.
    ///     Does not require <see cref="Checkpoints" /> (the graph already owns its checkpointer).
    ///     Also auto-registers a default <see cref="IGraphResolver" /> over that graph (via
    ///     <c>TryAdd</c> — an explicit <see cref="Graphs(IGraphResolver)" /> registration wins),
    ///     so resolver-based consumers (hosting worker, ops UI) coexist with single-graph hosts.
    /// </summary>
    /// <param name="graph">Compiled graph instance.</param>
    /// <returns>This builder for chaining.</returns>
    public VolutaBuilder Graph(CompiledGraph graph)
    {
        graphFactory = _ => graph;
        graphRequiresCheckpointer = false;
        return this;
    }

    /// <summary>
    ///     Registers a graph factory that receives the resolved <see cref="ICheckpointer" />.
    ///     Requires exactly one <c>Checkpoints.Use*</c> call. Also auto-registers a default
    ///     <see cref="IGraphResolver" /> (see <see cref="Graph(CompiledGraph)" />).
    /// </summary>
    /// <param name="factory">Builds and compiles the graph once per process.</param>
    /// <returns>This builder for chaining.</returns>
    public VolutaBuilder Graph(Func<IServiceProvider, ICheckpointer, CompiledGraph> factory)
    {
        graphFactory = serviceProvider =>
            factory(serviceProvider, serviceProvider.GetRequiredService<ICheckpointer>());
        graphRequiresCheckpointer = true;
        return this;
    }

    /// <summary>
    ///     Registers a graph factory with full service provider access.
    ///     Call <see cref="Checkpoints" /> first when the factory resolves <see cref="ICheckpointer" />.
    ///     Pass <c>new CompileOptions { Services = sp }</c> into <c>Compile</c> for DI nodes / MAF.
    ///     Also auto-registers a default <see cref="IGraphResolver" /> (see
    ///     <see cref="Graph(CompiledGraph)" />).
    /// </summary>
    /// <param name="factory">Builds and compiles the graph once per process.</param>
    /// <returns>This builder for chaining.</returns>
    public VolutaBuilder Graph(Func<IServiceProvider, CompiledGraph> factory)
    {
        graphFactory = factory;
        graphRequiresCheckpointer = false;
        return this;
    }

    /// <summary>
    ///     Registers a per-thread <see cref="IGraphResolver" /> as a singleton — resolver-based
    ///     multi-version hosting. Does <strong>NOT</strong> register a <see cref="CompiledGraph" />:
    ///     hosts that opt in resolve <see cref="IGraphResolver" />, not a concrete graph.
    /// </summary>
    /// <param name="resolver">Thread → graph resolver (host-owned default + versioned graphs).</param>
    /// <returns>This builder for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         Resume / continue validate a snapshot's identity pair against the resolved graph:
    ///         a thread whose snapshot carries a pair the resolver's graphs do not match fails with
    ///         <c>graph.version_mismatch</c> — that is the intended loud failure when a host
    ///         forgets to register (or deploy) a graph version that is still parked in storage.
    ///     </para>
    /// </remarks>
    public VolutaBuilder Graphs(IGraphResolver resolver)
    {
        resolverOverride = resolver;
        return this;
    }

    /// <summary>
    ///     Registers a resolver over a default graph plus optional versioned variants — a
    ///     convenience overload of <see cref="Graphs(IGraphResolver)" /> with the same contract:
    ///     <see cref="IGraphResolver" /> singleton, no <see cref="CompiledGraph" /> registration.
    ///     Resolution: a snapshot with an identity pair resolves the variant with that exact pair;
    ///     anything else (null snapshot, foreign pair) resolves the default graph — foreign pairs
    ///     then fail resume / continue with <c>graph.version_mismatch</c> (engine validation).
    /// </summary>
    /// <param name="defaultGraph">Graph resolved for new threads (null snapshot).</param>
    /// <param name="versions">Versioned graphs resolved by exact identity pair match.</param>
    /// <returns>This builder for chaining.</returns>
    public VolutaBuilder Graphs(CompiledGraph defaultGraph, params CompiledGraph[] versions)
    {
        resolverOverride = new IdentityPairGraphResolver(defaultGraph, versions);
        return this;
    }

    /// <summary>
    ///     Validates configuration and applies DI registrations.
    /// </summary>
    internal void Complete()
    {
        if (graphRequiresCheckpointer && !Checkpoints.IsProviderConfigured)
        {
            throw new InvalidOperationException(
                "AddVoluta Graph(sp, checkpointer => …) requires Checkpoints.Use* (UseInMemory, UseFile, UseSqlite, UseEntityFrameworkCore, UseS3, UsePostgres).");
        }

        if (resolverOverride is { } resolver)
        {
            // Resolver-based hosting: IGraphResolver only — no CompiledGraph registration.
            Services.RemoveAll<IGraphResolver>();
            Services.AddSingleton(resolver);
        }

        if (graphFactory is { } factory)
        {
            Services.RemoveAll<CompiledGraph>();
            Services.AddSingleton(factory);
            // Back-compat: single-graph hosts get a default resolver too (TryAdd so an
            // explicit Graphs(...) registration always wins, in any configuration order).
            Services.TryAddSingleton<IGraphResolver>(serviceProvider =>
                new SingleGraphResolver(serviceProvider.GetRequiredService<CompiledGraph>()));
        }
    }
}

/// <summary>
///     Resolver over a default graph plus identity-pair-matched variants (DI convenience).
///     Internal — hosts wanting custom rules implement <see cref="IGraphResolver" /> directly.
/// </summary>
internal sealed class IdentityPairGraphResolver(CompiledGraph defaultGraph, IReadOnlyList<CompiledGraph> versions)
    : IGraphResolver
{
    public Task<CompiledGraph> ResolveAsync(
        string threadId,
        CheckpointSnapshot? snapshot,
        CancellationToken cancellationToken = default)
    {
        if (snapshot?.GraphId is { } graphId && snapshot.GraphVersion is { } graphVersion)
        {
            foreach (var variant in versions)
            {
                if (string.Equals(variant.GraphId, graphId, StringComparison.Ordinal)
                    && string.Equals(variant.GraphVersion, graphVersion, StringComparison.Ordinal))
                {
                    return Task.FromResult(variant);
                }
            }
        }

        return Task.FromResult(defaultGraph);
    }
}

