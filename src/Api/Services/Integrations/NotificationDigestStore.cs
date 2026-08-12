using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// Holds the facts waiting to go out in a summary, and hands each provider's batch over exactly once.
/// </summary>
/// <remarks>
/// Same posture as the push stores: a singleton owning a scope per operation, writes behind a gate, and
/// an idempotent <c>CREATE TABLE IF NOT EXISTS</c> beside <c>EnsureCreated</c> so the table also appears
/// on an already-deployed database.
/// </remarks>
public sealed class NotificationDigestStore(IServiceScopeFactory scopeFactory)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <summary>
    /// How long a fact waits before its summary goes out. Measured from the <em>oldest</em> thing waiting,
    /// not from a wall clock: a digest then arrives this long after the first thing that would have been
    /// in it, which needs no notion of what hour counts as morning and no timezone to be wrong about.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(6);

    /// <summary>
    /// The most facts one summary carries. Past this the list stops being readable on a lock screen, so
    /// the rest are counted rather than named — and counted honestly, never dropped silently.
    /// </summary>
    public const int MaxListed = 8;

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
                CREATE TABLE IF NOT EXISTS notification_digest (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_notification_digest" PRIMARY KEY,
                    "Provider" TEXT NOT NULL,
                    "CatalogId" TEXT NOT NULL,
                    "Action" TEXT NOT NULL,
                    "ServerId" TEXT NULL,
                    "Severity" TEXT NOT NULL,
                    "Summary" TEXT NOT NULL,
                    "Ts" INTEGER NOT NULL
                );
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>Hold one fact for one provider.</summary>
    public async Task AddAsync(string provider, NotificationEvent ev, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.NotificationDigests.Add(new NotificationDigestEntity
            {
                Id = "dg_" + Guid.NewGuid().ToString("N")[..12],
                Provider = provider,
                CatalogId = ev.CatalogId,
                Action = ev.Action,
                ServerId = ev.ServerId,
                Severity = ev.Severity,
                Summary = ev.Summary,
                Ts = ev.Ts,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Take everything a provider is holding, if the oldest of it has waited long enough.
    /// </summary>
    /// <remarks>
    /// <b>The rows are deleted here, before anything is sent.</b> The alternative — send, then delete — is
    /// what turns one failed POST into the same summary arriving every window until it succeeds. A digest
    /// is a convenience; losing one to a failed send is a smaller wrong than repeating it forever, and the
    /// send failure is logged either way.
    /// </remarks>
    public async Task<IReadOnlyList<NotificationDigestEntity>> TakeDueAsync(
        string provider, DateTimeOffset now, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            List<NotificationDigestEntity> pending = await db.NotificationDigests
                .Where(d => d.Provider == provider)
                .OrderBy(d => d.Ts)
                .ToListAsync(ct).ConfigureAwait(false);

            if (pending.Count == 0 || now - pending[0].Ts < Window) return [];

            db.NotificationDigests.RemoveRange(pending);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return pending;
        }
        finally { _writeGate.Release(); }
    }
}
