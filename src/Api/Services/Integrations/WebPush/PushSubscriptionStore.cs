using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>
/// The reader/writer of the push-subscription table. A singleton owning a DI scope per operation and
/// serializing writes behind a gate — the same posture as <see cref="IntegrationStore"/> and
/// <c>HostSettingsStore</c> (SQLite is single-writer).
/// </summary>
/// <remarks>
/// Schema creation follows the house pattern for a table added after the DB exists: EnsureCreated
/// covers a fresh DB, and an idempotent <c>CREATE TABLE IF NOT EXISTS</c> matching EF's mapping covers
/// the already-deployed one. <b>This is not belt-and-braces</b> — EnsureCreated no-ops against an
/// existing database, so without the raw DDL this table would simply never appear on the live host and
/// every push call would 500 at runtime rather than fail at build.
/// </remarks>
public sealed class PushSubscriptionStore(IServiceScopeFactory scopeFactory)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <summary>Retire a row after this many consecutive non-definitive failures. A push service can
    /// have a bad hour; it should not keep a dead endpoint forever either.</summary>
    private const int MaxFailures = 10;

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
                CREATE TABLE IF NOT EXISTS push_subscriptions (
                    "Endpoint" TEXT NOT NULL CONSTRAINT "PK_push_subscriptions" PRIMARY KEY,
                    "UserSubject" TEXT NOT NULL,
                    "Username" TEXT NULL,
                    "P256dh" TEXT NOT NULL,
                    "Auth" TEXT NOT NULL,
                    "UserAgent" TEXT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "LastSeenAt" INTEGER NULL,
                    "FailureCount" INTEGER NOT NULL
                );
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>Every subscription on this host — the fan-out set for a delivery.</summary>
    public async Task<IReadOnlyList<PushSubscriptionEntity>> AllAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PushSubscriptions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One user's own devices. Always scoped by subject — a device list is personal.</summary>
    public async Task<IReadOnlyList<PushSubscriptionEntity>> ForUserAsync(string subject, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PushSubscriptions.AsNoTracking()
            .Where(s => s.UserSubject == subject)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Register or refresh a device. Keyed on the endpoint, which the browser owns: re-subscribing the
    /// same browser must update the existing row, never accumulate duplicates that each get a copy of
    /// every notification.
    /// </summary>
    public async Task SaveAsync(PushSubscriptionEntity sub, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PushSubscriptionEntity? row = await db.PushSubscriptions
                .FirstOrDefaultAsync(s => s.Endpoint == sub.Endpoint, ct).ConfigureAwait(false);
            if (row is null)
            {
                sub.CreatedAt = sub.CreatedAt == default ? DateTimeOffset.UtcNow : sub.CreatedAt;
                db.PushSubscriptions.Add(sub);
            }
            else
            {
                // An endpoint can be re-issued to the same browser under a different signed-in user;
                // the row follows whoever holds it now.
                row.UserSubject = sub.UserSubject;
                row.Username = sub.Username;
                row.P256dh = sub.P256dh;
                row.Auth = sub.Auth;
                row.UserAgent = sub.UserAgent ?? row.UserAgent;
                row.FailureCount = 0;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Remove one device, scoped to its owner. Returns whether a row went.</summary>
    public async Task<bool> DeleteAsync(string subject, string endpoint, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            int n = await db.PushSubscriptions
                .Where(s => s.Endpoint == endpoint && s.UserSubject == subject)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return n > 0;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Drop a row the push service has declared dead (404/410). Not scoped to a user: this is
    /// the service telling us the endpoint no longer exists, whoever owns it.</summary>
    public async Task ForgetAsync(string endpoint, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PushSubscriptions.Where(s => s.Endpoint == endpoint)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Record the outcome of a send: a success clears the failure run and stamps liveness, a
    /// failure counts toward retirement.</summary>
    public async Task RecordOutcomeAsync(string endpoint, bool ok, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PushSubscriptionEntity? row = await db.PushSubscriptions
                .FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct).ConfigureAwait(false);
            if (row is null) return;
            if (ok)
            {
                row.LastSeenAt = DateTimeOffset.UtcNow;
                row.FailureCount = 0;
            }
            else if (++row.FailureCount >= MaxFailures)
            {
                db.PushSubscriptions.Remove(row);
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }
}
