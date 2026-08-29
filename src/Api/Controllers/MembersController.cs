using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;
using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Cluster.Membership;
// Disambiguate from Microsoft.Extensions.Hosting.Host (pulled in by ImplicitUsings).
using Host = TheKrystalShip.Api.Contracts.Host;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// What a person does to this host's cluster membership: read the roster, add a member by pasting its URL,
/// disable one, forget one, and read a member's last-observed latency. Plus the two resource reads a
/// capacity fan-out needs — what this node exposes to another member, and the server-side relay onto
/// another member's copy of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The member-to-member protocol is not here.</b> The inbox, the join exchange, the roster sync and the
/// identity card are served by <c>TheKrystalShip.KGSM.Cluster</c>, mapped onto this API's router in
/// <c>Startup</c>. The test for what lives where is who is on the other end: a human or a browser keeps an
/// action here, and only members on both ends moves it. The button that starts a join is a person's, so it
/// stays; the exchange it starts was always member-to-member, so it moved.
/// </para>
/// <para>
/// <b>The <c>self/*</c> reads are the exception that proves the rule.</b> They are member-to-member and they
/// stay, because what they serve — this host's capacity, its capability block, its installable-game catalog
/// — is this API's to answer and no part of the cluster protocol. They authenticate with a member service
/// token like anything else between members, so they carry <see cref="AllowAnonymousAttribute"/> against
/// the user auth scheme and do their own fail-closed check inline. It must work identically whether
/// <c>Api__AuthDisabled</c> is set or not: a member-to-member call is not a browser session, so the user
/// pipeline must neither gate it nor auto-grant it.
/// </para>
/// <para>
/// <b>Deliberately not a class-level <see cref="AllowAnonymousAttribute"/></b> — that wins over every
/// action-level <c>[Authorize]</c> below, because it is resolved at the highest scope it appears, and it
/// would silently expose the admin-gated roster actions. Each action carries its own.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/members")]
public sealed class MembersController(
    IClusterTokenService clusterTokens,
    IClusterMemberGate memberGate,
    MembersStore members,
    MemberHandshakeService handshake,
    SelfIdentityStore selfIdentity,
    ApiOptions options,
    HostAggregator hostAggregator,
    LibraryAggregator library,
    ClusterPeerRelay relay) : ControllerBase
{
    /// <summary><c>GET /api/v1/members</c> — the full roster, enabled and disabled alike.</summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<MemberListResponse> List(CancellationToken ct)
    {
        IReadOnlyList<MemberRow> rows = await members.ListAsync(ct).ConfigureAwait(false);
        return new MemberListResponse([.. rows.Select(ToView)]);
    }

    /// <summary>
    /// <c>GET /api/v1/members/roster</c> — the browser-facing projection that powers "add one, see all" for
    /// somebody who is not an admin. Enabled members only: a disabled row is a management state a viewer
    /// must not see, or be handed a URL for. Every membership state is reported honestly, including the
    /// derived joining; only the enabled filter applies.
    /// </summary>
    [HttpGet("roster")]
    [Authorize(Policy = AuthPolicy.Viewer)]
    public async Task<ClusterMembersResponse> Roster(CancellationToken ct)
    {
        IReadOnlyList<MemberRow> rows = await members.ListEnabledAsync(ct).ConfigureAwait(false);
        return new ClusterMembersResponse([.. rows.Select(ToMemberView)]);
    }

    /// <summary><c>POST /api/v1/members</c> — the admin action that starts a join. The exchange itself is
    /// the cluster package's; this is the button that starts one.</summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> Add([FromBody] MemberAddRequest? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Url))
            return Error(StatusCodes.Status400BadRequest, "invalid_url", "url is required");

        // This request is the one moment this node can learn its own address honestly: an admin reached it
        // here, through a browser, to run an admin action. A node cannot work that address out for itself,
        // and it needs one before it can tell the member it is about to introduce itself to where to call
        // back. The browser's origin is recorded for the same reason — it is the panel's origin, and every
        // member of the cluster will need to answer it.
        await selfIdentity
            .RecordCandidateAsync($"{Request.Scheme}://{Request.Host}", client: true,
                SelfIdentityStore.BrowserObserved, ct)
            .ConfigureAwait(false);
        if (Request.Headers.TryGetValue("Origin", out Microsoft.Extensions.Primitives.StringValues origin)
            && origin.Count > 0 && origin[0] is { Length: > 0 } panel)
        {
            await selfIdentity.RecordPanelOriginAsync(panel, ct).ConfigureAwait(false);
        }

        MemberAddResult result = await handshake
            .AddMemberAsync(body.Url.Trim(), body.Nickname, ct).ConfigureAwait(false);

        if (result.Outcome != MemberAddOutcome.Added)
            return OutcomeError(result.Outcome, result.RemoteApiVersion);

        MemberRow member = result.Member!;
        return StatusCode(StatusCodes.Status201Created, new MemberAddedResponse(
            member.Id, member.Url, member.Nickname, member.MemberId, member.Kind, member.ApiVersion,
            member.Status, member.Enabled));
    }

    /// <summary><c>DELETE /api/v1/members/{id}</c> — forget a roster row entirely, which is a different act
    /// from disabling it.</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
        => await members.DeleteAsync(id, ct).ConfigureAwait(false) ? NoContent() : NotFound();

    /// <summary><c>PATCH /api/v1/members/{id}</c> — the disable toggle. Only the enabled flag is
    /// settable.</summary>
    [HttpPatch("{id}")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> SetEnabled(string id, [FromBody] MemberPatchRequest? body, CancellationToken ct)
    {
        if (body is null)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "enabled is required");

        if (!await members.SetEnabledAsync(id, body.Enabled, ct).ConfigureAwait(false))
            return NotFound();

        MemberRow? row = await members.GetAsync(id, ct).ConfigureAwait(false);
        return row is null ? NotFound() : Ok(ToView(row));
    }

    /// <summary><c>GET /api/v1/members/{id}/latency</c> — the roster row's last-observed liveness
    /// sample.</summary>
    [HttpGet("{id}/latency")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> Latency(string id, CancellationToken ct)
    {
        MemberRow? row = await members.GetAsync(id, ct).ConfigureAwait(false);
        return row is null ? NotFound() : Ok(new MemberLatencyView(row.LatencyMs, row.Status, row.LastSeen));
    }

    // Two distinct reads, deliberately not collapsed into one proxy:
    //   self/*  — what THIS node exposes to another member, so its fan-out can read our capacity,
    //             capabilities and library.
    //   {id}/*  — the server-side relay onto another member's self/*, using a minted service token.
    //             Consumed by a capacity fan-out and by the assistant, never by the SPA, which reads a
    //             member directly.

    /// <summary><c>GET /api/v1/members/self/resources</c> — this node's own capacity for another member's
    /// fan-out. Capacity is honest <see langword="null"/> when no metrics snapshot exists, never
    /// fabricated.</summary>
    [HttpGet("self/resources")]
    [AllowAnonymous]
    public async Task<IActionResult> SelfResources(CancellationToken ct)
    {
        if (await AuthenticateEnabledMemberAsync().ConfigureAwait(false) is { } error) return error;

        Host host = await hostAggregator.GetHostAsync(ct).ConfigureAwait(false);
        return Ok(new ClusterResourcesView(host.Id, host.Label, host.Status, host.CpuPct, host.Mem, host.Disks));
    }

    /// <summary><c>GET /api/v1/members/self/capabilities</c> — this node's capability block, verbatim.</summary>
    [HttpGet("self/capabilities")]
    [AllowAnonymous]
    public async Task<IActionResult> SelfCapabilities(CancellationToken ct)
    {
        if (await AuthenticateEnabledMemberAsync().ConfigureAwait(false) is { } error) return error;

        Host host = await hostAggregator.GetHostAsync(ct).ConfigureAwait(false);
        return Ok(host.Capabilities);
    }

    /// <summary><c>GET /api/v1/members/self/library</c> — this node's installable-game catalog, verbatim.
    /// Cover and hero URLs point at this node's own art endpoints.</summary>
    [HttpGet("self/library")]
    [AllowAnonymous]
    public async Task<IActionResult> SelfLibrary(CancellationToken ct)
    {
        if (await AuthenticateEnabledMemberAsync().ConfigureAwait(false) is { } error) return error;

        string baseUrl = !string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? options.PublicBaseUrl
            : $"{Request.Scheme}://{Request.Host}";
        IReadOnlyList<LibraryEntry> entries = await library.GetLibraryAsync(null, baseUrl, ct).ConfigureAwait(false);
        return Ok(entries);
    }

    /// <summary><c>GET /api/v1/members/{id}/resources</c> — the server-side relay onto a member's own
    /// capacity.</summary>
    [HttpGet("{id}/resources")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public Task<IActionResult> MemberResources(string id, CancellationToken ct) => RelayAsync(id, "resources", ct);

    /// <summary><c>GET /api/v1/members/{id}/capabilities</c> — the relay onto a member's capability
    /// block.</summary>
    [HttpGet("{id}/capabilities")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public Task<IActionResult> MemberCapabilities(string id, CancellationToken ct)
        => RelayAsync(id, "capabilities", ct);

    /// <summary><c>GET /api/v1/members/{id}/library</c> — the relay onto a member's catalog.</summary>
    [HttpGet("{id}/library")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public Task<IActionResult> MemberLibrary(string id, CancellationToken ct) => RelayAsync(id, "library", ct);

    /// <summary>Relay a read to a member's own surface. A member that is down degrades to a <c>502</c>,
    /// never a <c>500</c>: it is a fact about that member, not a failure of this one.</summary>
    private async Task<IActionResult> RelayAsync(string id, string leaf, CancellationToken ct)
    {
        ClusterRelayResult result = await relay.RelayGetAsync(id, leaf, ct).ConfigureAwait(false);
        return result.Status switch
        {
            ClusterRelayStatus.UnknownNode => NotFound(),
            ClusterRelayStatus.Disabled => Error(StatusCodes.Status403Forbidden, "member_disabled",
                $"member '{id}' is disabled on this node"),
            ClusterRelayStatus.Unreachable => Error(StatusCodes.Status502BadGateway, "member_unreachable",
                $"member '{id}' is unreachable"),
            // The member's body and content type pass through verbatim, so its own DTOs stay the contract.
            _ => Content(result.Payload ?? "null", result.ContentType ?? "application/json"),
        };
    }

    /// <summary>Fail-closed member-token auth plus the disable gate, for the <c>self/*</c> reads. Returns an
    /// error result to hand straight back, or null when the caller passed.</summary>
    private async Task<IActionResult?> AuthenticateEnabledMemberAsync()
    {
        string? token = ExtractBearerToken(Request);
        if (token is null)
            return Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token", "missing bearer token");

        ClusterPrincipal? principal = await clusterTokens.ValidateAsync(token).ConfigureAwait(false);
        if (principal is null)
            return Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token",
                "invalid, expired, or unsigned cluster service token");

        if (!await memberGate.IsEnabledAsync(principal.MemberId).ConfigureAwait(false))
            return Error(StatusCodes.Status403Forbidden, "member_disabled",
                $"member '{principal.MemberId}' is not an enabled member of this cluster");

        return null;
    }

    /// <summary>Map one handshake outcome onto its status code and error envelope, in the same words the
    /// cluster package's own refusals use, so an operator sees one vocabulary whichever side refused.</summary>
    private IActionResult OutcomeError(MemberAddOutcome outcome, string? remoteApiVersion) => outcome switch
    {
        MemberAddOutcome.InvalidUrl => Error(StatusCodes.Status400BadRequest, "invalid_url",
            "url is not a valid absolute http(s) address"),

        MemberAddOutcome.IsSelf => Error(StatusCodes.Status409Conflict, "member_is_self",
            "that address answers as this member"),

        MemberAddOutcome.VersionMismatch => Error(StatusCodes.Status409Conflict, "version_mismatch",
            "the two nodes serve different route versions",
            new MemberVersionMismatchDetails(remoteApiVersion ?? "unknown", ApiInfo.ApiVersion)),

        MemberAddOutcome.NotCluster => Error(StatusCodes.Status422UnprocessableEntity, "member_not_cluster",
            "that member takes no part in a cluster"),

        MemberAddOutcome.ProtocolMismatch => Error(StatusCodes.Status409Conflict, "protocol_mismatch",
            "the two members speak different cluster protocol versions — upgrade both to the same build"),

        MemberAddOutcome.InsecureTransport => Error(StatusCodes.Status422UnprocessableEntity, "insecure_transport",
            "a public address must be https — the cluster secret authenticates but does not encrypt"),

        _ => Error(StatusCodes.Status502BadGateway, "member_unreachable", "could not reach that member"),
    };

    private static MemberView ToView(MemberRow m) =>
        new(m.Id, m.Url, m.Nickname, m.MemberId, m.Kind, m.Status,
            GossipState.Display(m.MembershipState, m.LastSeen),
            m.LatencyMs, m.LastSeen, m.ApiVersion, m.Enabled);

    private static ClusterMemberView ToMemberView(MemberRow m) =>
        // A browser gets a browser-reachable address or nothing: handing the SPA one it cannot reach reads
        // as a broken panel rather than as the missing advertisement it is.
        new(m.MemberId, m.Nickname ?? m.MemberId, m.Kind,
            MemberCandidates.ClientUrl(MemberCandidates.Decode(m.Candidates)),
            GossipState.Display(m.MembershipState, m.LastSeen), m.Status, m.LatencyMs);

    /// <summary>Extract a bearer token, or null when absent or not a bearer. These endpoints have no other
    /// credential form.</summary>
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

    private ObjectResult Error(int statusCode, string code, string message, MemberVersionMismatchDetails details) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(
            code, message, JsonSerializer.SerializeToElement(details, DetailsJsonOptions))));

    // The same camelCase convention every other response on this API uses, so a details block reads like
    // the rest of the envelope rather than like a different serializer wrote it.
    private static readonly JsonSerializerOptions DetailsJsonOptions = BuildDetailsJsonOptions();

    private static JsonSerializerOptions BuildDetailsJsonOptions()
    {
        var jsonOptions = new JsonSerializerOptions();
        Json.ApiJson.Configure(jsonOptions);
        return jsonOptions;
    }
}
