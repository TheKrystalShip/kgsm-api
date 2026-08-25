using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The read seam for the monitor's metrics-history endpoint. The monitor is the single source of
/// truth for metrics history; the API relays its <c>GET /metrics/history</c> JSON verbatim, so this
/// returns the raw response body (never a re-serialized DTO). An interface so the history-proxy
/// controller can be tested against a fake monitor response in-process.
/// </summary>
public interface IMonitorHistoryClient
{
    /// <summary>Fetch the monitor's history JSON for an entity+range, verbatim. Returns <c>null</c>
    /// when the monitor is unprovisioned, unreachable, slow, or answers non-2xx (honest degrade —
    /// the caller then serves an empty response, never a fabricated curve).</summary>
    Task<string?> GetHistoryJsonAsync(string kind, string id, string? range, CancellationToken ct);

    /// <summary>Fetch the monitor's range summary (<c>GET /metrics/history/summary</c>) verbatim — one
    /// aggregate per entity of a kind, for a surface drawing many ranges at once. Returns <c>null</c> on
    /// the same terms as the history relay.</summary>
    Task<string?> GetHistorySummaryJsonAsync(string kind, string? range, CancellationToken ct);

    /// <summary>Fetch the monitor's own self-report (<c>GET /stats</c>) verbatim — what it is sampling
    /// and what its history store actually holds. Returns <c>null</c> on the same terms as the history
    /// relay (unprovisioned, unreachable, slow, non-2xx): the caller reports that it could not be read,
    /// which is a different statement from a monitor that answered with nothing recorded.</summary>
    Task<string?> GetStatsJsonAsync(CancellationToken ct);

    /// <summary>Fetch the monitor's threshold policy (<c>GET /thresholds</c>) verbatim. Returns
    /// <c>null</c> on the same terms as the other relays — the caller then reports that the policy could
    /// not be read, which is a different statement from a host that watches nothing.</summary>
    Task<string?> GetThresholdsJsonAsync(CancellationToken ct);

    /// <summary>Apply a threshold policy (<c>PUT /thresholds</c>), relaying <paramref name="json"/>
    /// verbatim and returning the monitor's own status and body. Unlike the read relays this does NOT
    /// collapse a failure to null: an operator changing a policy has to be told whether it was refused
    /// (and why) or simply could not be reached, and those are different answers.</summary>
    Task<LeafRelayResponse> PutThresholdsAsync(string json, CancellationToken ct);

    /// <summary>Drop the applied policy (<c>DELETE /thresholds</c>), returning this host to the monitor's
    /// built-in defaults. Same reporting contract as <see cref="PutThresholdsAsync"/>.</summary>
    Task<LeafRelayResponse> DeleteThresholdsAsync(CancellationToken ct);

    /// <summary>The monitor's record of what fired (<c>GET /thresholds/episodes</c>), verbatim. Returns
    /// <c>null</c> on the same terms as the other read relays; the caller must not read that as "nothing
    /// fired", which is what an empty list means.</summary>
    Task<string?> GetEpisodesJsonAsync(long sinceMs, int limit, CancellationToken ct);
}

/// <summary>
/// What a leaf said when this API relayed a write to it: its status code and its body, both verbatim.
/// <paramref name="Reached"/> separates "the leaf answered, with this" from "there was nothing to ask" —
/// a distinction a status code alone cannot carry, and one an operator needs, because a refused policy is
/// theirs to fix and an unreachable monitor is not.
/// </summary>
public readonly record struct LeafRelayResponse(bool Reached, int StatusCode, string? Body)
{
    public static LeafRelayResponse Unreachable => new(false, 0, null);

    public bool IsSuccess => Reached && StatusCode is >= 200 and < 300;
}

