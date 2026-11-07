using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Exceptions;
using Voluta.UI.Sse;

namespace Voluta.UI.Studio;

/// <summary>
///     ASP.NET Core endpoint mapping for the versioned Studio HTTP/SSE API (SPA-oriented contract).
/// </summary>
public static class StudioApiEndpointRouteBuilderExtensions
{
    /// <summary>
    ///     Maps Studio JSON + SSE under the default prefix (<c>/api/v1</c>).
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <returns>The endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapStudioApi(this IEndpointRouteBuilder endpoints)
    {
        return MapStudioApi(endpoints, new StudioApiOptions());
    }

    /// <summary>
    ///     Maps Studio JSON + SSE with an options mutator.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="configure">Options mutator.</param>
    /// <returns>The endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapStudioApi(
        this IEndpointRouteBuilder endpoints,
        Action<StudioApiOptions> configure)
    {
        var options = new StudioApiOptions();
        configure(options);
        return MapStudioApi(endpoints, options);
    }

    /// <summary>
    ///     Maps Studio JSON + SSE under the given options.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="options">Studio API options.</param>
    /// <returns>The endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapStudioApi(
        this IEndpointRouteBuilder endpoints,
        StudioApiOptions options)
    {
        var prefix = StudioApiRouteHelpers.NormalizePrefix(options.PathPrefix);
        var apiKey = options.ApiKey;

        var group = endpoints.MapGroup(prefix);
        group.AddEndpointFilter(new StudioApiKeyEndpointFilter(apiKey));

        group.MapGet(
            "/topology",
            async (VolutaUiSession session, CancellationToken cancellationToken) =>
                Results.Json(
                    VolutaUiJson.ToWire(await session.GetTopologyAsync(cancellationToken)),
                    JsonSerializerOptions.Web));

        group.MapGet(
            "/threads",
            async (VolutaUiSession session, CancellationToken cancellationToken) =>
                Results.Json(await session.ListThreadsAsync(cancellationToken), JsonSerializerOptions.Web));

        group.MapGet(
            "/threads/{threadId}",
            async (string threadId, VolutaUiSession session, CancellationToken cancellationToken) =>
            {
                var state = await session.GetStateAsync(threadId, cancellationToken);
                return state is null
                    ? Results.NotFound()
                    : Results.Json(VolutaUiJson.ToWire(state), JsonSerializerOptions.Web);
            });

        group.MapGet(
            "/threads/{threadId}/history",
            async (string threadId, VolutaUiSession session, CancellationToken cancellationToken) =>
            {
                try
                {
                    var history = await session.GetHistoryAsync(threadId, cancellationToken);
                    return Results.Json(
                        history.Select(static item => VolutaUiJson.ToWire(item)),
                        JsonSerializerOptions.Web);
                }
                catch (NotSupportedException exception)
                {
                    return Results.Json(
                        new { error = exception.Message, code = "checkpoint.list_not_supported" },
                        JsonSerializerOptions.Web,
                        statusCode: StatusCodes.Status501NotImplemented);
                }
            });

        group.MapPost(
            "/threads/{threadId}/resume",
            async (
                string threadId,
                ResumeRequest? body,
                VolutaUiSession session,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var command = StudioResumeCommand.Resolve(body?.Kind, body?.Payload);
                    var terminal = await session.ResumeAsync(threadId, command, cancellationToken);
                    return Results.Json(VolutaUiJson.ToWireTerminal(terminal), JsonSerializerOptions.Web);
                }
                catch (ArgumentException exception)
                {
                    return Results.Json(
                        new { error = exception.Message, code = "studio.invalid_command" },
                        JsonSerializerOptions.Web,
                        statusCode: StatusCodes.Status400BadRequest);
                }
                catch (GraphException exception) when (GraphExceptionResponse.StatusFor(exception) is { } status)
                {
                    return Results.Json(
                        new { error = exception.Message, code = exception.Code },
                        JsonSerializerOptions.Web,
                        statusCode: status);
                }
            });

        group.MapPost(
            "/threads/{threadId}/continue",
            async (string threadId, VolutaUiSession session, CancellationToken cancellationToken) =>
            {
                try
                {
                    var terminal = await session.ContinueAsync(threadId, cancellationToken);
                    return Results.Json(VolutaUiJson.ToWireTerminal(terminal), JsonSerializerOptions.Web);
                }
                catch (GraphException exception) when (GraphExceptionResponse.StatusFor(exception) is { } status)
                {
                    return Results.Json(
                        new { error = exception.Message, code = exception.Code },
                        JsonSerializerOptions.Web,
                        statusCode: status);
                }
            });

        group.MapPost(
            "/threads/{threadId}/update",
            async (
                string threadId,
                StudioUpdateStateRequest? body,
                VolutaUiSession session,
                CancellationToken cancellationToken) =>
            {
                if (body?.Writes is not { Count: > 0 })
                {
                    return Results.Json(
                        new { error = "writes are required", code = "studio.invalid_request" },
                        JsonSerializerOptions.Web,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var writes = body.Writes
                    .Select(static write => new ChannelWrite(write.ChannelName, write.Value))
                    .ToArray();
                var state = await session.UpdateStateAsync(threadId, writes, cancellationToken);
                return Results.Json(VolutaUiJson.ToWire(state), JsonSerializerOptions.Web);
            });

        group.MapPost(
            "/threads/{threadId}/fork",
            async (
                string threadId,
                StudioForkRequest? body,
                VolutaUiSession session,
                CancellationToken cancellationToken) =>
            {
                if (body is null || string.IsNullOrWhiteSpace(body.NewThreadId))
                {
                    return Results.Json(
                        new { error = "newThreadId is required", code = "studio.invalid_request" },
                        JsonSerializerOptions.Web,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var state = await session.ForkAsync(
                    threadId,
                    body.Step,
                    body.NewThreadId,
                    cancellationToken);
                return Results.Json(VolutaUiJson.ToWire(state), JsonSerializerOptions.Web);
            });

        group.MapGet(
            "/threads/{threadId}/stream",
            StudioApiStreamEndpoint.HandleAsync);

        group.MapGet(
            "/hitl",
            async (VolutaUiSession session, CancellationToken cancellationToken) =>
                Results.Json(await session.ListInterruptedAsync(cancellationToken), JsonSerializerOptions.Web));

        return endpoints;
    }
}

/// <summary>
///     Path prefix normalization for MapStudioApi.
/// </summary>
file static class StudioApiRouteHelpers
{
    public static string NormalizePrefix(string? pathPrefix)
    {
        var prefix = (pathPrefix ?? "/api/v1").TrimEnd('/');
        return string.IsNullOrEmpty(prefix)
            ? "/api/v1"
            : prefix.StartsWith('/') ? prefix : "/" + prefix;
    }
}

/// <summary>
///     Applies optional API-key auth to every Studio group endpoint.
/// </summary>
file sealed class StudioApiKeyEndpointFilter(string? apiKey) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (StudioApiKeyMiddleware.IsAuthorized(context.HttpContext.Request, apiKey))
        {
            return await next(context);
        }

        await StudioApiKeyMiddleware.WriteUnauthorizedAsync(
            context.HttpContext.Response,
            context.HttpContext.RequestAborted);
        return Results.Empty;
    }
}

