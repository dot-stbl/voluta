using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Exceptions;
using Voluta.UI.Sse;

namespace Voluta.UI;

/// <summary>
///     ASP.NET Core endpoint mapping for the Voluta ops UI (Swagger-style host API).
/// </summary>
public static class VolutaUiEndpointRouteBuilderExtensions
{
    /// <summary>
    ///     Registers <see cref="VolutaUiSession" /> as singleton.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="session">Bound session.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddVolutaUI(this IServiceCollection services, VolutaUiSession session)
    {
        services.AddSingleton(session);
        return services;
    }

    /// <summary>
    ///     Maps UI shell + JSON API + SSE under the default path prefix (<c>/voluta</c>).
    /// </summary>
    /// <param name="endpoints">Endpoint route builder (typically <see cref="WebApplication" />).</param>
    /// <returns>The endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapVolutaUI(this IEndpointRouteBuilder endpoints)
    {
        return MapVolutaUI(endpoints, new VolutaUiOptions());
    }

    /// <summary>
    ///     Maps UI shell + JSON API + SSE with an options mutator (e.g. path prefix).
    /// </summary>
    /// <param name="endpoints">Endpoint route builder (typically <see cref="WebApplication" />).</param>
    /// <param name="configure">Options mutator.</param>
    /// <returns>The endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapVolutaUI(
        this IEndpointRouteBuilder endpoints,
        Action<VolutaUiOptions> configure)
    {
        var options = new VolutaUiOptions();
        configure(options);
        return MapVolutaUI(endpoints, options);
    }

    /// <summary>
    ///     Maps UI shell + JSON API + SSE under the given options.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="options">UI host options.</param>
    /// <returns>The endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapVolutaUI(
        this IEndpointRouteBuilder endpoints,
        VolutaUiOptions options)
    {
        var prefix = VolutaUiRouteHelpers.NormalizePrefix(options.PathPrefix);

        // Single shell route — do not also MapGet(prefix+"/") (AmbiguousMatch with /voluta).
        // Inject <base href="{prefix}/"> so relative styles.css/app.js resolve under the prefix
        // when the browser URL is /voluta without a trailing slash.
        endpoints.MapGet(
            prefix,
            () => Results.Content(VolutaUiAssets.RenderIndexHtml(prefix), "text/html; charset=utf-8"));
        endpoints.MapGet(
            $"{prefix}/index.html",
            () => Results.Content(VolutaUiAssets.RenderIndexHtml(prefix), "text/html; charset=utf-8"));
        endpoints.MapGet(
            $"{prefix}/styles.css",
            () => Results.Content(VolutaUiAssets.StylesCss, "text/css; charset=utf-8"));
        endpoints.MapGet(
            $"{prefix}/app.js",
            () => Results.Content(VolutaUiAssets.AppJs, "text/javascript; charset=utf-8"));

        // Studio SPA (Vite → wwwroot/studio). Legacy shell stays at {prefix}.
        // SPA dist optional: missing assets → 503 HTML, host still starts.
        VolutaUiStudioRoutes.Map(endpoints, prefix);

        endpoints.MapGet(
            $"{prefix}/api/topology",
            async (VolutaUiSession session, CancellationToken cancellationToken) =>
                Results.Json(
                    VolutaUiJson.ToWire(await session.GetTopologyAsync(cancellationToken)),
                    JsonSerializerOptions.Web));

        endpoints.MapGet(
            $"{prefix}/api/hitl",
            async (VolutaUiSession session, CancellationToken cancellationToken) =>
                Results.Json(await session.ListInterruptedAsync(cancellationToken), JsonSerializerOptions.Web));

        endpoints.MapGet(
            $"{prefix}/api/threads",
            async (VolutaUiSession session, CancellationToken cancellationToken) =>
                Results.Json(await session.ListThreadsAsync(cancellationToken), JsonSerializerOptions.Web));

        endpoints.MapGet(
            $"{prefix}/api/threads/{{threadId}}",
            async (string threadId, VolutaUiSession session, CancellationToken cancellationToken) =>
            {
                var snapshot = await session.GetCheckpointAsync(threadId, cancellationToken);
                return snapshot is null
                    ? Results.NotFound()
                    : Results.Json(VolutaUiJson.ToWire(snapshot), JsonSerializerOptions.Web);
            });

        endpoints.MapGet(
            $"{prefix}/api/threads/{{threadId}}/history",
            async (string threadId, VolutaUiSession session, CancellationToken cancellationToken) =>
            {
                try
                {
                    var history = await session.GetHistoryAsync(threadId, cancellationToken);
                    return Results.Json(
                        history.Select(static state => VolutaUiJson.ToWire(state)),
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

        endpoints.MapPost(
            $"{prefix}/api/threads/{{threadId}}/resume",
            async (
                string threadId,
                ResumeRequest? body,
                VolutaUiSession session,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var command = VolutaUiResumeCommand.Resolve(body?.Kind, body?.Payload);
                    var terminal = await session.ResumeAsync(threadId, command, cancellationToken);
                    return Results.Json(VolutaUiJson.ToWireTerminal(terminal), JsonSerializerOptions.Web);
                }
                catch (GraphException exception) when (GraphExceptionResponse.StatusFor(exception) is { } status)
                {
                    return Results.Json(
                        new { error = exception.Message, code = exception.Code },
                        JsonSerializerOptions.Web,
                        statusCode: status);
                }
                catch (ArgumentException exception)
                {
                    return Results.Json(
                        new { error = exception.Message, code = "ui.invalid_command" },
                        JsonSerializerOptions.Web,
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        endpoints.MapGet(
            $"{prefix}/api/threads/{{threadId}}/stream",
            VolutaUiStreamEndpoint.HandleAsync);

        return endpoints;
    }

    /// <summary>
    ///     Legacy overload: maps UI under an explicit path prefix string.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="pathPrefix">URL prefix.</param>
    /// <returns>The endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapVolutaUI(
        this IEndpointRouteBuilder endpoints,
        string pathPrefix)
    {
        return MapVolutaUI(endpoints, new VolutaUiOptions { PathPrefix = pathPrefix });
    }
}

/// <summary>
///     Path prefix normalization for MapVolutaUI.
/// </summary>
file static class VolutaUiRouteHelpers
{
    public static string NormalizePrefix(string? pathPrefix)
    {
        var prefix = (pathPrefix ?? "/voluta").TrimEnd('/');
        return string.IsNullOrEmpty(prefix)
            ? "/voluta"
            : prefix.StartsWith('/') ? prefix : "/" + prefix;
    }
}

/// <summary>
///     Studio SPA routes under <c>{prefix}/studio</c> (embedded wwwroot/studio).
/// </summary>
file static class VolutaUiStudioRoutes
{
    public static void Map(IEndpointRouteBuilder endpoints, string prefix)
    {
        // Single catch-all route: {**assetPath} also matches empty (i.e. {prefix}/studio),
        // so exact /studio and /studio/ routes would be ambiguous matches in .NET 10.
        endpoints.MapGet(
            $"{prefix}/studio/{{**assetPath}}",
            (string? assetPath) => StudioAsset(prefix, assetPath));
    }

    private static IResult StudioIndex(string prefix)
    {
        return VolutaStudioAssets.RenderIndexHtml(prefix) is { } html
            ? Results.Content(html, "text/html; charset=utf-8")
            : Results.Content(
                VolutaStudioAssets.RenderUnavailableHtml(prefix),
                "text/html; charset=utf-8",
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult StudioAsset(string prefix, string? assetPath)
    {
        // Client-side routes (no file extension) fall back to SPA index when built.
        return string.IsNullOrEmpty(assetPath)
            || assetPath.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            ? StudioIndex(prefix)
            : VolutaStudioAssets.TryReadBytes(assetPath, out var bytes, out var contentType)
                ? Results.Bytes(bytes, contentType)
                : Path.HasExtension(assetPath)
                    ? Results.NotFound()
                    : VolutaStudioAssets.IsAvailable
                        ? StudioIndex(prefix)
                        : Results.Content(
                            VolutaStudioAssets.RenderUnavailableHtml(prefix),
                            "text/html; charset=utf-8",
                            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

/// <summary>
///     Maps UI resume request kind/payload onto the closed HITL Command taxonomy.
/// </summary>
file static class VolutaUiResumeCommand
{
    public static Command Resolve(string? kind, object? payload)
    {
        var resolvedKind = string.IsNullOrWhiteSpace(kind) ? Command.Kinds.Approve : kind;
        return resolvedKind switch
        {
            Command.Kinds.Approve => Command.Approve(payload),
            Command.Kinds.Reject => Command.Reject(payload),
            Command.Kinds.Update => throw new ArgumentException(
                "UI resume does not accept kind 'update' without channel Values; use the host SDK Command.Update(...)."),
            _ => new Command { Kind = resolvedKind, Payload = payload },
        };
    }
}

/// <summary>
///     SSE stream endpoint handler for live graph events.
/// </summary>
file static class VolutaUiStreamEndpoint
{
    public static async Task HandleAsync(
        string threadId,
        HttpContext httpContext,
        VolutaUiSession session,
        CancellationToken cancellationToken)
    {
        var mode = httpContext.Request.Query["mode"].FirstOrDefault() ?? "checkpoint";
        var kind = httpContext.Request.Query["kind"].FirstOrDefault() ?? "approve";
        var payload = httpContext.Request.Query["payload"].FirstOrDefault();

        IAsyncEnumerable<StreamEvent> stream;
        if (string.Equals(mode, "resume", StringComparison.OrdinalIgnoreCase))
        {
            stream = session.StreamResumeAsync(
                threadId,
                VolutaUiResumeCommand.Resolve(kind, payload),
                cancellationToken);
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
                    VolutaUiResumeCommand.Resolve(kind, payload ?? "ok"),
                    cancellationToken)
                : CheckpointAsStream.FromSnapshotAsync(snapshot);
        }

        await StreamEventSseWriter.WriteAsync(httpContext.Response, stream, cancellationToken);
    }
}

/// <summary>
///     Emits a single synthetic stream event from a checkpoint snapshot.
/// </summary>
file static class CheckpointAsStream
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
                GraphRunStatus.Running => throw new NotImplementedException(),
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
