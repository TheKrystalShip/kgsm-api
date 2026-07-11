using System.Diagnostics;
using System.Net.Http.Headers;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Polls every enabled peer's identity endpoint on a 10s interval (<c>PLAN-peers.md §4</c>), recording
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
    /// slow/hung peer must not stall the whole 10s tick).</summary>
    public const string HttpClientName = "cluster-peer-latency";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

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

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ClusterEnabled)
        {
            _logger.LogInformation("peer latency poller inert — cluster not configured");
            return;
        }

        _logger.LogInformation("peer latency poller: started (interval={IntervalMs}ms)", PollInterval.TotalMilliseconds);

        using var timer = new PeriodicTimer(PollInterval);
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

    private async Task ProbeAsync(PeerEntity peer, MintedClusterToken token, CancellationToken ct)
    {
        try
        {
            string baseUrl = (peer.GossipUrl ?? peer.Url).TrimEnd('/');
            HttpClient http = _httpClientFactory.CreateClient(HttpClientName);

            long start = Stopwatch.GetTimestamp();
            HttpResponseMessage? response = null;
            string? failure = null;
            try
            {
                // A per-request message (not a header on the shared named client) — probes for every peer
                // run concurrently off the same HttpClient instance, so a default header would race.
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/peers/identity");
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
                failure = ex.Message;
            }

            using (response)
            {
                if (response is not null && response.IsSuccessStatusCode)
                {
                    int latencyMs = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    await _peers.UpdateLivenessAsync(peer.Id, "reachable", latencyMs, DateTimeOffset.UtcNow, ct)
                        .ConfigureAwait(false);
                    if (peer.Status != "reachable")
                    {
                        _logger.LogInformation(
                            "peer {Id} ({NodeId}) is now reachable ({LatencyMs}ms)", peer.Id, peer.NodeId, latencyMs);
                    }
                    return;
                }

                // A non-2xx or a transport failure — never a fabricated latency, and lastSeen stays
                // whatever it already was (a failed probe never advances "last successfully reached").
                string reason = failure ?? $"HTTP {(int)response!.StatusCode}";
                await _peers.UpdateLivenessAsync(peer.Id, "unreachable", null, peer.LastSeen, ct)
                    .ConfigureAwait(false);
                if (peer.Status == "reachable")
                    _logger.LogInformation("peer {Id} ({NodeId}) went unreachable: {Reason}", peer.Id, peer.NodeId, reason);
                else
                    _logger.LogDebug("peer {Id} ({NodeId}) still unreachable: {Reason}", peer.Id, peer.NodeId, reason);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real shutdown
        }
        catch (Exception ex)
        {
            // Any other unexpected failure (including a liveness write itself) must never break the tick
            // for the other peers being probed alongside this one — fail-open, PLAN-peers.md §2 #25.
            _logger.LogDebug(ex, "peer latency probe failed unexpectedly for {Id}", peer.Id);
        }
    }
}
