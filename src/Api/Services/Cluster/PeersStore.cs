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
            existing.LatencyMs = peer.LatencyMs;
            existing.LastSeen = peer.LastSeen;
            existing.ApiVersion = peer.ApiVersion;
            existing.Enabled = peer.Enabled;
        }
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
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }
}
