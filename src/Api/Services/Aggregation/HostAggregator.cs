using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;
// The sibling namespace TheKrystalShip.Api.Services.Library (the game catalog) shadows the type name,
// so the engine model is aliased. A placement root and the blueprint catalog share a word and nothing else.
using KgsmLibrary = TheKrystalShip.KGSM.Core.Models.Library;
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
    // The node-capacity policy is config the operator TUNES, so it is
    // re-read on a short interval rather than pinned for the process lifetime: a changed floor should
    // reach the panel while the person who changed it is still looking. One kgsm invocation a minute at
    // most, and only while somebody is reading /hosts. The same TTL the watchdog uses for these keys.
    private static readonly TimeSpan MemoryGateTtl = TimeSpan.FromMinutes(1);
    private MemoryGatePolicy? _memoryGate;
    private DateTimeOffset _memoryGateReadAt = DateTimeOffset.MinValue;
    private readonly object _memoryGateLock = new();

    private MemoryGatePolicy? ReadMemoryGate()
    {
        lock (_memoryGateLock)
        {
            if (DateTimeOffset.UtcNow - _memoryGateReadAt < MemoryGateTtl) return _memoryGate;

            MemoryGatePolicy? policy = null;
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                if (scope.ServiceProvider.GetService<IConfigService>() is IConfigService config)
                {
                    string? rawEnabled = config.Get("enable_memory_gate");
                    string? rawHeadroom = config.Get("memory_gate_headroom_mb");

                    // Both keys absent means a host whose config predates the gate. The engine still gates
                    // there, on its own coded defaults — but this API does not know them, and publishing a
                    // guessed floor would have the panel warn against a number nobody configured. Null is
                    // the honest answer: the client warns about nothing and the engine still decides.
                    if (!string.IsNullOrWhiteSpace(rawEnabled) || !string.IsNullOrWhiteSpace(rawHeadroom))
                    {
                        bool enabled = !string.Equals(rawEnabled?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
                        policy = int.TryParse(rawHeadroom?.Trim(), out int mb) && mb >= 0
                            ? new MemoryGatePolicy(enabled, mb)
                            : null;
                    }
                }
            }
            catch
            {
                // An engine hiccup is an unknown policy, never a fabricated one.
                policy = null;
            }

            _memoryGate = policy;
            _memoryGateReadAt = DateTimeOffset.UtcNow;
            return _memoryGate;
        }
    }

    /// <summary>
    /// Read this host's library registry — the named roots instances are placed in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured on every read, never cached.</b> Whether a library is online and how much room it
    /// has left are facts about a disk that can be unplugged between two page loads. A cached answer here
    /// would report a mounted drive that is gone, and a free-space figure from an hour ago is a number
    /// somebody would place an install against.
    /// </para>
    /// <para>
    /// An engine that cannot answer yields <see langword="null"/>, which is not an empty list: a host with
    /// no library registered is a different, sayable fact from a host whose registry could not be read.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<KgsmLibrary>?> ReadLibrariesAsync()
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<ILibraryService>() is not ILibraryService libraries)
                return null;

            // kgsm-lib's services are synchronous process invocations; off the request thread, like every
            // other call site that reaches the engine from an async path.
            return await Task.Run(libraries.List).ConfigureAwait(false);
        }
        catch
        {
            // An engine hiccup is an unknown registry, never a fabricated one.
            return null;
        }
    }

    /// <summary>
    /// Join each library to the mount it sits on, so the storage card can name the device.
    /// </summary>
    /// <remarks>
    /// <b>Status from the engine, metrics from the monitor, and neither is inferred from the other —
    /// in both directions.</b> The join adds the mount point and the backing disk model and nothing else,
    /// so a library the monitor has no row for stays exactly as online as the engine measured it, and a
    /// library the monitor can see is not thereby online. The mirror of that rule is what bounds the join:
    /// an <b>offline</b> library is joined to nothing, because its disk is gone and every mount the monitor
    /// reports belongs to some other one. The root filesystem contains every path by definition, so a join
    /// run over an unreachable root would name the boot disk's model as the backing device of files that
    /// are not on it — an invented fact beside a <c>null</c> capacity that says nothing could be measured.
    /// <see cref="LibraryDto.Mount"/> and <see cref="LibraryDto.Device"/> are <see langword="null"/> there,
    /// on the same terms as <see cref="LibraryDto.FreeBytes"/>/<see cref="LibraryDto.TotalBytes"/>.
    /// <para>
    /// Longest prefix wins: <c>/mnt/ssd</c> and <c>/</c> both contain <c>/mnt/ssd/kgsm</c>, and the
    /// deeper mount is the filesystem the bytes are actually on.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<LibraryDto> JoinCapacity(
        IReadOnlyList<KgsmLibrary> libraries, IReadOnlyList<DiskCapacity>? disks)
    {
        var mapped = new List<LibraryDto>(libraries.Count);
        foreach (KgsmLibrary lib in libraries)
        {
            DiskCapacity? disk = null;
            if (disks is not null && lib.Online)
            {
                foreach (DiskCapacity candidate in disks)
                {
                    if (!Contains(candidate.Mount, lib.Path)) continue;
                    if (disk is null || candidate.Mount.Length > disk.Mount.Length) disk = candidate;
                }
            }

            mapped.Add(new LibraryDto(
                Name: lib.Name,
                Path: lib.Path,
                Online: lib.Online,
                // Null for an offline library — nothing measured an unplugged disk, and a 0 would read as
                // a full one.
                FreeBytes: lib.FreeBytes,
                TotalBytes: lib.TotalBytes,
                InstanceCount: lib.InstanceCount,
                Mount: disk?.Mount,
                Device: disk?.Device));
        }
        return mapped;
    }

    // Is `path` on the filesystem mounted at `mount`? Compared as whole path components, so /mnt/ssd
    // does not claim /mnt/ssd-backup.
    private static bool Contains(string mount, string path)
    {
        if (string.IsNullOrEmpty(mount) || string.IsNullOrEmpty(path)) return false;
        if (mount == "/") return path.StartsWith('/');
        if (!path.StartsWith(mount, StringComparison.Ordinal)) return false;
        return path.Length == mount.Length || path[mount.Length] == '/';
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

        IReadOnlyList<KgsmLibrary>? registry = await ReadLibrariesAsync().ConfigureAwait(false);

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
            // The named roots this host places instances in, measured on this read and joined to the
            // monitor's mounts for the device name. Null when the engine could not answer — which the
            // client renders as "no placement surface", not as "no libraries".
            Libraries: registry is null ? null : JoinCapacity(registry, capacity?.Disks),
            MemoryGate: ReadMemoryGate(),
            // M-diag depth: STATIC cpu identity comes straight off the snapshot (not on the metrics tick),
            // null when there is no snapshot; DYNAMIC sensors and fans ride the shared capacity DTO (so the
            // Host view and a metrics tick carry the same hwmon lists). Honest-null/empty when not measurable.
            Cpu: snapshot is null ? null : MetricsMapping.ToCpuInfo(snapshot.Cpu.Info),
            Sensors: capacity?.Sensors,
            Fans: capacity?.Fans,
            Gpus: capacity?.Gpus,
            Slice: capacity?.Slice,
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

        // Unprojected: the caller's tier decides what survives, and only the controller knows it. Serving this
        // straight to a viewer would name every process on the card — see GpuRedaction.
        return host with
        {
            Network = net,
            GpuProcesses = MetricsMapping.ToGpuProcesses(
                (await monitor.GetLatestAsync(ct).ConfigureAwait(false))?.Gpu),
        };
    }
}
