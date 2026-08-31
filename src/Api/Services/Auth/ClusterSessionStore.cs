using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// This node's sessions, as the cluster needs to reach them.
/// </summary>
/// <remarks>
/// An adapter rather than an interface bolted onto <see cref="SessionStore"/>, because the two
/// surfaces differ in exactly one thing and it is this node's: every write to the session table is
/// scoped to a host id, and the cluster contract has no concept of one. Supplying it here means the
/// store keeps the richer signatures its own admin endpoints use, and a member-triggered revoke and a
/// local one still land on the same rows through the same code.
/// </remarks>
public sealed class ClusterSessionStore(SessionStore sessions, ApiOptions options) : IClusterSessionAuthority
{
    public Task<bool> IsRevokedAsync(string sessionId, CancellationToken ct = default) =>
        sessions.IsRevokedAsync(sessionId, ct);

    public Task RecordRevocationAsync(string sessionId, DateTimeOffset until, CancellationToken ct = default) =>
        sessions.RecordRevocationAsync(sessionId, options.HostId, until, ct);

    /// <remarks>
    /// The bool the store returns says whether a row moved, which is worth knowing locally and is not
    /// worth telling the bus: the handler is idempotent by design and a redelivery that changes
    /// nothing is the ordinary case rather than a failure.
    /// </remarks>
    public async Task RevokeAsync(string sessionId, CancellationToken ct = default) =>
        await sessions.RevokeAsync(sessionId, ct).ConfigureAwait(false);

    public Task<IReadOnlyList<string>> RevokeAllForHandleAsync(string handle, CancellationToken ct = default) =>
        sessions.RevokeAllForUserAsync(handle, options.HostId, ct);
}
