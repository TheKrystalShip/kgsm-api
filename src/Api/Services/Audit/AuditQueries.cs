using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// The read side of the audit log — keyset pagination (architecture.html §3·d, §6), newest first.
/// Pure query logic over an <see cref="AppDbContext"/> (resolved on the request scope by the
/// controller), so it is unit-testable against a real SQLite without the write path.
/// </summary>
/// <remarks>
/// Pagination is keyset, not offset: <c>?cursor=&lt;rowid&gt;</c> returns rows with <c>RowId &lt; cursor</c>
/// ordered <c>DESC</c> — index-friendly and stable as new rows land (an offset would skip/repeat rows as
/// the head grows). The <c>severity</c>/<c>serverId</c>/<c>actor</c> filters map 1:1 to the indexed
/// columns. <see cref="AuditPage.NextCursor"/> is emitted only when the page came back full (a short
/// page means there are no older rows).
/// </remarks>
public static class AuditQueries
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>Clamp a client-supplied limit to <c>[1, <see cref="MaxLimit"/>]</c>, defaulting when unset.</summary>
    public static int ClampLimit(int? limit) =>
        limit is null || limit <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    /// <summary>Parse a client cursor string to a rowid, or null (absent/garbage → start from newest).</summary>
    public static long? ParseCursor(string? cursor) =>
        long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out long c) && c > 0
            ? c : null;

    public static async Task<AuditPage> PageAsync(
        AppDbContext db,
        long? cursor,
        int limit,
        string? severity,
        string? serverId,
        string? actor,
        string? since,
        string? category,
        CancellationToken ct)
    {
        IQueryable<AuditEntry> q = db.Audit.AsNoTracking();

        if (cursor is { } c) q = q.Where(a => a.RowId < c);

        // severity accepts a comma-separated set (the UI's "attention" = "warn,danger");
        // a single value keeps the indexed equality. Blank/whitespace entries are dropped.
        if (!string.IsNullOrWhiteSpace(severity))
        {
            string[] sevs = severity.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (sevs.Length == 1) q = q.Where(a => a.Severity == sevs[0]);
            else if (sevs.Length > 1) q = q.Where(a => sevs.Contains(a.Severity));
        }
        if (!string.IsNullOrWhiteSpace(serverId)) q = q.Where(a => a.ServerId == serverId);
        if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(a => a.ActorName == actor);

        // since: only rows at/after this instant (the UI's time-range tabs → ?since=ISO).
        // RowId order is a monotonic proxy for time, so this rides the keyset scan. An
        // unparseable value is ignored (no filter) — never a silently empty page.
        if (!string.IsNullOrWhiteSpace(since)
            && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset sinceTs))
        {
            q = q.Where(a => a.Ts >= sinceTs);
        }

        // category: the action group = the dotted prefix (server.* / player.* / backup.* …),
        // matching the FE's actionCategory(). The trailing dot stops "server" matching a
        // hypothetical "serverfoo.x".
        if (!string.IsNullOrWhiteSpace(category))
        {
            string prefix = category.Trim() + ".";
            q = q.Where(a => a.Action.StartsWith(prefix));
        }

        List<AuditEntry> rows = await q
            .OrderByDescending(a => a.RowId)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        string? next = rows.Count == limit && rows.Count > 0
            ? rows[^1].RowId.ToString(CultureInfo.InvariantCulture)
            : null;

        IReadOnlyList<AuditRecord> data = rows.Select(AuditMapping.ToRecord).ToList();
        return new AuditPage(data, next);
    }
}
