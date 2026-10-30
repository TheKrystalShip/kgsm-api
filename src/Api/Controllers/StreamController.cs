using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Sessions;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The per-host realtime endpoint — <c>GET /api/v1/stream</c> as a fetch-based SSE stream.
/// One stream per host multiplexes that host's topics (<c>reference/realtime-stream.md</c>); the
/// client chooses topics at connect via <c>?topics=a,b,c</c> and the pumps push
/// <c>{ topic, type, data }</c> envelopes. The action holds the request for the stream's
/// lifetime, registering the connection with the <see cref="StreamHub"/> for the pumps to
/// fan out to, and unregistering on disconnect.
/// </summary>
/// <remarks>
/// <b>The gate is per topic and per frame, not per endpoint.</b> Any authenticated caller connects, and
/// each topic they asked for delivers only what their access reaches (<see cref="StreamProtocol.Gate"/>)
/// — silently, never a 403 on the whole stream. Somebody holding nothing at all connects to hear about
/// their own account and nothing else, which is the whole of what a pending person is owed here. The
/// stream ends when the caller's access token expires (<see cref="KgsmPerson.UntilExpiry"/>); the client
/// reconnects with a fresh one, which is where a changed role takes effect.
/// </remarks>
[ApiController]
[Route("api/v1/stream")]
[Authorize]
public sealed class StreamController(
    StreamHub hub,
    NodeAccess nodeAccess,
    IHostApplicationLifetime lifetime,
    ILogger<StreamController> logger) : ControllerBase
{
    [HttpGet]
    public async Task Get()
    {
        // Parse topics from the query string: ?topics=a,b,c (comma-separated, URL-encoded).
        // Unknown topics are kept and reach an Owner alone. Empty/missing = valid stream with no subscriptions.
        List<string> topics = Request.Query["topics"]
            .FirstOrDefault()?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList() ?? [];

        // Set SSE headers — mirrors the proven pattern from AssistantController.Turn.
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // Who is reading, and what their token lists. Another member acting as itself, and the open
        // caller of an auth-disabled host, read everything.
        StreamAccess access = nodeAccess.StreamAccessOf(User);

        var connection = new StreamConnection(
            Response.Body, topics, hub.Json, logger, access);
        hub.Add(connection);
        try
        {
            // Ends with the request, the host, or the caller's token, whichever comes first.
            using CancellationTokenSource untilExpiry = KgsmPerson.UntilExpiry(HttpContext);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.ApplicationStopping, untilExpiry.Token);
            await connection.RunAsync(linked.Token);
        }
        finally
        {
            hub.Remove(connection);
        }
    }
}
