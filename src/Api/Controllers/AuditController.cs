using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The <c>/audit</c> read surface (architecture.html §3·d) — the immutable, append-only action record:
/// "what happened, who did it, through which surface, when." It is a MERGE of two sources: the local
/// table, holding only the API's own rows (auth/session/leaf/files/console-audit — written by
/// <see cref="AuditService.AppendAsync"/>), and kgsm engine history, read from the engine's own event
/// journal and folded in at read time (<see cref="AuditQueries.PageMergedAsync"/>). New rows also
/// arrive live on the <c>audit</c> SSE topic (<c>audit.append</c>, engine rows included —
/// <see cref="KgsmAuditConsumer"/> publishes them live without persisting them) — the client prepends;
/// this endpoint is the hydrate/backfill source (§3·j).
/// <para>
/// Gated at <b>viewer</b>: the audit feed is a core read surface (every "what happened" view reads
/// here), consistent with "viewer = reads". Pagination is keyset on the opaque <c>cursor</c> string.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/audit")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class AuditController(AppDbContext db, ApiOptions options)
    : ControllerBase
{
    /// <summary>
    /// The engine's journal reader, or <see langword="null"/> on a host with no engine.
    /// </summary>
    /// <remarks>
    /// Resolved lazily from the request scope rather than injected, because kgsm-lib's services are
    /// registered only when the engine is provisioned — the same pattern <c>IInstanceService</c> and
    /// the file browser use. A constructor parameter would make an unprovisioned host fail to
    /// construct this controller at all, turning "no engine history" into a 500 on the endpoint an
    /// operator reads to find out what happened.
    /// </remarks>
    private IEventJournalHistory? Journal =>
        HttpContext.RequestServices.GetService<IEventJournalHistory>();

    /// <summary>
    /// <c>GET /audit?cursor=&amp;limit=50&amp;severity=&amp;serverId=&amp;actor=&amp;since=&amp;category=</c> —
    /// newest first. Returns <c>{ data, nextCursor, engineHistoryDegraded }</c>; pass <c>nextCursor</c>
    /// back as <c>?cursor=</c> for the next page (null ⇒ no older rows). Filters are pushed to BOTH the
    /// local table and the engine journal before the merge: <c>severity</c> takes a comma-separated set (the
    /// UI's "attention" = <c>warn,danger</c>), <c>since</c> is an ISO instant lower bound (time-range
    /// tabs), and <c>category</c> is the action group prefix (<c>server</c> → <c>server.*</c>). An
    /// absent/garbage cursor starts from the newest row; <c>limit</c> is clamped to a sane maximum.
    /// An unreadable journal, or a host with no engine → <c>engineHistoryDegraded:true</c> + local-only
    /// rows, never a 500.
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
            Journal,
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
