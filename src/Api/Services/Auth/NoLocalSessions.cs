using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The session validator of a member that mints no sessions.
/// </summary>
/// <remarks>
/// The shared <c>session.revoke</c> handler evicts a member's own cached session answers after ending
/// one, because a member that mints its own holds a cache of them. This node mints nothing and
/// caches nothing, so the eviction lands here and does nothing; the anchor's sessions it accepts are
/// held to <see cref="TheKrystalShip.KGSM.Auth.Cluster.ClusterSessionRevocations"/> instead, which
/// the same handler evicts as well.
/// </remarks>
public sealed class NoLocalSessions : ISessionValidator
{
    /// <summary>No session this node would be asked about is one of its own.</summary>
    public Task<bool> IsValidAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(false);

    public void Evict(string sessionId)
    {
        // Nothing is cached, so there is nothing to drop.
    }
}
