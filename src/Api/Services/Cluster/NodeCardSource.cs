using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Builds this node's card — who it is, what it runs, and every address it knows it answers at
/// (<c>PLAN-peers.md</c> P0.6). A seam rather than a direct dependency so the handshake can be exercised
/// against a stated card without standing up leaf health and host identity behind it.
/// </summary>
public interface INodeCardSource
{
    /// <summary>This node's card, as it is right now.</summary>
    Task<NodeCard> BuildAsync(CancellationToken ct);
}

/// <inheritdoc cref="INodeCardSource"/>
public sealed class NodeCardSource(
    ApiOptions options,
    HostIdentityProvider hostIdentity,
    LeafHealthMonitor leafHealth,
    SelfIdentityStore selfIdentity,
    SelfIncarnation selfIncarnation) : INodeCardSource
{
    public async Task<NodeCard> BuildAsync(CancellationToken ct) =>
        new(options.NodeId,
            ApiInfo.ApiVersion,
            hostIdentity.Build,
            NodeCapabilities.Current(leafHealth.Current, options.ClusterEnabled),
            await selfIdentity.CandidatesAsync(ct).ConfigureAwait(false),
            selfIncarnation.Current);
}
