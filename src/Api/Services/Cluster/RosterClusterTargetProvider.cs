namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Resolves <see cref="IClusterBus.EnqueueAsync"/>'s <c>targets</c> parameter from the <c>Peers</c> roster
/// (<c>PLAN-peers.md §6</c>, P0 — "real outbox fan-out") — the roster-backed target list a durable-message
/// caller passes to <see cref="IClusterBus.EnqueueAsync"/> instead of hand-assembling one.
/// </summary>
/// <remarks>
/// Projects <see cref="PeersStore.ListEnabledAsync"/> rows into <see cref="ClusterTarget"/>s, using each
/// peer's pinned candidate (<c>PLAN-peers.md §2</c> #13a) — <see cref="Data.PeerEntity.Url"/> when
/// set, else the advertised <see cref="Data.PeerEntity.Url"/> — restricted to first-hand-
/// <see cref="GossipState.Alive"/> peers only (<c>PLAN-peers.md §P1</c>; see <see cref="GetEnabledTargetsAsync"/>).
/// </remarks>
public sealed class RosterClusterTargetProvider
{
    private readonly PeersStore _peers;

    public RosterClusterTargetProvider(PeersStore peers)
    {
        _peers = peers;
    }

    /// <summary>
    /// Every enabled, first-hand-<see cref="GossipState.Alive"/> peer, as a fan-out target list for
    /// <see cref="IClusterBus.EnqueueAsync"/> (<c>PLAN-peers.md §P1</c>, the durable→alive target rule).
    /// </summary>
    /// <remarks>
    /// A durable, identity-carrying message (e.g. <c>session.revoke</c>, which names a <c>discordId</c>)
    /// must reach only peers this node has authenticated first-hand — never a hearsay/phantom roster
    /// entry it has merely heard about via gossip and never reached. A gossip-injected phantom URL
    /// receiving a secret-bearing message would otherwise sit in the outbox retrying delivery to it for
    /// the full retry TTL. <b>First-hand-alive is two conditions, not one:</b>
    /// <see cref="Data.PeerEntity.MembershipState"/> == <see cref="GossipState.Alive"/> AND
    /// <see cref="Data.PeerEntity.LastSeen"/> is set. Membership alone is insufficient — a purely
    /// gossip-learned (hearsay) peer is <em>stored</em> <see cref="GossipState.Alive"/> with a null
    /// <see cref="Data.PeerEntity.LastSeen"/> (it displays as <see cref="GossipState.Joining"/>, a derived
    /// projection — see <see cref="GossipState.Display"/>); its <c>LastSeen</c> is stamped only once this
    /// node reaches it first-hand (the poller's <c>PromoteAliveAsync</c> or an authenticated inbound
    /// <c>RecordAliveContactAsync</c>). Requiring <c>LastSeen</c> is exactly what excludes an unconfirmed
    /// hearsay/phantom entry. Ephemeral gossip itself is unaffected by this filter:
    /// <c>GossipWorker</c>/<c>GossipService</c> read <see cref="PeersStore.ListEnabledAsync"/> directly,
    /// never through this provider, so anti-entropy sync still reaches suspect/joining peers.
    /// </remarks>
    public async Task<IReadOnlyList<ClusterTarget>> GetEnabledTargetsAsync(CancellationToken ct)
    {
        IReadOnlyList<Data.PeerEntity> enabled = await _peers.ListEnabledAsync(ct).ConfigureAwait(false);
        return enabled
            .Where(p => p.MembershipState == GossipState.Alive && p.LastSeen is not null)
            .Select(p => new ClusterTarget(p.NodeId, p.Url))
            .ToList();
    }
}
