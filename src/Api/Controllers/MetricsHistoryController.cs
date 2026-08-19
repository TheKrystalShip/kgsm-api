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
