using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Data;

using TheKrystalShip.KGSM.Auth.Sessions;

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
/// the honest signal the operator must run the one-shot first. See <c>Services/Auth/CLAUDE.md</c>.
/// </remarks>
public sealed class SessionStore(
    IServiceScopeFactory scopeFactory,
    ILogger<SessionStore> logger) : ISessionRegistry
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // ── ISessionRegistry ────────────────────────────────────────────────────
    // The shared contract every KGSM surface's sessions satisfy. This store also keeps a richer
    // surface below (listing a user's devices, revoking all of them) that belongs to this API's admin
    // endpoints rather than to the ecosystem, which is why the interface is the narrower of the two.

    Task ISessionRegistry.CreateAsync(SessionRegistration session, CancellationToken ct) =>
        CreateAsync(session.SessionId, session.UserId, session.HostId, session.Created,
                    session.Expires, session.UserAgent, session.CurrentJti, ct);

    Task<bool> ISessionRegistry.RotateAsync(
        string sessionId, string presentedJti, string newJti, DateTimeOffset newExpires, CancellationToken ct) =>
        UpdateForRefreshAsync(sessionId, presentedJti, newJti, newExpires, ct);

    /// <summary>
    /// The per-request liveness check, uncached — the cache lives in the validator above this. Reads
    /// through its own DI scope because this store is a singleton and the DbContext is scoped.
    /// </summary>
    public async Task<bool> IsAliveAsync(string sessionId, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && !s.Revoked && s.Expires > now, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Insert a new session row minted at the OAuth callback. Called once per login — this row IS
    /// the session (its <see cref="SessionEntry.Id"/> is the JWT <c>sid</c> claim). <paramref name="userAgent"/>
    /// is the raw <c>User-Agent</c> header (D5 — raw, no IP). <paramref name="initialJti"/> is the
    /// jti of the refresh token minted alongside this row — stored as <see cref="SessionEntry.CurrentJti"/>,
    /// the reuse-detection key the refresh action validates against. Best-effort: a failed insert must
    /// never break login (the caller catches + continues — a missing row is rejected by the per-request
    /// validator once the token is used, which forces a relogin — the honest recovery for a missing session).
    /// </summary>
    public async Task CreateAsync(
        string sessionId, string userId, string hostId,
        DateTimeOffset created, DateTimeOffset expires,
        string? userAgent, string? initialJti,
        CancellationToken ct = default)
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
            CurrentJti = initialJti,
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

    /// <summary>
    /// The refresh rotation handler. Validates the presented refresh token's jti against the row's
    /// stored <see cref="SessionEntry.CurrentJti"/> (reuse detection — a stale jti means an
    /// OLD/STOLEN refresh token was presented, reject it), and on a match rotates: stores the
    /// <paramref name="newJti"/> as <see cref="SessionEntry.CurrentJti"/>, slides
    /// <see cref="SessionEntry.Expires"/> to <paramref name="newExpires"/> (the rolling 30-day
    /// window — D8 re-opened by user directive), and bumps <see cref="SessionEntry.LastSeen"/> to now.
    /// Returns <see langword="false"/> (caller → 401) when:
    /// <list type="bullet">
    /// <item>The row doesn't exist (no sid match — pre-M4·c sid claim, or a malformed sid).</item>
    /// <item>The row is already <see cref="SessionEntry.Revoked"/> (logout / admin revoke happened).</item>
    /// <item>The presented jti DIFFERS from the stored <see cref="SessionEntry.CurrentJti"/> (stale
    /// refresh token — reuse detection fires). No grace period for tab races; a 401 here means the
    /// caller re-auths via Discord OAuth once. <b>Honest adoption boundary:</b> a row whose
    /// <see cref="SessionEntry.CurrentJti"/> is <see langword="null"/> (created before the rotation
    /// feature shipped — the one-shot ALTER TABLE on prod) accepts the FIRST non-null presented jti
    /// and adopts it; subsequent refreshes use the now-set CurrentJti. Post-deploy sessions always
    /// have a CurrentJti from login, so the null-accept branch is a deploy-boundary convenience only.</item>
    /// </list>
    /// Serialized (SQLite single-writer) so two concurrent refreshes on the same sid can't both
    /// pass the jti check and split the row into two "current" jtis.
    /// </summary>
    public async Task<bool> UpdateForRefreshAsync(
        string sid, string presentedJti, string newJti, DateTimeOffset newExpires,
        CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SessionEntry? row = await db.Sessions
                .FirstOrDefaultAsync(s => s.Id == sid, ct).ConfigureAwait(false);
            // No row → reject (the validator would also reject downstream, but the refresh path
            // validates first so a meaningful 401 returns without an extra round-trip later).
            if (row is null || row.Revoked)
                return false;
            // Reuse detection — the presented jti must match the stored current jti. The null-accept
            // branch (row.CurrentJti == null) is the one-shot ALTER TABLE deploy boundary: a session
            // created BEFORE this increment shipped has no CurrentJti; the first /refresh on it adopts
            // the presented jti. Every newly-created row sets CurrentJti from the login mint.
            if (row.CurrentJti is not null && row.CurrentJti != presentedJti)
                return false;
            // Rotate: store the new refresh's jti, slide Expires (rolling window D8), bump LastSeen.
            row.CurrentJti = newJti;
            row.Expires = newExpires;
            row.LastSeen = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Revoke a session by sid (the per-session soft-delete — <see cref="SessionEntry.Revoked"/>=true).
    /// Idempotent: an already-revoked row is a no-op (the first revoke's <see cref="SessionEntry.RevokedAt"/>
    /// is preserved). Called by the auth.logout POST, the self-revoke endpoint, and the admin
    /// cross-user revoke (Increment 6). The caller is expected to <c>Evict</c> the session from the
    /// per-request cache (via <see cref="ISessionValidator.Evict"/>) afterwards so the revoke is
    /// immediate rather than waiting for the 5s TTL backstop. Returns <see langword="false"/> if no
    /// row exists for the sid (already GC'd, or never created — silent for the revoke UX).
    /// </summary>
    public async Task<bool> RevokeAsync(string sid, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SessionEntry? row = await db.Sessions
                .FirstOrDefaultAsync(s => s.Id == sid, ct).ConfigureAwait(false);
            if (row is null || row.Revoked)
                return false;
            row.Revoked = true;
            row.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogDebug("session revoked: sid={Sid}", sid);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Whether a session has been explicitly ended here, uncached.
    /// </summary>
    /// <remarks>
    /// The question a cluster session is held to, and it is the opposite of the one a local session is
    /// held to. A session this host minted has a row, so absence means "no such session" and the
    /// answer is a refusal. A session the cluster's anchor minted has a row here only if somebody
    /// ended it, so absence means "nobody has ended this" and the answer is to let it through — the
    /// signature and its own expiry are what bound it.
    /// </remarks>
    public async Task<bool> IsRevokedAsync(string sid, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sid && s.Revoked, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Record that a session minted elsewhere in the cluster is over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cluster session is accepted on every member and has a real row on exactly one of them — the
    /// anchor that minted it. So an end has to be recorded here as a fact of its own, or this host
    /// goes on accepting the bearer for the rest of its life while its holder has signed out.
    /// </para>
    /// <para>
    /// <paramref name="expires"/> is when the row may be swept, not when the session dies — the
    /// session is already dead. It bounds the marker by the longest a bearer for it could still be
    /// presented, so the existing sweep clears it without anything new to run.
    /// </para>
    /// <para>
    /// Idempotent, and it never overwrites a row that is already here. The bus delivers at least once,
    /// and a redelivery must not reset a revocation time or resurrect a session as unrevoked.
    /// </para>
    /// </remarks>
    public async Task RecordRevocationAsync(
        string sid, string hostId, DateTimeOffset expires, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Sessions.AnyAsync(s => s.Id == sid, ct).ConfigureAwait(false))
                return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            db.Sessions.Add(new SessionEntry
            {
                Id = sid,
                // No account is named, because none is known: the message carries a session, and
                // inventing an owner for it would put a row in somebody's device list for a sign-in
                // that never happened on this host.
                UserId = "",
                HostId = hostId,
                Created = now,
                LastSeen = now,
                Expires = expires,
                UserAgent = null,
                Revoked = true,
                RevokedAt = now,
                CurrentJti = null,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogDebug("cluster session recorded as ended: sid={Sid}", sid);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Take up a session the cluster's auth anchor minted, so this node can serve it while the anchor
    /// is unreachable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row carries the <b>anchor's</b> session id and the <b>anchor's</b> expiry. That is what
    /// keeps a revoke naming that session effective against everything this node mints from it, and
    /// what stops this node extending a session past the cap it was granted.
    /// </para>
    /// <para>
    /// <b>A revoked session is never taken up again.</b> Otherwise a still-valid refresh token would
    /// resurrect a row somebody had ended, and signing out would be undone by the next refresh.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> when the session has been ended here, which the caller answers as a
    /// refusal. <see langword="true"/> when the row is present and live, whether this call wrote it or
    /// found it.
    /// </returns>
    public async Task<bool> AdoptClusterSessionAsync(
        string sid, string userId, string hostId, DateTimeOffset expires,
        string? userAgent, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            SessionEntry? row = await db.Sessions
                .FirstOrDefaultAsync(s => s.Id == sid, ct).ConfigureAwait(false);

            if (row is not null)
            {
                if (row.Revoked)
                    return false;

                row.LastSeen = now;
                // The anchor's cap, never this node's idea of one. It only ever moves because the
                // anchor moved it, and it is not slid by being used here.
                row.Expires = expires;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return true;
            }

            db.Sessions.Add(new SessionEntry
            {
                Id = sid,
                UserId = userId,
                HostId = hostId,
                Created = now,
                LastSeen = now,
                Expires = expires,
                UserAgent = userAgent,
                Revoked = false,
                RevokedAt = null,
                // No refresh token is minted here, so there is no rotation key to hold. The session
                // is refreshed at the anchor, which owns the one that exists.
                CurrentJti = null,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "took up cluster session {Sid} for {User} — the anchor is unreachable", sid, userId);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// M4·c Increment 6 — the active set for <c>GET /auth/sessions</c> (a caller's own set, or, for an
    /// admin, another user's set via <c>?userId=</c>). Matches the composite index
    /// <c>ix_sessions_user (UserId, Revoked, Expires)</c> — a read, no write-gate needed. AsNoTracking
    /// (display-only; nothing here is mutated). Ordered newest-active-first (<see cref="SessionEntry.LastSeen"/>
    /// descending) so the caller's most recently used device/browser sorts first, matching the "Active
    /// Sessions" display's expected order.
    /// </summary>
    public async Task<IReadOnlyList<SessionEntry>> ListActiveAsync(
        string userId, string hostId, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return await db.Sessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.HostId == hostId && !s.Revoked && s.Expires > now)
            .OrderByDescending(s => s.LastSeen)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// M4·c Increment 6 — a single row lookup by sid, AsNoTracking. Used by the viewer self-revoke
    /// endpoint to check OWNERSHIP before revoking (a viewer must not revoke another user's session by
    /// guessing/leaking a sid — see <c>SessionController.RevokeSelf</c>), and by any future read-only
    /// sid probe. Returns <see langword="null"/> when no row exists (already GC'd, or never created).
    /// </summary>
    public async Task<SessionEntry?> GetByIdAsync(string sid, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sid, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// M4·c Increment 8 — the session GC worker's delete. A permanent storage bound: any row whose
    /// <see cref="SessionEntry.Expires"/> has passed is dead <b>regardless of <see cref="SessionEntry.Revoked"/></b>
    /// (a revoked row would eventually age out anyway; an expired-but-never-revoked row is just as dead
    /// — the 30-day absolute cap already killed it, revoke or not) — so this deletes both. Uses EF Core's
    /// bulk <c>ExecuteDeleteAsync</c> (.NET 10) rather than load-then-remove: one indexed <c>DELETE FROM
    /// sessions WHERE Expires &lt; ?</c>, no change-tracker materialization of rows nobody needs to see.
    /// <see cref="SessionEntry.Expires"/>'s <c>ValueConverter</c> stores UTC ticks as <c>INTEGER</c>
    /// (see <see cref="AppDbContext.OnModelCreating"/>), so <c>s.Expires &lt; now</c> translates to a
    /// plain integer comparison that rides the <c>ix_sessions_expires</c> index — the same translatable
    /// shape <see cref="SessionValidator"/>'s <c>Expires &gt; now</c> query relies on. Serialized on the
    /// same <see cref="_writeGate"/> as every other write (SQLite single-writer) so the GC sweep can't
    /// race a concurrent login insert / refresh rotation update. Returns the number of rows deleted.
    /// </summary>
    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            int deleted = await db.Sessions
                .Where(s => s.Expires < now)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            // Debug level, unconditional (even when 0) — a per-pass debug line is cheap noise at this
            // level and useful to confirm the worker is actually ticking; it's not INFO so it doesn't
            // spam production logs at the default level.
            logger.LogDebug("session GC: deleted {Count} expired row(s)", deleted);
            return deleted;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// M4·c Increment 6 — "log out user everywhere" (self <c>{ all: true }</c> OR the admin
    /// cross-user <c>POST /auth/users/{userId}/sessions/revoke-all</c>). Soft-deletes every currently
    /// ACTIVE row (<c>!Revoked &amp;&amp; Expires &gt; now</c> — an already-revoked or already-expired
    /// row is left untouched, same idempotent posture as <see cref="RevokeAsync"/>) for
    /// <paramref name="userId"/> on <paramref name="hostId"/>, and returns the affected sids so the
    /// caller can <see cref="ISessionValidator.Evict"/> each one (the cache doesn't know to invalidate
    /// on a bulk write). Serialized on the same <see cref="_writeGate"/> as every other write (SQLite
    /// single-writer).
    /// </summary>
    public async Task<IReadOnlyList<string>> RevokeAllForUserAsync(
        string userId, string hostId, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<SessionEntry> rows = await db.Sessions
                .Where(s => s.UserId == userId && s.HostId == hostId && !s.Revoked && s.Expires > now)
                .ToListAsync(ct).ConfigureAwait(false);
            if (rows.Count == 0)
                return [];
            DateTimeOffset revokedAt = DateTimeOffset.UtcNow;
            var sids = new List<string>(rows.Count);
            foreach (SessionEntry row in rows)
            {
                row.Revoked = true;
                row.RevokedAt = revokedAt;
                sids.Add(row.Id);
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogDebug("session revoke-all: user={User} host={Host} count={Count}", userId, hostId, sids.Count);
            return sids;
        }
        finally { _writeGate.Release(); }
    }
}