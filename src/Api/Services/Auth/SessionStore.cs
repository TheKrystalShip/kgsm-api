using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The single reader/writer of the <c>sessions</c> table (M4·c — the session registry backing JWT
/// revocation). A singleton owning its own DI scope per operation — the same pattern as
/// <see cref="Audit.AuditService"/> — so writes arriving off the request path (a future background
/// GC worker, the auth.logout/revoke endpoints) all serialize on the same gate without capturing a
/// request-scoped <see cref="AppDbContext"/>. Writes are serialized (SQLite single-writer).
/// </summary>
/// <remarks>
/// <b>Survives an existing DB without a wipe</b>: a fresh DB has the <c>sessions</c> table created
/// automatically by <c>EnsureCreated</c> (it is registered in <see cref="AppDbContext"/>'s model).
/// On the already-deployed prod DB the table is added by a one-shot <c>sqlite3</c> command (D11) —
/// done before the new code ships; this store assumes the table exists (it does NOT issue a
/// <c>CREATE TABLE IF NOT EXISTS</c>, unlike <c>HostSettingsStore</c>/<c>LeafRegistry</c> which had
/// to — those lands before the one-shot migration script posture). If <c>EnsureCreated</c> no-ops
/// on the existing DB AND the one-shot command hasn't run, the first read/write throws — which is
/// the honest signal the operator must run the one-shot first. See <c>docs/session-management-plan.md</c> §9.
/// </remarks>
public sealed class SessionStore(
    IServiceScopeFactory scopeFactory,
    ILogger<SessionStore> logger)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Insert a new session row minted at the OAuth callback. Called once per login — this row IS
    /// the session (its <see cref="SessionEntry.Id"/> is the JWT <c>sid</c> claim). <paramref name="userAgent"/>
    /// is the raw <c>User-Agent</c> header (D5 — raw, no IP). Best-effort: a failed insert must
    /// never break login (the caller catches + continues — the per-request validator will only land in
    /// Increment 4, so a missing row here is inert until then; once it lands, a missing row rejects,
    /// which forces a relogin — the honest recovery for a missing session).
    /// </summary>
    public async Task CreateAsync(
        string sessionId, string userId, string hostId,
        DateTimeOffset created, DateTimeOffset expires,
        string? userAgent, CancellationToken ct = default)
    {
        var entity = new SessionEntry
        {
            Id = sessionId,
            UserId = userId,
            HostId = hostId,
            Created = created,
            LastSeen = created,
            Expires = expires,
            UserAgent = userAgent,
            Revoked = false,
            RevokedAt = null,
        };
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sessions.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogDebug("session row created: sid={Sid} user={User} host={Host} expires={Expires:O}",
                sessionId, userId, hostId, expires);
        }
        finally { _writeGate.Release(); }
    }
}