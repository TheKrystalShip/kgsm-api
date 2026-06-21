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
public sealed class HostAggregator(
    ApiOptions options, MonitorClient monitor, NetworkAggregator network, LeafHealthMonitor health)
{
    /// <summary>Build the single host this api serves (the <c>GET /hosts</c> list element — capacity +
    /// capabilities, no <c>network</c> grid; that is detail-only, see <see cref="GetHostDetailAsync"/>).</summary>
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
            Capabilities: health.Current,
            // M-diag telemetry — same null-when-no-snapshot honesty as the capacity trio above.
            PerCore: capacity?.PerCore,
            Load: capacity?.Load,
            DiskIo: capacity?.DiskIo,
            Interfaces: capacity?.Interfaces,
            Hostname: capacity?.Hostname,
            UptimeSec: capacity?.UptimeSec,
            SampleTs: capacity?.SampleTs,
            // The panel running on this host IS this api — its honest in-process version (shared with
            // the GET /api/v1 handshake). A build-time constant, present regardless of the metrics snapshot.
            PanelVersion: ApiInfo.ApiVersion,
            // M-diag depth: STATIC cpu identity comes straight off the snapshot (not on the metrics tick),
            // null when there is no snapshot; DYNAMIC sensors ride the shared capacity DTO (so the Host view
            // and a metrics tick carry the same hwmon list). Honest-null/empty when not measurable.
            Cpu: snapshot is null ? null : MetricsMapping.ToCpuInfo(snapshot.Cpu.Info),
            Sensors: capacity?.Sensors);
    }

    /// <summary>
    /// Build the host <strong>detail</strong> view (the <c>GET /hosts/{id}</c> body) — the list element
    /// plus the M6·b open-ports grid (<see cref="HostNetwork"/>). The grid is a single on-demand firewall
    /// probe (the Diagnostics panel, not the capacity-strip poll), bounded inside <see cref="NetworkAggregator"/>;
    /// it is <see langword="null"/> when the firewall can't answer, so the detail stays a superset of the list.
    /// </summary>
    public async Task<Host> GetHostDetailAsync(CancellationToken ct)
    {
        Host host = await GetHostAsync(ct).ConfigureAwait(false);
        HostNetwork? net = await network.BuildHostNetworkAsync(ct).ConfigureAwait(false);
        return host with { Network = net };
    }
}
