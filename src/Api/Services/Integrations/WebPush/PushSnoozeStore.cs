using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>
/// Per-account, per-condition push snoozes — read once per fan-out, written by a notification button.
/// </summary>
/// <remarks>
/// Only live rows are ever returned, and every write sweeps the lapsed ones, so a snooze cannot quietly
/// become permanent through a row nobody looks at again. Same schema posture as its siblings here.
/// </remarks>
public sealed class PushSnoozeStore(IServiceScopeFactory scopeFactory)
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
                CREATE TABLE IF NOT EXISTS push_snoozes (
                    "UserSubject" TEXT NOT NULL,
                    "SubjectKey" TEXT NOT NULL,
                    "ExpiresAt" INTEGER NOT NULL,
                    CONSTRAINT "PK_push_snoozes" PRIMARY KEY ("UserSubject", "SubjectKey")
                );
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>Every live snooze, as the (account, condition) pairs the fan-out checks. One read for
    /// the whole host, because the alternative is a query per device.</summary>
    public async Task<IReadOnlySet<(string Subject, string Condition)>> ActiveAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<PushSnoozeEntity> rows = await db.PushSnoozes.AsNoTracking()
            .Where(s => s.ExpiresAt > now)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => (r.UserSubject, r.SubjectKey)).ToHashSet();
    }

    /// <summary>Silence one condition for one account until <paramref name="until"/>. Idempotent — a
    /// second tap extends the existing row rather than failing on its key.</summary>
    public async Task SetAsync(string subject, string condition, DateTimeOffset until, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // The clock is read into a local first: EF cannot translate DateTimeOffset.UtcNow inside
            // the expression and throws at execution rather than at build.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await db.PushSnoozes.Where(s => s.ExpiresAt < now)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

            PushSnoozeEntity? row = await db.PushSnoozes
                .FirstOrDefaultAsync(s => s.UserSubject == subject && s.SubjectKey == condition, ct)
                .ConfigureAwait(false);
            if (row is null)
                db.PushSnoozes.Add(new PushSnoozeEntity { UserSubject = subject, SubjectKey = condition, ExpiresAt = until });
            else
                row.ExpiresAt = until;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }
}
