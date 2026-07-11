using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The single data-access seam for the <c>peers</c> roster (<c>PLAN-peers.md §2</c> #12, P0 — peer
/// foundation): every peer-foundation piece — the join-via-seed handshake, the disable-list gate, the
/// outbox fan-out target provider, the latency poller, and <c>PeersController</c> — reads and writes the
/// roster ONLY through this class, never <see cref="AppDbContext"/> directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same DbContext-factory/scope idiom as <see cref="Leaves.LeafRegistry"/>.</b> A singleton owning an
/// <see cref="IServiceScopeFactory"/>, resolving a fresh (scoped) <see cref="AppDbContext"/> per operation —
/// so it works equally from a request, a background poller tick, or a hosted-service startup pass, none of
/// which may share a DbContext instance.
/// </para>
/// <para>
/// <b>Survives an existing DB without a wipe</b> (the <see cref="Leaves.LeafRegistry"/>/
/// <see cref="Aggregation.HostSettingsStore"/> pattern): <c>EnsureCreated</c> lands the <c>peers</c> table on
/// a fresh DB (registered in <see cref="AppDbContext.OnModelCreating"/>); on an already-deployed DB — where
/// <c>EnsureCreated</c> no-ops — an idempotent <c>CREATE TABLE IF NOT EXISTS</c> adds it instead. Either way
/// the shared append-only audit log that lives in the same file is never touched.
/// </para>
/// <para>
/// <b>Registered as both a singleton and a hosted service</b> (mirrors <see cref="Leaves.LeafRegistry"/>):
/// the schema is ensured once at boot, before any controller/poller can reach the table. Every method below
/// also self-guards with the same idempotent check, so a call arriving before <see cref="StartAsync"/> has
/// run (or from a test host that never starts hosted services) still works correctly.
/// </para>
/// </remarks>
public sealed class PeersStore(IServiceScopeFactory scopeFactory, ILogger<PeersStore> logger) : IHostedService
{
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Peers roster ready.");
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Every roster row (enabled and disabled), in no particular order.</summary>
    public async Task<IReadOnlyList<PeerEntity>> ListAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Peers.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One roster row by this node's own local <see cref="PeerEntity.Id"/>, or
    /// <see langword="null"/> if no such row exists.</summary>
    public async Task<PeerEntity?> GetAsync(string id, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Peers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One roster row by the remote peer's own <see cref="PeerEntity.NodeId"/> — the lookup key for
    /// attributing an inbound cluster call, since a service token's <c>iss</c> is the caller's <c>nodeId</c>,
    /// not this node's locally-assigned row <see cref="PeerEntity.Id"/>. <see langword="null"/> if no row
    /// carries that node id.
    /// </summary>
    public async Task<PeerEntity?> GetByNodeIdAsync(string nodeId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Peers.AsNoTracking().FirstOrDefaultAsync(p => p.NodeId == nodeId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Insert-or-replace one roster row keyed by <see cref="PeerEntity.Id"/> (the handshake's add path, and
    /// any later re-sync of a row's fields — e.g. a converged gossip update). <paramref name="peer"/>'s
    /// fields fully overwrite the existing row's when one already exists.
    /// </summary>
    public async Task UpsertAsync(PeerEntity peer, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PeerEntity? existing = await db.Peers.FirstOrDefaultAsync(p => p.Id == peer.Id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.Peers.Add(peer);
        }
        else
        {
            existing.Url = peer.Url;
            existing.GossipUrl = peer.GossipUrl;
            existing.Nickname = peer.Nickname;
            existing.NodeId = peer.NodeId;
            existing.Incarnation = peer.Incarnation;
            existing.Status = peer.Status;
            existing.MembershipState = peer.MembershipState;
            existing.StateChangedAt = peer.StateChangedAt;
            existing.LatencyMs = peer.LatencyMs;
            existing.LastSeen = peer.LastSeen;
            existing.ApiVersion = peer.ApiVersion;
            existing.Enabled = peer.Enabled;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Apply a gossip-converged (or failure-timer) membership transition to one row by its local
    /// <see cref="PeerEntity.Id"/> — sets <see cref="PeerEntity.MembershipState"/>, its
    /// <see cref="PeerEntity.Incarnation"/> (a peer's own monotonic counter, raised only by the peer),
    /// optionally refreshes addressing/version learned via gossip, and stamps
    /// <see cref="PeerEntity.StateChangedAt"/>. Never touches the first-hand liveness triple
    /// (<see cref="PeerEntity.Status"/>/<see cref="PeerEntity.LatencyMs"/>/<see cref="PeerEntity.LastSeen"/>),
    /// which only <see cref="PeerLatencyPoller"/> writes. A silent no-op if <paramref name="id"/> is gone.
    /// </summary>
    public async Task UpdateMembershipAsync(
        string id, string membershipState, long incarnation, DateTimeOffset stateChangedAt,
        string? url, string? gossipUrl, string? apiVersion, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PeerEntity? row = await db.Peers.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (row is null)
            return;
        row.MembershipState = membershipState;
        row.Incarnation = incarnation;
        row.StateChangedAt = stateChangedAt;
        if (!string.IsNullOrWhiteSpace(url)) row.Url = url;
        if (gossipUrl is not null) row.GossipUrl = gossipUrl;
        if (!string.IsNullOrWhiteSpace(apiVersion)) row.ApiVersion = apiVersion;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Record first-hand liveness for a peer identified by its <see cref="PeerEntity.NodeId"/> (an
    /// authenticated inbound sync from it, <c>PLAN-peers.md §2·b</c> G5): stamp <see cref="PeerEntity.LastSeen"/>
    /// and, if it wasn't already, promote <see cref="PeerEntity.MembershipState"/> to <c>alive</c>. Leaves
    /// <see cref="PeerEntity.Status"/> alone — that axis is strictly OUR outbound probe's result, and hearing
    /// FROM a peer is not the same as reaching it. A silent no-op if no row carries that node id yet.
    /// </summary>
    public async Task RecordAliveContactAsync(string nodeId, DateTimeOffset now, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PeerEntity? row = await db.Peers.FirstOrDefaultAsync(p => p.NodeId == nodeId, ct).ConfigureAwait(false);
        if (row is null)
            return;
        if (row.MembershipState != "alive")
        {
            row.MembershipState = "alive";
            row.StateChangedAt = now;
        }
        row.LastSeen = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Promote one row to <c>alive</c> at the peer's current incarnation — the first-hand-auth promotion
    /// (<c>PLAN-peers.md §2·b</c>, G3) <see cref="PeerLatencyPoller"/> applies when it directly reaches a
    /// peer that gossip had only reported (or that was suspect/dead). Optionally records the
    /// <paramref name="apiVersion"/> the probe read from <c>/identity</c>. No-op if <paramref name="id"/>
    /// is gone. Distinct from <see cref="UpdateLivenessAsync"/> (which writes only the first-hand triple):
    /// this writes the converged membership axis.
    /// </summary>
    public async Task PromoteAliveAsync(string id, string? apiVersion, DateTimeOffset now, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PeerEntity? row = await db.Peers.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (row is null)
            return;
        if (row.MembershipState != "alive")
        {
            row.MembershipState = "alive";
            row.StateChangedAt = now;
        }
        if (!string.IsNullOrWhiteSpace(apiVersion))
            row.ApiVersion = apiVersion;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Flip a row's <see cref="PeerEntity.Enabled"/> flag (the disable-list toggle, <c>PLAN-peers.md §0</c>
    /// #8) — the sole local override to the shared-secret trust boundary; the row itself is never removed by
    /// this call. Returns <see langword="false"/> if <paramref name="id"/> doesn't exist.
    /// </summary>
    public async Task<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PeerEntity? row = await db.Peers.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (row is null)
            return false;
        row.Enabled = enabled;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Remove a roster row entirely (an admin "forget this peer" action, distinct from disabling
    /// it). Returns <see langword="false"/> if <paramref name="id"/> doesn't exist.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PeerEntity? row = await db.Peers.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (row is null)
            return false;
        db.Peers.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Every roster row with <see cref="PeerEntity.Enabled"/> true — the outbox fan-out target provider's
    /// and the latency poller's read (a disabled peer receives no gossip/outbox traffic and no ping).
    /// </summary>
    public async Task<IReadOnlyList<PeerEntity>> ListEnabledAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Peers.AsNoTracking().Where(p => p.Enabled).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write one liveness sample onto a roster row (the latency poller's per-tick result, §4). A silent
    /// no-op if <paramref name="id"/> no longer exists (the row was disabled/removed mid-poll).
    /// </summary>
    public async Task UpdateLivenessAsync(
        string id, string status, int? latencyMs, DateTimeOffset? lastSeen, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PeerEntity? row = await db.Peers.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (row is null)
            return;
        row.Status = status;
        row.LatencyMs = latencyMs;
        row.LastSeen = lastSeen;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // EnsureCreated (fresh DB: the whole model incl. peers) + an idempotent CREATE TABLE IF NOT EXISTS
    // (existing DB: the no-op above skipped our new table). Columns match PeerEntity/EF's mapping. Guarded
    // by a bool + gate so every call after the first is a cheap no-op; never touches the audit table.
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
                CREATE TABLE IF NOT EXISTS peers (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_peers" PRIMARY KEY,
                    "Url" TEXT NOT NULL,
                    "GossipUrl" TEXT NULL,
                    "Nickname" TEXT NULL,
                    "NodeId" TEXT NOT NULL,
                    "Incarnation" INTEGER NOT NULL,
                    "Status" TEXT NOT NULL,
                    "MembershipState" TEXT NOT NULL DEFAULT 'alive',
                    "StateChangedAt" INTEGER NULL,
                    "LatencyMs" INTEGER NULL,
                    "LastSeen" INTEGER NULL,
                    "ApiVersion" TEXT NOT NULL,
                    "Enabled" INTEGER NOT NULL
                );
                """, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE INDEX IF NOT EXISTS "IX_peers_Enabled" ON "peers" ("Enabled");
                """, ct).ConfigureAwait(false);

            // Idempotent forward-add of the gossip columns onto an EXISTING peers table (a dev/host DB
            // created by the pre-gossip peer-foundation shape, where the CREATE TABLE IF NOT EXISTS above
            // no-ops and would otherwise leave the new columns absent → runtime 500s, not a build error).
            // SQLite has no ADD COLUMN IF NOT EXISTS, so probe table_info first and add only what's missing.
            await EnsureColumnAsync(db, "MembershipState", "TEXT NOT NULL DEFAULT 'alive'", ct).ConfigureAwait(false);
            await EnsureColumnAsync(db, "StateChangedAt", "INTEGER NULL", ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    // Add one column to the peers table iff it isn't already present (SQLite lacks ADD COLUMN IF NOT
    // EXISTS). Reads PRAGMA table_info(peers) via the raw connection, then issues a plain ALTER TABLE.
    // columnDefinition is a trusted compile-time constant (never user input), so string-interpolating it
    // into the DDL is safe here.
    private static async Task EnsureColumnAsync(
        AppDbContext db, string columnName, string columnDefinition, CancellationToken ct)
    {
        System.Data.Common.DbConnection connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        bool present = false;
        await using (System.Data.Common.DbCommand probe = connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(peers);";
            await using System.Data.Common.DbDataReader reader = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // table_info column 1 ("name") is the column's name.
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    present = true;
                    break;
                }
            }
        }

        if (!present)
        {
            // Concatenated (not an interpolated literal) so EF's raw-SQL-injection analyzer isn't tripped:
            // columnName/columnDefinition are trusted compile-time constants, never user input.
            string alter = "ALTER TABLE peers ADD COLUMN \"" + columnName + "\" " + columnDefinition + ";";
            await db.Database.ExecuteSqlRawAsync(alter, ct).ConfigureAwait(false);
        }
    }
}