/// <summary>
///     SSE stream endpoint for live graph events (Studio contract).
/// </summary>
file static class StudioApiStreamEndpoint
{
    public static async Task HandleAsync(
        string threadId,
        HttpContext httpContext,
        VolutaUiSession session,
        CancellationToken cancellationToken)
    {
        var mode = httpContext.Request.Query["mode"].FirstOrDefault() ?? "checkpoint";
        var kind = httpContext.Request.Query["kind"].FirstOrDefault() ?? Command.Kinds.Approve;
        var payload = httpContext.Request.Query["payload"].FirstOrDefault();

        IAsyncEnumerable<StreamEvent> stream;
        if (string.Equals(mode, "resume", StringComparison.OrdinalIgnoreCase))
        {
            stream = session.StreamResumeAsync(
                threadId,
                StudioResumeCommand.Resolve(kind, payload),
                cancellationToken);
        }
        else if (string.Equals(mode, "continue", StringComparison.OrdinalIgnoreCase))
        {
            stream = session.StreamContinueAsync(threadId, cancellationToken);
        }
        else if (string.Equals(mode, "invoke", StringComparison.OrdinalIgnoreCase))
        {
            var seed = httpContext.Request.Query["seed"].FirstOrDefault()
                       ?? "user: transfer $50";
            stream = session.StreamInvokeAsync(
                threadId,
                [new ChannelWrite("messages", seed)],
                cancellationToken);
        }
        else
        {
            var snapshot = await session.GetCheckpointAsync(threadId, cancellationToken);
            if (snapshot is null)
            {
                await StreamEventSseWriter.WriteErrorAsync(
                    httpContext.Response,
                    $"thread '{threadId}' not found",
                    cancellationToken);
                return;
            }

            stream = snapshot.Status == GraphRunStatus.Interrupted
                     && string.Equals(
                         httpContext.Request.Query["auto"].FirstOrDefault(),
                         "1",
                         StringComparison.Ordinal)
                ? session.StreamResumeAsync(
                    threadId,
                    StudioResumeCommand.Resolve(kind, payload ?? "ok"),
                    cancellationToken)
                : StudioCheckpointAsStream.FromSnapshotAsync(snapshot);
        }

        await StreamEventSseWriter.WriteAsync(httpContext.Response, stream, cancellationToken);
    }
}

/// <summary>
///     Emits a single synthetic stream event from a checkpoint snapshot.
/// </summary>
file static class StudioCheckpointAsStream
{
    public static async IAsyncEnumerable<StreamEvent> FromSnapshotAsync(
        Abstractions.Checkpoint.CheckpointSnapshot snapshot)
    {
        yield return new StreamEvent
        {
            Mode = StreamMode.Events,
            Kind = snapshot.Status switch
            {
                GraphRunStatus.Interrupted => StreamEventKind.Interrupt,
                GraphRunStatus.Done => StreamEventKind.End,
                GraphRunStatus.Failed => StreamEventKind.Failed,
                GraphRunStatus.Cancelled => StreamEventKind.Cancelled,
                GraphRunStatus.Running => StreamEventKind.Values,
                _ => StreamEventKind.Values,
            },
            Step = snapshot.Step,
            NodeNames = snapshot.LastNode is { } last ? [last] : [],
            State = snapshot.ChannelValues,
            Payload = snapshot.InterruptPayload,
        };
        await Task.CompletedTask;
    }
}
