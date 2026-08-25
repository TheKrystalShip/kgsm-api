using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Infrastructure;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Sessions;

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
/// <b>The gate is per topic, not per endpoint.</b> Any authenticated caller connects, and each topic
/// they asked for is kept only if their tier reaches it (<see cref="StreamProtocol.MinimumTier"/>) —
/// silently, never a 403 on the whole stream. The panel's reads sit at the viewer floor as they
/// always have; what the per-topic gate adds is somebody holding nothing at all, who connects to hear
/// about their own account and to hear nothing else. That is the whole of what a pending user is owed
/// here, and the only alternative — telling them to keep reloading until an admin gets to them — is
/// worse for exactly the person with the least standing to complain.
/// </remarks>
[ApiController]
[Route("api/v1/stream")]
[Authorize]
public sealed class StreamController(
    StreamHub hub,
    ISessionValidator sessions,
    UserDirectory users,
    ApiOptions options,
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

        // Who is streaming, and what they may do. The tier is the one on the claims identity, which
        // the authority resolution at token validation has already replaced with what the account
        // store says now — the same value every gate on this request reads.
        ClaimsIdentity? ci = User.Identity as ClaimsIdentity;
        KgsmIdentity? identity = ci is not null ? SessionClaims.ReadIdentity(ci) : null;
        KgsmTier tier = ci is not null ? SessionClaims.ReadTier(ci) : KgsmTier.None;

        // Keep only the topics this caller's tier reaches (silent drop, not a 403 on the stream).
        topics = topics.Where(t => tier >= StreamProtocol.MinimumTier(t)).ToList();

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
        // probe, and the stream behaves exactly as before. The validator is a singleton that opens its
        // own DI scope per cache miss, so holding it for the connection's lifetime is safe.
        string? sid = ci is not null ? SessionClaims.ReadSessionId(ci) : null;
        Func<CancellationToken, ValueTask<bool>>? sessionAlive = string.IsNullOrEmpty(sid)
            ? null
            : async (ct) => await sessions.IsValidAsync(sid, ct).ConfigureAwait(false);

        // The account this connection is authenticated as, so a change to it can be addressed here,
        // and the re-read that keeps its authority current for as long as it streams. Both need a real
        // identity and a readable store. An auth-disabled host has neither: its synthetic principal
        // names a subject no account was ever created for, and resolving that would answer "stranger"
        // and re-gate the dev admin down to nothing twenty seconds into every stream.
        string? accountId = null;
        Func<CancellationToken, ValueTask<AccountStanding>>? authority = null;
        if (options.AuthEnabled && identity is not null && users.Available)
        {
            authority = async (ct) => await users.StandingAsync(identity, ct).ConfigureAwait(false);
            try
            {
                accountId = (await users.StandingAsync(identity, HttpContext.RequestAborted)).AccountId;
            }
            catch (KgsmAuthProviderException e)
            {
                // The store answered this request a moment ago at token validation, so this is a
                // store that has just gone. Stream on unaddressed rather than refusing: the re-check
                // will fail the same way within its interval and end the connection honestly, which
                // is the same answer arrived at through the path that already handles it.
                logger.LogWarning(e, "SSE stream: could not resolve the caller's account; nothing will be addressed to this connection.");
            }
        }

        var connection = new StreamConnection(
            Response.Body, topics, hub.Json, logger, sessionAlive, tier, accountId, sid, authority);
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
