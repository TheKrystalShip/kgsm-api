using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Json;
using TheKrystalShip.Api.Services.Cluster;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The cluster message bus's receive endpoint (<c>docs/cluster-message-bus-plan.md §4</c>). Named
/// <c>Peers</c>, not <c>ClusterInbox</c>, because a later peer-management milestone
/// (<c>PLAN-peers.md</c>) adds sibling actions here (peer listing/registration) — this is that
/// controller's natural first action, not a one-off.
/// </summary>
/// <remarks>
/// <b><see cref="AllowAnonymousAttribute"/> w.r.t. the user auth scheme.</b> The inbox authenticates
/// callers with the cluster service token (<see cref="IClusterTokenService"/>), a completely separate
/// credential from the Discord-derived user JWT the rest of the API's <c>[Authorize]</c> tier policies
/// check. It must work identically whether <c>KGSM_API_AUTH_DISABLED</c> is set or not — a node-to-node
/// call is not a browser session, so it must NOT be gated (or auto-granted) by the user auth pipeline
/// at all. <see cref="Inbox"/> does its OWN fail-closed auth inline, first thing, every call.
/// </remarks>
[ApiController]
[Route("api/v1/peers")]
[AllowAnonymous]
public sealed class PeersController(
    IClusterTokenService clusterTokens,
    IClusterPeerGate peerGate,
    ClusterInbox inbox,
    ILogger<PeersController> logger) : ControllerBase
{
    /// <summary>Max envelope size (§3) — a request over this is rejected before its body is even
    /// fully read.</summary>
    private const int MaxEnvelopeBytes = 64 * 1024;

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = BuildJsonOptions();

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var options = new JsonSerializerOptions();
        ApiJson.Configure(options);
        return options;
    }

    /// <summary>
    /// <c>POST /api/v1/peers/inbox</c> — receive one envelope from another cluster node. See the wire
    /// contract in <c>docs/cluster-message-bus-plan.md §4</c> for the full status-code table.
    /// </summary>
    [HttpPost("inbox")]
    public async Task<IActionResult> Inbox(CancellationToken ct)
    {
        // Cheap pre-check: reject on a lying/oversized Content-Length before touching the body at all.
        if (Request.ContentLength is long declaredLength && declaredLength > MaxEnvelopeBytes)
            return TooLarge();

        // Fail-closed cluster auth. A non-cluster node (blank ClusterSecret) makes ValidateAsync
        // always return null, so it 401s all bus traffic — the intended posture (§9).
        string? token = ExtractBearerToken(Request);
        if (token is null)
            return Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token", "missing bearer token");

        ClusterPrincipal? principal = await clusterTokens.ValidateAsync(token).ConfigureAwait(false);
        if (principal is null)
            return Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token",
                "invalid, expired, or unsigned cluster service token");

        // The §4 peer-enabled gate. No Peers table yet (a later milestone) — AllowAllClusterPeerGate
        // treats any validly-tokened node as enabled today; see IClusterPeerGate's remarks.
        if (!await peerGate.IsEnabledAsync(principal.NodeId).ConfigureAwait(false))
            return Error(StatusCodes.Status403Forbidden, "peer_disabled",
                $"node '{principal.NodeId}' is not an enabled peer of this cluster");

        // Read the body under a hard byte cap — defense in depth beyond the Content-Length pre-check
        // above, which a chunked-transfer request can omit or understate.
        (bool withinLimit, string body) = await ReadBoundedBodyAsync(Request.Body, MaxEnvelopeBytes, ct)
            .ConfigureAwait(false);
        if (!withinLimit)
            return TooLarge();

        ClusterEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ClusterEnvelope>(body, EnvelopeJsonOptions);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request", "the envelope body is not valid JSON");
        }

        if (envelope is null
            || string.IsNullOrWhiteSpace(envelope.Id)
            || string.IsNullOrWhiteSpace(envelope.Type)
            || string.IsNullOrWhiteSpace(envelope.From))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "the envelope is missing one or more of id/type/from");
        }

        // The from-spoof guard (§3): a node may not send "as" another node.
        if (!string.Equals(envelope.From, principal.NodeId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "cluster inbox: envelope.from={From} does not match the authenticated token's node {NodeId} — rejected",
                envelope.From, principal.NodeId);
            return Error(StatusCodes.Status403Forbidden, "from_mismatch",
                "envelope.from does not match the authenticated cluster service token");
        }

        InboxResult result = await inbox.ReceiveAsync(envelope, ct).ConfigureAwait(false);
        return result switch
        {
            InboxResult.TransientFailure => Error(StatusCodes.Status500InternalServerError, "internal",
                "a transient failure occurred processing this message; retry"),
            // Applied / Duplicate / DroppedUnknown all ack 200 — the sender cannot and need not tell
            // them apart, and none of them should keep the message in the sender's outbox (§4/§8).
            _ => Ok(new { status = "accepted" }),
        };
    }

    private ObjectResult TooLarge() =>
        Error(StatusCodes.Status413PayloadTooLarge, "payload_too_large",
            $"envelope exceeds the {MaxEnvelopeBytes}-byte limit");

    /// <summary>Read the request body into a string, bailing out (returning <c>ok:false</c>) the moment
    /// more than <paramref name="maxBytes"/> have been read — never buffers past the cap regardless of
    /// what <c>Content-Length</c> claimed.</summary>
    private static async Task<(bool ok, string body)> ReadBoundedBodyAsync(
        Stream requestBody, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await requestBody.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return (false, "");
            buffer.Write(chunk, 0, read);
        }
        return (true, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>Extract a bearer token from the <c>Authorization</c> header, or <see langword="null"/>
    /// if absent/not a bearer — this endpoint has no other credential form (no cookie, no query-string
    /// fallback; that hack is reserved for the SSE handshake elsewhere, not node-to-node calls).</summary>
    private static string? ExtractBearerToken(HttpRequest request)
    {
        string? header = request.Headers[HeaderNames.Authorization];
        const string prefix = "Bearer ";
        return header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
