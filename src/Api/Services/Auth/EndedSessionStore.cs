using Microsoft.EntityFrameworkCore;

using TheKrystalShip.Api.Data;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The sessions this node has been told are over.
/// </summary>
/// <remarks>
/// <para>
/// Every session this node accepts was minted by the cluster's auth anchor, verified offline against
/// the key it publishes, and has no row here. So the question asked on every request is a deny-list —
/// has anybody ended this one — and this store is its durable half, behind the cache
/// <see cref="ClusterSessionRevocations"/> keeps on the request path.
/// </para>
/// <para>
/// <b>This node mints nothing, so it holds nothing of its own to end.</b> The revoke handler's first
/// step ends a session the member itself minted; here that step finds none and returns, and the record
/// is what ends the session.
/// </para>
/// <para>
/// Idempotent, and a record is never overwritten: the bus delivers at least once, and a redelivery must
/// not move an end or resurrect a session. Every write sweeps the records whose bearers can no longer be
/// presented, so the table stays bounded with nothing else running.
/// </para>
/// </remarks>
public sealed class EndedSessionStore(IServiceScopeFactory scopeFactory, ILogger<EndedSessionStore> logger)
    : IClusterSessionAuthority
{
    /// <summary>
    /// How long a record of an ended session is kept. Only an access token is ever presented here — a
    /// refresh token is spent at the anchor, which refuses one for a session it has ended — so the
    /// record has to outlive the longest access lifetime an anchor might be configured with. Thirty
    /// days is the anchor's default refresh cap, far past any access lifetime, and a record is a few
    /// bytes, so erring long costs nothing.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

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
                CREATE TABLE IF NOT EXISTS ended_sessions (
                    "SessionId" TEXT NOT NULL CONSTRAINT "PK_ended_sessions" PRIMARY KEY,
                    "EndedAt" INTEGER NOT NULL,
                    "Until" INTEGER NOT NULL
                );
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    public async Task<bool> IsRevokedAsync(string sessionId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.EndedSessions.AsNoTracking()
            .AnyAsync(s => s.SessionId == sessionId, ct)
            .ConfigureAwait(false);
    }

    public async Task RecordRevocationAsync(string sessionId, DateTimeOffset until, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Read into a local: EF cannot translate DateTimeOffset.UtcNow inside the expression.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await db.EndedSessions.Where(s => s.Until < now)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

            if (await db.EndedSessions.AnyAsync(s => s.SessionId == sessionId, ct).ConfigureAwait(false))
                return;

            db.EndedSessions.Add(new EndedSessionEntity { SessionId = sessionId, EndedAt = now, Until = until });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogDebug("session recorded as ended: sid={Sid}", sessionId);
        }
        finally { _writeGate.Release(); }
    }

    public Task RevokeAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> RevokeAllForHandleAsync(string handle, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
