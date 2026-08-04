using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The <see cref="ISessionValidator"/> implementation — an <see cref="IMemoryCache"/>-backed lookup
/// of the <see cref="SessionEntry"/> row by <c>sid</c>. A singleton: the cache is process-local
/// (per-host single-instance, D2 — no cross-node coherence is needed) and the DB query opens its own
/// DI scope per cache miss (the <see cref="AuditService"/>/ <see cref="SessionStore"/> posture —
/// the validator runs on the request path but the <c>AppDbContext</c> is scoped, so it resolves
/// through <see cref="IServiceScopeFactory"/>).
/// </summary>
/// <remarks>
/// <b>Cache semantics.</b>
/// <list type="bullet">
/// <item>Absolute (NOT sliding) expiration = <see cref="ApiOptions.SessionsCacheTtlMs"/> (default 5s).
/// A sliding window would extend the cache on each hit, so a frequently-active session would never
/// re-validate against the DB — defeating the revocation-lag bound. Absolute keeps it honest: 5s
/// after the FIRST cache-miss query, the entry expires regardless of how many hits land in between.</item>
/// <item><c>true</c> is cached (the session is alive) — the next 5s of requests skip the DB.</item>
/// <item><c>false</c> is cached (the session is revoked/expired/missing) — a revoked session doesn't
/// DB-hammer every request for 5s. The next <see cref="Evict"/> OR the TTL expiry re-queries.</item>
/// <item><see cref="Evict"/> drops the entry so the revoke path (Increment 5/6) makes the next access
/// 401 ~instantly rather than waiting for the TTL backstop.</item>
/// </list>
/// </remarks>
public sealed class SessionValidator(
    IServiceScopeFactory scopeFactory,
    ApiOptions options,
    IMemoryCache cache) : ISessionValidator
{
    public async Task<bool> IsValidAsync(string sid, CancellationToken ct)
    {
        // The escape hatch is checked here (not just in OnTokenValidated) so a caller that bypasses
        // the pipeline (a test, a future internal use) still honors it. When sessions are disabled
        // every sid is "alive" — the pre-M4·c stateless-JWT posture, deliberately.
        if (!options.SessionsEnabled)
            return true;

        if (cache.TryGetValue(sid, out bool cached))
            return cached;

        bool valid = await QueryIsValidAsync(sid, ct).ConfigureAwait(false);

        var entryOpts = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(options.SessionsCacheTtlMs),
        };
        cache.Set(sid, valid, entryOpts);
        return valid;
    }

    /// <summary>
    /// The DB query on a cache miss — an EXISTS-style check: the row exists, <c>Revoked=false</c>,
    /// and <c>Expires &gt; now</c>. FirstOrDefaultAsync + the composite index
    /// <c>ix_sessions_user</c> keeps this a single index-friendly round-trip. Resolves its own DI
    /// scope per miss (the validator is a singleton; <c>AppDbContext</c> is scoped — NEVER split a
    /// tracked context across the request + this background-y path).
    /// </summary>
    private async Task<bool> QueryIsValidAsync(string sid, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // UserIsActive — Id==sid (PK, index) AND !Revoked AND Expires > now.
        return await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sid && !s.Revoked && s.Expires > now, ct)
            .ConfigureAwait(false);
    }

    public void Evict(string sid) => cache.Remove(sid);
}