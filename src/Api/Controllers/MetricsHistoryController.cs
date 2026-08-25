using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Historical metrics endpoints — a <b>verbatim proxy</b> to kgsm-monitor, the single source of truth
/// for metrics history. The API keeps the routes, the viewer gate, and the entity existence checks
/// (unknown id → 404), then relays the monitor's <c>GET /metrics/history</c> JSON body unchanged (tier
/// selection, series shaping, and retention all live in the monitor). Monitor absent/unreachable →
/// an honest empty response (200, never a fabricated curve), the same graceful-degrade the SPA already
/// handles.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class MetricsHistoryController(
    IMonitorHistoryClient monitor,
    ServerAggregator serverAggregator,
    ServicesAggregator services,
    ApiOptions options) : ControllerBase
{
    [HttpGet("servers/{id}/metrics/history")]
    public async Task<IActionResult> GetServerHistory(
        string id, [FromQuery] string? range, CancellationToken ct)
    {
        // null baseUrl: existence check only — skip the cover/hero art join (decorative, not needed here).
        Server? server = await serverAggregator.GetServerDetailAsync(id, null, ct);
        if (server is null)
            return NotFound();

        return await ProxyAsync("server", id, range, ct);
    }

    [HttpGet("hosts/{id}/metrics/history")]
    public async Task<IActionResult> GetHostHistory(
        string id, [FromQuery] string? range, CancellationToken ct)
    {
        if (id != options.HostId)
            return NotFound();

        return await ProxyAsync("host", id, range, ct);
    }

    /// <summary>
    /// One leaf's resource history — the same proxy, for the <c>leaf</c> entity kind the monitor persists
    /// its per-leaf samples under. The path mirrors the Services board a leaf is opened from
    /// (<c>/hosts/{id}/services</c>), so the URL addressing a leaf is the same one everywhere.
    /// <para>
    /// A leaf this host doesn't have is a 404, checked against the same catalog the board is built from. A
    /// leaf it <em>does</em> have but which has no rows yet — never running since the monitor started, or a
    /// monitor too old to sample leaves — is an honest empty series, not a 404: the leaf exists, its history
    /// doesn't.
    /// </para>
    /// </summary>
    [HttpGet("hosts/{id}/services/{leafId}/metrics/history")]
    public async Task<IActionResult> GetLeafHistory(
        string id, string leafId, [FromQuery] string? range, CancellationToken ct)
    {
        if (id != options.HostId || !services.Knows(leafId))
            return NotFound();

        return await ProxyAsync("leaf", leafId, range, ct);
    }

    /// <summary>
    /// One GPU's history — memory, utilisation, temperature and power for a single device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Addressed by the device's <b>UUID</b>, not its index: the index is an enumeration order that a driver
    /// reload or a card swap can renumber, and a series that silently changed which device it described would
    /// be worse than one that went empty.
    /// </para>
    /// <para>
    /// A UUID this host has no rows for is an honest empty series rather than a 404 — the same choice the
    /// per-leaf route makes. A card that was removed, or one whose rows have aged out of retention, is a real
    /// question with the answer "nothing recorded", and that is not the same statement as "no such route".
    /// </para>
    /// </remarks>
    [HttpGet("hosts/{id}/gpus/{uuid}/metrics/history")]
    public async Task<IActionResult> GetGpuHistory(
        string id, string uuid, [FromQuery] string? range, CancellationToken ct)
    {
        if (id != options.HostId)
            return NotFound();

        return await ProxyAsync("gpu", uuid, range, ct);
    }

    /// <summary>
    /// Every GPU's range at once — the same aggregate the hwmon summary returns, for the device kind whose
    /// rows are keyed by UUID.
    /// </summary>
    /// <remarks>
    /// A card's temperature belongs on the same thermal surface as the hwmon channels, and the surface asks
    /// for its window once. Separate from the sensor summary because the two kinds are separate row sets
    /// keyed differently, and folding them would put a UUID and a <c>chip/device/tempN</c> in one namespace.
    /// </remarks>
    [HttpGet("hosts/{id}/gpus/metrics/summary")]
    public async Task<IActionResult> GetGpuSummary(string id, [FromQuery] string? range, CancellationToken ct)
    {
        if (id != options.HostId)
            return NotFound();

        string? json = await monitor.GetHistorySummaryJsonAsync("gpu", range, ct);
        return json is null
            ? Ok(new MetricsSummaryRelay("gpu", range ?? MetricsRange.OneHour, "raw", []))
            : Content(json, "application/json");
    }

    /// <summary>
    /// Every hwmon channel's range at once — min/max/mean over the window, one row per channel.
    /// </summary>
    /// <remarks>
    /// A thermal panel draws a range per channel, which through the per-entity route is one request each,
    /// every one returning a full window of points the client reduces to three numbers. This is the same
    /// rows, aggregated where they live.
    /// </remarks>
    [HttpGet("hosts/{id}/sensors/metrics/summary")]
    public async Task<IActionResult> GetSensorSummary(string id, [FromQuery] string? range, CancellationToken ct)
    {
        if (id != options.HostId)
            return NotFound();

        string? json = await monitor.GetHistorySummaryJsonAsync("sensor", range, ct);
        return json is null
            ? Ok(new MetricsSummaryRelay("sensor", range ?? MetricsRange.OneHour, "raw", []))
            : Content(json, "application/json");
    }

    /// <summary>
    /// One hwmon channel's series. The channel id is a query parameter, not a path segment: a sensor id is
    /// <c>chip/device/tempN</c> and carries the separator a path would be split on.
    /// </summary>
    [HttpGet("hosts/{id}/sensors/metrics/history")]
    public async Task<IActionResult> GetSensorHistory(
        string id, [FromQuery] string? sensor, [FromQuery] string? range, CancellationToken ct)
    {
        if (id != options.HostId)
            return NotFound();
        if (string.IsNullOrWhiteSpace(sensor))
            return BadRequest();

        return await ProxyAsync("sensor", sensor, range, ct);
    }

    private async Task<IActionResult> ProxyAsync(string kind, string id, string? range, CancellationToken ct)
    {
        string? json = await monitor.GetHistoryJsonAsync(kind, id, range, ct);
        if (json is null)
            return Ok(EmptyResponse(id, kind, range ?? MetricsRange.OneHour));

        // Relay the monitor's body unchanged (it already carries the SPA's exact shape).
        return Content(json, "application/json");
    }

    /// <summary>The empty summary shape, for a monitor that could not be read. Mirrors the daemon's own
    /// field names so a client parses one shape either way.</summary>
    private sealed record MetricsSummaryRelay(string Kind, string Range, string Tier, object[] Entries);

    private static MetricsHistoryResponse EmptyResponse(string entityId, string kind, string range) =>
        new(entityId, kind, range, 0, "raw", new Dictionary<string, List<MetricsHistoryPoint>>());
}
