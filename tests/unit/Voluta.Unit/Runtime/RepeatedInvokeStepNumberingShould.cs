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

/// <summary>
///     Repeated invokes on one thread (0.5.1 regression): the second and further runs used to
///     start blind at step 0 and collide with the thread's guard — <c>checkpoint.conflict</c>.
///     Now the run entry loads the thread's latest snapshot, validates the graph identity,
///     and continues the thread's step numbering (guarded marker put).
/// </summary>
public sealed class RepeatedInvokeStepNumberingShould
{
    [Fact(DisplayName = "Given a thread with history, when the graph is invoked three times, then no conflict and every run continues the numbering")]
    public async Task RepeatedInvokesContinueStepNumbering()
    {
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildSuperstepGraph(checkpointer);
        var threadId = "repeat-numbering-1";

        long? previousLastStep = null;
        var historySizes = new List<int>();

        for (var run = 0; run < 3; run++)
        {
            var historyBefore = await checkpointer.ListAsync(threadId);
            var putsBefore = checkpointer.PutRecords.Count;

            var terminal = await graph.InvokeAsync([], new RunOptions { ThreadId = threadId });

            terminal.Kind.ShouldBe(StreamEventKind.End);

            var historyAfter = await checkpointer.ListAsync(threadId);
            historyAfter.Count.ShouldBeGreaterThan(historyBefore.Count);
            historySizes.Add(historyAfter.Count);

            var runSteps = historyAfter
                .Skip(historyBefore.Count)
                .Select(static snapshot => snapshot.Step)
                .ToArray();
            runSteps.ShouldNotBeEmpty();

            // Steps inside one run never go backwards.
            for (var index = 1; index < runSteps.Length; index++)
            {
                runSteps[index].ShouldBeGreaterThanOrEqualTo(runSteps[index - 1]);
            }

            var runPuts = checkpointer.PutRecords.Skip(putsBefore).ToArray();
            runPuts.ShouldNotBeEmpty();
            runPuts[0].Snapshot.Status.ShouldBe(GraphRunStatus.Running);

            if (previousLastStep is { } previous)
            {
                // Every step of the later run is strictly above the previous run's last step.
                runSteps.ShouldAllBe(step => step > previous);
                runSteps[0].ShouldBe(previous + 1);

                // The marker put of a thread with history is guarded against the loaded step.
                runPuts[0].ExpectedParentStep.ShouldBe(previous);
            }
            else
            {
                // A fresh thread starts blind at step 0 (0.4.0 behavior).
                runSteps[0].ShouldBe(0);
                runPuts[0].ExpectedParentStep.ShouldBeNull();
            }

            // The terminal Done reuses the last commit's step (same-step rewrite).
            runPuts[^1].Snapshot.Status.ShouldBe(GraphRunStatus.Done);
            runPuts[^1].Snapshot.Step.ShouldBe(runPuts[^2].Snapshot.Step);

            previousLastStep = runSteps[^1];
        }

        // The thread's history grows with every run.
        historySizes[1].ShouldBeGreaterThan(historySizes[0]);
        historySizes[2].ShouldBeGreaterThan(historySizes[1]);
    }

    [Fact(DisplayName = "Given a thread written by a foreign graph, when invoked, then fails graph.version_mismatch and leaves the snapshot untouched")]
    public async Task ForeignIdentityInvokeFails()
    {
        var checkpointer = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildSuperstepGraph(
            checkpointer,
            new CompileOptions { GraphId = "comuki-graph", GraphVersion = "v2" });

        await checkpointer.PutAsync(
            new CheckpointSnapshot
            {
                ThreadId = "foreign-repeat-1",
                Step = 4,
                Status = GraphRunStatus.Running,
                LastNode = "a",
                NextNodes = ["a"],
                GraphId = "other-graph",
                GraphVersion = "v1",
            });

        var exception = await Should.ThrowAsync<GraphVersionMismatchException>(async () =>
        {
            await graph.InvokeAsync([], new RunOptions { ThreadId = "foreign-repeat-1" });
        });

        exception.Code.ShouldBe(VolutaErrorCodes.GraphVersionMismatch);

        // Only the seeded snapshot exists — the run wrote nothing.
        checkpointer.PutRecords.Count.ShouldBe(1);
        var history = await checkpointer.ListAsync("foreign-repeat-1");
        history.Count.ShouldBe(1);
        history[0].Step.ShouldBe(4);
        history[0].Status.ShouldBe(GraphRunStatus.Running);
        history[0].GraphId.ShouldBe("other-graph");
        history[0].GraphVersion.ShouldBe("v1");
    }

