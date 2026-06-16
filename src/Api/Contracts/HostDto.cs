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
    // The host-wide open-ports grid (M6·b) — populated ONLY on the GET /hosts/{id} detail view; omitted
    // on the GET /hosts list (which stays the M1·a shape). Null when the firewall can't answer
    // (absent/unreachable/unknown); an empty OpenPorts means the firewall answered and owns no rules.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HostNetwork? Network = null);

/// <summary>Host memory capacity, in GiB (matching the §4·a contract unit).</summary>
public sealed record MemCapacity(double Used, double Total);

/// <summary>One mounted filesystem's capacity, in GiB.</summary>
public sealed record DiskCapacity(string Mount, double Used, double Total);

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
