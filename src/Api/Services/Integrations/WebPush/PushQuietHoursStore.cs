using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>
/// Per-account quiet windows. Same posture as its siblings here: a singleton owning a scope per operation,
/// writes behind a gate, and an idempotent <c>CREATE TABLE IF NOT EXISTS</c> beside <c>EnsureCreated</c> so
/// the table also appears on an already-deployed database.
/// </summary>
public sealed class PushQuietHoursStore(IServiceScopeFactory scopeFactory, ILogger<PushQuietHoursStore> logger)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_ensured) return;
        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured) return;
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS push_quiet_hours (
                    "UserSubject" TEXT NOT NULL CONSTRAINT "PK_push_quiet_hours" PRIMARY KEY,
                    "Enabled" INTEGER NOT NULL,
                    "StartMinute" INTEGER NOT NULL,
                    "EndMinute" INTEGER NOT NULL,
                    "TimeZoneId" TEXT NOT NULL,
                    "MinSeverity" TEXT NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL
                );
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>One account's window, or <see langword="null"/> when it has never set one.</summary>
    public async Task<PushQuietHoursEntity?> ForUserAsync(string subject, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PushQuietHours.AsNoTracking()
            .FirstOrDefaultAsync(q => q.UserSubject == subject, ct).ConfigureAwait(false);
    }

    /// <summary>Every enabled window, for the fan-out's one read per send.</summary>
    public async Task<IReadOnlyList<PushQuietHoursEntity>> ActiveAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PushQuietHours.AsNoTracking().Where(q => q.Enabled).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Store one account's window, replacing whatever it had.</summary>
    public async Task SetAsync(PushQuietHoursEntity row, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            PushQuietHoursEntity? existing = await db.PushQuietHours
                .FirstOrDefaultAsync(q => q.UserSubject == row.UserSubject, ct).ConfigureAwait(false);

            if (existing is null) db.PushQuietHours.Add(row);
            else
            {
                existing.Enabled = row.Enabled;
                existing.StartMinute = row.StartMinute;
                existing.EndMinute = row.EndMinute;
                existing.TimeZoneId = row.TimeZoneId;
                existing.MinSeverity = row.MinSeverity;
                existing.UpdatedAt = row.UpdatedAt;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Whether <paramref name="row"/>'s window is open at <paramref name="instant"/>.
    /// </summary>
    /// <remarks>
    /// <b>An unresolvable zone opens nothing.</b> A host without that entry in its tzdata cannot say what
    /// time it is where the person is, and guessing would silence a stretch of somebody's day chosen at
    /// random. This gate exists to hold notifications back, so the failure has to be the direction that
    /// delivers them — the cost of being wrong that way is a buzz at a bad hour, and the other way it is
    /// an outage nobody was told about.
    /// </remarks>
    public bool IsQuiet(PushQuietHoursEntity row, DateTimeOffset instant)
    {
        if (!row.Enabled) return false;

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(row.TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("quiet hours for {User} name the zone '{Zone}', which this host cannot resolve — "
                + "delivering rather than guessing at their local time", row.UserSubject, row.TimeZoneId);
            return false;
        }

        int minute = TimeZoneInfo.ConvertTime(instant, zone).TimeOfDay is var t
            ? (int)t.TotalMinutes
            : 0;

        // A window that wraps midnight is the normal one, so the two cases are equally first-class: within
        // a same-day span, or outside the gap a wrapping span leaves.
        return row.StartMinute <= row.EndMinute
            ? minute >= row.StartMinute && minute < row.EndMinute
            : minute >= row.StartMinute || minute < row.EndMinute;
    }
}
