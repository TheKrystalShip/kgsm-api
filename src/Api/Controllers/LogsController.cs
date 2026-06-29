using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Logs;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The aggregated host-log read surface — <c>GET /hosts/{id}/logs</c> (architecture.html §3, "Hosts &amp;
/// diagnostics"). Funnels the host's leaf-service logs (assistant, monitor, watchdog, kgsm-firewall, +the
/// api &amp; bot) out of the <b>systemd journal</b> into one source-tagged, cursor-paginated stream. This is
/// host-OS introspection (journald is the system's own merged log bus), sourced by the api directly via
/// <see cref="JournalReader"/> — NOT through kgsm-lib (that chokepoint is for engine domain data; the host
/// journal is not engine data). New lines also arrive live on the <c>hosts/{id}/logs</c> WS topic; this
/// endpoint is the hydrate/backfill source (the patch-only realtime rule).
/// <para>
/// Gated at <b>operator</b> — stricter than the (viewer-gated) audit log on purpose: the audit feed is a
/// curated, closed-vocabulary action record, whereas raw journald lines are uncurated and can carry stack
/// traces or secrets. Pagination is keyset on the opaque journald cursor (newest first).
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/hosts/{id}/logs")]
[Authorize(Policy = AuthPolicy.Operator)]
public sealed class LogsController(JournalReader journal, ApiOptions options) : ControllerBase
{
    /// <summary>
    /// <c>GET /hosts/{id}/logs?source=&amp;cursor=&amp;limit=100&amp;priority=</c> — newest first. Returns
    /// <c>{ data, nextCursor }</c>; pass <c>nextCursor</c> back as <c>?cursor=</c> for the next (older) page
    /// (null ⇒ no older lines). <c>source</c> narrows to one leaf (one of the configured source ids — an
    /// unknown one is a 400, never a silently-merged page); absent ⇒ all leaves merged. <c>priority</c> is a
    /// max severity (<c>error|warn|info|debug</c> or 0–7). <c>limit</c> is clamped to a sane maximum.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<LogPage>> GetLogs(
        string id,
        [FromQuery] string? source,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        [FromQuery] string? priority,
        CancellationToken ct)
    {
        // Per-host api: the only valid {id} is this host. Unknown id -> 404 (the envelope via UseStatusCodePages),
        // mirroring HostsController so the id space is consistent across the hosts surface.
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        // An explicit but unknown source is a client error (not a silent merge) — the dropdown only ever sends
        // a configured id. Blank/whitespace is treated as absent (merged), like the audit filters.
        if (!string.IsNullOrWhiteSpace(source) && !journal.IsKnownSource(source))
            return StatusCode(StatusCodes.Status400BadRequest, new ErrorEnvelope(new ErrorBody(
                "bad_request", $"unknown log source '{source}'")));

        string? src = string.IsNullOrWhiteSpace(source) ? null : source;
        LogPage page = await journal.PageAsync(src, cursor, JournalReader.ClampLimit(limit), priority, ct);
        return page;
    }
}
