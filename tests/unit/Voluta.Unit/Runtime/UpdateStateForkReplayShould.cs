using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Exceptions.Run;
using Voluta.Graph.Builder;
using Voluta.Testing.Checkpoint;
using Xunit;

namespace Voluta.Unit.Runtime;

public sealed class UpdateStateForkReplayShould
{
    [Fact(DisplayName = "Given missing thread, when UpdateStateAsync, then GraphThreadNotFoundException")]
    public async Task UpdateStateMissingThreadThrows()
    {
        var graph = BuildLinear(new InMemoryCheckpointer());

        var exception = await Should.ThrowAsync<GraphThreadNotFoundException>(
            async () => await graph.UpdateStateAsync(
                "no-thread",
                [new ChannelWrite("messages", "x")]));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphThreadNotFound);
    }

    [Fact(DisplayName = "Given interrupt, when UpdateStateAsync then Resume, then channel patch is visible")]
    public async Task UpdateStateThenResumeInterrupted()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "gate",
                static (context, _) => context.ResumePayload is null
                    ? Task.FromResult<NodeResult>(NodeResult.Interrupt("need-approve"))
                    : Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "after-resume"))))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);

        await graph.InvokeAsync(
            [new ChannelWrite("messages", "seed")],
            new RunOptions { ThreadId = "upd-int", StreamMode = StreamMode.Events });

        var updated = await graph.UpdateStateAsync(
            "upd-int",
            [new ChannelWrite("messages", "host-patch")]);

        updated.Status.ShouldBe(GraphRunStatus.Interrupted);
        var values = updated.Values["messages"].ShouldBeOfType<List<object?>>();
        values.ShouldContain("seed");
        values.ShouldContain("host-patch");

        var terminal = await graph.ResumeInvokeAsync("upd-int", Command.Approve("ok"));

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await graph.GetStateAsync("upd-int");
        done.ShouldNotBeNull();
        done.Status.ShouldBe(GraphRunStatus.Done);
        var final = done.Values["messages"].ShouldBeOfType<List<object?>>();
        final.ShouldContain("host-patch");
        final.ShouldContain("after-resume");
    }

    [Fact(DisplayName = "Given mid-run fork at step, when Continue on new thread, then independent of source")]
    public async Task ForkThenIndependentContinue()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildLinear(checkpointer);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "src-fork", StreamMode = StreamMode.Values });

        var history = await graph.GetHistoryAsync("src-fork");
        history.Count.ShouldBeGreaterThanOrEqualTo(2);

        // Fork a mid-history Running step when present; else first non-terminal-looking step.
        var forkStep = history.FirstOrDefault(static item => item.Status == GraphRunStatus.Running)?.Step
                       ?? history[0].Step;

        var forked = await graph.ForkAsync("src-fork", forkStep, "fork-child");

        forked.ThreadId.ShouldBe("fork-child");
        forked.Step.ShouldBe(forkStep);

        var sourceLatest = await graph.GetStateAsync("src-fork");
        sourceLatest.ShouldNotBeNull();
        sourceLatest.Status.ShouldBe(GraphRunStatus.Done);

        var childState = await graph.GetStateAsync("fork-child");
        childState.ShouldNotBeNull();
        childState.ThreadId.ShouldBe("fork-child");
        childState.Step.ShouldBe(forkStep);

        // Patch child only — source history must not gain the write.
        await graph.UpdateStateAsync(
            "fork-child",
            [new ChannelWrite("messages", "only-child")]);

        var sourceHistory = await graph.GetHistoryAsync("src-fork");
        foreach (var step in sourceHistory)
        {
            if (step.Values.TryGetValue("messages", out var raw) && raw is List<object?> list)
            {
                list.ShouldNotContain("only-child");
            }
        }

        var childAfter = await graph.GetStateAsync("fork-child");
        childAfter.ShouldNotBeNull();
        var childMessages = childAfter.Values["messages"].ShouldBeOfType<List<object?>>();
        childMessages.ShouldContain("only-child");
    }

    [Fact(DisplayName = "Given missing step, when ForkAsync, then GraphStepNotFoundException")]
    public async Task ForkMissingStepThrows()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildLinear(checkpointer);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "fork-miss", StreamMode = StreamMode.Values });

        var exception = await Should.ThrowAsync<GraphStepNotFoundException>(
            async () => await graph.ForkAsync("fork-miss", 999_999, "fork-x"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphStepNotFound);
    }

    [Fact(DisplayName = "Given a fork onto a fresh thread, when put, then the put stays blind (no expected parent)")]
    public async Task ForkFreshTargetStaysBlind()
    {
        var recording = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildLinear(recording);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "fork-src-b", StreamMode = StreamMode.Values });
        var sourceStep = (await graph.GetHistoryAsync("fork-src-b"))[0].Step;

        await graph.ForkAsync("fork-src-b", sourceStep, "fork-child-b");

        var forkPuts = recording.PutRecords
            .Where(static put => put.Snapshot.ThreadId == "fork-child-b")
            .ToList();
        forkPuts.ShouldHaveSingleItem();
        // Fresh target has no parent — the first write stays the blind write (back-compat).
        forkPuts[0].ExpectedParentStep.ShouldBeNull();
    }

    [Fact(DisplayName = "Given a fork onto an existing thread, when put, then guarded with the target's latest step")]
    public async Task ForkExistingTargetIsGuarded()
    {
        var recording = new RecordingCheckpointer(new InMemoryCheckpointer());
        var graph = BuildLinear(recording);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "fork-src-g", StreamMode = StreamMode.Values });
        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "fork-child-g", StreamMode = StreamMode.Values });
        var targetLatest = (await graph.GetStateAsync("fork-child-g"))!.Step;
        var sourceStep = (await graph.GetHistoryAsync("fork-src-g"))[0].Step;

        await graph.ForkAsync("fork-src-g", sourceStep, "fork-child-g");

        var forkPuts = recording.PutRecords
            .Where(static put => put.Snapshot.ThreadId == "fork-child-g")
            .ToList();
        forkPuts.Count.ShouldBeGreaterThanOrEqualTo(2);
        // The fork lands on a thread with history: the target's latest step is the parent guard.
        forkPuts.Last().ExpectedParentStep.ShouldBe(targetLatest);
    }

    [Fact(DisplayName = "Given a fork whose target advances mid-fork (competing writer), when put, then checkpoint.conflict instead of a silent overwrite")]
    public async Task ForkConflictsWhenTargetMoved()
    {
        var checkpointer = new CompetingForkTargetCheckpointer(new InMemoryCheckpointer(), "fork-race-target");
        var graph = BuildLinear(checkpointer);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "fork-race-src", StreamMode = StreamMode.Values });
        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "fork-race-target", StreamMode = StreamMode.Values });

        var sourceStep = (await graph.GetHistoryAsync("fork-race-src"))[0].Step;
        var exception = await Should.ThrowAsync<CheckpointStoreException>(
            async () => await graph.ForkAsync("fork-race-src", sourceStep, "fork-race-target"));

        exception.Code.ShouldBe(VolutaErrorCodes.CheckpointConflict);
        // The forked snapshot must not overwrite the competitor's write.
        var target = await graph.GetStateAsync("fork-race-target");
        target!.Step.ShouldBeGreaterThan(sourceStep);
        target.Status.ShouldBe(GraphRunStatus.Running);
    }

    [Fact(DisplayName = "Given Done thread, when ContinueAsync, then GraphInvalidContinueException")]
    public async Task ContinueDoneThrows()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildLinear(checkpointer);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "cont-done", StreamMode = StreamMode.Values });

        var exception = await Should.ThrowAsync<GraphInvalidContinueException>(
            async () => await graph.ContinueInvokeAsync("cont-done"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidContinue);
    }

    [Fact(DisplayName = "Given interrupt mid-graph, when UpdateState + Continue path docs: use Resume not Continue")]
    public async Task ContinueInterruptedThrows()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "gate",
                static (context, _) => context.ResumePayload is null
                    ? Task.FromResult<NodeResult>(NodeResult.Interrupt("stop"))
                    : Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "cont-int", StreamMode = StreamMode.Events });

        var exception = await Should.ThrowAsync<GraphInvalidContinueException>(
            async () => await graph.ContinueInvokeAsync("cont-int"));

        exception.Code.ShouldBe(VolutaErrorCodes.GraphInvalidContinue);
    }

    [Fact(DisplayName = "Given linear graph paused via fork at Running step, when ContinueInvoke, then reaches Done")]
    public async Task ForkRunningThenContinueCompletes()
    {
        var checkpointer = new InMemoryCheckpointer();
        // Three nodes so history has a Running snapshot with next nodes mid-way.
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "a"))))
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "b"))))
            .AddNode(
                "c",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "c"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddEdge("b", "c")
            .AddEdge("c", GraphConstants.End)
            .Compile(checkpointer);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "full-run", StreamMode = StreamMode.Values });

        var history = await graph.GetHistoryAsync("full-run");
        var running = history.LastOrDefault(static item =>
            item.Status == GraphRunStatus.Running && item.NextNodes.Count > 0);
        running.ShouldNotBeNull("expected a Running checkpoint with next nodes in history");

        await graph.ForkAsync("full-run", running.Step, "cont-child");

        // Optional host patch before continue (Append reducer).
        await graph.UpdateStateAsync(
            "cont-child",
            [new ChannelWrite("messages", "patched")]);

        var afterUpdate = await graph.GetStateAsync("cont-child");
        afterUpdate.ShouldNotBeNull();
        afterUpdate.Status.ShouldBe(GraphRunStatus.Running);

        var terminal = await graph.ContinueInvokeAsync("cont-child");

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var final = await graph.GetStateAsync("cont-child");
        final.ShouldNotBeNull();
        final.Status.ShouldBe(GraphRunStatus.Done);
        var messages = final.Values["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("patched");
    }

    [Fact(DisplayName = "Given Interrupted source, when ForkAsync, then fork carries PendingInterrupts and resumes")]
    public async Task ForkInterruptedCarriesPendingInterrupts()
    {
        var checkpointer = new InMemoryCheckpointer();
        // Fan-out: both branches interrupt in the same superstep → multi-interrupt snapshot.
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "left",
                static (context, _) => context.IsResume
                    ? Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "left-resumed")))
                    : Task.FromResult<NodeResult>(NodeResult.Interrupt("need-left")))
            .AddNode(
                "right",
                static (context, _) => context.IsResume
                    ? Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "right-resumed")))
                    : Task.FromResult<NodeResult>(NodeResult.Interrupt("need-right")))
            .AddEdge(GraphConstants.Start, "left")
            .AddEdge(GraphConstants.Start, "right")
            .AddEdge("left", GraphConstants.End)
            .AddEdge("right", GraphConstants.End)
            .Compile(checkpointer);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "fork-int-src", StreamMode = StreamMode.Events });

        var source = await checkpointer.GetAsync("fork-int-src");
        source.ShouldNotBeNull();
        source.Status.ShouldBe(GraphRunStatus.Interrupted);
        source.PendingInterrupts.Count.ShouldBe(2);

        var forkStep = (await graph.GetHistoryAsync("fork-int-src"))[^1].Step;
        await graph.ForkAsync("fork-int-src", forkStep, "fork-int-child");

        // The fork must carry the full interrupt state, not just the compat payload.
        var forked = await checkpointer.GetAsync("fork-int-child");
        forked.ShouldNotBeNull();
        forked.Status.ShouldBe(GraphRunStatus.Interrupted);
        forked.InterruptPayload.ShouldBe(source.InterruptPayload);
        forked.PendingInterrupts.Count.ShouldBe(source.PendingInterrupts.Count);
        forked.PendingInterrupts.Select(static item => item.TaskId)
            .ShouldBe(source.PendingInterrupts.Select(static item => item.TaskId));
        forked.PendingInterrupts.Select(static item => item.Payload)
            .ShouldBe(source.PendingInterrupts.Select(static item => item.Payload));

        // Resume on the fork resolves every pending interrupt — no invalid resume/command.
        var terminal = await graph.ResumeInvokeAsync(
            "fork-int-child",
            Command.ApproveResumes(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["left"] = "ok-left",
                    ["right"] = "ok-right",
                }));

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await graph.GetStateAsync("fork-int-child");
        done.ShouldNotBeNull();
        done.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.Values["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("left-resumed");
        messages.ShouldContain("right-resumed");
    }

    private static Voluta.Graph.CompiledGraph BuildLinear(ICheckpointer checkpointer)
    {
        return new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "a"))))
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "b"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "b")
            .AddEdge("b", GraphConstants.End)
            .Compile(checkpointer);
    }

    /// <summary>
    ///     Simulates a writer racing the fork: when the fork loads the target thread (its guard
    ///     baseline), the competitor blindly advances that thread before the fork's guarded put
    ///     lands — the fork must lose with <c>checkpoint.conflict</c> instead of overwriting.
    /// </summary>
    private sealed class CompetingForkTargetCheckpointer(
        InMemoryCheckpointer inner,
        string targetThreadId) : ICheckpointer
    {
        private bool competed;

        public async Task PutAsync(
            CheckpointSnapshot snapshot,
            long? expectedParentStep,
            ICheckpointUnitOfWork? unitOfWork,
            CancellationToken cancellationToken)
        {
            await inner.PutAsync(snapshot, expectedParentStep, unitOfWork, cancellationToken);
        }

        public Task PutAsync(
            CheckpointSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            return PutAsync(snapshot, null, null, cancellationToken);
        }

        public async Task<CheckpointSnapshot?> GetAsync(
            string threadId,
            CancellationToken cancellationToken = default)
        {
            var snapshot = await inner.GetAsync(threadId, cancellationToken);
            if (threadId == targetThreadId && snapshot is not null && !competed)
            {
                competed = true;
                // Competing writer advances the target (blind, next step).
                await inner.PutAsync(
                    new CheckpointSnapshot
                    {
                        FormatVersion = snapshot.FormatVersion,
                        ThreadId = threadId,
                        Step = snapshot.Step + 1,
                        Status = GraphRunStatus.Running,
                        ChannelValues = new Dictionary<string, object?>(StringComparer.Ordinal),
                        ChannelVersions = new Dictionary<string, long>(StringComparer.Ordinal),
                        VersionsSeen = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal),
                    },
                    cancellationToken);
            }

            return snapshot;
        }

        public Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
            string threadId,
            CancellationToken cancellationToken = default)
        {
            return inner.ListAsync(threadId, cancellationToken);
        }
    }
}