/// <summary>
/// The kgsm-monitor leaf client: scrapes <c>GET /metrics</c> over the monitor's unix-domain
/// socket and serves a <strong>cached-latest</strong> <see cref="Snapshot"/>. The HTTP-over-unix
/// transport reuses the same <see cref="SocketsHttpHandler.ConnectCallback"/> pattern as
/// kgsm-lib's watchdog client; the snapshot is deserialized with the monitor's own shared
/// <see cref="MonitorJsonContext"/>, so producer and consumer share one build-time contract. It is
/// also the read seam for the monitor's metrics-history endpoint (<see cref="IMonitorHistoryClient"/>),
/// which the API relays verbatim.
/// </summary>
/// <remarks>
/// Honesty: a failed, timed-out, or not-yet-ready (503) scrape yields <c>null</c> — the caller
/// then reports the metrics capability <c>down</c> and nulls host capacity. M1·a does NOT serve
/// stale last-good data (that "last values hold" behavior belongs to the M2 stream); the cache
/// only conflates rapid requests within a short TTL.
/// </remarks>
public sealed class MonitorClient : IMonitorHistoryClient, IDisposable
{
    // The monitor self-ticks (~1s) and serves its latest in-memory frame, so a short api-side
    // cache bounds socket round-trips without adding meaningful staleness.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(1);

    // A scrape returns an in-memory frame and must be fast; bound it so a hung socket can never
    // stall a /hosts request.
    private static readonly TimeSpan ScrapeTimeout = TimeSpan.FromSeconds(2);

    // Where the monitor's metrics socket lives on a standard install — used to build the transport when no
    // explicit path is configured, so a runtime "connect monitor" works against the standard socket.
    private const string DefaultSocketPath = "/run/kgsm-monitor/metrics.sock";

    private readonly ILogger<MonitorClient> _logger;
    private readonly LeafRegistry _registry;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private Snapshot? _cached;
    private long _lastFetchTicks;
    private bool _hasFetched;

    public MonitorClient(ApiOptions options, LeafRegistry registry, ILogger<MonitorClient> logger)
    {
        _logger = logger;
        _registry = registry;

        // ALWAYS build the transport (from the configured-or-default socket) so flipping the registry arms
        // probing/scraping live without a restart; the CALL-time registry gate decides whether to use it.
        string socketPath = string.IsNullOrWhiteSpace(options.MonitorSocketPath)
            ? DefaultSocketPath
            : options.MonitorSocketPath;
        var handler = new SocketsHttpHandler
        {
            // Every connection is dialed over the unix-domain socket; the request URI host is
            // a placeholder the monitor ignores.
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = ScrapeTimeout,
        };
    }

