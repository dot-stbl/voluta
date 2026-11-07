using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Voluta;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Checkpoint;
using Voluta.DependencyInjection;
using Voluta.DependencyInjection.Checkpoints;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Hosting;
using Voluta.Hosting.Wake;
using Voluta.Hosting.Worker;
using Xunit;

namespace Voluta.Hosting.Unit;

public sealed class GraphWorkerServiceShould
{
    [Fact(DisplayName = "Given start then resume wakes, when worker runs, then park then complete")]
    public async Task ParkThenCompleteOnHitl()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVoluta(voluta =>
        {
            voluta.Checkpoints.UseInMemory();
            voluta.Graph((_, checkpointer) => BuildGateGraph().Compile(checkpointer));
        });
        services.AddVolutaWorkerHosting();

        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<InMemoryThreadWakeBus>();
        var worker = provider.GetRequiredService<GraphWorkerService>();
        using var hostLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await worker.StartAsync(hostLifetime.Token);

        const string threadId = "worker-unit-1";
        await bus.EnqueueAsync(
            ThreadWake.Start(threadId, new ChannelWrite("messages", "go")),
            hostLifetime.Token);

        await WaitForAsync(
            () => worker.Outcomes.Any(outcome =>
                outcome.ThreadId == threadId && outcome.Disposition == GraphThreadDisposition.Parked),
            hostLifetime.Token);

        await bus.EnqueueAsync(
            ThreadWake.Resume(threadId, Command.Approve("yes")),
            hostLifetime.Token);

        await WaitForAsync(
            () => worker.Outcomes.Any(outcome =>
                outcome.ThreadId == threadId && outcome.Disposition == GraphThreadDisposition.Completed),
            hostLifetime.Token);

        bus.Complete();
        await worker.StopAsync(CancellationToken.None);

        var dispositions = worker.Outcomes
            .Where(outcome => outcome.ThreadId == threadId)
            .Select(outcome => outcome.Disposition)
            .ToArray();
        dispositions.ShouldContain(GraphThreadDisposition.Parked);
        dispositions.ShouldContain(GraphThreadDisposition.Completed);
        dispositions.Length.ShouldBe(2);
    }

    [Fact(DisplayName = "Given concurrent wakes for same thread, when first is in-flight, then second is skipped")]
    public async Task SkipConcurrentWakeForSameThread()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var bus = new InMemoryThreadWakeBus();
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "slow",
                async (_, cancellationToken) =>
                {
                    entered.TrySetResult();
                    await hold.Task.WaitAsync(cancellationToken);
                    return NodeResult.Continue(new ChannelWrite("messages", "done"));
                })
            .AddEdge(GraphConstants.Start, "slow")
            .AddEdge("slow", GraphConstants.End)
            .Compile(checkpointer);

        var runner = new GraphThreadRunner(
            new SingleGraphResolver(graph),
            checkpointer,
            NullLogger<GraphThreadRunner>.Instance);
        var worker = new GraphWorkerService(bus, runner, NullLogger<GraphWorkerService>.Instance);

        using var hostLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.StartAsync(hostLifetime.Token);

        const string threadId = "same-thread";
        await bus.EnqueueAsync(ThreadWake.Start(threadId, new ChannelWrite("messages", "a")), hostLifetime.Token);
        await entered.Task.WaitAsync(hostLifetime.Token);

        // First turn still in-flight — duplicate must be skipped.
        await bus.EnqueueAsync(ThreadWake.Start(threadId, new ChannelWrite("messages", "b")), hostLifetime.Token);
        await Task.Delay(50, hostLifetime.Token);

        hold.TrySetResult();

        await WaitForAsync(
            () => worker.Outcomes.Any(outcome =>
                outcome.ThreadId == threadId && outcome.Disposition == GraphThreadDisposition.Completed),
            hostLifetime.Token);

        bus.Complete();
        await worker.StopAsync(CancellationToken.None);

        worker.Outcomes.Count(outcome => outcome.ThreadId == threadId).ShouldBe(1);
    }

    private static StateGraph BuildGateGraph()
    {
        return new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode("gate", GateAsync)
            .AddNode("finish", FinishAsync)
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", "finish")
            .AddEdge("finish", GraphConstants.End);
    }

    private static Task<NodeResult> GateAsync(GraphContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return context.ResumePayload is null
            ? Task.FromResult<NodeResult>(NodeResult.Interrupt(new { need = "approve" }))
            : Task.FromResult<NodeResult>(NodeResult.Continue(new ChannelWrite("messages", "resumed")));
    }

    private static Task<NodeResult> FinishAsync(GraphContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<NodeResult>(NodeResult.Continue(new ChannelWrite("messages", "done")));
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException("Condition not met within deadline.");
    }
}
