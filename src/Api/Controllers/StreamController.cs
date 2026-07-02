using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Infrastructure;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The per-host realtime endpoint — <c>GET /api/v1/stream</c> as a fetch-based SSE stream.
/// One stream per host multiplexes that host's topics (<c>architecture.html §3·b</c>); the
/// client chooses topics at connect via <c>?topics=a,b,c</c> and the pumps push
/// <c>{ topic, type, data }</c> envelopes. The action holds the request for the stream's
/// lifetime, registering the connection with the <see cref="StreamHub"/> for the pumps to
/// fan out to, and unregistering on disconnect.
/// </summary>
[ApiController]
[Route("api/v1/stream")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class StreamController(
    StreamHub hub,
    IAuthorizationService authorization,
    IHostApplicationLifetime lifetime,
    ILogger<StreamController> logger) : ControllerBase
{
    [HttpGet]
    public async Task Get()
    {
        // Parse topics from the query string: ?topics=a,b,c (comma-separated, URL-encoded).
        // Unknown topics are ignored (forward-compat). Empty/missing = valid stream with no subscriptions.
        List<string> topics = Request.Query["topics"]
            .FirstOrDefault()?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList() ?? [];

        // Resolve operator once so the connection can silently drop operator-only topics
        // requested by a non-operator (parity with the WS-era per-subscribe refusal).
        bool isOperator =
            (await authorization.AuthorizeAsync(HttpContext.User, policyName: AuthPolicy.Operator)).Succeeded;

        // Filter out operator-only topics for non-operators (silent drop, not 403).
        if (!isOperator)
            topics = topics.Where(t => !StreamProtocol.RequiresOperator(t)).ToList();

        // Set SSE headers — mirrors the proven pattern from AssistantController.Turn.
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var connection = new StreamConnection(Response.Body, topics, hub.Json, logger);
        hub.Add(connection);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.ApplicationStopping, HttpContext.RequestAborted);
            await connection.RunAsync(linked.Token);
        }
        finally
        {
            hub.Remove(connection);
        }
    }
}
