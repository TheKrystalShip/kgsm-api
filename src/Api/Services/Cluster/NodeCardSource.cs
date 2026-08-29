using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// This node's card: the cluster package's own answer about who this member is and where it answers, plus
/// the block only a node has — its route version, its build, and the leaves provisioned on it.
/// </summary>
/// <remarks>
/// It wraps the package's source rather than replacing it, because the identity and the reflected addresses
/// are the package's to state and a second implementation of them is how two members come to disagree about
/// where one of them lives.
/// </remarks>
public sealed class NodeCardSource(
    SelfMemberCardSource inner,
    HostIdentityProvider hostIdentity,
    LeafHealthMonitor leafHealth,
    ApiOptions options) : IMemberCardSource
{
    public async Task<MemberCard> BuildAsync(CancellationToken ct)
    {
        MemberCard card = await inner.BuildAsync(ct).ConfigureAwait(false);
        return card with
        {
            Node = new NodeFacts(
                ApiInfo.ApiVersion,
                hostIdentity.Build,
                NodeCapabilities.Current(leafHealth.Current, options.ClusterEnabled)),
        };
    }
}