    /// <summary>
    /// The latest host snapshot, cached for <see cref="CacheTtl"/>. Returns <c>null</c> when the
    /// metrics capability is unprovisioned, the monitor is unreachable/slow, or it has not yet
    /// produced a frame (HTTP 503).
    /// </summary>
    public async Task<Snapshot?> GetLatestAsync(CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Monitor))
            return null; // disconnected at runtime: honest absent, no scrape.

        if (IsFresh())
            return _cached;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh()) // recheck: another caller may have refreshed while we waited.
                return _cached;

            _cached = await ScrapeAsync(ct).ConfigureAwait(false);
            _lastFetchTicks = Environment.TickCount64;
            _hasFetched = true;
            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Liveness probe for the metrics capability: <c>GET /health</c> over the same unix socket. A 2xx
    /// means the monitor process is up and serving — the canonical "is this leaf able to provide its
    /// capability" signal (polled frequently by <c>LeafHealthMonitor</c>), deliberately decoupled from
    /// whether <c>/metrics</c> has produced a frame yet (a warming monitor is operational with no data,
    /// not down). Returns <c>false</c> on unprovisioned, unreachable, slow, or non-2xx — never throws.
    /// </summary>
    /// <remarks>
    /// Targets the ecosystem-standard <c>/health</c> path (uniform across leaves), which the monitor
    /// now serves (unified 2026-06-15, renamed from <c>/healthz</c>) — see PLAN.md §6.
    /// </remarks>
    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Monitor))
            return false; // disconnected at runtime: capability is absent, not down.

        try
        {
            using HttpResponseMessage resp = await _http.GetAsync("/health", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("monitor /health probe timed out after {Timeout}", ScrapeTimeout);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "monitor /health probe failed");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetHistorySummaryJsonAsync(string kind, string? range, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Monitor))
            return null; // disconnected at runtime: honest absent, no request.

        try
        {
            string url =
                $"/metrics/history/summary?kind={Uri.EscapeDataString(kind)}&range={Uri.EscapeDataString(range ?? "1h")}";
            using HttpResponseMessage resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("monitor /metrics/history/summary returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("monitor /metrics/history/summary timed out after {Timeout}", ScrapeTimeout);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "monitor /metrics/history/summary failed");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetHistoryJsonAsync(string kind, string id, string? range, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Monitor))
            return null; // disconnected at runtime: honest absent, no request.

        try
        {
            string url =
                $"/metrics/history?kind={Uri.EscapeDataString(kind)}&id={Uri.EscapeDataString(id)}&range={Uri.EscapeDataString(range ?? "1h")}";
            using HttpResponseMessage resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("monitor /metrics/history returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("monitor /metrics/history timed out after {Timeout}", ScrapeTimeout);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "monitor /metrics/history failed");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetStatsJsonAsync(CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Monitor))
            return null; // disconnected at runtime: honest absent, no request.

        try
        {
            using HttpResponseMessage resp = await _http.GetAsync("/stats", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("monitor /stats returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("monitor /stats timed out after {Timeout}", ScrapeTimeout);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "monitor /stats failed");
            return null;
        }
    }

    public Task<string?> GetThresholdsJsonAsync(CancellationToken ct) => GetJsonAsync("/thresholds", ct);

    public Task<string?> GetEpisodesJsonAsync(long sinceMs, int limit, CancellationToken ct) =>
        GetJsonAsync(
            $"/thresholds/episodes?since={sinceMs.ToString(CultureInfo.InvariantCulture)}" +
            $"&limit={limit.ToString(CultureInfo.InvariantCulture)}", ct);

    public Task<LeafRelayResponse> PutThresholdsAsync(string json, CancellationToken ct) =>
        SendThresholdsAsync(HttpMethod.Put, new StringContent(json, Encoding.UTF8, "application/json"), ct);

    public Task<LeafRelayResponse> DeleteThresholdsAsync(CancellationToken ct) =>
        SendThresholdsAsync(HttpMethod.Delete, content: null, ct);

    // The write relay. The monitor's answer is passed back whole — status and body — because it is the one
    // that knows why a policy was refused, and restating its reasoning here would mean maintaining a second
    // copy of validation rules this API deliberately does not own.
    private async Task<LeafRelayResponse> SendThresholdsAsync(HttpMethod method, HttpContent? content, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Monitor))
            return LeafRelayResponse.Unreachable;

        try
        {
            using var request = new HttpRequestMessage(method, "/thresholds") { Content = content };
            using HttpResponseMessage resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new LeafRelayResponse(Reached: true, (int)resp.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("monitor {Method} /thresholds timed out after {Timeout}", method, ScrapeTimeout);
            return LeafRelayResponse.Unreachable;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogWarning(ex, "monitor {Method} /thresholds failed", method);
            return LeafRelayResponse.Unreachable;
        }
    }

    // The shared body of the verbatim read relays (/stats, /thresholds): provisioned-gate, GET, pass the
    // body through, and collapse every way of not getting one to null.
    private async Task<string?> GetJsonAsync(string path, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Monitor))
            return null; // disconnected at runtime: honest absent, no request.

        try
        {
            using HttpResponseMessage resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("monitor {Path} returned {Status}", path, (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("monitor {Path} timed out after {Timeout}", path, ScrapeTimeout);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "monitor {Path} failed", path);
            return null;
        }
    }

    private bool IsFresh() =>
        _hasFetched && Environment.TickCount64 - _lastFetchTicks < CacheTtl.TotalMilliseconds;

    private async Task<Snapshot?> ScrapeAsync(CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage resp = await _http.GetAsync("/metrics", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 503 until the first tick lands; any non-2xx is "no data right now".
                _logger.LogDebug("monitor /metrics returned {Status}", (int)resp.StatusCode);
                return null;
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, MonitorJsonContext.Default.Snapshot, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout fired (monitor too slow) — "no data", not an error to surface.
            _logger.LogDebug("monitor scrape timed out after {Timeout}", ScrapeTimeout);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or JsonException)
        {
            _logger.LogDebug(ex, "monitor scrape failed");
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshLock.Dispose();
    }
}
