using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;
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
public sealed class LogsController(JournalReader journal, ApiOptions options, SystemdReader systemd) : ControllerBase
{
    /// <summary>
    /// <c>GET /hosts/{id}/logs/sources</c> — the host-log sources <b>this</b> host can serve: the ordered
    /// <see cref="ApiOptions.LogSources"/> map (labelled from the canonical <see cref="LeafCatalog"/>), less
    /// the units systemd reports as <c>not-installed</c> here. The map is the ecosystem's whole leaf set,
    /// which no single node has to carry; a source for a unit that is not on the host is one a person can
    /// select and never hear from, so it is not offered.
    /// <para>
    /// Absence is only claimed when systemd actually said so. A unit the reader could not read at all
    /// (<c>unknown</c> — systemctl missing, errored, timed out) stays in the list: not knowing whether a
    /// service is here is not the same as knowing it is not, and hiding it would state the stronger thing.
    /// Presence is the only test — an installed unit that is stopped, failed or masked stays selectable,
    /// because its journal is exactly what someone comes to this tab to read. A source with nothing in the
    /// recent window is a client-side "no recent log lines", not an absent source.
    /// </para>
    /// </summary>
    [HttpGet("sources")]
    public async Task<ActionResult<IReadOnlyList<LogSourceInfo>>> GetSources(CancellationToken ct)
    {
        IReadOnlyDictionary<string, UnitState> units = await systemd
            .ReadAsync([.. options.LogSources.Select(s => s.Unit)], ct)
            .ConfigureAwait(false);
        return Ok(SelectSources(options.LogSources, units));
    }

    /// <summary>The offering rule itself, separated from the systemd read so it can be driven with a
    /// synthetic unit table. A source is offered unless systemd said its unit is <c>not-installed</c>;
    /// every other state, and a unit missing from the table entirely, keeps it.</summary>
    internal static IReadOnlyList<LogSourceInfo> SelectSources(
        IReadOnlyList<LogSourceMap> configured, IReadOnlyDictionary<string, UnitState> units) =>
        [.. configured
            .Where(s => !units.TryGetValue(s.Unit, out UnitState? unit)
                        || !string.Equals(unit.State, "not-installed", StringComparison.Ordinal))
            .Select(s => new LogSourceInfo(
                s.Source,
                LeafCatalog.Default.FirstOrDefault(l => l.Id == s.Source)?.DisplayName ?? s.Source,
                s.Unit))];

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
