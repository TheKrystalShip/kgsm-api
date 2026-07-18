using System.Globalization;
using System.Net.Sockets;
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
}

/// <summary>
/// The read seam for the monitor's <c>GET /events</c> endpoint (kgsm-monitor Phase B,
/// event-history-plan.md) — the monitor is the single source of truth for kgsm ENGINE event history.
/// Unlike <see cref="IMonitorHistoryClient"/> (a verbatim string proxy) this returns a parsed page: the
/// merge reader (<see cref="Audit.AuditQueries"/>) needs structured access to shape each raw event via
/// <see cref="Audit.MonitorEventShaping"/>, not just a passthrough body.
/// </summary>
public interface IMonitorEventsClient
{
    /// <summary>
    /// <c>GET /events?instance=&amp;type=&amp;since=&amp;until=&amp;before_ts=&amp;before_id=&amp;limit=</c>,
    /// ts-DESC, composite <c>(before_ts, before_id)</c> keyset cursor (matches the monitor's own wire
    /// contract exactly — see <c>kgsm-monitor/src/Monitor/History/EventHistoryStore.cs</c>). Returns
    /// <see langword="null"/> when the monitor is unprovisioned, unreachable, slow, or answers non-2xx —
    /// the honest "engine history degraded" signal the merge reader turns into
    /// <c>AuditPage.EngineHistoryDegraded</c>, never a silently empty/fabricated page.
    /// </summary>
    Task<MonitorEventPage?> GetEventsAsync(
        string? instance, string? type, long? sinceMs, long? untilMs,
        long? beforeTs, string? beforeId, int limit, CancellationToken ct);
}

/// <summary>
/// A local mirror of kgsm-monitor's <c>GET /events</c> response shape — daemon-local DTOs on the
/// monitor's side (NOT part of the shared <c>TheKrystalShip.KGSM.Monitor.Contracts</c> package, exactly
/// like its metrics-history counterpart), so kgsm-api defines its own copy to deserialize the wire JSON
/// (camelCase, reflection-based STJ — fine under this project's JIT runtime). Field-for-field match with
/// <c>EventHistoryResponse</c>/<c>EventHistoryItem</c> in the monitor repo.
/// </summary>
public sealed record MonitorEventPage(
    int Count,
    string? NextCursorTs,
    string? NextCursorId,
    List<MonitorEventItem> Events);

/// <summary>One raw engine event row as kgsm-monitor persisted it — neutral, undomained (see
/// <see cref="Audit.MonitorEventShaping"/> for the read-time shaping into an <c>AuditRecord</c>).</summary>
public sealed record MonitorEventItem(
    string Id,
    DateTimeOffset Ts,
    string Type,
    string? Instance,
    string? Actor,
    string? Origin,
    JsonElement? Data);

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
public sealed class MonitorClient : IMonitorHistoryClient, IMonitorEventsClient, IDisposable
{
    // camelCase, reflection-based — the monitor's daemon-local history JSON context serializes
    // camelCase (MonitorHistoryJsonContext); kgsm-api is JIT so a reflection-based deserialize here is
    // fine (unlike kgsm-lib, this project is deliberately not AOT — see the api CLAUDE.md stack decision).
    private static readonly JsonSerializerOptions MonitorEventJsonOptions = new(JsonSerializerDefaults.Web);

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

    /// <inheritdoc cref="IMonitorEventsClient.GetEventsAsync"/>
    public async Task<MonitorEventPage?> GetEventsAsync(
        string? instance, string? type, long? sinceMs, long? untilMs,
        long? beforeTs, string? beforeId, int limit, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Monitor))
            return null; // disconnected at runtime: honest absent, no request.

        try
        {
            var qs = new List<string>(7);
            if (!string.IsNullOrEmpty(instance)) qs.Add($"instance={Uri.EscapeDataString(instance)}");
            if (!string.IsNullOrEmpty(type)) qs.Add($"type={Uri.EscapeDataString(type)}");
            if (sinceMs is { } sv) qs.Add($"since={sv.ToString(CultureInfo.InvariantCulture)}");
            if (untilMs is { } uv) qs.Add($"until={uv.ToString(CultureInfo.InvariantCulture)}");
            if (beforeTs is { } bt) qs.Add($"before_ts={bt.ToString(CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrEmpty(beforeId)) qs.Add($"before_id={Uri.EscapeDataString(beforeId)}");
            qs.Add($"limit={limit.ToString(CultureInfo.InvariantCulture)}");
            string url = "/events?" + string.Join('&', qs);

            using HttpResponseMessage resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("monitor /events returned {Status}", (int)resp.StatusCode);
                return null;
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<MonitorEventPage>(stream, MonitorEventJsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("monitor /events timed out after {Timeout}", ScrapeTimeout);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or JsonException)
        {
            _logger.LogDebug(ex, "monitor /events failed");
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
