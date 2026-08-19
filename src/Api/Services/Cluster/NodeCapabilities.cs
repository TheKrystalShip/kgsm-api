using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Projects this node's capability set into the flat id list <c>GET /api/v1/peers/identity</c> advertises
/// (<c>PLAN-peers.md §7</c>, e.g. <c>["monitor","watchdog","cluster"]</c>). Deliberately distinct from
/// <see cref="HostCapabilities"/> (the rich <c>provisioned</c>+<c>status</c>+<c>since</c> block
/// <c>GET /hosts</c> serves, architecture §4·b): a node-to-node identity handshake only needs to know WHICH
/// capabilities exist here, not their live health — so this stays a plain id projection, honest and simple.
/// </summary>
public static class NodeCapabilities
{
    /// <summary>
    /// The ids of every capability currently provisioned on this node — the same leaves the §4·b capability
    /// model reports (<paramref name="capabilities"/>, read from <see cref="LeafHealthMonitor.Current"/>),
    /// plus <c>"cluster"</c> when <paramref name="clusterEnabled"/> (<see cref="ApiOptions.ClusterEnabled"/>)
    /// is true. <c>"cluster"</c> is never a <see cref="LeafCatalog"/> entry — it has no systemd unit, so a
    /// Services-board card would be a phantom — this is the one place it is advertised.
    /// </summary>
    public static IReadOnlyList<string> Current(HostCapabilities capabilities, bool clusterEnabled)
    {
        var ids = new List<string>(6);
        if (capabilities.Metrics.Provisioned) ids.Add(ProvisionableLeaf.Monitor);
        if (capabilities.Watchdog.Provisioned) ids.Add(ProvisionableLeaf.Watchdog);
        if (capabilities.Assistant.Provisioned) ids.Add(ProvisionableLeaf.Assistant);
        if (capabilities.Scheduler.Provisioned) ids.Add(ProvisionableLeaf.Scheduler);
        if (capabilities.Reactor.Provisioned) ids.Add(ProvisionableLeaf.Reactor);
        if (clusterEnabled) ids.Add("cluster");
        return ids;
    }
}
