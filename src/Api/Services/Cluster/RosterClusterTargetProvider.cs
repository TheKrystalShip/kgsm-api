namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Resolves <see cref="IClusterBus.EnqueueAsync"/>'s <c>targets</c> parameter from the <c>Peers</c> roster
/// (<c>PLAN-peers.md §6</c>, P0 — "real outbox fan-out"): today <see cref="IClusterBus"/> callers must supply
/// their own target list (there was no roster yet); this is the roster-backed replacement — "every enabled
/// peer" — a future caller passes to <see cref="IClusterBus.EnqueueAsync"/> instead of hand-assembling one.
/// </summary>
/// <remarks>
/// Projects <see cref="PeersStore.ListEnabledAsync"/> rows into <see cref="ClusterTarget"/>s, using each
/// peer's gossip-or-client URL (<c>PLAN-peers.md §2</c> #13a) — <see cref="Data.PeerEntity.GossipUrl"/> when
/// set, else the advertised <see cref="Data.PeerEntity.Url"/>.
/// </remarks>
public sealed class RosterClusterTargetProvider
{
    private readonly PeersStore _peers;

    public RosterClusterTargetProvider(PeersStore peers)
    {
        _peers = peers;
    }

    /// <summary>Every enabled peer, as a fan-out target list for <see cref="IClusterBus.EnqueueAsync"/>.</summary>
    public async Task<IReadOnlyList<ClusterTarget>> GetEnabledTargetsAsync(CancellationToken ct)
    {
        IReadOnlyList<Data.PeerEntity> enabled = await _peers.ListEnabledAsync(ct).ConfigureAwait(false);
        return enabled.Select(p => new ClusterTarget(p.NodeId, p.GossipUrl ?? p.Url)).ToList();
    }
}
