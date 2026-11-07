using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Exceptions.Run;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;
using Voluta.Testing.Checkpoint;
using Xunit;

namespace Voluta.Unit.Runtime;

public sealed class GraphIdentityGuardedPutsAndCancelShould
{
    [Fact(DisplayName = "Given a graph compiled with identity, when a run checkpoints, then every snapshot carries the pair")]
    public async Task StampIdentityOnEverySnapshot()
    {
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildLinearGraph(
            checkpointer,
            new CompileOptions { GraphId = "comuki-graph", GraphVersion = "2026.11.0" });

        await graph.InvokeAsync([], new RunOptions { ThreadId = "identity-1" });

        checkpointer.PutRecords.ShouldNotBeEmpty();
        checkpointer.PutRecords.ShouldAllBe(static put =>
            put.Snapshot.GraphId == "comuki-graph" && put.Snapshot.GraphVersion == "2026.11.0");
        checkpointer.PutRecords.ShouldAllBe(static put => put.Snapshot.FormatVersion == 1);
    }

    [Fact(DisplayName = "Given a graph compiled without identity, when a run checkpoints, then snapshots carry null identity")]
    public async Task CompileWithoutIdentityYieldsNullFields()
    {
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildLinearGraph(checkpointer, options: null);

        await graph.InvokeAsync([], new RunOptions { ThreadId = "identity-2" });

        checkpointer.PutRecords.ShouldNotBeEmpty();
        checkpointer.PutRecords.ShouldAllBe(static put =>
            put.Snapshot.GraphId == null && put.Snapshot.GraphVersion == null);
    }

    [Fact(DisplayName = "Given runtime puts, when inspected, then each carries the previous step as expected parent")]
    public async Task GuardInternalPutsWithLastStep()
    {
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildSuperstepGraph(checkpointer);

        await graph.InvokeAsync([], new RunOptions { ThreadId = "guarded-1" });

        checkpointer.PutRecords.Count.ShouldBeGreaterThanOrEqualTo(2);
        // First put of the run: no loaded/written snapshot yet → blind (null guard).
        checkpointer.PutRecords[0].ExpectedParentStep.ShouldBeNull();
        // Every later put: guarded with the step written right before it.
        for (var index = 1; index < checkpointer.PutRecords.Count; index++)
        {
            checkpointer.PutRecords[index].ExpectedParentStep
                .ShouldBe(checkpointer.PutRecords[index - 1].Snapshot.Step);
        }
    }

