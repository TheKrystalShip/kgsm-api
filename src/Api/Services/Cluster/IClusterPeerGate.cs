namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The "is this calling node an ENABLED peer" gate the §4 wire contract reserves a <c>403
/// peer_disabled</c> response for. There is no <c>Peers</c> table yet — peer registration/enable-
/// disable is a separate, later peer-foundation milestone (<c>PLAN-peers.md</c> P0) — so this is a
/// tiny seam <see cref="Controllers.PeersController"/> calls right after cluster-token auth succeeds,
/// giving that milestone a drop-in replacement without touching the controller's request-handling
/// shape. Until then, <see cref="AllowAllClusterPeerGate"/> is the only implementation: a node that
/// presents a validly-signed cluster service token (fail-closed auth already happened) is treated as
/// enabled.
/// </summary>
public interface IClusterPeerGate
{
    /// <summary>Is <paramref name="nodeId"/> an enabled peer of this cluster? Called AFTER the caller's
    /// cluster service token has already validated (this is not itself an auth check).</summary>
    Task<bool> IsEnabledAsync(string nodeId);
}

/// <summary>
/// The default, pre-peer-registry <see cref="IClusterPeerGate"/>: every node that can present a
/// validly-signed cluster service token is enabled. Correct for today's posture — a valid token
/// already implies "knows the shared cluster secret," which is the whole trust boundary
/// (<c>PLAN-peers.md §0</c>, the one-guild single-owner cluster) until a real per-node enable/disable
/// registry exists. Swap this registration for a <c>Peers</c>-table-backed implementation when that
/// milestone lands — no other code changes.
/// </summary>
public sealed class AllowAllClusterPeerGate : IClusterPeerGate
{
    public Task<bool> IsEnabledAsync(string nodeId) => Task.FromResult(true);
}
