using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;
// Disambiguate from Microsoft.Extensions.Hosting.Host (pulled in by ImplicitUsings).
using Host = TheKrystalShip.Api.Contracts.Host;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// Builds this host's <see cref="Host"/> view (architecture §4·a/§4·b) for the <c>GET /hosts</c> read.
/// It joins two <strong>independent</strong> sources: the measured <strong>capacity</strong> figures
/// from one kgsm-monitor <c>/metrics</c> scrape, and the <strong>capability</strong> block from the
/// always-on <see cref="LeafHealthMonitor"/> (frequent <c>/health</c> polls). The two are never inferred
/// from one another — a warming monitor reports its metrics capability <c>operational</c> (health up)
/// with <c>null</c> capacity (no frame yet), honoring the "metric-presence ≠ status" invariant.
/// </summary>
/// <remarks>
/// Capacity is present iff a fresh snapshot exists; otherwise honest <c>null</c> ("not measurable now").
/// All capability liveness (which leaves are operational/down, since when) lives in the
/// <see cref="LeafHealthMonitor"/> — the single source feeding both this REST read and the M2
/// <c>hosts/{id}/capabilities</c> stream — so the two surfaces can never disagree.
/// </remarks>
public sealed class HostAggregator(ApiOptions options, MonitorClient monitor, LeafHealthMonitor health)
{
    /// <summary>Build the single host this api serves.</summary>
    public async Task<Host> GetHostAsync(CancellationToken ct)
    {
        Snap.Snapshot? snapshot = await monitor.GetLatestAsync(ct).ConfigureAwait(false);
        HostMetricsDto? capacity = snapshot is null ? null : MetricsMapping.ToHostMetrics(snapshot);

        return new Host(
            Id: options.HostId,
            Label: options.HostLabel,
            // The api answers on the host it runs on, so reaching this response means the host is up.
            Status: "online",
            CpuPct: capacity?.CpuPct,
            Mem: capacity?.Mem,
            Disks: capacity?.Disks,
            Capabilities: health.Current);
    }
}
