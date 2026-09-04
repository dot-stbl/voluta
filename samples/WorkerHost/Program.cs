// WorkerHost — durable BackgroundService runner pattern for Voluta.
//
// Run:
//   dotnet run --project samples/WorkerHost
//
// Pattern:
//   producer enqueues ThreadWake(threadId) → worker invokes/resumes graph
//   → interrupt parks (checkpoint SoT) → resume wake continues → done / fail policy
//
// Multi-instance: share a durable checkpointer (File / EF / S3). Wakes may fan out;
// only one instance should process a given thread at a time (lease / partition).
// Types live in Voluta.Hosting (IThreadWakeBus, GraphWorkerService, …).

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Voluta;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.DependencyInjection;
using Voluta.DependencyInjection.Checkpoints;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Hosting;
using Voluta.Hosting.Wake;
using Voluta.Hosting.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddVoluta(static voluta =>
{
    _ = voluta.Checkpoints.UseInMemory();
    _ = voluta.Graph(static (_, checkpointer) => new StateGraph()
        .AddChannel("messages", ChannelKind.Append)
        .AddChannel("status", ChannelKind.LastValue)
        .AddNode("prepare", PrepareAsync)
        .AddNode("gate", GateAsync)
        .AddNode("finish", FinishAsync)
        .AddEdge(GraphConstants.Start, "prepare")
        .AddEdge("prepare", "gate")
        .AddEdge("gate", "finish")
        .AddEdge("finish", GraphConstants.End)
        .Compile(checkpointer));
});

builder.Services.AddVolutaWorkerHosting();
builder.Services.AddHostedService<DemoProducerService>();

using var host = builder.Build();
await host.RunAsync();

static Task<NodeResult> PrepareAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult<NodeResult>(
        NodeResult.Continue(
            new ChannelWrite("messages", "prepare: started"),
            new ChannelWrite("status", "awaiting-approval")));
}

static Task<NodeResult> GateAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    return !context.IsResume
        ? Task.FromResult<NodeResult>(
            NodeResult.Interrupt(new { action = "approve-payout", amount = 120, currency = "EUR" }))
        : Task.FromResult<NodeResult>(
            NodeResult.Continue(
                new ChannelWrite("messages", $"gate: resumed · {context.ResumePayload}"),
                new ChannelWrite("status", "approved")));
}

static Task<NodeResult> FinishAsync(GraphContext context, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult<NodeResult>(
        NodeResult.Continue(
            new ChannelWrite("messages", "finish: payout queued"),
            new ChannelWrite("status", "done")));
}

/// <summary>
///     Demo driver: start → wait for park → resume → wait for complete → stop host.
///     Production producers are HTTP handlers, queues, or schedulers — not this class.
/// </summary>
file sealed class DemoProducerService(
    InMemoryThreadWakeBus wakes,
    GraphWorkerService worker,
    ICheckpointer checkpointer,
    IHostApplicationLifetime lifetime,
    ILogger<DemoProducerService> logger) : BackgroundService
{
    private const string DemoThreadId = "worker-hitl-1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the worker subscribe before the first wake.
        await Task.Yield();

        logger.LogInformation("Demo: enqueue start for {ThreadId}", DemoThreadId);
        await wakes.EnqueueAsync(
            ThreadWake.Start(
                DemoThreadId,
                new ChannelWrite("messages", "user: pay vendor ACME"),
                new ChannelWrite("status", "start")),
            stoppingToken);

        await WaitForDispositionAsync(GraphThreadDisposition.Parked, stoppingToken);

        var parked = await checkpointer.GetAsync(DemoThreadId, stoppingToken);
        logger.LogInformation(
            "Demo: checkpoint status={Status} payload={Payload}",
            parked?.Status,
            parked?.InterruptPayload);

        logger.LogInformation("Demo: enqueue resume (approve) for {ThreadId}", DemoThreadId);
        await wakes.EnqueueAsync(
            ThreadWake.Resume(DemoThreadId, Command.Approve("ok-from-worker-sample")),
            stoppingToken);

        await WaitForDispositionAsync(GraphThreadDisposition.Completed, stoppingToken);

        var done = await checkpointer.GetAsync(DemoThreadId, stoppingToken);
        logger.LogInformation("Demo: final status={Status}", done?.Status);
        if (done?.ChannelValues.TryGetValue("messages", out var messages) is true)
        {
            logger.LogInformation("Demo: messages={Messages}", messages);
        }

        wakes.Complete();
        lifetime.StopApplication();
    }

    private async Task WaitForDispositionAsync(
        GraphThreadDisposition expected,
        CancellationToken cancellationToken)
    {
        var deadline = TimeSpan.FromSeconds(15);
        var started = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow - started < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (worker.Outcomes.Any(outcome =>
                    outcome.ThreadId == DemoThreadId && outcome.Disposition == expected))
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException(
            $"Timed out waiting for {expected} on thread {DemoThreadId}.");
    }
}
