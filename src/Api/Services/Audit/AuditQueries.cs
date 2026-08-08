using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// The read side of the audit log — a ts-DESC merge of two disjoint sources: the local EF table
/// (API-only rows — auth/session/leaf/files/console-audit, plus whatever pre-cutover engine rows are
/// frozen there) and the engine's own event journal, read through kgsm-lib's
/// <see cref="IEventJournalHistory"/> and shaped at read time via <see cref="EngineEventShaping"/>.
/// Pure query logic over an <see cref="AppDbContext"/> + the journal reader, so it is unit-testable
/// against a real SQLite and a fake reader with no live kgsm.
/// </summary>
/// <remarks>
/// <para><b>Why the journal and not a leaf.</b> Engine history is a property of the record on disk, so
/// this API answers for it with no daemon involved. A host missing every optional leaf still returns a
/// complete audit trail.</para>
/// <para><b>Cursor.</b> A composite <c>(ts, id)</c> keyset — see <see cref="AuditCursor"/> — spanning
/// both sources. It works across them because a journal id is the event's position and therefore sorts
/// like the journal itself, so one cursor addresses a local row and an engine row alike. The wire string
/// stays opaque to the client (kgsm-web only stores and echoes it), so the encoding is free to change.</para>
/// <para><b>Local exclusion.</b> <see cref="EngineSourcedActions"/> is the set of dotted actions that,
/// post-cutover, can ONLY be freshly written by the (now-removed) kgsm-event-echo path in
/// <see cref="KgsmAuditConsumer"/> — so a local row bearing one of them is frozen pre-cutover history,
/// excluded here to keep the two sources disjoint (Locked decision #4's "no dedup headaches"; a
/// defensive id-based dedup still runs at the merge boundary as belt-and-braces).</para>
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

    // How many journal pages one merge page may read before giving up on filling itself. A query
    // whose filters match almost nothing would otherwise walk the whole retention window in one
    // request; stopping early and reporting "there is more" keeps the work bounded without ever
    // claiming the feed ended.
    private const int MaxJournalFetches = 20;

    /// <summary>
    /// The dotted actions <see cref="AuditMapping"/>'s <c>From*Event</c> mappers produce that are, post
    /// Phase-C, EXCLUSIVELY sourced from the kgsm event echo. <see cref="KgsmAuditConsumer"/> no longer
    /// writes any of these to the local table (it now only publishes them live — see
    /// <see cref="AuditService.PublishLive"/>), so a local row bearing one is frozen pre-cutover history;
    /// excluding it here means the merge's engine-sourced rows come solely from the journal.
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
        AuditAction.NetworkPortsOpen,
        AuditAction.NetworkPortsClose,
        AuditAction.NetworkUpnpOpen,
        AuditAction.NetworkUpnpClose,
        AuditAction.PlayerJoin,
        AuditAction.PlayerLeave,
        // The moderation trio is cleanly echo-only, like blueprint.*: the endpoints thread actor+origin
        // into the kgsm call and the engine emits the event, so there is no second source to preserve.
        AuditAction.PlayerKick,
        AuditAction.PlayerBan,
        AuditAction.PlayerUnban,
        AuditAction.ConfigSet,
        AuditAction.ConsoleInput,
        // blueprint.* is cleanly echo-only: the library editor's PUT/DELETE thread actor+origin into
        // kgsm-lib's write, which emits the kgsm event — the api never direct-writes one, so unlike
        // network.ports.open there is no second source to preserve here.
        AuditAction.BlueprintWrite,
        AuditAction.BlueprintRevert,
    };

    /// <summary>Clamp a client-supplied limit to <c>[1, <see cref="MaxLimit"/>]</c>, defaulting when unset.</summary>
    public static int ClampLimit(int? limit) =>
        limit is null || limit <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    /// <summary>
    /// The merged, ts-DESC page: local API-only rows ∪ the journal's shaped engine rows. Every filter is
    /// pushed to both sources before merge (<c>serverId</c> as the journal query's instance scope,
    /// <c>since</c> as its lower bound); <c>severity</c>/<c>actor</c>/<c>category</c> have no journal-side
    /// equivalent (the journal holds raw events, not the shaped vocabulary), so they narrow the EF query
    /// via SQL and the shaped engine records via an equivalent in-memory filter, applied identically.
    /// An unreadable journal, or a host with no engine → <see cref="AuditPage.EngineHistoryDegraded"/>
    /// true and local-only rows: never a silent drop, never a 500.
    /// </summary>
    public static async Task<AuditPage> PageMergedAsync(
        AppDbContext db,
        IEventJournalHistory? journal,
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

        (List<AuditRecord> engineRecords, bool degraded, bool journalFull) = await QueryJournalAsync(
            journal, hostId, c, limit, severities, serverId, sinceTs?.ToUnixTimeMilliseconds(),
            categoryPrefix, ct).ConfigureAwait(false);

        List<AuditRecord> merged = localRecords.Concat(engineRecords)
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
        bool hasMore = localRows.Count == limit || journalFull
            || localRecords.Count + engineRecords.Count > limit;

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

    /// <summary>
    /// The engine half of the merge: up to <paramref name="limit"/> <em>shaped</em> records, newest
    /// first, or the journal exhausted.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this loops.</b> A journal page is <paramref name="limit"/> RAW events, and shaping
    /// drops some of them unconditionally — a silent type is not a domain fact, so it never becomes a
    /// row — while severity/actor/category narrow further, having no journal-side equivalent. Returning
    /// whatever survived one fetch under-fills this side of the merge, and that is not merely a short
    /// page: the merged page's last row then comes from the local table, the cursor advances to it, and
    /// every engine event between there and the journal's fetched window is skipped for good. Measured
    /// before this loop existed: one page fetched 200 raw events covering 2026-08-05→08-07, 49 of them
    /// silent, and the cursor landed on a local row at 2026-07-31 — losing four days of engine history
    /// from the feed while every one of those events sat in the journal.
    /// </para>
    /// <para>
    /// So it keeps fetching until it has a full page of records the caller can actually use, or the
    /// journal runs out. <see cref="MaxJournalFetches"/> bounds the work; stopping there reports
    /// <c>journalFull</c>, which keeps <c>hasMore</c> true so the caller pages again rather than being
    /// told the feed ended.
    /// </para>
    /// </remarks>
    private static async Task<(List<AuditRecord> Records, bool Degraded, bool JournalFull)> QueryJournalAsync(
        IEventJournalHistory? journal, string hostId, AuditCursor? cursor, int limit,
        string[]? severities, string? serverId, long? sinceMs, string? categoryPrefix, CancellationToken ct)
    {
        // No engine provisioned on this host: there is no journal to read, which is a missing
        // capability rather than a failure. Reported the same way an unreadable one is — the
        // caller cannot have engine history either way, and must not be told it has all of it.
        if (journal is null) return (new List<AuditRecord>(), true, false);

        var records = new List<AuditRecord>(limit);
        long? beforeTs = cursor?.TsMs;
        string? beforeId = cursor?.Id;
        bool more = false;

        for (int fetch = 0; fetch < MaxJournalFetches; fetch++)
        {
            EventHistoryPage page;
            try
            {
                page = await journal.QueryAsync(new EventHistoryQuery
                {
                    Instance = string.IsNullOrWhiteSpace(serverId) ? null : serverId,
                    Type = null, // no clean 1:1 category->type mapping (a category spans many event types)
                    SinceMs = sinceMs,
                    // Both halves of the cursor. The id is often a LOCAL row's — whichever source
                    // supplied the page's boundary row — so it can name no event in the journal at all;
                    // it is a tie-break only, and the timestamp is what bounds the page. Passing the id
                    // alone would leave the reader unable to place a local cursor, and the walk would
                    // restart from the newest page every time the local side supplied the boundary.
                    BeforeTsMs = beforeTs,
                    BeforeId = beforeId,
                    Limit = limit
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // QueryAsync itself never throws for a missing or unreadable journal (see
                // EventJournalHistory) — this is a last-resort guard so a surprise degrades the page
                // rather than 500ing the whole endpoint. Anything already gathered is kept: a partial
                // engine half is still better than dropping it, and `degraded` says so.
                return (records, true, false);
            }

            // An unreadable journal is not an empty one. Serving "no engine events" for a directory the
            // API cannot read would report silence as fact.
            if (!page.JournalReadable) return (records, true, false);

            foreach (EventHistoryEntry item in page.Events)
            {
                AuditRecord? shaped = EngineEventShaping.Shape(item, hostId);
                if (shaped is null) continue; // deliberately-silent type
                if (severities is { Length: > 0 } && !severities.Contains(shaped.Severity)) continue;
                if (!string.IsNullOrWhiteSpace(serverId) && shaped.ServerId != serverId) continue;
                if (categoryPrefix is not null && !shaped.Action.StartsWith(categoryPrefix, StringComparison.Ordinal)) continue;
                records.Add(shaped);
            }

            // A truncated scan stopped on its byte budget, so there is more behind it either way.
            if (page.NextCursorTsMs is null || page.Truncated)
            {
                more = records.Count > limit || page.Truncated;
                break;
            }

            beforeTs = page.NextCursorTsMs;
            beforeId = page.NextCursorId;

            if (records.Count >= limit)
            {
                more = true;
                break;
            }

            // Ran the fetch budget out with a page still unfilled — honest "there is more", so the
            // caller keeps walking instead of being told the feed ended.
            if (fetch == MaxJournalFetches - 1) more = true;
        }

        // Only the newest `limit` are this page's; the overshoot is re-read from the cursor next time,
        // exactly as the local side's own batch overshoot is.
        if (records.Count > limit)
        {
            records.RemoveRange(limit, records.Count - limit);
            more = true;
        }

        return (records, false, more);
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
/// (ordinal), spanning both the local table and the engine's journal history — a single local
/// <c>rowid</c> cannot address a journal-sourced row. Wire-encoded as
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
