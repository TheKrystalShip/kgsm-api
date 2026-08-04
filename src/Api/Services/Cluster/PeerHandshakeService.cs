using System.Net.Http.Headers;
using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Json;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Why <see cref="PeerHandshakeService.AddPeerAsync"/> did or didn't add a peer — the four outcomes the
/// <c>POST /api/v1/peers</c> wire contract distinguishes (<c>PLAN-peers.md §7</c>), collapsed to one type
/// so the controller has a single switch instead of parsing exceptions.
/// </summary>
public enum PeerAddOutcome
{
    /// <summary>Reachable, cluster-advertising, version-matched — the row was added (<c>201</c>).</summary>
    Added,

    /// <summary>The candidate URL doesn't parse as an absolute <c>http(s)</c> URL (<c>400 invalid_url</c>).</summary>
    InvalidUrl,

    /// <summary>The candidate didn't answer (or errored) within the handshake timeout
    /// (<c>502 peer_unreachable</c>).</summary>
    Unreachable,

    /// <summary>The candidate answered but its <c>/identity</c> capabilities don't include <c>cluster</c>
    /// (<c>422 peer_not_cluster</c>).</summary>
    NotCluster,

    /// <summary>The candidate's <c>apiVersion</c> doesn't match this node's (<c>409 version_mismatch</c>).</summary>
    VersionMismatch,
}

/// <summary>
/// The result of one <see cref="PeerHandshakeService.AddPeerAsync"/> call. <see cref="Peer"/> is populated
/// only on <see cref="PeerAddOutcome.Added"/>; <see cref="RemoteApiVersion"/> is populated only on
/// <see cref="PeerAddOutcome.VersionMismatch"/> (the <c>409</c> response's <c>details.remote</c>).
/// </summary>
public sealed record PeerAddResult(PeerAddOutcome Outcome, PeerEntity? Peer = null, string? RemoteApiVersion = null);

/// <summary>
/// The join-via-seed handshake (<c>PLAN-peers.md §2</c> #6/#11): given an admin-pasted peer URL, pull its
/// <c>GET /api/v1/peers/identity</c> over TLS, validate it (reachable, advertises <c>cluster</c>, a matching
/// <c>apiVersion</c>), and — on success — persist it into the roster via <see cref="PeersStore"/>.
/// Reachability is validated at add time only in this milestone (<c>PLAN-peers.md §6</c>, P0); the later
/// gossip milestone (P0.5) re-validates continuously instead of relying on this one-shot check.
/// </summary>
public sealed class PeerHandshakeService
{
    /// <summary>The named <see cref="HttpClient"/> (via <see cref="IHttpClientFactory"/>) the handshake
    /// pulls a candidate peer's <c>/identity</c> with — registered in <c>Startup</c> with a short timeout (a
    /// hung candidate must not stall the admin's "add peer" request).</summary>
    public const string HttpClientName = "cluster-peer-handshake";

    private static readonly JsonSerializerOptions IdentityJsonOptions = BuildJsonOptions();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PeersStore _peers;
    private readonly IClusterTokenService _clusterTokens;
    private readonly ILogger<PeerHandshakeService> _logger;

