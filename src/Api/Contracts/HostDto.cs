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
    // The named roots this host places instances in — the engine's library registry, measured on every
    // read (state and free space are facts about a disk that may be unplugged between two page loads, so
    // nothing here is cached). Null when the engine isn't provisioned or cannot answer, which is an
    // honest unknown and NOT the same as an empty list (a host with no library registered): a client
    // hides the placement surface entirely on null and offers "register one" on empty.
    IReadOnlyList<LibraryDto>? Libraries = null,
    // This host's node-capacity policy, read from the engine's own config (per host: each host runs its
    // own kgsm, and each may be tuned differently). It is the FLOOR a surface needs to warn before a
    // start — joined against the server's StartMemoryMb and this host's live free memory — so the
    // panel's hint and the engine's refusal are computed from the same rule and the same numbers.
    // Null when the engine isn't provisioned or the keys are unset; the gate then still runs on its own
    // coded defaults, so a client that finds this null must warn about nothing rather than assume a floor.
    MemoryGatePolicy? MemoryGate = null,
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
    // Fan tachometers (hwmon) — DYNAMIC, mirrored on HostMetricsDto exactly like Sensors. Empty array when
    // no fan is turning or the host exposes no tachometer; null only when there is no snapshot at all.
    IReadOnlyList<FanSample>? Fans = null,
    // The host's GPUs — DYNAMIC, so mirrored on HostMetricsDto too (like Sensors above), which is what lets
    // the overview render a device on its first REST load and keep it current from the tick. Null when the
    // host has no readable card, or when the metrics capability is not operational.
    IReadOnlyList<GpuSample>? Gpus = null,
    // The kgsm.slice aggregate — what the game servers collectively cost, measured at the parent cgroup
    // itself (recursive over every descendant, so it also covers instances mid-teardown that have no
    // per-server row). DYNAMIC, mirrored on HostMetricsDto like Sensors/Gpus. Null when kgsm.slice doesn't
    // exist on this host (no watchdog, nothing native ever started) or there is no snapshot.
    KgsmSliceSample? Slice = null,
    // The host identity card — operator-declared region (+ the editable display label, already surfaced as
    // Label) joined with the runtime-derived OS / .NET runtime / build version / API start time. Present on
    // BOTH the list and detail (cheap, static, no leaf needed — like PanelVersion); each inner field is
    // honest-null when its source can't be read, never fabricated. See HostIdentity.
    HostIdentity? Identity = null,
    // The host-wide open-ports grid (M6·b) — populated ONLY on the GET /hosts/{id} detail view; omitted
    // on the GET /hosts list (this block stays detail-only; the metrics telemetry above rides both). Null
    // when the firewall can't answer (absent/unreachable/unknown); an empty OpenPorts means the firewall
    // answered and owns no rules.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HostNetwork? Network = null,
    // The per-process GPU breakdown — detail-only (like Network above) and OPERATOR-GATED: below operator a
    // context belonging to no known unit is stripped of its pid and name and folded into one unnamed row per
    // device, keeping its memory so the card still totals honestly. Null when the host has no GPU. The device
    // list itself is not gated and rides the metrics tick (HostMetricsDto.Gpus).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GpuProcessSample>? GpuProcesses = null);

/// <summary>
/// This host's node-capacity policy — KGSM's <c>[resources]</c> keys, as the engine will apply them.
/// </summary>
/// <remarks>
/// <para>
/// The engine refuses a start that would leave the node with less than <see cref="HeadroomMb"/> free,
/// judging an instance by its own <c>memory_cap_mb</c> or, failing that, its blueprint's advisory
/// <c>min_ram_mb</c> (published per server as <see cref="Server.StartMemoryMb"/>).
/// </para>
/// <para>
/// Published so a surface can warn <em>before</em> a start instead of only reporting the refusal
/// afterwards. It is the rule's INPUT, not its verdict: the engine reads the node's free memory at the
/// moment it acts, and re-decides then.
/// </para>
/// </remarks>
/// <param name="Enabled">Whether the gate refuses at all. False means starts proceed regardless, and a
/// surface has nothing to warn about.</param>
/// <param name="HeadroomMb">Megabytes that must remain available after a start.</param>
public sealed record MemoryGatePolicy(bool Enabled, int HeadroomMb);

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