    [Fact(DisplayName = "Given a competing writer, when a runtime guarded put loses, then checkpoint.conflict escapes the stream and no Failed snapshot is synthesized")]
    public async Task ConflictEscapesStreamWithoutFailedSnapshot()
    {
        var checkpointer = new CompetingWriterCheckpointer(new InMemoryCheckpointer(), "conflict-1");
        var graph = BuildSuperstepGraph(checkpointer);

        var exception = await Should.ThrowAsync<CheckpointStoreException>(async () =>
        {
            await foreach (var _ in graph.StreamAsync([], new RunOptions { ThreadId = "conflict-1" }))
            {
            }
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);
        checkpointer.PutStatuses.ShouldNotContain(static status => status == GraphRunStatus.Failed);
    }

    [Fact(DisplayName = "Given a legacy snapshot without identity, when resumed on an identity graph, then accepted without a check")]
    public async Task LegacySnapshotResumesUnchecked()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildGateGraph(
            checkpointer,
            new CompileOptions { GraphId = "comuki-graph", GraphVersion = "v2" });

        // Park a snapshot the way Voluta ≤ 0.4 wrote it (no identity fields).
        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "legacy-1",
                Step = 1,
                Status = GraphRunStatus.Interrupted,
                LastNode = "gate",
                NextNodes = ["gate"],
                InterruptPayload = new Dictionary<string, object?> { ["amount"] = 5 },
            });

        var terminal = await graph.ResumeInvokeAsync("legacy-1", Command.Approve("ok"));

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await checkpointer.GetAsync("legacy-1");
        done!.Status.ShouldBe(GraphRunStatus.Done);
    }

    [Fact(DisplayName = "Given a snapshot with a foreign identity pair, when resumed, then fails graph.version_mismatch without mutating the checkpoint")]
    public async Task ForeignIdentityResumeFails()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildGateGraph(
            checkpointer,
            new CompileOptions { GraphId = "comuki-graph", GraphVersion = "v2" });

        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "foreign-1",
                Step = 1,
                Status = GraphRunStatus.Interrupted,
                LastNode = "gate",
                NextNodes = ["gate"],
                InterruptPayload = new Dictionary<string, object?> { ["amount"] = 5 },
                GraphId = "comuki-graph",
                GraphVersion = "v1",
            });

        var exception = await Should.ThrowAsync<GraphVersionMismatchException>(async () =>
        {
            await graph.ResumeInvokeAsync("foreign-1", Command.Approve("ok"));
        });

        exception.Code.ShouldBe(VolutaErrorCodes.GraphVersionMismatch);
        var unchanged = await checkpointer.GetAsync("foreign-1");
        unchanged!.Status.ShouldBe(GraphRunStatus.Interrupted);
        unchanged.Step.ShouldBe(1);
    }

    [Fact(DisplayName = "Given a snapshot with a matching identity pair, when resumed, then the run proceeds")]
    public async Task MatchingIdentityResumeProceeds()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildGateGraph(
            checkpointer,
            new CompileOptions { GraphId = "comuki-graph", GraphVersion = "v2" });

        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "match-1",
                Step = 1,
                Status = GraphRunStatus.Interrupted,
                LastNode = "gate",
                NextNodes = ["gate"],
                InterruptPayload = new Dictionary<string, object?> { ["amount"] = 5 },
                GraphId = "comuki-graph",
                GraphVersion = "v2",
            });

        var terminal = await graph.ResumeInvokeAsync("match-1", Command.Approve("ok"));

        terminal.Kind.ShouldBe(StreamEventKind.End);
    }

    [Fact(DisplayName = "Given a Running snapshot with a foreign identity pair, when ContinueAsync is called, then fails graph.version_mismatch")]
    public async Task ForeignIdentityContinueFails()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildLinearGraph(
            checkpointer,
            new CompileOptions { GraphId = "comuki-graph", GraphVersion = "v9" });

        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "foreign-continue",
                Step = 1,
                Status = GraphRunStatus.Running,
                LastNode = "a",
                NextNodes = ["a"],
                GraphId = "other-graph",
                GraphVersion = "v9",
            });

        var exception = await Should.ThrowAsync<GraphVersionMismatchException>(async () =>
        {
            await foreach (var _ in graph.ContinueAsync("foreign-continue"))
            {
            }
        });

        exception.Code.ShouldBe(VolutaErrorCodes.GraphVersionMismatch);
    }

    [Fact(DisplayName = "Given a run with a unit of work, when checkpoints are written, then the unit of work arrives at every put")]
    public async Task DeliverUnitOfWorkToPuts()
    {
        var unitOfWork = new NoopUnitOfWork();
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildSuperstepGraph(checkpointer);

        await graph.InvokeAsync([], new RunOptions { ThreadId = "uow-1", UnitOfWork = unitOfWork });

        checkpointer.PutRecords.ShouldNotBeEmpty();
        checkpointer.PutRecords.ShouldAllBe(static put => put.UnitOfWork != null);
    }

    [Fact(DisplayName = "Given a resume with a unit of work, when checkpoints are written, then the unit of work arrives at resume puts")]
    public async Task DeliverUnitOfWorkOnResumeOverload()
    {
        var unitOfWork = new NoopUnitOfWork();
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildGateGraph(checkpointer);

        var interrupted = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "uow-resume" });
        interrupted.Kind.ShouldBe(StreamEventKind.Interrupt);
        var putsBeforeResume = checkpointer.PutRecords.Count;

        await using var enumerator = graph
            .ResumeAsync(
                "uow-resume",
                Command.Approve("ok"),
                new RunOptions
                {
                    ThreadId = "uow-resume",
                    StreamMode = StreamMode.Updates,
                    UnitOfWork = unitOfWork,
                },
                CancellationToken.None)
            .GetAsyncEnumerator();
        while (await enumerator.MoveNextAsync())
        {
        }

        var resumePuts = checkpointer.PutRecords.Skip(putsBeforeResume).ToArray();
        resumePuts.ShouldNotBeEmpty();
        resumePuts.ShouldAllBe(static put => put.UnitOfWork != null);
        // Pre-resume (invoke) puts had no unit of work.
        checkpointer.PutRecords.Take(putsBeforeResume)
            .ShouldAllBe(static put => put.UnitOfWork == null);
    }

    [Fact(DisplayName = "Given a run without a unit of work, when checkpoints are written, then puts stay standalone")]
    public async Task AbsentUnitOfWorkKeepsStandalonePuts()
    {
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildSuperstepGraph(checkpointer);

        await graph.InvokeAsync([], new RunOptions { ThreadId = "uow-2" });

        checkpointer.PutRecords.ShouldNotBeEmpty();
        checkpointer.PutRecords.ShouldAllBe(static put => put.UnitOfWork == null);
    }

    [Fact(DisplayName = "Given a missing thread, when CancelAsync is called, then fails graph.thread_not_found")]
    public async Task CancelMissingThreadFails()
    {
        var graph = BuildLinearGraph(new InMemoryCheckpointer());

        var exception = await Should.ThrowAsync<GraphThreadNotFoundException>(
            () => graph.CancelAsync("never-checkpointed"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphThreadNotFound);
    }

    [Fact(DisplayName = "Given a Done thread, when CancelAsync is called, then fails graph.invalid_cancel and leaves the snapshot unchanged")]
    public async Task CancelDoneThreadFails()
    {
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildLinearGraph(checkpointer);
        await graph.InvokeAsync([], new RunOptions { ThreadId = "done-cancel" });
        var putsBefore = checkpointer.PutRecords.Count;

        var exception = await Should.ThrowAsync<GraphInvalidCancelException>(
            () => graph.CancelAsync("done-cancel", "late"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidCancel);
        checkpointer.PutRecords.Count.ShouldBe(putsBefore);
        var snapshot = await checkpointer.GetAsync("done-cancel");
        snapshot!.Status.ShouldBe(GraphRunStatus.Done);
    }

    [Fact(DisplayName = "Given an Interrupted thread, when CancelAsync is called with a reason, then writes Cancelled snapshot guarded at the latest step with the reason recorded")]
    public async Task CancelInterruptedThreadWritesCancelledSnapshot()
    {
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildGateGraph(checkpointer);
        var interrupted = await graph.InvokeAsync([], new RunOptions { ThreadId = "parked-1" });
        interrupted.Kind.ShouldBe(StreamEventKind.Interrupt);
        var parked = await checkpointer.GetAsync("parked-1");
        var putsBefore = checkpointer.PutRecords.Count;

        var cancelled = await graph.CancelAsync("parked-1", "operator requested");

        cancelled.Status.ShouldBe(GraphRunStatus.Cancelled);
        cancelled.CancelReason.ShouldBe("operator requested");
        cancelled.Step.ShouldBe(parked!.Step);
        var put = checkpointer.PutRecords[putsBefore];
        put.ExpectedParentStep.ShouldBe(parked.Step);
        put.UnitOfWork.ShouldBeNull();
        var stored = await checkpointer.GetAsync("parked-1");
        stored!.Status.ShouldBe(GraphRunStatus.Cancelled);
        stored.CancelReason.ShouldBe("operator requested");
    }

    [Fact(DisplayName = "Given a Running snapshot parked by a dead process, when CancelAsync is called, then writes Cancelled with the reason")]
    public async Task CancelRunningParkedThread()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildLinearGraph(checkpointer);
        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "dead-process",
                Step = 3,
                Status = GraphRunStatus.Running,
                LastNode = "a",
                NextNodes = ["a"],
            });

        var cancelled = await graph.CancelAsync("dead-process", "crash recovery");

        cancelled.Status.ShouldBe(GraphRunStatus.Cancelled);
        cancelled.CancelReason.ShouldBe("crash recovery");
        var stored = await checkpointer.GetAsync("dead-process");
        stored!.Status.ShouldBe(GraphRunStatus.Cancelled);
        stored.CancelReason.ShouldBe("crash recovery");
        stored.Step.ShouldBe(3);
    }

    [Fact(DisplayName = "Given an already Cancelled thread, when CancelAsync is called again, then succeeds idempotently without a duplicate write")]
    public async Task CancelIsIdempotent()
    {
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildLinearGraph(checkpointer);
        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "already-cancelled",
                Step = 2,
                Status = GraphRunStatus.Running,
                LastNode = "a",
                NextNodes = ["a"],
            });
        await graph.CancelAsync("already-cancelled", "first");
        var putsAfterFirstCancel = checkpointer.PutRecords.Count;

        var second = await graph.CancelAsync("already-cancelled", "second");

        second.Status.ShouldBe(GraphRunStatus.Cancelled);
        second.CancelReason.ShouldBe("first"); // loaded snapshot returned; no rewrite
        checkpointer.PutRecords.Count.ShouldBe(putsAfterFirstCancel);
        var stored = await checkpointer.GetAsync("already-cancelled");
        stored!.CancelReason.ShouldBe("first");
    }

    [Fact(DisplayName = "Given a cancelled thread snapshot, when CancelAsync is called, then identity fields are preserved")]
    public async Task CancelPreservesIdentityFields()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildLinearGraph(
            checkpointer,
            new CompileOptions { GraphId = "comuki-graph", GraphVersion = "v3" });
        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "identity-cancel",
                Step = 2,
                Status = GraphRunStatus.Running,
                LastNode = "a",
                NextNodes = ["a"],
                GraphId = "comuki-graph",
                GraphVersion = "v3",
            });

        var cancelled = await graph.CancelAsync("identity-cancel", "reason");

        cancelled.GraphId.ShouldBe("comuki-graph");
        cancelled.GraphVersion.ShouldBe("v3");
    }

    private static CompiledGraph BuildLinearGraph(ICheckpointer checkpointer, CompileOptions? options = null)
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

    private static CompiledGraph BuildGateGraph(ICheckpointer checkpointer, CompileOptions? options = null)
    {
        var builder = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "gate",
                static (context, _) => context.ResumePayload is null
                    ? Task.FromResult<NodeResult>(NodeResult.Interrupt("need-approve"))
                    : Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "gate-done"))))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End);
        return options is null
            ? builder.Compile(checkpointer)
            : builder.Compile(checkpointer, options);
    }

    /// <summary>Two-superstep graph (a → b → END) so internal puts beyond the first occur.</summary>
    private static CompiledGraph BuildSuperstepGraph(ICheckpointer checkpointer)
    {
        return new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "from-a"))))
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "from-b"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddEdge("b", GraphConstants.End)
            .Compile(checkpointer);
    }

    /// <summary>
    ///     Simulates a competing writer: before the second engine put lands, a blind write of the
    ///     same snapshot wins the race; the engine's guarded put must then lose with conflict.
    /// </summary>
    private sealed class CompetingWriterCheckpointer : ICheckpointer
    {
        private readonly ICheckpointer inner;
        private readonly string threadId;
        private long puts;

        public CompetingWriterCheckpointer(ICheckpointer inner, string threadId)
        {
            this.inner = inner;
            this.threadId = threadId;
        }

        /// <summary>Status of every snapshot the engine attempted to put, in call order.</summary>
        public List<GraphRunStatus> PutStatuses { get; } = [];

        public Task PutAsync(
            CheckpointSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            return PutAsync(snapshot, null, null, cancellationToken);
        }

        public async Task PutAsync(
            CheckpointSnapshot snapshot,
            long? expectedParentStep,
            ICheckpointUnitOfWork? unitOfWork,
            CancellationToken cancellationToken)
        {
            PutStatuses.Add(snapshot.Status);
            puts++;
            if (puts == 2)
            {
                // Competing writer wins first (blind, same step).
                await inner.PutAsync(snapshot, cancellationToken: cancellationToken);
            }

            await inner.PutAsync(snapshot, expectedParentStep, unitOfWork, cancellationToken);
        }

        public Task<CheckpointSnapshot?> GetAsync(string thread, CancellationToken cancellationToken = default)
        {
            return inner.GetAsync(thread, cancellationToken);
        }

        public Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
            string thread,
            CancellationToken cancellationToken = default)
        {
            return inner.ListAsync(thread, cancellationToken);
        }
    }
}
