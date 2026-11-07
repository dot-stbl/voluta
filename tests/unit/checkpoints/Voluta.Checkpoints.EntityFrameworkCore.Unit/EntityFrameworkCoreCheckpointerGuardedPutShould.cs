using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;
using Voluta.Testing.Checkpoint;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Runtime;
using Xunit;

namespace Voluta.Checkpoints.EntityFrameworkCore.Unit;

public sealed class EntityFrameworkCoreCheckpointerGuardedPutShould
{
    [Fact(DisplayName = "Given a guarded put with a stale expected parent, when put, then throws checkpoint.conflict and the snapshot is not persisted")]
    public async Task StaleParentConflicts()
    {
        await using var harness = await GuardedHarness.CreateAsync();
        await harness.PutAsync(Snapshot("ef-guard-1", step: 1), cancellationToken: default);
        await harness.PutAsync(Snapshot("ef-guard-1", step: 2), cancellationToken: default);

        var exception = await Should.ThrowAsync<CheckpointStoreException>(async () =>
        {
            await harness.PutAsync(
                Snapshot("ef-guard-1", step: 2),
                expectedParentStep: 1,
                unitOfWork: null,
                cancellationToken: default);
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);
        var latest = await harness.GetAsync("ef-guard-1");
        latest!.Step.ShouldBe(2);
    }

    [Fact(DisplayName = "Given a guarded put with the matching parent, when put, then succeeds and advances the latest step")]
    public async Task MatchingParentAccepts()
    {
        await using var harness = await GuardedHarness.CreateAsync();
        await harness.PutAsync(Snapshot("ef-guard-2", step: 4), cancellationToken: default);

        await harness.PutAsync(
            Snapshot("ef-guard-2", step: 5),
            expectedParentStep: 4,
            unitOfWork: null,
            cancellationToken: default);

        var latest = await harness.GetAsync("ef-guard-2");
        latest!.Step.ShouldBe(5);
    }

    [Fact(DisplayName = "Given a same-step rewrite lost update (stale concurrency token), when the rewrite races, then EF raises DbUpdateConcurrencyException — the token the checkpointer translates to checkpoint.conflict")]
    public async Task SameStepLostUpdateConflicts()
    {
        await using var harness = await GuardedHarness.CreateAsync();
        await harness.PutAsync(Snapshot("ef-guard-3", step: 7), cancellationToken: default);

        // Writer A holds the row with original token 1; writer B rewrites first (token 2).
        await using (var writerA = await harness.OpenTrackedContextAsync())
        {
            _ = await writerA.Checkpoints.SingleAsync(
                static record => record.ThreadId == "ef-guard-3" && record.Step == 7);

            await using var writerB = await harness.OpenTrackedContextAsync();
            var rowB = await writerB.Checkpoints.SingleAsync(
                static record => record.ThreadId == "ef-guard-3" && record.Step == 7);
            rowB.Status = Voluta.Abstractions.Runtime.GraphRunStatus.Done;
            rowB.ConcurrencyToken = rowB.ConcurrencyToken + 1;
            await writerB.SaveChangesAsync();

            // Writer A's late same-step rewrite must lose.
            var rowA = writerA.Checkpoints.Local.Single(
                static record => record.Step == 7);
            rowA.Status = Voluta.Abstractions.Runtime.GraphRunStatus.Interrupted;
            rowA.ConcurrencyToken = rowA.ConcurrencyToken + 1;

            await Should.ThrowAsync<DbUpdateConcurrencyException>(() =>
                writerA.SaveChangesAsync());
        }

        // Sequential same-step rewrite (fresh writer, current token) succeeds — conformance semantics.
        await harness.PutAsync(
            Snapshot("ef-guard-3", step: 7),
            expectedParentStep: 7,
            unitOfWork: null,
            cancellationToken: default);
        var latest = await harness.GetAsync("ef-guard-3");
        latest!.Status.ShouldBe(Voluta.Abstractions.Runtime.GraphRunStatus.Running);
    }

    [Fact(DisplayName = "Given a guarded put on an empty thread with a non-null expected parent, when put, then conflicts (fail closed)")]
    public async Task GuardedPutOnMissingThreadConflicts()
    {
        await using var harness = await GuardedHarness.CreateAsync();

        var exception = await Should.ThrowAsync<CheckpointStoreException>(async () =>
        {
            await harness.PutAsync(
                Snapshot("ef-guard-4", step: 0),
                expectedParentStep: 0,
                unitOfWork: null,
                cancellationToken: default);
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);
    }

    [Fact(DisplayName = "Given a unit of work carrying the host context, when put, then changes are staged on the host context and become visible only when the host saves")]
    public async Task UnitOfWorkEnlistsOnHostContext()
    {
        await using var harness = await GuardedHarness.CreateAsync();
        await harness.PutAsync(Snapshot("ef-uow-1", step: 1), cancellationToken: default);

        var unitOfWork = harness.OpenHostContext();
        await harness.PutAsync(
            Snapshot("ef-uow-1", step: 2),
            expectedParentStep: 1,
            unitOfWork,
            cancellationToken: default);

        // Host has NOT saved — the checkpoint row is not visible to other contexts yet.
        var beforeCommit = await harness.GetAsync("ef-uow-1");
        beforeCommit!.Step.ShouldBe(1);

        var staged = unitOfWork.Context!.Checkpoints.Local
            .Single(static record => record.Step == 2);
        staged.Status.ShouldBe(Voluta.Abstractions.Runtime.GraphRunStatus.Running);

        // Host commits — checkpoint and host writes become visible together.
        await unitOfWork.Context.SaveChangesAsync();
        var afterCommit = await harness.GetAsync("ef-uow-1");
        afterCommit!.Step.ShouldBe(2);
    }
    [Fact(DisplayName = "Given a unit of work without the provider context, when put, then falls back to a standalone write")]
    public async Task UnitOfWorkWithoutContextStaysStandalone()
    {
        await using var harness = await GuardedHarness.CreateAsync();
        var unitOfWork = new NoopUnitOfWork();

        await harness.PutAsync(
            Snapshot("ef-uow-2", step: 1),
            unitOfWork: unitOfWork,
            cancellationToken: default);

        var latest = await harness.GetAsync("ef-uow-2");
        latest!.Step.ShouldBe(1);
    }

    [Fact(DisplayName = "Given a sequential blind rewrite of the latest step, when put, then stays a silent last-writer-wins (no checkpoint.conflict, token untouched)")]
    public async Task BlindRewriteStaysSilentLastWriterWins()
    {
        await using var harness = await GuardedHarness.CreateAsync();
        await harness.PutAsync(Snapshot("ef-blind-1", step: 4), cancellationToken: default);
        await harness.PutAsync(
            Snapshot("ef-blind-1", step: 4, status: GraphRunStatus.Done),
            cancellationToken: default);
        await harness.PutAsync(
            Snapshot("ef-blind-1", step: 4, status: GraphRunStatus.Interrupted),
            cancellationToken: default);

        var latest = await harness.GetAsync("ef-blind-1");
        latest!.Step.ShouldBe(4);
        latest.Status.ShouldBe(GraphRunStatus.Interrupted);
    }

    [Fact(DisplayName = "Given two overlapping blind rewrites of the same step, when both put, then neither conflicts (token untouched, last writer wins)")]
    public async Task OverlappingBlindRewritesDoNotConflict()
    {
        await using var harness = await GuardedHarness.CreateAsync();
        await harness.PutAsync(Snapshot("ef-blind-2", step: 5), cancellationToken: default);

        await Task.WhenAll(
            harness.PutAsync(
                Snapshot("ef-blind-2", step: 5, status: GraphRunStatus.Done),
                cancellationToken: default),
            harness.PutAsync(
                Snapshot("ef-blind-2", step: 5, status: GraphRunStatus.Interrupted),
                cancellationToken: default));

        var latest = await harness.GetAsync("ef-blind-2");
        latest!.Step.ShouldBe(5);
        // No conflict and no put_failed: whichever blind write landed last won silently.
        latest.Status.ShouldBeOneOf(GraphRunStatus.Done, GraphRunStatus.Interrupted);
    }

    [Fact(DisplayName = "Given a guarded commit that bumps the token between the blind put's read and save, when the blind put completes, then it lands silently as the last writer (never checkpoint.conflict)")]
    public async Task OverlappingBlindVsGuardedCommitStaysSilentLastWriterWins()
    {
        await using var harness = await GuardedHarness.CreateAsync();
        const string threadId = "ef-blind-vs-guarded";
        await harness.PutAsync(Snapshot(threadId, step: 7), cancellationToken: default);

        // Guarded rewrite of the same row commits first: token 1 → 2 (status Done).
        await harness.PutAsync(
            Snapshot(threadId, step: 7, status: GraphRunStatus.Done),
            expectedParentStep: 7,
            unitOfWork: null,
            cancellationToken: default);

        // Worst-case overlap: another guarded commit bumps the token (2 → 3) right between
        // the blind put's read and its UPDATE — EF's zero-row match must not surface.
        harness.ArmTokenBump(threadId, step: 7);

        await harness.PutAsync(
            Snapshot(threadId, step: 7, status: GraphRunStatus.Interrupted),
            cancellationToken: default);

        // Hook sanity: the interleaving must actually have happened, or the test proves nothing.
        harness.TokenBumpFired.ShouldBeTrue();

        var latest = await harness.GetAsync(threadId);
        latest!.Step.ShouldBe(7);
        // No conflict and no put_failed: the blind write landed last and won silently.
        latest.Status.ShouldBe(GraphRunStatus.Interrupted);
    }

    private static CheckpointSnapshot Snapshot(string threadId, long step, GraphRunStatus? status = null)
    {
        return new CheckpointSnapshot
        {
            ThreadId = threadId,
            Step = step,
            Status = status ?? Voluta.Abstractions.Runtime.GraphRunStatus.Running,
            GraphId = "comuki-graph",
            GraphVersion = "v1",
            ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal),
            ChannelVersions = new Dictionary<string, long>(StringComparer.Ordinal),
            VersionsSeen = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal),
        };
    }

