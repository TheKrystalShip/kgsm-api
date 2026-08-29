using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Json;

using TheKrystalShip.KGSM.Cluster.Identity;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Polls every enabled peer's identity endpoint on the <see cref="ApiOptions.ClusterPollMs"/> interval
/// (<c>PLAN-peers.md §4</c>, default 10s), recording
/// <c>latencyMs</c>/<c>status</c> via <see cref="PeersStore.UpdateLivenessAsync"/>. No response within
/// timeout (or a non-2xx) ⇒ <c>"unreachable"</c> with a null latency and an untouched <c>lastSeen</c> —
/// never a fabricated value; a disabled peer is never pinged (<see cref="PeersStore.ListEnabledAsync"/>
/// already excludes it). Doubles as the message bus's liveness signal (an <c>unreachable → reachable</c>
/// flip should trigger an immediate outbox flush toward that peer — the bus spec's concern, not this
/// poller's).
/// </summary>
/// <remarks>
/// Inert (early return, no timer at all) when <see cref="ApiOptions.ClusterEnabled"/> is false, mirroring
/// <see cref="OutboxDrainer"/>/<see cref="ClusterBusGcWorker"/>. Each tick probes every enabled peer
/// concurrently; each peer's probe is isolated in its own try/catch (fail-open, <c>PLAN-peers.md §2</c>
/// #25 — a dead/slow peer degrades to an honest "unknown," never a thrown exception or a stalled tick for
/// its siblings) and the tick body itself is wrapped too, so the loop is unkillable short of cancellation.
/// </remarks>
public sealed class PeerLatencyPoller : BackgroundService
{
    /// <summary>The named <see cref="HttpClient"/> (via <see cref="IHttpClientFactory"/>) this poller calls
    /// each peer's identity endpoint with — registered in <c>Startup</c> with a short timeout (one
    /// slow/hung peer must not stall the whole tick).</summary>
    public const string HttpClientName = "cluster-peer-latency";

    private static readonly JsonSerializerOptions IdentityJsonOptions = BuildJsonOptions();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PeersStore _peers;
    private readonly IClusterTokenService _tokens;
    private readonly ApiOptions _options;
    private readonly ILogger<PeerLatencyPoller> _logger;