/// <summary>
/// One named root instances are placed in — a KGSM library, the host-local answer to "which disk does
/// this server live on".
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Two sources, never inferred from one another.</b> <see cref="Online"/>, <see cref="FreeBytes"/>
/// and <see cref="TotalBytes"/> come from the engine's own per-invocation check (the root exists and
/// carries its marker; <c>df</c> on that root). <see cref="Mount"/> and <see cref="Device"/> come from
/// the monitor's capacity snapshot, joined by longest-prefix match on the mount point. A library with
/// no monitor row is still online; a library the monitor can see is not thereby online.
/// </para>
/// <para>
/// ⚠ <see cref="FreeBytes"/>/<see cref="TotalBytes"/> are <see langword="null"/> for an offline library
/// — nothing measured an unplugged disk. A client must render that as unknown: a <c>0</c> reads as a
/// full disk, which is the opposite fact. <see cref="Mount"/>/<see cref="Device"/> are null there on the
/// same grounds: they are the same class of fact, and naming a device beside an unmeasurable capacity
/// would say a disk is known when the whole point is that it is gone.
/// </para>
/// </remarks>
/// <param name="Name">The registry name, host-unique — what <c>--library</c> and the install form take.</param>
/// <param name="Path">The absolute, canonical library root.</param>
/// <param name="Online">Whether the root is reachable and carries its marker. Placement into an offline
/// library is refused by the engine.</param>
/// <param name="InstanceCount">How many registered instances resolve here. Counted from the instance
/// registry, so it is answered for an offline library too — which is exactly when it matters, because it
/// is what a removal is refused over.</param>
/// <param name="Mount">The filesystem the root sits on, from the monitor. Null when there is no snapshot,
/// when no mount contains the path, and for an offline library — an unreachable root sits on no measured
/// filesystem, and the root mount contains every path by definition.</param>
/// <param name="Device">The backing disk model behind <see cref="Mount"/>, from the monitor. Null on the
/// same terms.</param>
public sealed record LibraryDto(
    string Name,
    string Path,
    bool Online,
    long? FreeBytes,
    long? TotalBytes,
    int InstanceCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Device = null);

/// <summary>The request body for <c>POST /hosts/{id}/libraries</c> — register a root.</summary>
/// <param name="Path">Absolute path of the root. Required; the engine creates the directory when it does
/// not exist and adopts a root that already carries a marker.</param>
/// <param name="Name">Optional registry name. Absent, the engine takes the directory's own name, or the
/// name written in an existing marker.</param>
/// <param name="Origin">The driving surface, like <see cref="CommandRequest.Origin"/>.</param>
public sealed record AddLibraryRequest(string? Path, string? Name = null, string? Origin = null);

/// <summary>The request body for <c>PATCH /hosts/{id}/libraries/{name}</c> — rename a library.</summary>
/// <remarks>
/// Instances are unaffected: each records its library's <em>path</em>, and the name lives only in the
/// registry, so a rename costs nothing and moves no files.
/// </remarks>
public sealed record RenameLibraryRequest(string? Name, string? Origin = null);

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

/// <summary>One hwmon temperature reading (monitor <c>SensorReading</c>). Passed through 1:1 from the
/// snapshot — the sensors array is empty (never an invented row) when no chip exposes a temperature.</summary>
/// <remarks>
/// <para><see cref="Id"/> is the key to correlate and de-duplicate on, not <see cref="Chip"/>: chips share
/// names (two DDR4 DIMMs are both <c>jc42</c>) and the hwmon index behind them moves between reboots. It is
/// also what a <c>HostTempC</c> threshold condition puts in its <c>ref</c>.</para>
/// <para><see cref="Role"/> and <see cref="Name"/> are the monitor's classification of the channel — the
/// daemon that reads the register is the one that knows what the register is. Both are null together when
/// the monitor's catalog has no entry for the chip, which means unrecognised hardware, NOT a doubtful
/// reading: <see cref="ValueC"/> is measured either way and a surface falls back to chip/label.</para>
/// </remarks>
public sealed record SensorSample(
    string Id, string Chip, string? Label, double ValueC, string? Role, string? Name);

