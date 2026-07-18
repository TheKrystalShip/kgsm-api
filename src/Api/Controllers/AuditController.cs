using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The <c>/audit</c> read surface (M5, architecture.html §3·d) — the immutable, append-only action
/// record: "what happened, who did it, through which surface, when." As of event-history-plan.md Phase C
/// this is a MERGE: the local table holds only the API's own rows (auth/session/leaf/files/console-audit
/// — written by <see cref="AuditService.AppendAsync"/>), while kgsm engine history now lives in
/// kgsm-monitor and is folded in at read time (<see cref="AuditQueries.PageMergedAsync"/>). The wire
/// shape, routes, auth gate, and cursor CONTRACT (an opaque string) are unchanged — kgsm-web is unaware
/// the backend split. New rows also arrive live on the <c>audit</c> WS topic (<c>audit.append</c>,
/// engine rows included — <see cref="KgsmAuditConsumer"/> still publishes them live, just no longer
/// persists them locally) — the client prepends; this endpoint is the hydrate/backfill source (§3·j).
/// <para>
/// Gated at <b>viewer</b>: the audit feed is a core read surface (every "what happened" view reads
/// here), consistent with "viewer = reads". Pagination is keyset on the opaque <c>cursor</c> string.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/audit")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class AuditController(AppDbContext db, IMonitorEventsClient monitorEvents, ApiOptions options)
    : ControllerBase
{
    /// <summary>
    /// <c>GET /audit?cursor=&amp;limit=50&amp;severity=&amp;serverId=&amp;actor=&amp;since=&amp;category=</c> —
    /// newest first. Returns <c>{ data, nextCursor, engineHistoryDegraded }</c>; pass <c>nextCursor</c>
    /// back as <c>?cursor=</c> for the next page (null ⇒ no older rows). Filters are pushed to BOTH the
    /// local table and kgsm-monitor before the merge: <c>severity</c> takes a comma-separated set (the
    /// UI's "attention" = <c>warn,danger</c>), <c>since</c> is an ISO instant lower bound (time-range
    /// tabs), and <c>category</c> is the action group prefix (<c>server</c> → <c>server.*</c>). An
    /// absent/garbage cursor starts from the newest row; <c>limit</c> is clamped to a sane maximum.
    /// kgsm-monitor unreachable → <c>engineHistoryDegraded:true</c> + local-only rows, never a 500.
    /// </summary>
    [HttpGet]
    public Task<AuditPage> GetAudit(
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        [FromQuery] string? severity,
        [FromQuery] string? serverId,
        [FromQuery] string? actor,
        [FromQuery] string? since,
        [FromQuery] string? category,
        CancellationToken ct) =>
        AuditQueries.PageMergedAsync(
            db,
            monitorEvents,
            options.HostId,
            cursor,
            AuditQueries.ClampLimit(limit),
            severity,
            serverId,
            actor,
            since,
            category,
            ct);
}
