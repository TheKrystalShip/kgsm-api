using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The <c>/audit</c> read surface (M5, architecture.html §3·d) — the immutable, append-only action
/// record: "what happened, who did it, through which surface, when." Rows are written by
/// <see cref="AuditService"/> (engine events via <see cref="KgsmAuditConsumer"/> + API-internal auth
/// actions); this controller only reads. New rows also arrive live on the <c>audit</c> WS topic
/// (<c>audit.append</c>) — the client prepends; this endpoint is the hydrate/backfill source (§3·j).
/// <para>
/// Gated at <b>viewer</b>: the audit feed is a core read surface (every "what happened" view reads
/// here), consistent with "viewer = reads". Pagination is keyset on the opaque <c>rowid</c> cursor.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/audit")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class AuditController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// <c>GET /audit?cursor=&amp;limit=50&amp;severity=&amp;serverId=&amp;actor=</c> — newest first.
    /// Returns <c>{ data, nextCursor }</c>; pass <c>nextCursor</c> back as <c>?cursor=</c> for the next
    /// page (null ⇒ no older rows). The filters map 1:1 to indexed columns; an absent/garbage cursor
    /// starts from the newest row, and <c>limit</c> is clamped to a sane maximum.
    /// </summary>
    [HttpGet]
    public Task<AuditPage> GetAudit(
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        [FromQuery] string? severity,
        [FromQuery] string? serverId,
        [FromQuery] string? actor,
        CancellationToken ct) =>
        AuditQueries.PageAsync(
            db,
            AuditQueries.ParseCursor(cursor),
            AuditQueries.ClampLimit(limit),
            severity,
            serverId,
            actor,
            ct);
}
