using Microsoft.Extensions.Caching.Memory;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Whether a session minted by the cluster's auth anchor has been ended, cached on the request path.
/// </summary>
/// <remarks>
/// <para>
/// A session this host minted is held to an allow-list: it has a row, and no row means no session. A
/// session the anchor minted is held to the opposite question, because the bearer is verified against
/// a published key and needs nothing stored here to be accepted. So the only thing worth storing is
/// that somebody ended it, and the check is "has anyone said this is over".
/// </para>
/// <para>
/// <b>The cache TTL is the lag, and an end evicts.</b> Same shape and the same reasoning as the
/// session validator beside it: a revoke arriving over the bus drops the entry so the kill is
/// immediate, and the TTL is the backstop for a read that raced it. Absolute rather than sliding, so
/// the busiest session — the one most worth being able to end — is not the one that never re-checks.
/// </para>
/// <para>
/// <b>Both answers are cached.</b> Not caching "this is over" would send exactly the session most
/// worth refusing to the database on every request it makes, which is what a stolen bearer does.
/// </para>
/// </remarks>
public sealed class ClusterSessionRevocations(
    SessionStore sessions,
    IMemoryCache cache,
    TimeSpan cacheTtl)
{
    // Its own namespace, so an answer to this question can never be read as an answer to the session
    // validator's — the two ask opposite things about the same session id.
    private static string Key(string sessionId) => "kgsm.cluster-session.revoked." + sessionId;

    private readonly TimeSpan _cacheTtl = cacheTtl > TimeSpan.Zero ? cacheTtl : TimeSpan.FromSeconds(5);

    /// <summary>Whether this session has been ended.</summary>
    public async Task<bool> IsRevokedAsync(string sessionId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key(sessionId), out bool cached))
            return cached;

        bool revoked = await sessions.IsRevokedAsync(sessionId, ct).ConfigureAwait(false);

        cache.Set(Key(sessionId), revoked, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _cacheTtl,
        });
        return revoked;
    }

    /// <summary>Drop a cached answer, so an end that has just arrived takes effect now.</summary>
    public void Evict(string sessionId) => cache.Remove(Key(sessionId));
}
