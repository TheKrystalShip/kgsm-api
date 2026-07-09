namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The per-request session validator (M4·c). Called from JwtBearer's <c>OnTokenValidated</c> after the
/// standard access-token-kind check — the authority the stateless JWT alone cannot be. The
/// <c>sid</c> claim is read off the validated principal; this checks the <see cref="SessionStore"/>
/// registry for <c>Id == sid &amp;&amp; !Revoked &amp;&amp; Expires &gt; now</c>, cached for
/// <c>KGSM_API_SESSIONS_CACHE_TTL_MS</c> (default 5s) so the hot path stays light — every active
/// <c>sid</c> produces at most one DB read per cache window.
/// </summary>
/// <remarks>
/// <b>The cache is the accepted revocation-lag bound (D2).</b> A revoke sets
/// <see cref="SessionEntry.Revoked"/>=true AND calls <see cref="Evict"/> — the next access → 401 within
/// the cache window's eviction path (best-effort ~instant); the TTL is the backstop (worst case
/// ≤5s). <b>Caching <c>false</c> is deliberate</b> — a revoked/expired session that's still being
/// queried (the token hasn't naturally expired) doesn't DB-hammer every request for 5s.
/// </remarks>
public interface ISessionValidator
{
    /// <summary>
    /// Is the session <paramref name="sid"/> still alive (row exists, not revoked, not past the
    /// 30-day absolute cap)? Cached. Checked per-request by the JwtBearer pipeline (REST + SSE —
    /// the bearer rides the Authorization header or <c>?access_token=</c> on the SSE handshake; the
    /// <c>OnTokenValidated</c> event fires for both).
    /// </summary>
    Task<bool> IsValidAsync(string sid, CancellationToken ct);

    /// <summary>
    /// Drop the cached result for <paramref name="sid"/> so the next <see cref="IsValidAsync"/> query
    /// re-reads the DB rather than serving a stale <c>true</c>. Called by the revoke path
    /// (<c>POST /auth/logout</c> lands in Increment 5; the self/admin revoke endpoints in Increment 6)
    /// AFTER setting <see cref="SessionEntry.Revoked"/>=true — so the next access sees the revoke
    /// immediately rather than waiting for the <c>KGSM_API_SESSIONS_CACHE_TTL_MS</c> backstop.
    /// </summary>
    void Evict(string sid);
}