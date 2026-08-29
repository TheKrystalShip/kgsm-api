using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Json;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Why an introduce exchange did or didn't produce a peer — the outcomes the <c>POST /api/v1/peers</c> and
/// <c>POST /api/v1/peers/introduce</c> wire contracts distinguish (<c>PLAN-peers.md §7</c>), collapsed to
/// one type so both sides have a single switch instead of parsing exceptions.
/// </summary>
public enum PeerAddOutcome
{
    /// <summary>Reachable, cluster-advertising, version-matched — the row was recorded (<c>201</c>).</summary>
    Added,

    /// <summary>The candidate URL doesn't parse as an absolute <c>http(s)</c> URL (<c>400 invalid_url</c>).</summary>
    InvalidUrl,

    /// <summary>The candidate didn't answer (or errored) within the handshake timeout
    /// (<c>502 peer_unreachable</c>).</summary>
    Unreachable,

    /// <summary>The candidate answered but its capabilities don't include <c>cluster</c>
    /// (<c>422 peer_not_cluster</c>).</summary>
    NotCluster,

    /// <summary>The candidate's <c>apiVersion</c> doesn't match this node's (<c>409 version_mismatch</c>).</summary>
    VersionMismatch,

    /// <summary>The candidate is this node (<c>409 peer_is_self</c>) — a node is not its own peer, and a
    /// roster row for self would make the mesh gossip with a mirror.</summary>
    IsSelf,

    /// <summary>An address outside loopback and the private ranges was offered over plaintext
    /// (<c>422 insecure_transport</c>). The cluster secret authenticates but does not encrypt, and a vouch
    /// carries identity and roles.</summary>
    InsecureTransport,
}

/// <summary>
/// The result of one introduce exchange. <see cref="Peer"/> is populated only on
/// <see cref="PeerAddOutcome.Added"/>; <see cref="RemoteApiVersion"/> only on
/// <see cref="PeerAddOutcome.VersionMismatch"/> (the <c>409</c> response's <c>details.remote</c>).
/// </summary>
public sealed record PeerAddResult(PeerAddOutcome Outcome, PeerEntity? Peer = null, string? RemoteApiVersion = null);

/// <summary>
/// The symmetric join handshake (<c>PLAN-peers.md §2</c> #6, P0.6). Both halves live here so that the
/// initiator and the receiver run <em>the same</em> validation over <em>the same</em> record and record the
/// same things: adding B from A leaves the identical cluster state as adding A from B, and simultaneous
/// mutual introduction leaves one roster row per node rather than two.
/// <para>
/// The exchange also carries the addresses. A node cannot determine its own public address, so the URL an
/// operator pasted — the one address a human has stated and this node has just proven answers — is handed
/// to the far side as <c>youAre</c>, and the far side adopts it. That is what lets a node join a cluster
/// with nothing configured but the shared secret.
/// </para>
/// </summary>
public sealed class PeerHandshakeService
{
    /// <summary>The named <see cref="HttpClient"/> (via <see cref="IHttpClientFactory"/>) the handshake
    /// reaches a candidate peer with — registered in <c>Startup</c> with a short timeout (a hung candidate
    /// must not stall the admin's "add peer" request).</summary>
    public const string HttpClientName = "cluster-peer-handshake";

    /// <summary>Provenance of an address a human pasted into a panel and a peer then proved answers — the
    /// strongest address statement in the system.</summary>
    public const string OperatorProvenance = "operator";

    /// <summary>Provenance of a source address a node saw a request arrive from: a socket address, not a
    /// URL, so it seeds a candidate and nothing depends on it.</summary>
    public const string ObservedProvenance = "peer-observed";

    private static readonly JsonSerializerOptions ExchangeJsonOptions = BuildJsonOptions();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PeersStore _peers;
    private readonly SelfIdentityStore _selfIdentity;
    private readonly INodeCardSource _cards;
    private readonly IClusterTokenService _clusterTokens;
    private readonly ApiOptions _options;
    private readonly ILogger<PeerHandshakeService> _logger;

