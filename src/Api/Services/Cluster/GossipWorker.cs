using System.Net.Http.Headers;
using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Json;

using TheKrystalShip.KGSM.Cluster.Identity;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The masterless anti-entropy gossip loop (<c>PLAN-peers.md §2·b</c>, G1 / P0.5): each interval, advance the
/// failure timers, then pick ONE random enabled, non-terminal peer and run a push-pull roster sync with it
/// (<c>POST {peer}/api/v1/peers/sync</c>) — O(1) work per node per round, O(log n) rounds to converge. The
/// EPHEMERAL transport (G4): a sync is a plain best-effort round-trip, never an outbox row, never retried to
/// a corpse — the durable bus stays for guaranteed messages only.
/// </summary>
/// <remarks>
/// Inert (no timer at all) when <see cref="ApiOptions.ClusterEnabled"/> is false, mirroring
/// <see cref="OutboxDrainer"/>/<see cref="PeerLatencyPoller"/>. Every round is isolated in its own try/catch
/// (fail-open — a dead/slow sync partner degrades to nothing, never a thrown tick), and outbound sync auth is
/// a freshly-minted cluster service token, same as every other node-to-node call.
/// </remarks>
public sealed class GossipWorker : BackgroundService
{
    /// <summary>The named <see cref="HttpClient"/> the outbound push-pull sync uses — a short per-request
    /// timeout so one hung partner never stalls a round.</summary>
    public const string HttpClientName = "cluster-peer-gossip";

    private static readonly JsonSerializerOptions SyncJsonOptions = BuildJsonOptions();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GossipService _gossip;
    private readonly PeersStore _peers;
    private readonly IClusterTokenService _tokens;
    private readonly ApiOptions _options;
    private readonly ILogger<GossipWorker> _logger;

    public GossipWorker(
        IHttpClientFactory httpClientFactory, GossipService gossip, PeersStore peers,
        IClusterTokenService tokens, ApiOptions options, ILogger<GossipWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _gossip = gossip;
        _peers = peers;
        _tokens = tokens;
        _options = options;
        _logger = logger;
    }

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var jsonOptions = new JsonSerializerOptions();
        ApiJson.Configure(jsonOptions);
        return jsonOptions;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ClusterEnabled)
        {
            _logger.LogInformation("gossip worker inert — cluster not configured");
            return;
        }

        _logger.LogInformation(
            "gossip worker: started (interval={IntervalMs}ms)", _options.ClusterGossipMs);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.ClusterGossipMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunRoundAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "gossip worker: round failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    // One round: advance the failure timers (even if there's no sync partner this round), then push-pull
    // with ONE random enabled, non-terminal peer. Ephemeral and fail-open (PLAN-peers.md §2 #25) — a
    // dead/slow partner degrades to nothing, never a retry or a durable record.
    private async Task RunRoundAsync(CancellationToken ct)
    {
        await _gossip.AdvanceFailureTimersAsync(ct).ConfigureAwait(false);

        IReadOnlyList<PeerEntity> enabled = await _peers.ListEnabledAsync(ct).ConfigureAwait(false);
        List<PeerEntity> candidates = enabled.Where(p => !GossipState.IsTerminal(p.MembershipState)).ToList();
        if (candidates.Count == 0)
            return;

        PeerEntity partner = candidates[Random.Shared.Next(candidates.Count)];

        MintedClusterToken token;
        try
        {
            token = _tokens.Mint();
        }
        catch (InvalidOperationException)
        {
            // Unreachable in practice — ExecuteAsync already returned early when !ClusterEnabled — but
            // fail closed rather than let a mint failure escape the tick's own try/catch as something worse.
            return;
        }

        IReadOnlyList<SyncMember> members = await _gossip.BuildLocalRosterAsync(ct).ConfigureAwait(false);

        string baseUrl = partner.Url.TrimEnd('/');
        HttpClient http = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/peers/sync");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Content = JsonContent.Create(new SyncRequest(_options.NodeId, members), options: SyncJsonOptions);

            response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "gossip sync with {NodeId} failed: HTTP {Status}", partner.NodeId, (int)response.StatusCode);
                return;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            SyncResponse? sync = await JsonSerializer
                .DeserializeAsync<SyncResponse>(body, SyncJsonOptions, ct)
                .ConfigureAwait(false);
            if (sync?.Members is not null)
                await _gossip.MergeIncomingAsync(sync.Members, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real shutdown — propagate, don't treat this as a sync failure
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            // Connect refused, DNS failure, the client's own request timeout, a malformed body — all
            // collapse to the one honest "sync didn't happen this round" outcome. No retry, no outbox row.
            _logger.LogDebug(ex, "gossip sync with {NodeId} failed", partner.NodeId);
        }
        finally
        {
            response?.Dispose();
        }
    }
}
