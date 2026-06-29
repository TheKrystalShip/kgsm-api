using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
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
    ApiOptions options, MonitorClient monitor, NetworkAggregator network, LeafHealthMonitor health,
    HostSettingsStore settings, HostIdentityProvider identity, IServiceScopeFactory scopeFactory)
{
    // The host's KGSM default install directory is effectively static config, so read it once (lazily,
    // thread-safe) and cache it for the process lifetime rather than spawning `kgsm config get` on every
    // GET /hosts poll. A runtime config change needs an api restart to re-surface — acceptable for a base
    // install path that almost never changes. Null = engine unprovisioned or key unset (honest unknown).
    private string? _installDir;
    private bool _installDirRead;
    private readonly object _installDirGate = new();

    private string? ReadInstallDirectory()
    {
        if (_installDirRead) return _installDir;
        lock (_installDirGate)
        {
            if (_installDirRead) return _installDir;
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                if (scope.ServiceProvider.GetService<IConfigService>() is IConfigService config)
                {
                    string? value = config.Get("default_install_directory");
                    _installDir = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }
            catch
            {
                // Any failure (engine hiccup) → honest null, never a fabricated path. Cache it so a one-off
                // failure doesn't re-spawn kgsm on every poll; an api restart re-attempts.
                _installDir = null;
            }
            _installDirRead = true;
            return _installDir;
        }
    }

    /// <summary>Build the single host this api serves (the <c>GET /hosts</c> list element — capacity +
    /// capabilities, no <c>network</c> grid; that is detail-only, see <see cref="GetHostDetailAsync"/>).</summary>
    public async Task<Host> GetHostAsync(CancellationToken ct)
    {
        Snap.Snapshot? snapshot = await monitor.GetLatestAsync(ct).ConfigureAwait(false);
        HostMetricsDto? capacity = snapshot is null ? null : MetricsMapping.ToHostMetrics(snapshot);

        // Editable identity overrides (region/label) — the stored value wins, else config (cached; no DB hit
        // on the hot path). Effective label is what the SPA renders as the host name.
        HostSettingsRecord overrides = await settings.GetAsync(ct).ConfigureAwait(false);

        return new Host(
            Id: options.HostId,
            Label: settings.EffectiveLabel(overrides),
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
            // The engine's configured base install directory (cached once) — per-host, so the install modal
            // shows this host's real default. Null when the engine isn't provisioned / the key is unset.
            InstallDirectory: ReadInstallDirectory(),
            // M-diag depth: STATIC cpu identity comes straight off the snapshot (not on the metrics tick),
            // null when there is no snapshot; DYNAMIC sensors ride the shared capacity DTO (so the Host view
            // and a metrics tick carry the same hwmon list). Honest-null/empty when not measurable.
            Cpu: snapshot is null ? null : MetricsMapping.ToCpuInfo(snapshot.Cpu.Info),
            Sensors: capacity?.Sensors,
            // The identity card: operator-declared region joined with the runtime-derived OS/runtime/build/
            // start-time (each honest-null when unsourceable). Cheap + static, so present on both list and detail.
            Identity: new HostIdentity(
                Region: settings.EffectiveRegion(overrides),
                Os: identity.Os,
                Runtime: identity.Runtime,
                Build: identity.Build,
                StartedAt: identity.StartedAt));
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
