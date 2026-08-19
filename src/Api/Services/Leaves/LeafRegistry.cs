using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>One provisionable leaf's resolved runtime state — whether it is connected on this host, and an
/// optional (forward-compat) endpoint override.</summary>
public sealed record LeafProvisioningState(bool Provisioned, string? Endpoint);

/// <summary>
/// The DB-backed, in-memory-cached, <strong>mutable</strong> registry of which leaves are provisioned
/// (connected) on this host — the runtime-flippable replacement for the immutable startup
/// <see cref="ApiOptions"/> <c>*Provisioned</c> flags (the leaf-runtime-provisioning feature, Phase 1). The
/// <see cref="LeafHealthMonitor"/> reads it each tick (so a flip lights up / tears down the §4·b capability
/// live), the leaf clients gate their probes/scrapes on it (so disconnect disarms data flow without a
/// restart), and the Services board reads it for the per-leaf <c>provisioned</c> flag.
/// </summary>
/// <remarks>
/// <para><b>Synchronous, always-correct reads.</b> <see cref="IsProvisioned"/> is on hot paths (every metrics
/// scrape), so it never touches the DB: the in-memory cache is seeded from config in the constructor (so it
/// is correct from the very first read, identical to the pre-feature behaviour), then reconciled with the
/// persisted rows by <see cref="StartAsync"/>: the persisted flag stands — so a runtime flip survives a
/// restart — unless the host's configuration itself moved since that row was written, which is why each row
/// also records the seed it was written against. A connect/disconnect updates the cache immediately and
/// persists.</para>
/// <para><b>Survives an existing DB without a wipe</b> (the <see cref="Aggregation.HostSettingsStore"/>
/// pattern): EnsureCreated for a fresh DB + an idempotent <c>CREATE TABLE IF NOT EXISTS</c> for a deployed
/// one, the shared audit log untouched.</para>
/// </remarks>
public sealed class LeafRegistry : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ApiOptions _options;
    private readonly ILogger<LeafRegistry> _logger;

    // Single source of truth at runtime. Concurrent: read from many threads (probes/scrapes), written by
    // the loader + connect/disconnect. Reference-typed value, replaced atomically per leaf.
    private readonly ConcurrentDictionary<string, LeafProvisioningState> _state = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public LeafRegistry(IServiceScopeFactory scopeFactory, ApiOptions options, ILogger<LeafRegistry> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;

        // Seed the in-memory cache from config IMMEDIATELY (synchronous, no DB) so IsProvisioned is correct
        // before StartAsync runs — preserving the exact pre-feature, config-derived behaviour on first read.
        foreach (string leaf in ProvisionableLeaf.All)
            _state[leaf] = new LeafProvisioningState(SeedFromConfig(leaf), Endpoint: null);
    }

    /// <summary>Whether <paramref name="leafId"/> is provisioned (connected) on this host. Synchronous, lock-free,
    /// always-current. A non-provisionable / unknown id is always <c>false</c>.</summary>
    public bool IsProvisioned(string leafId) =>
        _state.TryGetValue(leafId, out LeafProvisioningState? s) && s.Provisioned;

    /// <summary>The forward-compat endpoint override for <paramref name="leafId"/> (Phase 1: always null →
    /// callers use the configured-or-default endpoint). Reserved for a future per-leaf endpoint rewire.</summary>
    public string? EndpointFor(string leafId) =>
        _state.TryGetValue(leafId, out LeafProvisioningState? s) ? s.Endpoint : null;

    /// <summary>Flip a leaf's provisioning and persist it. Updates the in-memory cache immediately (so the
    /// next probe/scrape sees it) then writes the DB. A non-provisionable id is rejected by the caller (404);
    /// this is defensive.</summary>
    public async Task SetProvisionedAsync(string leafId, bool provisioned, CancellationToken ct = default)
    {
        string? endpoint = _state.TryGetValue(leafId, out LeafProvisioningState? s) ? s.Endpoint : null;
        _state[leafId] = new LeafProvisioningState(provisioned, endpoint); // arm/disarm live, before the DB write
        await PersistAsync(leafId, provisioned, endpoint, ct).ConfigureAwait(false);
    }

    /// <summary>Load persisted rows + seed-persist any missing leaf, once at startup. Runs as a hosted service
    /// BEFORE the <see cref="LeafHealthMonitor"/> (registration order) so the first capability poll reads the
    /// reconciled state.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureSchemaAsync(db, ct).ConfigureAwait(false);

            List<LeafRegistryEntity> rows = await db.LeafRegistryEntries.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            var byId = rows.ToDictionary(r => r.LeafId, StringComparer.Ordinal);

            foreach (string leaf in ProvisionableLeaf.All)
            {
                bool configProvisioned = SeedFromConfig(leaf);
                if (byId.TryGetValue(leaf, out LeafRegistryEntity? row))
                {
                    // Two different questions, and only the recorded seed can tell them apart: an explicit
                    // runtime flip survives a restart, while a genuine change to the host's configuration
                    // (an endpoint added or removed) takes effect on the next start. Comparing the stored
                    // flag against the current seed cannot distinguish them — a runtime connect on a leaf
                    // config never mentioned looks exactly like a seed that changed, and gets wiped. So
                    // compare seed to seed; the flag is only ever overwritten when the seed actually moved.
                    bool seedChanged = row.ConfigSeed is bool was && was != configProvisioned;
                    if (seedChanged)
                        row.Provisioned = configProvisioned;
                    if (seedChanged || row.ConfigSeed is null)
                    {
                        row.ConfigSeed = configProvisioned;
                        row.UpdatedAt = DateTimeOffset.UtcNow;
                        db.LeafRegistryEntries.Update(row); // read AsNoTracking above
                    }
                    _state[leaf] = new LeafProvisioningState(row.Provisioned, row.Endpoint);
                }
                else
                {
                    LeafProvisioningState seed = new(configProvisioned, Endpoint: null);
                    _state[leaf] = seed;
                    db.LeafRegistryEntries.Add(new LeafRegistryEntity
                    {
                        LeafId = leaf,
                        Provisioned = seed.Provisioned,
                        ConfigSeed = configProvisioned,
                        Endpoint = seed.Endpoint,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Degrade gracefully: a load failure leaves the config-seeded cache in place (the pre-feature
            // behaviour), never crashes startup.
            _logger.LogWarning(ex, "leaf registry load failed; falling back to config-derived provisioning.");
        }
        finally { _writeGate.Release(); }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task PersistAsync(string leafId, bool provisioned, string? endpoint, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureSchemaAsync(db, ct).ConfigureAwait(false);

            LeafRegistryEntity? row = await db.LeafRegistryEntries
                .FirstOrDefaultAsync(r => r.LeafId == leafId, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new LeafRegistryEntity { LeafId = leafId };
                db.LeafRegistryEntries.Add(row);
            }
            row.Provisioned = provisioned;
            // Stamp the seed this flip was made against, so the next start compares like with like.
            row.ConfigSeed = SeedFromConfig(leafId);
            row.Endpoint = endpoint;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    private bool SeedFromConfig(string leaf) => leaf switch
    {
        ProvisionableLeaf.Monitor => _options.MetricsProvisioned,
        ProvisionableLeaf.Watchdog => _options.WatchdogProvisioned,
        ProvisionableLeaf.Assistant => _options.AssistantProvisioned,
        ProvisionableLeaf.Firewall => _options.FirewallProvisioned,
        ProvisionableLeaf.Scheduler => _options.SchedulerProvisioned,
        ProvisionableLeaf.Reactor => _options.ReactorProvisioned,
        _ => false,
    };

    // EnsureCreated (fresh DB: the whole model incl. leaf_registry) + an idempotent CREATE TABLE IF NOT
    // EXISTS (existing DB: the no-op above skipped our new table). Columns match EF's mapping. Never touches
    // the audit table.
    private static async Task EnsureSchemaAsync(AppDbContext db, CancellationToken ct)
    {
        await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS leaf_registry (
                "LeafId" TEXT NOT NULL CONSTRAINT "PK_leaf_registry" PRIMARY KEY,
                "Provisioned" INTEGER NOT NULL,
                "ConfigSeed" INTEGER NULL,
                "Endpoint" TEXT NULL,
                "UpdatedAt" INTEGER NOT NULL
            );
            """, ct).ConfigureAwait(false);

        // A host deployed before ConfigSeed existed has the table already, so neither statement above adds
        // the column. SQLite has no ADD COLUMN IF NOT EXISTS, so ask first. Nullable and unstamped: an
        // existing row's seed is genuinely unknown, and the first start records it rather than inventing it.
        if (!await ColumnExistsAsync(db, "leaf_registry", nameof(LeafRegistryEntity.ConfigSeed), ct).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE leaf_registry ADD COLUMN "ConfigSeed" INTEGER NULL;""", ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ColumnExistsAsync(AppDbContext db, string table, string column, CancellationToken ct)
    {
        System.Data.Common.DbConnection conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);

        using System.Data.Common.DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        object? count = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(count) > 0;
    }
}