    // ApiOptions is accepted but not captured: the version check below compares against the fixed route
    // version ApiInfo.ApiVersion (this node's /api/v1), not anything host-configured. Kept as a ctor
    // parameter so a later slice (e.g. an options-driven handshake timeout) can start using it without
    // a DI signature change.
    public PeerHandshakeService(
        IHttpClientFactory httpClientFactory, PeersStore peers, IClusterTokenService clusterTokens,
        ApiOptions options, ILogger<PeerHandshakeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _peers = peers;
        _clusterTokens = clusterTokens;
        _logger = logger;
    }

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var jsonOptions = new JsonSerializerOptions();
        ApiJson.Configure(jsonOptions);
        return jsonOptions;
    }

    /// <summary>
    /// Validate and add one peer by its advertised URL (the admin "paste a URL" action, <c>POST
    /// /api/v1/peers</c>). Fail-open on reachability (<c>PLAN-peers.md §2</c> #25 — an unreachable
    /// candidate is an honest <see cref="PeerAddOutcome.Unreachable"/>, never a thrown exception) and
    /// fail-closed on the version/capability checks (#26 — a mismatch never gets added "anyway").
    /// </summary>
    public async Task<PeerAddResult> AddPeerAsync(string url, string? nickname, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? candidate)
            || (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps))
        {
            return new PeerAddResult(PeerAddOutcome.InvalidUrl);
        }

        // GET /identity is cluster-token authed on the far side (it's the same handshake endpoint this
        // node itself exposes) — mint OUR OWN service token to present, same as any other node-to-node
        // call. Both sides share Api__ClusterSecret, so a candidate that is genuinely a cluster
        // member of ours will accept it.
        MintedClusterToken token;
        try
        {
            token = _clusterTokens.Mint();
        }
        catch (InvalidOperationException)
        {
            // This node itself isn't cluster-enabled (blank Api__ClusterSecret) — it has no
            // identity to present to anyone, so the handshake cannot proceed. POST /peers is
            // meaningless on a non-cluster node in the first place; collapse to the same honest
            // "couldn't reach it" outcome as any other handshake failure rather than a 500.
            _logger.LogWarning(
                "peer handshake: this node is not cluster-enabled — cannot mint a service token for {Url}", url);
            return new PeerAddResult(PeerAddOutcome.Unreachable);
        }

        PeerIdentityResponse? identity;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(candidate, "/api/v1/peers/identity"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "peer handshake: {Url} answered {Status} on /identity — treating as unreachable",
                    url, (int)response.StatusCode);
                return new PeerAddResult(PeerAddOutcome.Unreachable);
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            identity = await JsonSerializer
                .DeserializeAsync<PeerIdentityResponse>(body, IdentityJsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Connection refused, DNS failure, TLS failure, or a body that isn't valid JSON — all
            // collapse to the one honest "couldn't reach it" outcome (§2 #25 fail-open on reachability).
            _logger.LogInformation(ex, "peer handshake: {Url} is unreachable", url);
            return new PeerAddResult(PeerAddOutcome.Unreachable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The named HttpClient's own 10s timeout fired (mirrors MonitorClient/AssistantClient's probe
            // idiom) — the candidate is just slow/hung, not a caller-cancelled request; report it the
            // same as any other unreachable candidate rather than letting a bare OperationCanceledException
            // propagate out of an otherwise-successful admin request.
            _logger.LogInformation("peer handshake: {Url} timed out on /identity", url);
            return new PeerAddResult(PeerAddOutcome.Unreachable);
        }

        if (identity is null || string.IsNullOrWhiteSpace(identity.NodeId) || string.IsNullOrWhiteSpace(identity.ApiVersion))
            return new PeerAddResult(PeerAddOutcome.Unreachable);

        if (identity.Capabilities is null || !identity.Capabilities.Contains("cluster", StringComparer.Ordinal))
            return new PeerAddResult(PeerAddOutcome.NotCluster);

        if (!string.Equals(identity.ApiVersion, ApiInfo.ApiVersion, StringComparison.Ordinal))
            return new PeerAddResult(PeerAddOutcome.VersionMismatch, RemoteApiVersion: identity.ApiVersion);

        var peer = new PeerEntity
        {
            Id = "peer_" + Guid.NewGuid().ToString("N")[..10],
            Url = url,
            Nickname = nickname,
            NodeId = identity.NodeId,
            Incarnation = 0,
            Status = "reachable",
            LastSeen = DateTimeOffset.UtcNow,
            ApiVersion = identity.ApiVersion,
            Enabled = true,
        };
        await _peers.UpsertAsync(peer, ct).ConfigureAwait(false);
        return new PeerAddResult(PeerAddOutcome.Added, peer);
    }

    /// <summary>The <c>GET /api/v1/peers/identity</c> response shape (<c>PLAN-peers.md §7</c>) — just the
    /// fields the handshake needs, not the full <see cref="PeerEntity"/>.</summary>
    private sealed record PeerIdentityResponse(
        string NodeId, string ApiVersion, string? Build, IReadOnlyList<string>? Capabilities);
}
