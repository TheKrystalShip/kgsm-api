namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The host capacity sample pushed on the <c>hosts/{id}/metrics</c> topic (M2). It is exactly the
/// mutable, measured portion of the <see cref="Host"/> view (<c>architecture.html §4·a</c>) — the
/// same <see cref="MemCapacity"/>/<see cref="DiskCapacity"/> shapes and units (GiB) the REST
/// <c>GET /hosts</c> emits — minus the stable identity/label and the capability block (capability
/// flips ride their own <c>hosts/{id}/capabilities</c> topic). Built from one monitor snapshot via
/// the shared <c>MetricsMapping</c>, so a tick is byte-identical to the REST capacity figures.
/// <para>The M-diag enrichment adds the rest of the honestly-measured snapshot the diagnostics deep-dive
/// reads — per-core CPU, load average, host-aggregate block-IO, per-interface throughput, hostname, uptime,
/// and the sample timestamp (<see cref="SampleTs"/>, the honest freshness source). The Monitor.Contracts 1.1.0
/// depth adds the DYNAMIC fields: mem cached/buffers (on <see cref="MemCapacity"/>), per-interface mac/errors
/// (on <see cref="InterfaceSample"/>), and the hwmon <see cref="Sensors"/> list — all measured, honest-null/empty
/// when absent. The STATIC CPU identity and per-mount disk device are constant per frame: cpu-info rides the
/// <see cref="Host"/> view only (not this tick); disk device rides the shared <see cref="DiskCapacity"/> shape
/// (so it appears here too — the byte-identical <c>Host.Disks == tick.Disks</c> invariant, as <c>Fs</c> already
/// does). Still nothing fabricated: the snapshot has no ip address, so that never appears.</para>
/// </summary>
public sealed record HostMetricsDto(
    double CpuPct,
    MemCapacity Mem,
    IReadOnlyList<DiskCapacity> Disks,
    IReadOnlyList<double> PerCore,
    LoadSample Load,
    DiskIoSample DiskIo,
    IReadOnlyList<InterfaceSample> Interfaces,
    string Hostname,
    long UptimeSec,
    long SampleTs,
    // M-diag depth (Monitor.Contracts 1.1.0) — the DYNAMIC sensor temperatures, mirrored from the Host view so a
    // WS tick stays byte-identical to the REST element (cached/buffers + mac/errors ride MemCapacity/InterfaceSample
    // above). Empty array when no hwmon chip reports (never invented); on the tick a snapshot always exists.
    IReadOnlyList<SensorSample> Sensors);

/// <summary>
/// The roster-removal tombstone pushed on the <c>servers</c> topic as <c>server.removed</c> (M2):
/// the instance with this id is gone from the roster, so the client drops it. Distinct from a
/// <c>server.patch</c> (which carries a full element to merge); a removal and a pending patch for
/// the same id share a coalesce key, so the latest event wins.
/// </summary>
public sealed record ServerRemoved(string Id);
