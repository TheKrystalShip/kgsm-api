using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The per-host realtime endpoint — <c>GET /api/v1/stream</c> as a fetch-based SSE stream.
/// One stream per host multiplexes that host's topics (<c>architecture.html §3·b</c>); the
/// client chooses topics at connect via <c>?topics=a,b,c</c> and the pumps push
/// <c>{ topic, type, data }</c> envelopes. The action holds the request for the stream's
/// lifetime, registering the connection with the <see cref="StreamHub"/> for the pumps to
/// fan out to, and unregistering on disconnect.
/// </summary>
/// <remarks>
/// <b>The gate is per topic and per frame, not per endpoint.</b> Any authenticated caller connects, and
/// each topic they asked for delivers only what their access reaches (<see cref="StreamProtocol.Gate"/>)
/// — silently, never a 403 on the whole stream. Somebody holding nothing at all connects to hear about
/// their own account and nothing else, which is the whole of what a pending person is owed here; access
/// granted while the stream is open starts delivering without a reconnect.
/// </remarks>
[ApiController]
[Route("api/v1/stream")]
[Authorize]
public sealed class StreamController(
    StreamHub hub,
    ClusterSessionRevocations clusterRevocations,
    MemberAccess memberAccess,
    NodeAccess nodeAccess,
    ApiOptions options,
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

        ClaimsIdentity? ci = User.Identity as ClaimsIdentity;

        // Set SSE headers — mirrors the proven pattern from AssistantController.Turn.
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // The session behind this connection, re-checked for as long as it streams. [Authorize] gates
        // the CONNECT; nothing in the framework re-runs it on a request that lasts hours, so without
        // this a revoked session keeps its live channel until the tab closes while every REST call it
        // makes 401s within 5s. `sid` is absent on an auth-disabled host's synthetic principal → no
        // probe.
        //
        // Every session here was minted by the auth anchor and has no row on this node, so what is
        // re-asked is the deny-list the bus fills when somebody ends one — the same question the
        // [Authorize] gate asked at connect. The revocation cache is a singleton that opens its own DI
        // scope per miss, so holding it for the connection's lifetime is safe.
        string? sid = ci is not null ? SessionClaims.ReadSessionId(ci) : null;
        Func<CancellationToken, ValueTask<bool>>? sessionAlive = string.IsNullOrEmpty(sid)
            ? null
            : async (ct) => !await clusterRevocations.IsRevokedAsync(sid, ct).ConfigureAwait(false);

        // Who is reading, evaluated from this node's replica for as long as the connection lasts. The
        // account is the one authentication stamped on the principal; an auth-disabled host streams to
        // its synthetic Owner, which no replica holds.
        StreamAccess access = StreamAccess.Nobody;
        Func<CancellationToken, ValueTask<AccessEvaluator?>>? evaluate = null;
        if (options.AuthDisabled)
        {
            access = StreamAccess.Unrestricted;
        }
        else if (User.FindFirst(AccessClaims.Account)?.Value is { Length: > 0 } accountId)
        {
            if (await memberAccess.EvaluatorAsync(HttpContext.RequestAborted) is { } evaluator)
            {
                access = StreamAccess.For(accountId, nodeAccess, evaluator);
                evaluate = async (ct) => await memberAccess.EvaluatorAsync(ct).ConfigureAwait(false);
            }
            else
            {
                // The replica answered this request a moment ago at token validation, so this is a
                // replica that has just gone. Stream as nobody rather than refusing: the client hears
                // about its own account and nothing else until it reconnects.
                logger.LogWarning("SSE stream: the authority replica could not be read; streaming as nobody.");
            }
        }

        var connection = new StreamConnection(
            Response.Body, topics, hub.Json, logger, sessionAlive, access, sid, evaluate);
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
