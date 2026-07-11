namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The <c>Peers</c>-table-backed <see cref="IClusterPeerGate"/> (<c>PLAN-peers.md §0</c> #8, §2 #7/#8, P0) —
/// the disable-list gate that replaces <see cref="AllowAllClusterPeerGate"/> once the roster exists: a node
/// bearing a validly-signed cluster service token is enabled UNLESS its row's <see cref="Data.PeerEntity.Enabled"/>
/// flag is explicitly <see langword="false"/>. Absence from the roster is NOT rejection — only an explicit
/// disable is (§2 #7 — the shared secret alone already proves guild membership, so an unknown-but-validly-tokened
/// node is trusted rather than rejected; this keeps the mesh working under a partial topology view).
/// </summary>
/// <remarks>
/// Registered in place of <see cref="AllowAllClusterPeerGate"/> in <c>Startup</c>; <see cref="Controllers.PeersController"/>
/// calls this AFTER the caller's cluster service token has already validated — it is not itself an auth check.
/// </remarks>
public sealed class PeersTableGate : IClusterPeerGate
{
    private readonly PeersStore _peers;
    private readonly ILogger<PeersTableGate> _logger;

    public PeersTableGate(PeersStore peers, ILogger<PeersTableGate> logger)
    {
        _peers = peers;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> IsEnabledAsync(string nodeId)
    {
        Data.PeerEntity? row = await _peers.GetByNodeIdAsync(nodeId, CancellationToken.None).ConfigureAwait(false);
        if (row is not null && !row.Enabled)
        {
            _logger.LogDebug("Rejecting cluster call from disabled peer {NodeId}.", nodeId);
            return false;
        }
        return true;
    }
}
