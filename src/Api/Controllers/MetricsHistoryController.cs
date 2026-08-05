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

    private async Task<IActionResult> ProxyAsync(string kind, string id, string? range, CancellationToken ct)
    {
        string? json = await monitor.GetHistoryJsonAsync(kind, id, range, ct);
        if (json is null)
            return Ok(EmptyResponse(id, kind, range ?? MetricsRange.OneHour));

        // Relay the monitor's body unchanged (it already carries the SPA's exact shape).
        return Content(json, "application/json");
    }

    private static MetricsHistoryResponse EmptyResponse(string entityId, string kind, string range) =>
        new(entityId, kind, range, 0, "raw", new Dictionary<string, List<MetricsHistoryPoint>>());
}