    public PeerHandshakeService(
        IHttpClientFactory httpClientFactory, PeersStore peers, SelfIdentityStore selfIdentity,
        INodeCardSource cards, IClusterTokenService clusterTokens, ApiOptions options,
        ILogger<PeerHandshakeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _peers = peers;
        _selfIdentity = selfIdentity;
        _cards = cards;
        _clusterTokens = clusterTokens;
        _options = options;
        _logger = logger;
    }

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var jsonOptions = new JsonSerializerOptions();
        ApiJson.Configure(jsonOptions);
        return jsonOptions;
    }

    /// <summary>This node's own card: who it is, what it runs, and every address it knows it answers at.</summary>
    public Task<NodeCard> BuildCardAsync(CancellationToken ct) => _cards.BuildAsync(ct);

    /// <summary>
    /// The one predicate both sides run over the other's card. Keeping it a single function is what makes
    /// the handshake symmetric in fact rather than by convention: there is no second implementation to
    /// drift out of step with this one.
    /// </summary>
    public PeerAddOutcome Validate(NodeCard? card)
    {
        if (card is null || string.IsNullOrWhiteSpace(card.NodeId) || string.IsNullOrWhiteSpace(card.ApiVersion))
            return PeerAddOutcome.Unreachable;

        if (string.Equals(card.NodeId, _options.NodeId, StringComparison.Ordinal))
            return PeerAddOutcome.IsSelf;

        if (card.Capabilities is null || !card.Capabilities.Contains("cluster", StringComparer.Ordinal))
            return PeerAddOutcome.NotCluster;

        if (!string.Equals(card.ApiVersion, ApiInfo.ApiVersion, StringComparison.Ordinal))
            return PeerAddOutcome.VersionMismatch;

        foreach (NodeCandidate candidate in card.Candidates ?? [])
        {
            if (!IsTransportAcceptable(candidate.Url))
                return PeerAddOutcome.InsecureTransport;
        }

        return PeerAddOutcome.Added;
    }

    /// <summary>
    /// Whether an address may be spoken to. Plaintext is fine inside a machine or a private network, where
    /// the operator already controls the wire; across anything else the cluster secret authenticates but
    /// does not encrypt, and a vouch carries a person's identity and roles.
    /// </summary>
    public static bool IsTransportAcceptable(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        if (uri.Scheme != Uri.UriSchemeHttp) return false;
        return IsLocalOrPrivate(uri.Host);
    }

    private static readonly string[] LocalSuffixes = [".local", ".lan", ".internal", ".home.arpa"];

    private static bool IsLocalOrPrivate(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;

        if (!IPAddress.TryParse(host, out IPAddress? ip))
        {
            // A name rather than an address. A single label resolves only inside a local search domain — a
            // name reachable from the public internet always carries a dot — and the private-use suffixes
            // are local by definition. Anything else is treated as public and must come over TLS; this is
            // decided without a DNS lookup, so a validation predicate never waits on a resolver.
            if (!host.Contains('.')) return true;
            return LocalSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] switch
            {
                10 => true,
                127 => true,
                172 => b[1] >= 16 && b[1] <= 31,
                192 => b[1] == 168,
                169 => b[1] == 254,
                _ => false,
            };
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
            || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;   // fc00::/7 unique-local
    }

    /// <summary>
    /// The initiator half: the admin pasted <paramref name="url"/>, so introduce ourselves to it and record
    /// what comes back. Fail-open on reachability (an unreachable candidate is an honest
    /// <see cref="PeerAddOutcome.Unreachable"/>, never a thrown exception) and fail-closed on the identity
    /// checks (a mismatch never gets added anyway).
    /// </summary>
    public async Task<PeerAddResult> AddPeerAsync(string url, string? nickname, CancellationToken ct)
    {
        string? target = SelfIdentityStore.Normalize(url);
        if (target is null)
            return new PeerAddResult(PeerAddOutcome.InvalidUrl);

        if (!IsTransportAcceptable(target))
            return new PeerAddResult(PeerAddOutcome.InsecureTransport);

        MintedClusterToken token;
        try
        {
            token = _clusterTokens.Mint();
        }
        catch (InvalidOperationException)
        {
            // This node itself isn't cluster-enabled (blank Api__ClusterSecret) — it has no identity to
            // present to anyone, so the handshake cannot proceed. POST /peers is meaningless on a
            // non-cluster node in the first place; collapse to the same honest "couldn't reach it" outcome
            // as any other handshake failure rather than a 500.
            _logger.LogWarning(
                "peer handshake: this node is not cluster-enabled — cannot mint a service token for {Url}", url);
            return new PeerAddResult(PeerAddOutcome.Unreachable);
        }

        var outgoing = new IntroduceExchange(
            await BuildCardAsync(ct).ConfigureAwait(false),
            // The address a human wrote down, which this request is about to prove answers. It is the one
            // thing the far side cannot work out for itself, and the reason it needs no configuration.
            new ReflectedAddress(target, OperatorProvenance),
            await _selfIdentity.PanelOriginsAsync(ct).ConfigureAwait(false));

        IntroduceExchange? incoming;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{target}/api/v1/peers/introduce")
            {
                Content = JsonContent.Create(outgoing, options: ExchangeJsonOptions),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The far side ran the same predicate and refused. Its verdict is the admin's answer, so it
                // is reported as itself rather than flattened into "unreachable".
                PeerAddOutcome refusal = await RefusalOutcomeAsync(response, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "peer handshake: {Url} answered {Status} on /introduce ({Outcome})",
                    url, (int)response.StatusCode, refusal);
                return new PeerAddResult(refusal);
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            incoming = await JsonSerializer
                .DeserializeAsync<IntroduceExchange>(body, ExchangeJsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Connection refused, DNS failure, TLS failure, or a body that isn't valid JSON — all collapse
            // to the one honest "couldn't reach it" outcome.
            _logger.LogInformation(ex, "peer handshake: {Url} is unreachable", url);
            return new PeerAddResult(PeerAddOutcome.Unreachable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The named HttpClient's own timeout fired — the candidate is slow or hung, not a
            // caller-cancelled request.
            _logger.LogInformation("peer handshake: {Url} timed out on /introduce", url);
            return new PeerAddResult(PeerAddOutcome.Unreachable);
        }

        if (incoming is null)
            return new PeerAddResult(PeerAddOutcome.Unreachable);

        PeerAddOutcome verdict = Validate(incoming.Self);
        if (verdict != PeerAddOutcome.Added)
            return new PeerAddResult(verdict, RemoteApiVersion: incoming.Self?.ApiVersion);

        PeerEntity peer = await RecordAsync(incoming, target, nickname, ct).ConfigureAwait(false);
        return new PeerAddResult(PeerAddOutcome.Added, peer);
    }

    /// <summary>
    /// The receiver half: a peer has introduced itself. Runs the same predicate over its card, records the
    /// mirror of what it recorded about us, and answers with our own card so one round trip leaves both
    /// sides equally informed.
    /// </summary>
    /// <param name="incoming">The caller's exchange record.</param>
    /// <param name="observedAddress">The source address this request arrived from, when one could be
    /// determined — a hint, reported back honestly labelled, never treated as a URL.</param>
    public async Task<(PeerAddOutcome Outcome, IntroduceExchange? Answer)> ReceiveAsync(
        IntroduceExchange? incoming, string? observedAddress, CancellationToken ct)
    {
        if (incoming is null)
            return (PeerAddOutcome.Unreachable, null);

        PeerAddOutcome verdict = Validate(incoming.Self);
        if (verdict != PeerAddOutcome.Added)
            return (verdict, null);

        // What the caller says about us. It reached this node at that address, so the claim is worth more
        // than anything this node could infer about itself.
        if (incoming.YouAre is { } reflection && SelfIdentityStore.Normalize(reflection.Url) is { } mine)
        {
            await _selfIdentity
                .RecordCandidateAsync(mine, client: true, OperatorProvenance, ct)
                .ConfigureAwait(false);
        }

        await AdoptPanelOriginsAsync(incoming.PanelOrigins, ct).ConfigureAwait(false);

        // The caller's own candidates are all we have to reach it by — it named no address for itself that
        // we can verify yet, so the row starts unverified and the poller settles it.
        await RecordAsync(incoming, address: null, nickname: null, ct).ConfigureAwait(false);

        var answer = new IntroduceExchange(
            await BuildCardAsync(ct).ConfigureAwait(false),
            observedAddress is null ? null : new ReflectedAddress(observedAddress, ObservedProvenance),
            await _selfIdentity.PanelOriginsAsync(ct).ConfigureAwait(false));

        return (PeerAddOutcome.Added, answer);
    }

    /// <summary>
    /// Write (or refresh) the roster row for the node in <paramref name="exchange"/>, keyed on its node id
    /// so a simultaneous introduction from both directions converges on one row instead of two.
    /// <paramref name="address"/> is the operator-pasted URL when this node initiated — proven reachable by
    /// the exchange that just succeeded, so it leads the candidate list.
    /// </summary>
    private async Task<PeerEntity> RecordAsync(
        IntroduceExchange exchange, string? address, string? nickname, CancellationToken ct)
    {
        NodeCard card = exchange.Self;
        List<NodeCandidate> candidates = address is null
            ? [.. card.Candidates ?? []]
            : [new NodeCandidate(address, Client: true), .. card.Candidates ?? []];

        DateTimeOffset now = DateTimeOffset.UtcNow;

        return await _peers.UpsertByNodeIdAsync(card.NodeId, existing => Build(existing), ct)
            .ConfigureAwait(false);

        PeerEntity Build(PeerEntity? existing)
        {
            var peer = new PeerEntity
            {
                Id = existing?.Id ?? "peer_" + Guid.NewGuid().ToString("N")[..10],
                NodeId = card.NodeId,
                Nickname = nickname ?? existing?.Nickname,
                Candidates = PeerCandidates.Merge(existing?.Candidates, candidates),
                Incarnation = Math.Max(card.Incarnation, existing?.Incarnation ?? 0),
                MembershipState = existing?.MembershipState ?? GossipState.Alive,
                StateChangedAt = existing?.StateChangedAt ?? now,
                // An address the far side answered on is proven for THIS exchange; anything it merely
                // listed is a claim the poller settles. Reaching it as part of the handshake is first-hand
                // evidence, so status and lastSeen are honest here — the address flag is not, until a probe
                // confirms the node id behind it.
                Status = address is null ? existing?.Status ?? "unknown" : "reachable",
                LatencyMs = existing?.LatencyMs,
                LastSeen = address is null ? existing?.LastSeen : now,
                AddressVerified = existing?.AddressVerified ?? false,
                ApiVersion = card.ApiVersion,
                Enabled = existing?.Enabled ?? true,
            };
            peer.Url = address ?? (existing is { AddressVerified: true, Url.Length: > 0 }
                ? existing.Url
                : PeerCandidates.Best(PeerCandidates.Decode(peer.Candidates)));
            return peer;
        }
    }

    /// <summary>Merge the panel origins a peer knows into ours. The shared secret is the trust boundary
    /// (§2 #7), so a peer's origins are as good as our own — and without the merge a person signing in
    /// through one node could not reach the others from the same panel.</summary>
    private async Task AdoptPanelOriginsAsync(IReadOnlyList<string>? origins, CancellationToken ct)
    {
        foreach (string origin in origins ?? [])
            await _selfIdentity.RecordPanelOriginAsync(origin, ct).ConfigureAwait(false);
    }

    /// <summary>Map the far side's refusal back onto the outcome it named, so the admin sees the reason the
    /// other node gave rather than a generic failure. An unreadable body degrades to unreachable.</summary>
    private static async Task<PeerAddOutcome> RefusalOutcomeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string code;
        try
        {
            using JsonDocument document = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
                .ConfigureAwait(false);
            code = document.RootElement.TryGetProperty("error", out JsonElement error)
                   && error.TryGetProperty("code", out JsonElement value)
                ? value.GetString() ?? ""
                : "";
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
            return PeerAddOutcome.Unreachable;
        }

        return code switch
        {
            "peer_is_self" => PeerAddOutcome.IsSelf,
            "version_mismatch" => PeerAddOutcome.VersionMismatch,
            "peer_not_cluster" => PeerAddOutcome.NotCluster,
            "insecure_transport" => PeerAddOutcome.InsecureTransport,
            _ => PeerAddOutcome.Unreachable,
        };
    }
}
