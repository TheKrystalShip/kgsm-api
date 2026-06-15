using System.Net.Sockets;
using System.Text.Json;
using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The kgsm-monitor leaf client: scrapes <c>GET /metrics</c> over the monitor's unix-domain
/// socket and serves a <strong>cached-latest</strong> <see cref="Snapshot"/>. The HTTP-over-unix
/// transport reuses the same <see cref="SocketsHttpHandler.ConnectCallback"/> pattern as
/// kgsm-lib's watchdog client; the snapshot is deserialized with the monitor's own shared
/// <see cref="MonitorJsonContext"/>, so producer and consumer share one build-time contract.
/// </summary>
/// <remarks>
/// Honesty: a failed, timed-out, or not-yet-ready (503) scrape yields <c>null</c> — the caller
/// then reports the metrics capability <c>down</c> and nulls host capacity. M1·a does NOT serve
/// stale last-good data (that "last values hold" behavior belongs to the M2 stream); the cache
/// only conflates rapid requests within a short TTL.
/// </remarks>
public sealed class MonitorClient : IDisposable
{
    // The monitor self-ticks (~1s) and serves its latest in-memory frame, so a short api-side
    // cache bounds socket round-trips without adding meaningful staleness.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(1);

    // A scrape returns an in-memory frame and must be fast; bound it so a hung socket can never
    // stall a /hosts request.
    private static readonly TimeSpan ScrapeTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<MonitorClient> _logger;
    private readonly HttpClient? _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private Snapshot? _cached;
    private long _lastFetchTicks;
    private bool _hasFetched;

    public MonitorClient(ApiOptions options, ILogger<MonitorClient> logger)
    {
        _logger = logger;

        if (!options.MetricsProvisioned)
            return; // unprovisioned: GetLatestAsync short-circuits to null, capability is absent.

        string socketPath = options.MonitorSocketPath;
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
        if (_http is null)
            return null;

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
        if (_http is null)
            return false; // unprovisioned: capability is absent, not down.

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

    private bool IsFresh() =>
        _hasFetched && Environment.TickCount64 - _lastFetchTicks < CacheTtl.TotalMilliseconds;

    private async Task<Snapshot?> ScrapeAsync(CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage resp = await _http!.GetAsync("/metrics", ct).ConfigureAwait(false);
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
        _http?.Dispose();
        _refreshLock.Dispose();
    }
}
