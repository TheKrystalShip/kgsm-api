using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Json;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Library;
// Disambiguate from Microsoft.Extensions.Hosting.Host (pulled in by ImplicitUsings).
using Host = TheKrystalShip.Api.Contracts.Host;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The cluster message bus's receive endpoint (<c>docs/cluster-message-bus-plan.md §4</c>) plus the
/// peer-management surface (<c>PLAN-peers.md §7</c>, P0): roster CRUD, the join-via-seed handshake, this
/// node's own identity card, and per-peer latency. Named <c>Peers</c>, not <c>ClusterInbox</c>, because
/// the roster actions are this controller's natural home, not a one-off.
/// </summary>
/// <remarks>
/// <b><see cref="AllowAnonymousAttribute"/> w.r.t. the user auth scheme.</b> <see cref="Inbox"/> and
/// <see cref="GetIdentity"/> authenticate callers with the cluster service token
/// (<see cref="IClusterTokenService"/>), a completely separate credential from the Discord-derived user
/// JWT the rest of the API's <c>[Authorize]</c> tier policies check — they carry <c>[AllowAnonymous]</c>
/// individually and do their OWN fail-closed auth inline, first thing, every call. It must work
/// identically whether <c>Api__AuthDisabled</c> is set or not — a node-to-node call is not a
/// browser session, so it must NOT be gated (or auto-granted) by the user auth pipeline at all.
/// <b>Deliberately NOT a class-level <see cref="AllowAnonymousAttribute"/></b> — that would win over
/// every action-level <c>[Authorize]</c> below (ASP.NET Core resolves <c>AllowAnonymous</c> at the
/// highest scope it appears), silently exposing the admin-gated roster CRUD. Each action carries its
/// own attribute instead.
/// </remarks>
[ApiController]
[Route("api/v1/peers")]
public sealed class PeersController(
    IClusterTokenService clusterTokens,
    IClusterPeerGate peerGate,
    ClusterInbox inbox,
    PeersStore peers,
    PeerHandshakeService handshake,
    GossipService gossip,
    ApiOptions options,
    HostIdentityProvider hostIdentity,
    LeafHealthMonitor leafHealth,
    HostAggregator hostAggregator,
    LibraryAggregator library,
    ClusterPeerRelay relay,
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
    [AllowAnonymous]
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

        // The §4 peer-enabled gate — a disable-list (PLAN-peers.md §2 #7/#8): the shared cluster secret
        // is the trust boundary, so an unknown validly-tokened node is accepted; only a node explicitly
        // present-and-disabled in the Peers table is rejected here.
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

    /// <summary><c>POST /api/v1/peers/sync</c> — the ephemeral membership-gossip roster exchange
    /// (PLAN-peers.md §2·b, G4). Cluster-token authed + disable-list gated, same fail-closed posture as
    /// <see cref="Inbox"/>; DELIBERATELY separate from the durable message bus — a best-effort push-pull, no
    /// outbox row, no retry. The caller pushes its roster (request body); this node merges it and returns its
    /// own roster for the caller to merge back.</summary>
    [HttpPost("sync")]
    [AllowAnonymous]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        string? token = ExtractBearerToken(Request);
        if (token is null)
            return Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token", "missing bearer token");

        ClusterPrincipal? principal = await clusterTokens.ValidateAsync(token).ConfigureAwait(false);
        if (principal is null)
            return Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token",
                "invalid, expired, or unsigned cluster service token");

        if (!await peerGate.IsEnabledAsync(principal.NodeId).ConfigureAwait(false))
            return Error(StatusCodes.Status403Forbidden, "peer_disabled",
                $"node '{principal.NodeId}' is not an enabled peer of this cluster");

        (bool withinLimit, string body) = await ReadBoundedBodyAsync(Request.Body, MaxEnvelopeBytes, ct)
            .ConfigureAwait(false);
        if (!withinLimit)
            return TooLarge();

        SyncRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SyncRequest>(body, EnvelopeJsonOptions);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request", "the sync body is not valid JSON");
        }

        if (request is null || request.Members is null)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "the sync body is missing members");

        // Unlike Inbox, no from-spoof guard here — a sync payload describes many nodes at once, so `From`
        // is informational only, not a single actor to authenticate.
        await gossip.MergeIncomingAsync(request.Members, ct).ConfigureAwait(false);

        // The caller authenticated a valid cluster token and reached us — first-hand liveness evidence for
        // it, from the opposite direction to our own probe (PLAN-peers.md §2·b G5). Records it against the
        // token's node id (never the spoofable body `From`), so a peer we can't probe but that still gossips
        // to us stays alive instead of falsely dying.
        await gossip.RecordInboundContactAsync(principal.NodeId, ct).ConfigureAwait(false);

        IReadOnlyList<SyncMember> members = await gossip.BuildLocalRosterAsync(ct).ConfigureAwait(false);
        return Ok(new SyncResponse(options.NodeId, members));
    }

    /// <summary><c>GET /api/v1/peers</c> — the full roster, enabled and disabled alike (admin-gated,
    /// <c>PLAN-peers.md §7</c>).</summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<PeerListResponse> List(CancellationToken ct)
    {
        IReadOnlyList<PeerEntity> rows = await peers.ListAsync(ct).ConfigureAwait(false);
        return new PeerListResponse(rows.Select(ToView).ToList());
    }

    /// <summary><c>GET /api/v1/peers/roster</c> — the viewer-tier node list (<c>PLAN-peers.md §7</c>,
    /// gap G1): the browser-facing projection of the cluster roster that powers "add one, see all" for
    /// a non-admin user. Enabled peers only (<see cref="PeersStore.ListEnabledAsync"/>) — a disabled row
    /// is an admin management state a viewer must not see or be handed a URL for; self is never a peer
    /// row, so it never appears here either. Every <see cref="PeerEntity.MembershipState"/> is honestly
    /// reported (including the derived "joining"), only the enabled filter applies.</summary>
    [HttpGet("roster")]
    [Authorize(Policy = AuthPolicy.Viewer)]
    public async Task<ClusterNodesResponse> Roster(CancellationToken ct)
    {
        IReadOnlyList<PeerEntity> rows = await peers.ListEnabledAsync(ct).ConfigureAwait(false);
        return new ClusterNodesResponse(rows.Select(ToNodeView).ToList());
    }

    /// <summary><c>POST /api/v1/peers</c> — the admin "paste a URL" join-via-seed action. Maps every
    /// <see cref="PeerAddOutcome"/> to its frozen status code (<c>PLAN-peers.md §7</c>).</summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> Add([FromBody] PeerAddRequest? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Url))
            return Error(StatusCodes.Status400BadRequest, "invalid_url", "url is required");

        PeerAddResult result = await handshake.AddPeerAsync(body.Url.Trim(), body.Nickname, ct)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case PeerAddOutcome.Added:
                PeerEntity peer = result.Peer!;
                var added = new PeerAddedResponse(
                    peer.Id, peer.Url, peer.Nickname, peer.NodeId, peer.ApiVersion, peer.Status, peer.Enabled);
                return StatusCode(StatusCodes.Status201Created, added);

            case PeerAddOutcome.InvalidUrl:
                return Error(StatusCodes.Status400BadRequest, "invalid_url",
                    "url is not a valid absolute http(s) address");

            case PeerAddOutcome.VersionMismatch:
                JsonElement details = JsonSerializer.SerializeToElement(
                    new PeerVersionMismatchDetails(result.RemoteApiVersion ?? "unknown", ApiInfo.ApiVersion),
                    EnvelopeJsonOptions);
                return Error(StatusCodes.Status409Conflict, "version_mismatch",
                    "the candidate node's api version does not match this node's", details);

            case PeerAddOutcome.NotCluster:
                return Error(StatusCodes.Status422UnprocessableEntity, "peer_not_cluster",
                    "remote node does not advertise the cluster capability");

            case PeerAddOutcome.Unreachable:
            default:
                return Error(StatusCodes.Status502BadGateway, "peer_unreachable",
                    "could not reach the candidate node");
        }
    }

    /// <summary><c>DELETE /api/v1/peers/{id}</c> — forget a roster row entirely (admin-gated).</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        bool removed = await peers.DeleteAsync(id, ct).ConfigureAwait(false);
        return removed ? NoContent() : NotFound();
    }

    /// <summary><c>PATCH /api/v1/peers/{id}</c> — the disable-list toggle (admin-gated,
    /// <c>PLAN-peers.md §0</c> #8). Only <see cref="PeerPatchRequest.Enabled"/> is settable.</summary>
    [HttpPatch("{id}")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> SetEnabled(string id, [FromBody] PeerPatchRequest? body, CancellationToken ct)
    {
        if (body is null)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "enabled is required");

        bool updated = await peers.SetEnabledAsync(id, body.Enabled, ct).ConfigureAwait(false);
        if (!updated)
            return NotFound();

        PeerEntity? row = await peers.GetAsync(id, ct).ConfigureAwait(false);
        return row is null ? NotFound() : Ok(ToView(row));
    }

    /// <summary><c>GET /api/v1/peers/identity</c> — this node's identity card
    /// (<c>PLAN-peers.md §7</c>), the payload a candidate's join-via-seed handshake pulls. Cluster-token
    /// authed, NOT user-authed — same inline fail-closed check as <see cref="Inbox"/>, minus the
    /// enabled-peer gate (a node that hasn't joined the roster yet must still be able to identify
    /// itself; the gate only matters for calls that act on this node's state).</summary>
    [HttpGet("identity")]
    [AllowAnonymous]
    public async Task<IActionResult> GetIdentity(CancellationToken ct)
    {
        string? token = ExtractBearerToken(Request);
        if (token is null)
            return Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token", "missing bearer token");

        ClusterPrincipal? principal = await clusterTokens.ValidateAsync(token).ConfigureAwait(false);
        if (principal is null)
            return Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token",
                "invalid, expired, or unsigned cluster service token");

        var view = new PeerIdentityView(
            options.NodeId,
            ApiInfo.ApiVersion,
            hostIdentity.Build,
            NodeCapabilities.Current(leafHealth.Current, options.ClusterEnabled));
        return Ok(view);
    }

    /// <summary><c>GET /api/v1/peers/{id}/latency</c> — the roster row's last-observed liveness sample
    /// (admin-gated, §4). <c>404</c> if <paramref name="id"/> isn't a known row.</summary>
    [HttpGet("{id}/latency")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> Latency(string id, CancellationToken ct)
    {
        PeerEntity? row = await peers.GetAsync(id, ct).ConfigureAwait(false);
        if (row is null)
            return NotFound();
        return Ok(new PeerLatencyView(row.LatencyMs, row.Status, row.LastSeen));
    }

    // ── P2: resource visibility ──────────────────────────────────────────────────────────────────────
    // Two distinct reads, deliberately not collapsed into one node-proxy (PLAN-peers.md P2):
    //   • self/*  — what THIS node exposes to a cluster peer (cluster-token authed + disable-gated), so a
    //               peer's fan-out can read our capacity/capabilities/library.
    //   • {id}/*  — the server-side node-proxy: relay to a peer's self/* via a minted service token. Consumed
    //               by capacity-fan-out / the assistant, never the SPA (which reads a peer directly, §8).

    /// <summary><c>GET /api/v1/peers/self/resources</c> — this node's own capacity for a cluster peer's
    /// server-side fan-out (cluster-token authed + disable-gated). A lean projection of the §4·a Host capacity
    /// strip; capacity is honest <see langword="null"/> when no metrics snapshot exists.</summary>
    [HttpGet("self/resources")]
    [AllowAnonymous]
    public async Task<IActionResult> SelfResources(CancellationToken ct)
    {
        (ClusterPrincipal? _, IActionResult? error) = await AuthenticateEnabledClusterCallerAsync().ConfigureAwait(false);
        if (error is not null) return error;

        Host host = await hostAggregator.GetHostAsync(ct).ConfigureAwait(false);
        return Ok(new ClusterResourcesView(host.Id, host.Label, host.Status, host.CpuPct, host.Mem, host.Disks));
    }

    /// <summary><c>GET /api/v1/peers/self/capabilities</c> — this node's §4·b capability block, verbatim
    /// (cluster-token authed + disable-gated).</summary>
    [HttpGet("self/capabilities")]
    [AllowAnonymous]
    public async Task<IActionResult> SelfCapabilities(CancellationToken ct)
    {
        (ClusterPrincipal? _, IActionResult? error) = await AuthenticateEnabledClusterCallerAsync().ConfigureAwait(false);
        if (error is not null) return error;

        Host host = await hostAggregator.GetHostAsync(ct).ConfigureAwait(false);
        return Ok(host.Capabilities);
    }

    /// <summary><c>GET /api/v1/peers/self/library</c> — this node's installable-game catalog, verbatim
    /// (cluster-token authed + disable-gated). Cover/hero URLs point at this node's own art endpoints.</summary>
    [HttpGet("self/library")]
    [AllowAnonymous]
    public async Task<IActionResult> SelfLibrary(CancellationToken ct)
    {
        (ClusterPrincipal? _, IActionResult? error) = await AuthenticateEnabledClusterCallerAsync().ConfigureAwait(false);
        if (error is not null) return error;

        string baseUrl = !string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? options.PublicBaseUrl
            : $"{Request.Scheme}://{Request.Host}";
        IReadOnlyList<LibraryEntry> entries = await library.GetLibraryAsync(null, baseUrl, ct).ConfigureAwait(false);
        return Ok(entries);
    }

    /// <summary><c>GET /api/v1/peers/{id}/resources</c> — server-side relay to peer <paramref name="id"/>'s
    /// own capacity (admin-gated; §8 keeps this off the SPA). <c>404</c> unknown peer, <c>403</c> disabled,
    /// <c>502</c> unreachable.</summary>
    [HttpGet("{id}/resources")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public Task<IActionResult> PeerResources(string id, CancellationToken ct) => RelayToPeerAsync(id, "resources", ct);

    /// <summary><c>GET /api/v1/peers/{id}/capabilities</c> — server-side relay to peer
    /// <paramref name="id"/>'s capability block (admin-gated).</summary>
    [HttpGet("{id}/capabilities")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public Task<IActionResult> PeerCapabilities(string id, CancellationToken ct) => RelayToPeerAsync(id, "capabilities", ct);

    /// <summary><c>GET /api/v1/peers/{id}/library</c> — server-side relay to peer <paramref name="id"/>'s
    /// installable-game catalog (admin-gated).</summary>
    [HttpGet("{id}/library")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public Task<IActionResult> PeerLibrary(string id, CancellationToken ct) => RelayToPeerAsync(id, "library", ct);

    /// <summary>Relay a GET to a peer's <c>self/{leaf}</c> surface and map the result to the frozen envelope.
    /// A down peer degrades to <c>502 peer_unreachable</c>, never a 500 (the P2 honesty rule).</summary>
    private async Task<IActionResult> RelayToPeerAsync(string id, string leaf, CancellationToken ct)
    {
        ClusterRelayResult result = await relay.RelayGetAsync(id, leaf, ct).ConfigureAwait(false);
        return result.Status switch
        {
            ClusterRelayStatus.UnknownNode => NotFound(),
            ClusterRelayStatus.Disabled => Error(StatusCodes.Status403Forbidden, "peer_disabled",
                $"peer '{id}' is disabled on this node"),
            ClusterRelayStatus.Unreachable => Error(StatusCodes.Status502BadGateway, "peer_unreachable",
                $"peer '{id}' is unreachable"),
            // Pass the peer's body + content type through verbatim (reuse the peer's existing DTOs).
            _ => Content(result.Payload ?? "null", result.ContentType ?? "application/json"),
        };
    }

    /// <summary>Fail-closed cluster-token auth + the disable-list gate — the same preamble
    /// <see cref="Inbox"/>/<see cref="Sync"/> run, factored out for the P2 <c>self/*</c> reads. Returns the
    /// authenticated <see cref="ClusterPrincipal"/> on success, else an error result to return as-is. Unlike
    /// <see cref="GetIdentity"/> (token-only, so a not-yet-joined node can still identify itself), a resource
    /// read IS gated: an explicitly-disabled peer gets <c>403 peer_disabled</c>.</summary>
    private async Task<(ClusterPrincipal? principal, IActionResult? error)> AuthenticateEnabledClusterCallerAsync()
    {
        string? token = ExtractBearerToken(Request);
        if (token is null)
            return (null, Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token", "missing bearer token"));

        ClusterPrincipal? principal = await clusterTokens.ValidateAsync(token).ConfigureAwait(false);
        if (principal is null)
            return (null, Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token",
                "invalid, expired, or unsigned cluster service token"));

        if (!await peerGate.IsEnabledAsync(principal.NodeId).ConfigureAwait(false))
            return (null, Error(StatusCodes.Status403Forbidden, "peer_disabled",
                $"node '{principal.NodeId}' is not an enabled peer of this cluster"));

        return (principal, null);
    }

    private static PeerView ToView(PeerEntity p) =>
        new(p.Id, p.Url, p.Nickname, p.NodeId, p.Status,
            GossipState.Display(p.MembershipState, p.LastSeen),
            p.LatencyMs, p.LastSeen, p.ApiVersion, p.Enabled);

    private static ClusterNodeView ToNodeView(PeerEntity p) =>
        new(p.NodeId, p.Nickname ?? p.NodeId, p.Url,
            GossipState.Display(p.MembershipState, p.LastSeen),
            p.Status, p.LatencyMs);

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

    private ObjectResult Error(int statusCode, string code, string message, JsonElement details) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message, details)));
}