/// <summary>One hwmon fan tachometer (monitor <c>FanReading</c>), in RPM, passed through 1:1.</summary>
/// <remarks>
/// Separate from <see cref="SensorSample"/> because that record's <c>ValueC</c> is a °C contract. Only fans
/// that are turning appear: an unpopulated header and a stopped fan both read zero and hwmon cannot
/// separate them, so the monitor omits zeros rather than asserting fans that do not exist. <see cref="Name"/>
/// is the driver's own label where it publishes one and the channel number otherwise — which header drives
/// which physical fan is board wiring that hwmon does not carry.
/// </remarks>
public sealed record FanSample(string Id, string Chip, string? Label, int Rpm, string? Name);

/// <summary>
/// The kgsm.slice aggregate (monitor <c>SliceMetrics</c>) — the game servers' collective share of the
/// host. <see cref="CpuPctCore"/> follows the per-server unit (percent of ONE core, may exceed 100;
/// null on the monitor's first observation, when there is no delta to rate against); <see cref="MemBytes"/>
/// stays in bytes like <c>ServerMetricsDto.MemBytes</c>. Each field null when its counter was unreadable —
/// passed through 1:1, never substituted.
/// </summary>
public sealed record KgsmSliceSample(double? CpuPctCore, long? MemBytes, int? Pids);

/// <summary>
/// One GPU on this host (monitor <c>GpuDevice</c>). Rides the metrics tick: a device describes hardware and
/// names nobody, so it needs no gating — unlike <see cref="GpuProcessSample"/>.
/// </summary>
/// <remarks>
/// ⚠ <see cref="MemUsed"/> is the <em>device's</em> figure and is larger than the sum of the processes on it:
/// driver and CUDA context overhead occupies memory while belonging to no process. Never reconcile the two by
/// adjusting either.
/// <para>
/// ⚠ Video memory does not pool. A client showing several devices renders them separately and never sums
/// <see cref="MemTotal"/> or <see cref="MemUsed"/> across them — a total would imply a model could use it, and
/// a model that does not fit on one card fails to load rather than spilling onto another.
/// </para>
/// </remarks>
/// <param name="Uuid">Stable across reboots; the key to join a device to its history series. Not the index.</param>
/// <param name="MemUsed">Device memory in use, GiB.</param>
/// <param name="MemTotal">Total device memory, GiB.</param>
/// <param name="SmPct">Device-wide compute utilisation, or null when the driver can't report it.</param>
public sealed record GpuSample(
    int Index,
    string Name,
    string Uuid,
    double MemUsed,
    double MemTotal,
    double? SmPct,
    double? TempC = null,
    double? PowerW = null,
    double? PowerCapW = null);

/// <summary>
/// One compute context on a GPU (monitor <c>GpuProcess</c>), carried on the <see cref="Host"/> detail only and
/// <strong>operator-gated</strong>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why gated:</b> the monitor sees every process using the card, including ones that have nothing to do with
/// KGSM — somebody's training run, a CUDA experiment. Naming those to every viewer is the same mistake as
/// putting a player's connection address on an audit row. Below operator, contexts that resolve to no known
/// unit are collapsed into a single unnamed row by <c>GpuRedaction</c>.
/// </para>
/// <para>
/// ⚠ That collapse <b>keeps the memory figure</b>. Dropping the rows would leave the per-process figures
/// failing to sum to the device's, which reports a quieter falsehood than the one it avoids.
/// </para>
/// </remarks>
/// <param name="Pid">Null on a row that stands for several withheld contexts.</param>
/// <param name="Name">The process name, or null when withheld.</param>
/// <param name="Unit">The systemd unit that owns it, or null when it belongs to none.</param>
/// <param name="MemUsed">Device memory held, GiB. Always real, whether or not the row is named.</param>
/// <param name="SmPct">
/// Compute utilisation, or null when the process did no work in the sampling window. Null is idleness, not a
/// measured zero — a client must not render it as 0.
/// </param>
public sealed record GpuProcessSample(
    int DeviceIndex,
    int? Pid,
    string? Name,
    string? Unit,
    double MemUsed,
    double? SmPct);