    /// <summary>SQLite harness over the dedicated Voluta checkpoint context.</summary>
    private sealed class GuardedHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<VolutaCheckpointDbContext> options;
        private readonly TokenBumpInterceptor tokenBump;

        private GuardedHarness(
            SqliteConnection connection,
            DbContextOptions<VolutaCheckpointDbContext> options,
            TokenBumpInterceptor tokenBump)
        {
            this.connection = connection;
            this.options = options;
            this.tokenBump = tokenBump;
        }

        public static async Task<GuardedHarness> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var tokenBump = new TokenBumpInterceptor();
            var options = new DbContextOptionsBuilder<VolutaCheckpointDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(tokenBump)
                .Options;
            await using (var setup = new VolutaCheckpointDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
            }

            return new GuardedHarness(connection, options, tokenBump);
        }

        /// <summary>
        ///     Arms the one-shot token bump on the given row: the next UPDATE against
        ///     <c>voluta_checkpoints</c> gets <c>ConcurrencyToken + 1</c> applied just before it
        ///     runs — simulating a guarded commit that lands between the blind put's read and save.
        /// </summary>
        public void ArmTokenBump(string threadId, long step)
        {
            this.tokenBump.Arm(threadId, step);
        }

        /// <summary>Whether the armed token bump actually fired (hook sanity check).</summary>
        public bool TokenBumpFired => this.tokenBump.Fired;

        public async Task PutAsync(
            CheckpointSnapshot snapshot,
            long? expectedParentStep = null,
            ICheckpointUnitOfWork? unitOfWork = null,
            CancellationToken cancellationToken = default)
        {
            var checkpointer = new EntityFrameworkCoreCheckpointer(
                new OptionsFactory(options));
            await checkpointer.PutAsync(snapshot, expectedParentStep, unitOfWork, cancellationToken);
        }

        public async Task<CheckpointSnapshot?> GetAsync(
            string threadId,
            CancellationToken cancellationToken = default)
        {
            var checkpointer = new EntityFrameworkCoreCheckpointer(
                new OptionsFactory(options));
            return await checkpointer.GetAsync(threadId, cancellationToken);
        }

        public async Task<VolutaCheckpointDbContext> OpenTrackedContextAsync()
        {
            var context = new VolutaCheckpointDbContext(options);
            await context.Database.OpenConnectionAsync();
            return context;
        }

        public HostUnitOfWork OpenHostContext()
        {
            return new HostUnitOfWork(new VolutaCheckpointDbContext(options));
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>Factory over fixed options (no DI in the harness).</summary>
    private sealed class OptionsFactory(DbContextOptions<VolutaCheckpointDbContext> options)
        : IDbContextFactory<VolutaCheckpointDbContext>
    {
        public VolutaCheckpointDbContext CreateDbContext()
        {
            return new VolutaCheckpointDbContext(options);
        }
    }

    /// <summary>
    ///     One-shot test hook: bumps a row's concurrency token right before its next UPDATE,
    ///     replaying the exact interleaving of an overlapping guarded commit on the same
    ///     (thread, step). Inert until armed; disarms itself after firing.
    /// </summary>
    private sealed class TokenBumpInterceptor : DbCommandInterceptor
    {
        private (string ThreadId, long Step)? target;

        /// <summary>Whether the bump actually fired (guards the hook against silent no-ops).</summary>
        public bool Fired { get; private set; }

        public void Arm(string threadId, long step)
        {
            this.target = (threadId, step);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            this.BumpOnce(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            this.BumpOnce(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            this.BumpOnce(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            this.BumpOnce(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void BumpOnce(DbCommand command)
        {
            if (this.target is not { } target
                || !command.CommandText.Contains("UPDATE \"voluta_checkpoints\"", StringComparison.Ordinal))
            {
                return;
            }

            this.target = null;
            this.Fired = true;
            using var bump = command.Connection!.CreateCommand();
            bump.CommandText =
                "UPDATE voluta_checkpoints SET ConcurrencyToken = ConcurrencyToken + 1 " +
                $"WHERE ThreadId = '{target.ThreadId}' AND Step = {target.Step}";
            _ = bump.ExecuteNonQuery();
        }
    }

    /// <summary>Host unit of work exposing its own checkpoint context (SQLite shared connection).</summary>
    private sealed class HostUnitOfWork : ICheckpointUnitOfWork
    {
        public HostUnitOfWork(VolutaCheckpointDbContext context)
        {
            Context = context;
        }

        public VolutaCheckpointDbContext? Context { get; }

        public bool TryGetResource<T>(out T resource)
            where T : notnull
        {
            if (typeof(T) == typeof(VolutaCheckpointDbContext) && Context is not null)
            {
                resource = (T)(object)Context;
                return true;
            }

            resource = default!;
            return false;
        }

        public Task ExecuteAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken)
        {
            return write(cancellationToken);
        }
    }

    private sealed class UnrelatedUnitOfWork : ICheckpointUnitOfWork
    {
        public bool TryGetResource<T>(out T resource)
            where T : notnull
        {
            resource = default!;
            return false;
        }

        public Task ExecuteAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken)
        {
            return write(cancellationToken);
        }
    }
}
