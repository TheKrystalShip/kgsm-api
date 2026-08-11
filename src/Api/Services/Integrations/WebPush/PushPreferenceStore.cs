using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>
/// Per-account push preferences. Same posture as <see cref="PushSubscriptionStore"/>: a singleton
/// owning a scope per operation, writes behind a gate, and an idempotent <c>CREATE TABLE IF NOT
/// EXISTS</c> beside <c>EnsureCreated</c> so the table also appears on an already-deployed database.
/// </summary>
public sealed class PushPreferenceStore(IServiceScopeFactory scopeFactory)
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
                CREATE TABLE IF NOT EXISTS push_preferences (
                    "UserSubject" TEXT NOT NULL,
                    "CatalogId" TEXT NOT NULL,
                    "Enabled" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL,
                    CONSTRAINT "PK_push_preferences" PRIMARY KEY ("UserSubject", "CatalogId")
                );
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>
    /// One account's explicit choices, as catalog id → wanted. An id absent from the map has no stored
    /// choice and therefore defaults ON — callers must treat "missing" as yes, not no.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, bool>> ForUserAsync(string subject, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PushPreferences.AsNoTracking()
            .Where(p => p.UserSubject == subject)
            .ToDictionaryAsync(p => p.CatalogId, p => p.Enabled, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Every account's choices in one read, as (subject, catalogId) → wanted. The delivery fan-out
    /// needs them for whoever is subscribed, and one query beats one per device.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string Subject, string CatalogId), bool>> AllAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<PushPreferenceEntity> rows = await db.PushPreferences.AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(p => (p.UserSubject, p.CatalogId), p => p.Enabled);
    }

    /// <summary>Upsert a sparse set of choices for one account. Only the ids present change.</summary>
    public async Task SetAsync(string subject, IReadOnlyDictionary<string, bool> choices, CancellationToken ct = default)
    {
        if (choices.Count == 0) return;
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            List<PushPreferenceEntity> existing = await db.PushPreferences
                .Where(p => p.UserSubject == subject).ToListAsync(ct).ConfigureAwait(false);

            foreach ((string catalogId, bool enabled) in choices)
            {
                PushPreferenceEntity? row = existing.Find(p => p.CatalogId == catalogId);
                if (row is null)
                    db.PushPreferences.Add(new PushPreferenceEntity
                    {
                        UserSubject = subject, CatalogId = catalogId,
                        Enabled = enabled, UpdatedAt = DateTimeOffset.UtcNow,
                    });
                else
                {
                    row.Enabled = enabled;
                    row.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }
}
