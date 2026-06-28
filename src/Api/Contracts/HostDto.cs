using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// A host (a machine running KGSM) with its capacity summary and capability block —
/// the honest realization of <c>architecture.html §4·a / §4·b</c>, frozen at M1·a.
/// <para>
/// This api is per-host: <c>GET /hosts</c> returns exactly this one host, and the SPA
/// fans out across hosts and rolls up client-side (there is no <c>/fleet</c>). The
/// capacity fields are <strong>nullable</strong>: when the metrics capability is not
/// <c>operational</c> they are <c>null</c> ("not measurable now"), never a fabricated
/// number — honest unknown over invented data.
/// </para>
/// </summary>
public sealed record Host(
    string Id,
    string Label,
    // The api answers on the host it runs on, so reaching this response means the host is
    // up: "online". The SPA's "N of M online" rollup is its own client-side count.
    string Status,
    double? CpuPct,
    MemCapacity? Mem,
    IReadOnlyList<DiskCapacity>? Disks,
    HostCapabilities Capabilities,
    // M-diag additive host telemetry — the rest of the monitor Snapshot the diagnostics deep-dive needs:
    // per-core CPU %, load average, host-aggregate block-IO, per-interface throughput, the hostname, uptime,
    // and the sample timestamp (for honest, server-sourced freshness). Present on BOTH the list and the
    // detail view: unlike the Network block below (an on-demand firewall probe), these come straight from the
    // already-cached metrics snapshot at zero marginal cost, so the "keep the list lean" rationale doesn't
    // apply. All null when the metrics capability isn't operational (honest-unknown, never fabricated), and
    // emitted as explicit null rather than omitted so the SPA binds one stable shape.
    IReadOnlyList<double>? PerCore = null,
    LoadSample? Load = null,
    DiskIoSample? DiskIo = null,
    IReadOnlyList<InterfaceSample>? Interfaces = null,
    string? Hostname = null,
    long? UptimeSec = null,
    long? SampleTs = null,
    // The Control Panel API's own in-process version (== ApiInfo.ApiVersion) — the host's "panel"
    // is this api. Always present (it's a build-time constant, not a measured value), and sourced
    // from the same shared const as the GET /api/v1 handshake so the two can't drift. NOT the host
    // OS / kernel version (those have no honest source today and stay client-side "—").
    string? PanelVersion = null,
    // This host's KGSM default install directory (config_default_install_directory) — the base path under
    // which new instances are created as <dir>/<blueprint>/<instance>. Read once from the engine's own
    // config (per host: each host runs its own kgsm), so the install modal can show the real, host-specific
    // base instead of a hardcoded path. Null when the engine isn't provisioned or the key is unset (honest
    // unknown, never a fabricated default).
    string? InstallDirectory = null,
    // M-diag depth (Monitor.Contracts 1.1.0). STATIC CPU identity — model/cores/threads/maxFreqGhz — is the
    // same every frame, so it lives on this Host view ONLY (like nothing on the tick) and is NOT re-pushed per
    // metrics WS tick. Null when there is no snapshot, and each inner field null when its /proc/sys source can't
    // be read (honest-unknown, never guessed). The DYNAMIC depth fields ride their existing shared shapes
    // (mem cached/buffers on MemCapacity, mac/errors on InterfaceSample, disk device on DiskCapacity) so they
    // mirror onto HostMetricsDto automatically — except sensors, which is its own list (below).
    CpuInfoSample? Cpu = null,
    // Sensor temperatures (hwmon) — DYNAMIC, so mirrored on HostMetricsDto too. Empty array when no hwmon chip
    // exposes a temperature (never an invented row); null only when there is no snapshot at all.
    IReadOnlyList<SensorSample>? Sensors = null,
    // The host-wide open-ports grid (M6·b) — populated ONLY on the GET /hosts/{id} detail view; omitted
    // on the GET /hosts list (this block stays detail-only; the metrics telemetry above rides both). Null
    // when the firewall can't answer (absent/unreachable/unknown); an empty OpenPorts means the firewall
    // answered and owns no rules.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HostNetwork? Network = null);

/// <summary>
/// Host memory capacity, in GiB (matching the §4·a contract unit). <see cref="Used"/>/<see cref="Total"/>
/// are the frozen M1·a summary; <see cref="Available"/>/<see cref="SwapUsed"/>/<see cref="SwapTotal"/> are the
/// M-diag additive breakdown — null when constructed without a snapshot (honest-unknown, never a fabricated 0).
/// </summary>
public sealed record MemCapacity(
    double Used,
    double Total,
    double? Available = null,
    double? SwapUsed = null,
    double? SwapTotal = null,
    // M-diag depth (Monitor.Contracts 1.1.0), in GiB to match the other mem figures. The kernel page cache
    // (Cached) and block-device buffers (Buffers) the monitor always sources — present whenever a snapshot is.
    double? Cached = null,
    double? Buffers = null);

/// <summary>One mounted filesystem's capacity, in GiB. <see cref="Fs"/> (filesystem type, e.g. <c>ext4</c>) and
/// <see cref="Device"/> (the M-diag-depth backing-disk MODEL string, e.g. "Samsung SSD 990 EVO Plus 1TB" — NOT
/// the <c>/dev</c> node) are additive, each null when not sourced. Both are static-per-mount but ride this one
/// shared <see cref="DiskCapacity"/> shape (used by both <see cref="Host"/> and the metrics tick), so — like
/// <see cref="Fs"/> already does — <see cref="Device"/> appears on both surfaces; there is no per-mount home
/// to keep it off the tick without breaking the byte-identical <c>Host.Disks == tick.Disks</c> invariant.</summary>
public sealed record DiskCapacity(string Mount, double Used, double Total, string? Fs = null, string? Device = null);

/// <summary>Host load average over the last 1/5/15 minutes (monitor <c>LoadAvg</c>). Diagnostics-only.</summary>
public sealed record LoadSample(double One, double Five, double Fifteen);

/// <summary>Host-aggregate block-IO throughput in bytes/sec (monitor <c>DiskIo</c>).</summary>
public sealed record DiskIoSample(long ReadBps, long WriteBps);

/// <summary>One network interface's throughput (monitor <c>InterfaceRate</c>): bytes/sec and packets/sec in
/// each direction, plus the M-diag-depth <see cref="Mac"/> and <see cref="Errors"/> (Monitor.Contracts 1.1.0).
/// The monitor still sources no ip address (honest-unknown on the client, rendered "—"). <see cref="Mac"/> is
/// the hardware address (null when unreadable). <see cref="Errors"/> is total link errors (rx+tx summed) — null
/// ONLY when neither counter file reads; a genuine <c>0</c> stays <c>0</c> and is never conflated with unknown.</summary>
public sealed record InterfaceSample(
    string Name, long RxBps, long TxBps, long RxPps, long TxPps, string? Mac = null, long? Errors = null);

/// <summary>Static CPU identity (monitor <c>CpuInfo</c>, Monitor.Contracts 1.1.0) — read once at startup, the
/// same on every frame, so carried on the <see cref="Host"/> view only (not the metrics tick). Each field is
/// null when its <c>/proc/cpuinfo</c>/<c>cpufreq</c> source can't be read (never guessed). <see cref="MaxFreqGhz"/>
/// is already in GHz (the monitor converts kHz→GHz) — passed through rounded, never re-converted.</summary>
public sealed record CpuInfoSample(string? Model, int? Cores, int? Threads, double? MaxFreqGhz);

/// <summary>One hwmon temperature reading (monitor <c>SensorReading</c>, Monitor.Contracts 1.1.0):
/// <see cref="Chip"/> (e.g. <c>k10temp</c>), an optional <see cref="Label"/> (e.g. <c>Tctl</c>), and
/// <see cref="ValueC"/> in °C. Passed through 1:1 from the snapshot — the sensors array is empty (never an
/// invented row) when no chip exposes a temperature.</summary>
public sealed record SensorSample(string Chip, string? Label, double ValueC);

/// <summary>
/// The per-host capability block (architecture §4·b). Each optionally-exposed backend
/// service reports its live state independently; a capability can fail without the host
/// going offline, and each surface degrades per-capability.
/// </summary>
public sealed record HostCapabilities(
    Capability Metrics,
    Capability Assistant,
    Capability Watchdog);

/// <summary>
/// One capability's state. <see cref="Provisioned"/> = declared on this host;
/// <c>provisioned:false</c> is what the client folds into the derived <c>absent</c> render
/// state. <see cref="Status"/> is the live report: see <see cref="CapabilityStatus"/>.
/// <see cref="Since"/>/<see cref="Message"/>/<see cref="Info"/> are optional and omitted
/// when absent (<c>since</c> needs status-change tracking, which arrives with the M2
/// capabilities stream — it is intentionally not emitted yet rather than fabricated).
/// </summary>
public sealed record Capability(
    bool Provisioned,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? Since = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Info = null)
{
    /// <summary>A capability that is configured but currently not functioning.</summary>
    public static Capability Down(bool provisioned = true, string? message = null) =>
        new(provisioned, CapabilityStatus.Down, Message: message);

    /// <summary>A capability that is not declared on this host. The client folds
    /// <c>provisioned:false</c> into <c>absent</c> regardless of status; we fill <c>unknown</c>
    /// rather than imply a provisioned-but-broken service.</summary>
    public static readonly Capability Absent = new(false, CapabilityStatus.Unknown);
}

/// <summary>
/// The reported capability statuses (architecture §4·b). <c>operational</c>/<c>degraded</c>
/// are "usable"; <c>down</c>/<c>unknown</c> are not, and <c>provisioned:false</c> derives to
/// <c>absent</c> on the client. M1·a emits <c>operational</c>/<c>down</c> (+ <c>absent</c> via
/// <c>provisioned:false</c>); <c>degraded</c> (stale/last-good) and <c>unknown</c> arrive with
/// the M2 stream.
/// </summary>
public static class CapabilityStatus
{
    public const string Operational = "operational";
    public const string Degraded = "degraded";
    public const string Down = "down";
    public const string Unknown = "unknown";
}

/// <summary>Honestly-sourced <c>info</c> for the metrics capability — the monitor's nominal
/// sampling interval, straight from the snapshot.</summary>
public sealed record MetricsCapabilityInfo(int IntervalMs);
