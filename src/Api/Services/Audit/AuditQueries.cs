using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// The read side of the audit log — a ts-DESC merge (event-history-plan.md Phase C, Locked decision #4)
/// of two disjoint sources: the local EF table (API-only rows — auth/session/leaf/files/console-audit,
/// plus whatever pre-cutover engine rows are already frozen there) and kgsm-monitor's <c>GET /events</c>
/// (the engine's own history, shaped at read time via <see cref="MonitorEventShaping"/>). Pure query
/// logic over an <see cref="AppDbContext"/> + an <see cref="IMonitorEventsClient"/> (both resolved on
/// the request scope by the controller), so it is unit-testable against a real SQLite + a fake monitor
/// client without a live kgsm/monitor.
/// </summary>
/// <remarks>
/// <para><b>Cursor.</b> A composite <c>(ts, id)</c> keyset — see <see cref="AuditCursor"/> — spans both
/// sources, unlike the old bare local <c>rowid</c> cursor (which can't address a monitor-sourced row).
/// The wire string stays opaque to the client (kgsm-web only stores/echoes it — confirmed by reading its
/// audit store — so changing the internal encoding is safe).</para>
/// <para><b>Local exclusion.</b> <see cref="EngineSourcedActions"/> is the set of dotted actions that,
/// post-cutover, can ONLY be freshly written by the (now-removed) kgsm-event-echo path in
/// <see cref="KgsmAuditConsumer"/> — so a local row bearing one of them is frozen pre-cutover history,
/// excluded here to keep the two sources disjoint (Locked decision #4's "no dedup headaches"; a
/// defensive id-based dedup still runs at the merge boundary as belt-and-braces).
/// <see cref="AuditAction.NetworkPortsOpen"/> is deliberately NOT excluded — see that field's remarks.</para>
/// </remarks>
public static class AuditQueries
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    // A local fetch batch overshoots `limit` by this much so the exact (ts,id) boundary trim below never
    // has to worry about starving a page when several local rows share the same Ts to tick precision —
    // vanishingly rare (each local write is serialized through AuditService's write gate) but handled
    // precisely rather than assumed away.
    private const int TieBreakSlack = 32;

    /// <summary>
    /// The dotted actions <see cref="AuditMapping"/>'s <c>From*Event</c> mappers produce that are, post
    /// Phase-C, EXCLUSIVELY sourced from the kgsm event echo. <see cref="KgsmAuditConsumer"/> no longer
    /// writes any of these to the local table (it now only publishes them live — see
    /// <see cref="AuditService.PublishLive"/>), so a local row bearing one is frozen pre-cutover history;
    /// excluding it here means the merge's engine-sourced rows come solely from the monitor.
    /// <para>
    /// <see cref="AuditAction.NetworkPortsOpen"/> is DELIBERATELY NOT in this set: it is dual-sourced —
    /// kgsm's CLI-echo (<c>instance_ports_opened</c>, now monitor-only, shaped via
    /// <see cref="MonitorEventShaping"/>) AND the api-issued <c>open_ports</c> command's DIRECT local
    /// write (<c>AuditMapping.FromPortsOpenedCommand</c>, called from
    /// <c>Services/Commands/CommandRunner.cs</c> — untouched by Phase C, no kgsm event exists for it to
    /// echo). Excluding <c>network.ports.open</c> here would silently drop every <c>open_ports</c>
    /// command's own audit trail, which is genuinely API-only and has no monitor counterpart.
    /// <c>network.ports.close</c> has no such direct writer (§3·g is open-only) so it excludes cleanly.
    /// </para>
    /// </summary>
    internal static readonly HashSet<string> EngineSourcedActions = new(StringComparer.Ordinal)
    {
        AuditAction.ServerStart,
        AuditAction.ServerStop,
        AuditAction.ServerRestart,
        AuditAction.ServerUpdate,
        AuditAction.ServerInstall,
        AuditAction.ServerUninstall,
        AuditAction.ServerCrash,
        AuditAction.BackupCreate,
        AuditAction.BackupRestore,
        AuditAction.NetworkPortsClose, // .Open is deliberately excluded from this set — see remarks above
        AuditAction.NetworkUpnpOpen,
        AuditAction.NetworkUpnpClose,
        AuditAction.PlayerJoin,
        AuditAction.PlayerLeave,
        AuditAction.ConfigSet,
        AuditAction.ConsoleInput,
    };

    /// <summary>Clamp a client-supplied limit to <c>[1, <see cref="MaxLimit"/>]</c>, defaulting when unset.</summary>
    public static int ClampLimit(int? limit) =>
        limit is null || limit <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    /// <summary>
    /// The merged, ts-DESC page: local API-only rows ∪ kgsm-monitor's shaped engine rows. Every filter is
    /// pushed to both sources before merge (<c>serverId</c> as the monitor's <c>instance=</c>, <c>since</c>
    /// as its <c>since=</c> in unix ms); <c>severity</c>/<c>actor</c>/<c>category</c> have no monitor-side
    /// equivalent (the monitor stores raw events, not the shaped vocabulary), so they narrow the EF query
    /// via SQL and the shaped monitor records via an equivalent in-memory filter, applied identically.
    /// Monitor unreachable → <see cref="AuditPage.EngineHistoryDegraded"/> true, local-only rows (never a
    /// silent drop, never a 500).
    /// </summary>
    public static async Task<AuditPage> PageMergedAsync(
        AppDbContext db,
        IMonitorEventsClient monitor,
        string hostId,
        string? cursor,
        int limit,
        string? severity,
        string? serverId,
        string? actor,
        string? since,
        string? category,
        CancellationToken ct)
    {
        AuditCursor? c = AuditCursor.Parse(cursor);
        string[]? severities = ParseSeverities(severity);
        string? categoryPrefix = string.IsNullOrWhiteSpace(category) ? null : category.Trim() + ".";
        DateTimeOffset? sinceTs = ParseSince(since);

        List<AuditEntry> localRows = await QueryLocalAsync(
            db, c, limit, severities, serverId, actor, sinceTs, categoryPrefix, ct).ConfigureAwait(false);
        List<AuditRecord> localRecords = localRows.Select(AuditMapping.ToRecord).ToList();

        (List<AuditRecord> monitorRecords, bool degraded, bool monitorFull) = await QueryMonitorAsync(
            monitor, hostId, c, limit, severities, serverId, sinceTs?.ToUnixTimeMilliseconds(),
            categoryPrefix, ct).ConfigureAwait(false);

        List<AuditRecord> merged = localRecords.Concat(monitorRecords)
            .GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First()) // defensive boundary dedup
            .OrderByDescending(r => r.Ts)
            .ThenByDescending(r => r.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        // Top-K-of-a-merge-of-two-sorted-sources: fetching `limit` from EACH source and taking the top
        // `limit` of their union always yields the true page (see AuditQueries remarks / the merge design
        // note in the Phase-C report — an exchange-argument proof, not a heuristic). "More rows might
        // exist" iff either source's own fetch came back full (it might have more beyond what we took) or
        // the combined candidate pool exceeded one page (some fetched rows didn't make the cut).
        bool hasMore = localRows.Count == limit || monitorFull
            || localRecords.Count + monitorRecords.Count > limit;

        string? next = hasMore && merged.Count > 0
            ? new AuditCursor(merged[^1].Ts.ToUnixTimeMilliseconds(), merged[^1].Id).ToString()
            : null;

        return new AuditPage(merged, next, degraded);
    }

    private static async Task<List<AuditEntry>> QueryLocalAsync(
        AppDbContext db, AuditCursor? cursor, int limit,
        string[]? severities, string? serverId, string? actor, DateTimeOffset? since, string? categoryPrefix,
        CancellationToken ct)
    {
        IQueryable<AuditEntry> q = db.Audit.AsNoTracking()
            .Where(a => !EngineSourcedActions.Contains(a.Action));

        if (severities is { Length: 1 }) q = q.Where(a => a.Severity == severities[0]);
        else if (severities is { Length: > 1 }) q = q.Where(a => severities.Contains(a.Severity));
        if (!string.IsNullOrWhiteSpace(serverId)) q = q.Where(a => a.ServerId == serverId);
        if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(a => a.ActorName == actor);
        if (since is { } sinceTs) q = q.Where(a => a.Ts >= sinceTs);
        if (categoryPrefix is not null) q = q.Where(a => a.Action.StartsWith(categoryPrefix));

        if (cursor is { } c)
        {
            DateTimeOffset cts = DateTimeOffset.FromUnixTimeMilliseconds(c.TsMs);
            // Coarse, fully-translatable bound (Ts is stored as UTC ticks — see AppDbContext); the exact
            // (ts,id) boundary is trimmed in memory below rather than risking an untranslatable string
            // comparison in the LINQ-to-SQL expression.
            q = q.Where(a => a.Ts <= cts);
        }

        List<AuditEntry> batch = await q
            .OrderByDescending(a => a.Ts).ThenByDescending(a => a.RowId)
            .Take(limit + TieBreakSlack)
            .ToListAsync(ct).ConfigureAwait(false);

        if (cursor is { } cur)
        {
            DateTimeOffset cts = DateTimeOffset.FromUnixTimeMilliseconds(cur.TsMs);
            batch = batch.Where(a => a.Ts < cts || (a.Ts == cts && string.CompareOrdinal(a.Id, cur.Id) < 0))
                .ToList();
        }

        return batch.Take(limit).ToList();
    }

    private static async Task<(List<AuditRecord> Records, bool Degraded, bool MonitorFull)> QueryMonitorAsync(
        IMonitorEventsClient monitor, string hostId, AuditCursor? cursor, int limit,
        string[]? severities, string? serverId, long? sinceMs, string? categoryPrefix, CancellationToken ct)
    {
        MonitorEventPage? page;
        try
        {
            page = await monitor.GetEventsAsync(
                instance: string.IsNullOrWhiteSpace(serverId) ? null : serverId,
                type: null, // no clean 1:1 category->type mapping (a category spans many event types)
                sinceMs: sinceMs,
                untilMs: null,
                beforeTs: cursor?.TsMs,
                beforeId: cursor?.Id,
                limit: limit,
                ct: ct).ConfigureAwait(false);
        }
        catch
        {
            // GetEventsAsync itself never throws (see MonitorClient) — this is a last-resort guard so a
            // merge-time surprise degrades the page rather than 500ing the whole endpoint.
            page = null;
        }

        if (page is null) return (new List<AuditRecord>(), true, false);

        var records = new List<AuditRecord>(page.Events.Count);
        foreach (MonitorEventItem item in page.Events)
        {
            AuditRecord? shaped = MonitorEventShaping.Shape(item, hostId);
            if (shaped is null) continue; // deliberately-silent type
            if (severities is { Length: > 0 } && !severities.Contains(shaped.Severity)) continue;
            if (!string.IsNullOrWhiteSpace(serverId) && shaped.ServerId != serverId) continue;
            if (categoryPrefix is not null && !shaped.Action.StartsWith(categoryPrefix, StringComparison.Ordinal)) continue;
            records.Add(shaped);
        }

        // The RAW page fullness (before the in-memory severity/actor/category trim above), not the
        // filtered count — filtering can shrink `records` well below `limit` even though the monitor may
        // still have more rows beyond this batch (it doesn't support these filters server-side, so a
        // filtered merge page can legitimately come back sparse; kgsm-web already walks pages until it
        // has enough rows or the cursor exhausts — see the audit-feed investigation in the Phase-C report).
        bool monitorFull = page.Events.Count == limit;
        return (records, false, monitorFull);
    }

    private static string[]? ParseSeverities(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity)) return null;
        string[] parsed = severity.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parsed.Length > 0 ? parsed : null;
    }

    private static DateTimeOffset? ParseSince(string? since) =>
        !string.IsNullOrWhiteSpace(since)
        && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset ts)
            ? ts
            : null; // an unparseable value is ignored (no filter), never a silently empty page

    /// <summary>
    /// The most recent <paramref name="limit"/> rows for an EXACT <paramref name="action"/> by a given
    /// <paramref name="actorName"/>, newest first — the read behind <c>/me.recentLogins</c> (M4·c
    /// Increment 7). Always <c>auth.*</c> (API-only, never excluded), so this stays a plain local query —
    /// no merge needed. Deliberately NOT a thin wrapper over <see cref="PageMergedAsync"/>: that method's
    /// <c>category</c> filter matches a dotted PREFIX (<c>"auth."</c> → <c>auth.login</c> AND
    /// <c>auth.logout</c>), which would silently pull logouts into a "recent logins" list. This filters
    /// <see cref="AuditEntry.Action"/> by equality instead, so a login-only read is honest by
    /// construction rather than by caller discipline.
    /// </summary>
    public static Task<List<AuditEntry>> RecentByActionAsync(
        AppDbContext db, string action, string actorName, int limit, CancellationToken ct) =>
        db.Audit.AsNoTracking()
            .Where(a => a.Action == action && a.ActorName == actorName)
            .OrderByDescending(a => a.RowId)
            .Take(limit)
            .ToListAsync(ct);
}

/// <summary>
/// The merged feed's keyset cursor: a composite <c>(ts, id)</c> pair, total-ordered ts-DESC then id-DESC
/// (ordinal), spanning both the local table and kgsm-monitor's event history — a single local
/// <c>rowid</c> can't address a monitor-sourced row (Locked decision #4). Wire-encoded as
/// <c>"{tsUnixMs}:{id}"</c>; deliberately simple/readable rather than obfuscated, since the string is
/// opaque to the client by convention (kgsm-web never parses it — confirmed by reading its audit store),
/// not by construction.
/// </summary>
internal readonly record struct AuditCursor(long TsMs, string Id)
{
    public override string ToString() => $"{TsMs.ToString(CultureInfo.InvariantCulture)}:{Id}";

    public static AuditCursor? Parse(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        int i = s.IndexOf(':');
        if (i <= 0 || i == s.Length - 1) return null;
        return long.TryParse(s.AsSpan(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts)
            ? new AuditCursor(ts, s[(i + 1)..])
            : null;
    }
}
