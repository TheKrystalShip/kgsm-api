using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>One leaf override row, decoupled from the EF entity: a manifest key + its (possibly secret) value.</summary>
public sealed record LeafOverrideRow(string Key, string? Value, bool IsSecret);

/// <summary>
/// The single reader/writer of the <c>leaf_override</c> table (the leaf-runtime-config feature) — the
/// persisted source of truth the override file is rendered from. A singleton owning its own DI scope per op
/// (the <see cref="Audit.AuditService"/>/<c>IntegrationStore</c> pattern), writes serialized behind a gate
/// (SQLite single-writer).
/// </summary>
/// <remarks>
/// <b>Secret hygiene:</b> a secret value lives here plaintext (host-local SQLite, the same trust as the
/// env-stored bot token) — it is NEVER returned on the read API, NEVER logged, and only ever flows to the
/// <c>0600</c> override file. <b>Survives an existing DB without a wipe</b> via EnsureCreated +
/// <c>CREATE TABLE IF NOT EXISTS</c> (the audit log untouched).
/// </remarks>
public sealed class LeafOverrideStore(IServiceScopeFactory scopeFactory)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <summary>All override rows for a leaf (empty when none), in key order.</summary>
    public async Task<IReadOnlyList<LeafOverrideRow>> GetAsync(string leafId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<LeafOverrideEntity> rows = await db.LeafOverrides.AsNoTracking()
            .Where(o => o.LeafId == leafId)
            .OrderBy(o => o.Key)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new LeafOverrideRow(r.Key, r.Value, r.IsSecret)).ToList();
    }

    /// <summary>
    /// Set a leaf's overrides to <strong>exactly</strong> <paramref name="rows"/> (delete the rest) — the
    /// atomic primitive the apply broker uses for both applying a new set and restoring a snapshot on
    /// rollback. Serialized (SQLite single-writer).
    /// </summary>
    public async Task ReplaceAsync(string leafId, IReadOnlyList<LeafOverrideRow> rows, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            List<LeafOverrideEntity> existing = await db.LeafOverrides
                .Where(o => o.LeafId == leafId).ToListAsync(ct).ConfigureAwait(false);
            db.LeafOverrides.RemoveRange(existing);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (LeafOverrideRow row in rows)
            {
                db.LeafOverrides.Add(new LeafOverrideEntity
                {
                    LeafId = leafId,
                    Key = row.Key,
                    Value = row.Value,
                    IsSecret = row.IsSecret,
                    UpdatedAt = now,
                });
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

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
                CREATE TABLE IF NOT EXISTS leaf_override (
                    "LeafId" TEXT NOT NULL,
                    "Key" TEXT NOT NULL,
                    "Value" TEXT NULL,
                    "IsSecret" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL,
                    CONSTRAINT "PK_leaf_override" PRIMARY KEY ("LeafId", "Key")
                );
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }
}
