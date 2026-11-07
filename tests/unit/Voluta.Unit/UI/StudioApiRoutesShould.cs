using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.UI;
using Voluta.UI.Studio;
using Xunit;

namespace Voluta.Unit.UI;

public sealed class StudioApiRoutesShould
{
    [Fact(DisplayName = "Given seeded threads, when GET /api/v1/threads, then lists them with status")]
    public async Task ListThreadsReturnsSeededThreads()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.GetAsync("/api/v1/threads");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var threads = document.RootElement.EnumerateArray().ToList();
        threads.ShouldContain(static thread => thread.GetProperty("threadId").GetString() == "hitl-1");
        threads.ShouldContain(static thread => thread.GetProperty("threadId").GetString() == "work-1");
        threads.Single(static thread => thread.GetProperty("threadId").GetString() == "hitl-1")
            .GetProperty("status")
            .GetString()
            .ShouldBe(GraphRunStatus.Interrupted.ToString());
    }

    [Fact(DisplayName = "Given existing and missing thread, when GET /api/v1/threads/{id}, then 200 state or 404")]
    public async Task ThreadDetailReturnsStateOrNotFound()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var found = await api.Client.GetAsync("/api/v1/threads/hitl-1");
        var missing = await api.Client.GetAsync("/api/v1/threads/no-such-thread");

        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await found.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("threadId").GetString().ShouldBe("hitl-1");
        document.RootElement.GetProperty("status").GetString().ShouldBe(GraphRunStatus.Interrupted.ToString());
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "When GET /api/v1/topology, then returns graph nodes")]
    public async Task TopologyReturnsGraphNodes()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.GetAsync("/api/v1/topology");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var nodes = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(static node => node.GetString())
            .ToList();
        nodes.ShouldContain("gate");
    }

    [Fact(DisplayName = "Given drained thread, when GET /api/v1/threads/{id}/history, then steps oldest-first")]
    public async Task HistoryReturnsSteps()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.GetAsync("/api/v1/threads/work-1/history");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        document.RootElement.GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact(DisplayName = "Given one interrupted thread, when GET /api/v1/hitl, then lists only it")]
    public async Task HitlListsOnlyInterrupted()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.GetAsync("/api/v1/hitl");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var threadIds = document.RootElement.EnumerateArray()
            .Select(static thread => thread.GetProperty("threadId").GetString())
            .ToList();
        threadIds.ShouldBe(["hitl-1"]);
    }

    [Fact(DisplayName = "Given interrupted thread, when POST resume approve, then terminal End")]
    public async Task ResumeApproveDrainsToTerminalEnd()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.PostAsync(
            "/api/v1/threads/hitl-1/resume",
            JsonContent("""{ "kind": "approve", "payload": "ok" }"""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("kind").GetString().ShouldBe(StreamEventKind.End.ToString());
    }

    [Fact(DisplayName = "Given unknown resume kind, when POST resume, then 400 invalid_command")]
    public async Task ResumeUnknownKindReturns400()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.PostAsync(
            "/api/v1/threads/hitl-1/resume",
            JsonContent("""{ "kind": "explode" }"""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("studio.invalid_command");
    }

    [Fact(DisplayName = "Given done thread, when POST resume, then 409 not interrupted")]
    public async Task ResumeDoneThreadReturns409()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.PostAsync(
            "/api/v1/threads/work-1/resume",
            JsonContent("""{ "kind": "approve" }"""));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("graph.invalid_resume");
    }

    [Fact(DisplayName = "Given done thread, when POST continue, then 409 not runnable")]
    public async Task ContinueDoneThreadReturns409()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.PostAsync("/api/v1/threads/work-1/continue", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("graph.");
    }

    [Fact(DisplayName = "Given forked Running thread, when POST continue, then reaches End")]
    public async Task ContinueRunningThreadReachesEnd()
    {
        var session = await CreateSeededSessionAsync();
        var history = await session.GetHistoryAsync("work-1");
        var runningStep = history
            .First(static item => item.Status == GraphRunStatus.Running && item.NextNodes.Contains("b"))
            .Step;
        await session.ForkAsync("work-1", runningStep, "cont-1");

        using var api = await StudioApiTestHost.StartAsync(session);
        var response = await api.Client.PostAsync("/api/v1/threads/cont-1/continue", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("kind").GetString().ShouldBe(StreamEventKind.End.ToString());
    }

    [Fact(DisplayName = "Given update without writes, when POST update, then 400 invalid_request")]
    public async Task UpdateWithoutWritesReturns400()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.PostAsync(
            "/api/v1/threads/hitl-1/update",
            JsonContent("""{ }"""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("studio.invalid_request");
    }

    [Fact(DisplayName = "Given channel writes, when POST update, then patch visible in state")]
    public async Task UpdateMergesChannelWrites()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.PostAsync(
            "/api/v1/threads/hitl-1/update",
            JsonContent("""{ "writes": [ { "channelName": "messages", "value": "host-patch" } ] }"""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("threadId").GetString().ShouldBe("hitl-1");
        document.RootElement.GetProperty("values")
            .GetProperty("messages")
            .GetString()
            .ShouldNotBeNull()
            .ShouldContain("host-patch");
    }

    [Fact(DisplayName = "Given fork without or blank newThreadId, when POST fork, then 400 invalid_request")]
    public async Task ForkWithoutNewThreadIdReturns400()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        // required member missing — rejected at JSON binding (400, empty body).
        var missing = await api.Client.PostAsync(
            "/api/v1/threads/work-1/fork",
            JsonContent("""{ "step": 1 }"""));
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // present but blank — reaches the endpoint's own validation.
        var blank = await api.Client.PostAsync(
            "/api/v1/threads/work-1/fork",
            JsonContent("""{ "step": 1, "newThreadId": "  " }"""));
        blank.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await blank.Content.ReadAsStringAsync()).ShouldContain("studio.invalid_request");
    }

    [Fact(DisplayName = "Given valid step and newThreadId, when POST fork, then new thread state returned")]
    public async Task ForkCreatesNewThread()
    {
        var session = await CreateSeededSessionAsync();
        var sourceStep = (await session.GetHistoryAsync("work-1"))[0].Step;
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.PostAsync(
            "/api/v1/threads/work-1/fork",
            JsonContent($$"""{ "step": {{sourceStep}}, "newThreadId": "fork-1" }"""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("threadId").GetString().ShouldBe("fork-1");
        document.RootElement.GetProperty("step").GetInt64().ShouldBe(sourceStep);
    }

    [Fact(DisplayName = "Given missing thread, when GET stream default mode, then SSE error event")]
    public async Task StreamUnknownThreadEmitsErrorEvent()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.GetAsync("/api/v1/threads/missing/stream");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (response.Content.Headers.ContentType?.MediaType).ShouldBe("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("event: error");
    }

    [Fact(DisplayName = "Given interrupted thread, when GET stream default mode, then SSE snapshot frames")]
    public async Task StreamCheckpointModeEmitsSnapshot()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session);

        var response = await api.Client.GetAsync("/api/v1/threads/hitl-1/stream");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("event: stream");
        body.ShouldContain("event: done");
    }

    [Fact(DisplayName = "Given ApiKey configured, when no key / header key / bearer key, then 401 / 200 / 200")]
    public async Task ApiKeyGatesRoutesWhenConfigured()
    {
        var session = await CreateSeededSessionAsync();
        using var api = await StudioApiTestHost.StartAsync(session, apiKey: "test-secret");

        var unauthorized = await api.Client.GetAsync("/api/v1/threads");
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await unauthorized.Content.ReadAsStringAsync()).ShouldContain("studio.unauthorized");

        api.Client.DefaultRequestHeaders.Add(StudioApiKeyMiddleware.HeaderName, "test-secret");
        (await api.Client.GetAsync("/api/v1/threads")).StatusCode.ShouldBe(HttpStatusCode.OK);

        api.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-secret");
        (await api.Client.GetAsync("/api/v1/topology")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<VolutaUiSession> CreateSeededSessionAsync()
    {
        var session = CreateSession();

        await DrainAsync(session.StreamInvokeAsync(
            "hitl-1",
            [new ChannelWrite("messages", "seed-hitl")]));
        await DrainAsync(session.StreamInvokeAsync(
            "work-1",
            [new ChannelWrite("messages", "seed-work")]));
        await session.ResumeAsync("work-1", Command.Approve("ok"));

        return session;
    }

    private static VolutaUiSession CreateSession()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "a",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "from-a"))))
            .AddNode(
                "gate",
                static (context, _) => context.ResumePayload is null
                    ? Task.FromResult<NodeResult>(NodeResult.Interrupt("need-approve"))
                    : Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite("messages", "gate-done"))))
            .AddNode(
                "b",
                static (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite("messages", "from-b"))))
            .AddEdge(GraphConstants.Start, "a")
            .AddEdge("a", "gate")
            .AddEdge("gate", "b")
            .AddEdge("b", GraphConstants.End)
            .Compile(checkpointer);
        return new VolutaUiSession(new SingleGraphResolver(graph), checkpointer);
    }

    private static async Task DrainAsync(IAsyncEnumerable<StreamEvent> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }
}

file sealed class StudioApiTestHost(IHost application) : IDisposable
{
    public HttpClient Client { get; } = application.GetTestClient();

    public static async Task<StudioApiTestHost> StartAsync(
        VolutaUiSession session,
        string? apiKey = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddVolutaUI(session);

        var applicationHost = builder.Build();
        applicationHost.MapStudioApi(new StudioApiOptions { ApiKey = apiKey });
        await applicationHost.StartAsync();
        return new StudioApiTestHost(applicationHost);
    }

    public void Dispose()
    {
        Client.Dispose();
        application.Dispose();
    }
}