    [Fact(DisplayName = "Given a thread with history, when two invokes race, then exactly one wins and the other fails checkpoint.conflict")]
    public async Task RacingInvokesHaveExactlyOneWinner()
    {
        var checkpointer = new LoadBarrierCheckpointer(new InMemoryCheckpointer(), "repeat-race-1");
        var graph = BuildSuperstepGraph(checkpointer);
        var threadId = "repeat-race-1";

        // Seed the thread with one completed run so the race is decided by the guarded
        // marker put (a blind start on an empty thread would let both racers in).
        var seeded = await graph.InvokeAsync([], new RunOptions { ThreadId = threadId });
        seeded.Kind.ShouldBe(StreamEventKind.End);

        // From now on both racers must observe the same latest snapshot.
        checkpointer.Arm();

        var outcomes = await Task.WhenAll(
            InvokeCatchingConflictAsync(graph, threadId),
            InvokeCatchingConflictAsync(graph, threadId));

        outcomes.Count(static outcome => outcome.Succeeded).ShouldBe(1);
        outcomes.Count(static outcome => outcome.Conflict is not null).ShouldBe(1);
        var conflict = outcomes.Single(static outcome => outcome.Conflict is not null).Conflict;
        conflict!.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);

        // The loser persisted nothing: the winner's history is intact and ends with Done.
        var history = await checkpointer.ListAsync(threadId);
        history.ShouldNotBeEmpty();
        history[^1].Status.ShouldBe(GraphRunStatus.Done);
        history.ShouldAllBe(static snapshot => snapshot.Status != GraphRunStatus.Failed);
    }

    /// <summary>Runs one invoke, capturing the store conflict instead of throwing it.</summary>
    private static async Task<InvokeOutcome> InvokeCatchingConflictAsync(CompiledGraph graph, string threadId)
    {
        try
        {
            await graph.InvokeAsync([], new RunOptions { ThreadId = threadId });
            return new InvokeOutcome(true, null);
        }
        catch (CheckpointStoreException exception)
        {
            return new InvokeOutcome(false, exception);
        }
    }

    /// <summary>Two-superstep graph (a → b → END) so internal puts beyond the first occur.</summary>
    private static CompiledGraph BuildSuperstepGraph(ICheckpointer checkpointer, CompileOptions? options = null)
    {
        var builder = new StateGraph()
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
            .AddEdge("b", GraphConstants.End);
        return options is null
            ? builder.Compile(checkpointer)
            : builder.Compile(checkpointer, options);
    }

    /// <summary>Outcome of one racing invoke (success or the store conflict it lost with).</summary>
    private sealed record InvokeOutcome(bool Succeeded, CheckpointStoreException? Conflict);

    /// <summary>
    ///     Checkpointer decorator that makes a race deterministic: once armed, the loads of the
    ///     watched thread block until two of them arrived, so both racers hold the same latest
    ///     snapshot and the guarded marker put decides the winner.
    /// </summary>
    private sealed class LoadBarrierCheckpointer(ICheckpointer inner, string threadId) : ICheckpointer
    {
        private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

        private readonly TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int loads;
        private volatile bool armed;

        /// <summary>Starts blocking the loads of the watched thread.</summary>
        public void Arm()
        {
            armed = true;
        }

        public Task PutAsync(
            CheckpointSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            return inner.PutAsync(snapshot, cancellationToken);
        }

        public Task PutAsync(
            CheckpointSnapshot snapshot,
            long? expectedParentStep,
            ICheckpointUnitOfWork? unitOfWork,
            CancellationToken cancellationToken)
        {
            return inner.PutAsync(snapshot, expectedParentStep, unitOfWork, cancellationToken);
        }

        public async Task<CheckpointSnapshot?> GetAsync(
            string thread,
            CancellationToken cancellationToken = default)
        {
            // The snapshot is read before the barrier — both racers must see the same one.
            var snapshot = await inner.GetAsync(thread, cancellationToken);
            if (armed && string.Equals(thread, threadId, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref loads) >= 2)
                {
                    barrier.TrySetResult();
                }

                try
                {
                    await barrier.Task.WaitAsync(BarrierTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    // The other racer never reached the barrier — race as it is.
                }
            }

            return snapshot;
        }

        public Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
            string thread,
            CancellationToken cancellationToken = default)
        {
            return inner.ListAsync(thread, cancellationToken);
        }
    }
}
