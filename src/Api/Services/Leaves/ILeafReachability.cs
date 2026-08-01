using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The seam for the apply broker's <strong>reachability check</strong> — "after that change, can this API
/// still talk to the leaf?" Distinct from <see cref="ILeafProbe"/>, which asks systemd whether the unit came
/// back up. A wiring change passes the liveness canary and fails this one: the leaf restarts perfectly, the
/// API just cannot find it any more.
/// </summary>
public interface ILeafReachability
{
    /// <summary>
    /// True when this API can reach the leaf, false when it provably cannot, and <strong>null when there is
    /// no signal</strong> — the API has no probe for that leaf (the firewall idle-exits, the bot has no
    /// listening surface) or it is not provisioned to probe it. Null is never collapsed into false: reporting
    /// a leaf unreachable because nothing was measured would be fabricating a status.
    /// </summary>
    Task<bool?> IsReachableAsync(string leafId, CancellationToken ct);
}

/// <summary>
/// The real check: force an immediate capability poll, then read that leaf's capability status. Using the
/// capability model rather than a bespoke probe keeps one definition of "reachable" across the API.
/// </summary>
public sealed class LeafReachability(LeafHealthMonitor health) : ILeafReachability
{
    public async Task<bool?> IsReachableAsync(string leafId, CancellationToken ct)
    {
        await health.PollNowAsync(ct).ConfigureAwait(false);
        HostCapabilities caps = health.Current;

        Capability? capability = leafId switch
        {
            ProvisionableLeaf.Monitor => caps.Metrics,
            ProvisionableLeaf.Assistant => caps.Assistant,
            ProvisionableLeaf.Watchdog => caps.Watchdog,
            ProvisionableLeaf.Scheduler => caps.Scheduler,
            _ => null,
        };

        // No capability for this leaf, or not provisioned to probe it → no signal, honestly.
        if (capability is null || !capability.Provisioned)
            return null;

        return capability.Status switch
        {
            CapabilityStatus.Operational => true,
            CapabilityStatus.Down => false,
            _ => null,   // unknown stays unknown
        };
    }
}