/// <summary>
/// GPU attributed to one leaf (monitor <c>LeafGpu</c>) — kept apart from that leaf's own CPU and memory, which
/// stay scoped to its process tree.
/// </summary>
/// <remarks>
/// <see cref="Attribution"/> is <c>own</c> when the leaf's own processes hold the contexts and <c>backend</c>
/// when the figures come from units it drives but does not own. A client renders <see cref="Units"/> either
/// way, so the reader sees where the number came from.
/// <para>
/// ⚠ A <c>backend</c> figure can be present while the leaf itself is stopped — a socket-activated model stays
/// resident after the service that asked for it exits. Never render this as the leaf's own footprint.
/// </para>
/// </remarks>
public sealed record LeafGpuSample(
    string Attribution,
    IReadOnlyList<string> Units,
    double MemUsed,
    double? SmPct);

/// <summary>
/// This host's identity card — the descriptive "who/where/what is this host" block that sits alongside the
/// live <see cref="HostCapabilities"/> (the "what can it do" block) on the <see cref="Host"/> record. Two
/// honesty classes, never conflated: <see cref="Region"/> is <strong>operator-declared</strong> (config or a
/// <c>PATCH /hosts/{id}</c>, null when unset — never guessed); <see cref="Os"/>/<see cref="Runtime"/>/<see
/// cref="Build"/>/<see cref="StartedAt"/> are <strong>runtime-derived</strong> (read from the OS / the
/// assembly). The editable display label is surfaced as <see cref="Host.Label"/> (not duplicated here).
/// </summary>
public sealed record HostIdentity(
    // Deployment region — an arbitrary free string (e.g. "eu-west", "homelab-basement"), NOT a restricted
    // enum. Null when neither the override nor Api__Region is set (honest unknown).
    string? Region,
    // The OS the API/host runs on (name/kernel/arch). Null only if even the runtime OS description is blank.
    OsInfo? Os,
    // The .NET runtime, e.g. ".NET 10.0.0".
    string Runtime,
    // This build's version (assembly informational version: <Version> + git SHA) — "which build is this host
    // running". The route version stays "v1" (ApiInfo.ApiVersion / panelVersion); this is the build axis.
    string Build,
    // When the API process started (UTC) — distinct from host uptime; answers "did the API restart".
    DateTimeOffset StartedAt);

/// <summary>
/// Sparse update body for <c>PATCH /hosts/{id}</c> (admin) — the editable half of the identity card. Only
/// the present fields change: a <see langword="null"/> field is left unchanged; an explicit empty string
/// <strong>clears</strong> the override (the value falls back to its <c>Api__*</c> config default). Both
/// are free-form strings (length-bounded by the controller); region is NOT a restricted enum.
/// </summary>
public sealed record HostPatch(string? Label, string? Region);

/// <summary>The OS the API/host runs on. <see cref="Name"/> is the distro pretty-name (e.g. "Arch Linux"),
/// <see cref="Kernel"/> the kernel release (e.g. "7.0.12-arch1-1"); each null when unreadable. <see cref="Arch"/>
/// (e.g. "x64") is always available from the runtime.</summary>
public sealed record OsInfo(string? Name, string? Kernel, string Arch);

/// <summary>
/// The per-host capability block (architecture §4·b). Each optionally-exposed backend
/// service reports its live state independently; a capability can fail without the host
/// going offline, and each surface degrades per-capability.
/// </summary>
public sealed record HostCapabilities(
    Capability Metrics,
    Capability Assistant,
    Capability Watchdog,
    Capability Scheduler,
    Capability Reactor);

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

/// <summary>
/// <c>info</c> for the assistant capability — the public origin a browser reaches the assistant leaf
/// on, straight from <c>Api:AssistantPublicUrl</c>. The Control Panel's chat addresses the leaf
/// directly on it; this API relays nothing for its own host. Emitted only when the origin is
/// configured, so a host without one reports the capability with no <c>info</c> and the chat says the
/// assistant has no browser route rather than guessing at an address.
/// </summary>
public sealed record AssistantCapabilityInfo(string Url);
