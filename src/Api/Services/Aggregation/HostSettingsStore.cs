using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>The resolved, editable identity overrides for this host. <see cref="Label"/>/<see cref="Region"/>
/// are <see langword="null"/> when no override is stored (the caller falls back to config).</summary>
public sealed record HostSettingsRecord(string? Label, string? Region, DateTimeOffset? UpdatedAt)
{
    public static readonly HostSettingsRecord Empty = new(null, null, null);
}

/// <summary>
/// The single reader/writer of the <c>host_settings</c> table — this host's operator-editable identity
/// overrides (region/label, the runtime-mutable half of the identity card). A singleton owning its own DI
/// scope per operation (the same pattern as <see cref="Audit.AuditService"/>/<c>IntegrationStore</c>), with
/// the resolved row cached in memory (it changes only on an admin PATCH) so neither <c>GET /hosts</c> nor
/// the open <c>GET /api/v1</c> handshake touches the DB on the hot path.
/// </summary>
/// <remarks>
/// <b>Survives an existing DB without a wipe.</b> The schema is created via <c>EnsureCreated</c> (project
/// dev authority — no EF migrations), which <em>no-ops on an already-created DB</em>. Since this table is
/// new and the DB also holds the append-only audit log we must not destroy, <see cref="EnsureSchemaAsync"/>
/// follows EnsureCreated with an idempotent <c>CREATE TABLE IF NOT EXISTS</c> matching EF's mapping — so the
/// table appears on a freshly-created DB (via EnsureCreated) AND on a pre-existing deployed DB (via the raw
/// DDL), with the audit rows untouched either way.
/// </remarks>
public sealed class HostSettingsStore(IServiceScopeFactory scopeFactory, ApiOptions options)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;
    // Reference assignment is atomic; a racing read may briefly see the prior value (acceptable — the
    // next read converges). Set under the write gate / after a confirmed DB read.
    private HostSettingsRecord? _cache;

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_ensured) return;
        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured) return;
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Fresh DB: creates the whole model (incl. host_settings). Existing DB: no-ops.
            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            // Existing DB: the no-op above skipped our new table, so create it explicitly. Idempotent, and
            // it never touches the audit table. Columns/types match EF's mapping (UpdatedAt = UTC ticks INTEGER).
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS host_settings (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_host_settings" PRIMARY KEY,
                    "Label" TEXT NULL,
                    "Region" TEXT NULL,
                    "UpdatedAt" INTEGER NULL
                );
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>The stored overrides for this host, or <see cref="HostSettingsRecord.Empty"/> if none.</summary>
    public async Task<HostSettingsRecord> GetAsync(CancellationToken ct = default)
    {
        if (_cache is { } cached) return cached;
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        HostSettingsEntity? entity = await db.HostSettings.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == options.HostId, ct).ConfigureAwait(false);
        HostSettingsRecord record = entity is null
            ? HostSettingsRecord.Empty
            : new HostSettingsRecord(entity.Label, entity.Region, entity.UpdatedAt);
        _cache = record;
        return record;
    }

    /// <summary>Upsert this host's overrides (serialized; SQLite single-writer). A <see langword="null"/>
    /// field clears that override (the resolver falls back to config). Returns the saved record.</summary>
    public async Task<HostSettingsRecord> SaveAsync(string? label, string? region, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            HostSettingsEntity? entity = await db.HostSettings
                .FirstOrDefaultAsync(h => h.Id == options.HostId, ct).ConfigureAwait(false);
            if (entity is null)
            {
                entity = new HostSettingsEntity { Id = options.HostId };
                db.HostSettings.Add(entity);
            }
            entity.Label = label;
            entity.Region = region;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            HostSettingsRecord record = new(entity.Label, entity.Region, entity.UpdatedAt);
            _cache = record;
            return record;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Effective display label: the stored override, else the configured label (which itself
    /// defaults to the host id — never blank).</summary>
    public string EffectiveLabel(HostSettingsRecord record) => record.Label ?? options.HostLabel;

    /// <summary>Effective region: the stored override, else the configured <c>KGSM_API_REGION</c>
    /// (null when neither is set — honest unknown, never fabricated).</summary>
    public string? EffectiveRegion(HostSettingsRecord record) => record.Region ?? options.Region;
}