    public PeerLatencyPoller(
        IHttpClientFactory httpClientFactory, PeersStore peers, IClusterTokenService tokens, ApiOptions options,
        ILogger<PeerLatencyPoller> logger)
    {
        _httpClientFactory = httpClientFactory;
        _peers = peers;
        _tokens = tokens;
        _options = options;
        _logger = logger;
    }

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var options = new JsonSerializerOptions();
        ApiJson.Configure(options);
        return options;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ClusterEnabled)
        {
            _logger.LogInformation("peer latency poller inert — cluster not configured");
            return;
        }

        _logger.LogInformation("peer latency poller: started (interval={IntervalMs}ms)", _options.ClusterPollMs);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.ClusterPollMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunTickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "peer latency poller: tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    // One tick: fetch the enabled roster, mint a single cluster service token for it (the identity endpoint
    // is cluster-token authed — its ~60s TTL comfortably outlives the 10s cadence, so one mint covers every
    // peer this tick), then probe every peer concurrently. Concurrent is safe here — each probe is bounded
    // by the named HttpClient's own timeout and isolated in its own try/catch, and the roster size this
    // poller targets (a handful to a few dozen peers) is nowhere near a fan-out concern.
    private async Task RunTickAsync(CancellationToken ct)
    {
        IReadOnlyList<PeerEntity> peers = await _peers.ListEnabledAsync(ct).ConfigureAwait(false);
        if (peers.Count == 0)
            return;

        MintedClusterToken token = _tokens.Mint();
        await Task.WhenAll(peers.Select(peer => ProbeAsync(peer, token, ct))).ConfigureAwait(false);
    }

    /// <summary>
    /// One probe round for one peer: walk its candidate addresses in order (the pinned one first) and stop
    /// at the first that answers <c>/identity</c> as this peer. The winner is pinned, so the next round —
    /// and every node-to-node call in between — goes straight there. Reachability is a property of a pair,
    /// so the address this node pins is its own answer and need not match what another peer pinned.
    /// </summary>
    private async Task ProbeAsync(PeerEntity peer, MintedClusterToken token, CancellationToken ct)
    {
        try
        {
            HttpClient http = _httpClientFactory.CreateClient(HttpClientName);
            string? lastFailure = null;

            foreach (string address in AddressesFor(peer))
            {
                (int? latencyMs, NodeCard? identity, string? failure) =
                    await TryAddressAsync(http, address, token, ct).ConfigureAwait(false);

                if (latencyMs is null)
                {
                    lastFailure = failure;
                    continue;
                }

                bool pinned = string.Equals(address, peer.Url, StringComparison.OrdinalIgnoreCase);
                if (!pinned)
                {
                    _logger.LogInformation(
                        "peer {NodeId} answers at {Address} — pinning it", peer.NodeId, address);
                }

                await _peers.UpdateLivenessAsync(peer.Id, "reachable", latencyMs.Value, DateTimeOffset.UtcNow, ct)
                    .ConfigureAwait(false);
                if (peer.Status != "reachable")
                {
                    _logger.LogInformation(
                        "peer {Id} ({NodeId}) is now reachable ({LatencyMs}ms)", peer.Id, peer.NodeId, latencyMs);
                }

                // First-hand authentication (PLAN-peers.md §2·b, G3): a peer we just reached directly is
                // promoted to alive only once its own /identity confirms it is a real cluster member, at our
                // route version, under the node id this row is keyed on — direct reachability alone is not
                // membership proof, and an address that answers as somebody else is not this peer's address.
                // A body we cannot parse still counts as reachable; it just isn't vouched for.
                bool authentic = identity is not null
                    && string.Equals(identity.NodeId, peer.NodeId, StringComparison.Ordinal)
                    && identity.Capabilities?.Contains("cluster") == true
                    && string.Equals(identity.ApiVersion, ApiInfo.ApiVersion, StringComparison.Ordinal);

                if (authentic)
                {
                    // An address only becomes this peer's address once it has answered under this peer's
                    // node id (§2 #13c) — until then it is a claim the roster carries unverified.
                    await _peers.PinAddressAsync(peer.Id, address, identity!.Candidates, ct).ConfigureAwait(false);

                    bool wasAlive = peer.MembershipState == GossipState.Alive;
                    await _peers.PromoteAliveAsync(peer.Id, identity.ApiVersion, DateTimeOffset.UtcNow, ct)
                        .ConfigureAwait(false);
                    if (!wasAlive)
                    {
                        _logger.LogInformation(
                            "peer {NodeId} promoted alive (first-hand authenticated)", peer.NodeId);
                    }
                }
                else if (identity is not null)
                {
                    _logger.LogDebug(
                        "peer {NodeId} reached at {Address} but not a matching cluster member: id={Id} caps={Caps} version={Version}",
                        peer.NodeId, address, identity.NodeId,
                        identity.Capabilities is null ? "(none)" : string.Join(",", identity.Capabilities),
                        identity.ApiVersion);
                }

                return;
            }

            // Every candidate failed — never a fabricated latency, and lastSeen stays whatever it already
            // was (a failed probe never advances "last successfully reached").
            string reason = lastFailure ?? "no address to try";
            await _peers.UpdateLivenessAsync(peer.Id, "unreachable", null, peer.LastSeen, ct).ConfigureAwait(false);
            if (peer.Status == "reachable")
                _logger.LogInformation("peer {Id} ({NodeId}) went unreachable: {Reason}", peer.Id, peer.NodeId, reason);
            else
                _logger.LogDebug("peer {Id} ({NodeId}) still unreachable: {Reason}", peer.Id, peer.NodeId, reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "peer {Id} ({NodeId}) probe failed unexpectedly", peer.Id, peer.NodeId);
        }
    }

    /// <summary>The addresses to try, in order: the pinned one first (it worked last time, or it is the
    /// most-trusted thing on offer), then every other candidate the peer has advertised.</summary>
    private static IEnumerable<string> AddressesFor(PeerEntity peer)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(peer.Url) && seen.Add(peer.Url.TrimEnd('/')))
            yield return peer.Url.TrimEnd('/');

        foreach (NodeCandidate candidate in PeerCandidates.Decode(peer.Candidates))
        {
            string url = candidate.Url.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(url) && seen.Add(url))
                yield return url;
        }
    }

    /// <summary>One <c>GET /identity</c> against one address. Returns the measured latency and the parsed
    /// card on success, or the transport/status failure that explains why not.</summary>
    private async Task<(int? LatencyMs, NodeCard? Identity, string? Failure)> TryAddressAsync(
        HttpClient http, string address, MintedClusterToken token, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        HttpResponseMessage? response = null;
        try
        {
            // A per-request message (not a header on the shared named client) — probes for every peer run
            // concurrently off the same HttpClient instance, so a default header would race.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{address}/api/v1/peers/identity");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real shutdown — propagate, don't treat this as a probe failure
        }
        catch (Exception ex)
        {
            // Connect refused, DNS failure, the client's own request timeout — all transport-level
            // failures, all honestly "unreachable."
            return (null, null, ex.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return (null, null, $"HTTP {(int)response.StatusCode}");

            int latencyMs = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            try
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                NodeCard? card = await JsonSerializer
                    .DeserializeAsync<NodeCard>(body, IdentityJsonOptions, ct)
                    .ConfigureAwait(false);
                return (latencyMs, card, null);
            }
            catch (JsonException)
            {
                _logger.LogDebug("identity body from {Address} did not parse — reachable, not promoted", address);
                return (latencyMs, null, null);
            }
        }
    }
}
