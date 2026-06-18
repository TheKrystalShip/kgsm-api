using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// A game server (a kgsm instance on this host) — the <strong>honest realization</strong> of the
/// <c>architecture.html §3</c> <c>Server</c> example, frozen at M1·b. It is the join of the kgsm
/// engine's domain + run-state (via kgsm-lib) with the per-instance metrics (via kgsm-monitor),
/// keyed on the instance id.
/// <para>
/// The aspirational v0.3 example asks for fields that have <strong>no honest backing source
/// today</strong> — emitting them verbatim would be fabrication (the sin that scrapped the old
/// kgsm-api). So this DTO deliberately diverges, and that divergence is the contract:
/// </para>
/// <list type="bullet">
///   <item><description><c>status</c> is the tri-state <c>running|stopped|unknown</c> derived from
///     kgsm-lib's <c>Reading&lt;InstanceRuntimeStatus&gt;</c> — never the aspirational
///     <c>online|offline|updating|crashed|installing</c> (the transitional states need the M3 job
///     tracker and crash detection that don't exist yet).</description></item>
///   <item><description><c>metrics</c> preserves the monitor's native units: <c>cpuPctCore</c>
///     (% of <em>one</em> core, can exceed 100 — NOT the host's 0–100), <c>memBytes</c>, nullable
///     <c>io*</c>. The whole block is <c>null</c> when no per-server sample is available.</description></item>
///   <item><description>Omitted as unsourceable: <c>players</c> (no player-query), <c>cpu</c> 0–100,
///     <c>ram.max</c> (no memory limit), <c>ip</c> (not resolved), <c>updatedAt</c> (no state-change
///     tracking until the M2 stream), and the curated <c>game</c> display name (we emit the real
///     <c>blueprint</c> id instead — blueprint metadata curation is deferred, never guessed).</description></item>
/// </list>
/// Keys are always present with explicit <c>null</c> values (honest unknown over omission), so the
/// SPA binds a stable shape.
/// </summary>
public sealed record Server(
    // Stable kgsm instance id and the join key (== monitor ServerMetrics.Id == the lib dict key).
    string Id,
    // Display name. Equal to Id today (kgsm has no separate alias); kept distinct for future labels.
    string Name,
    // Blueprint id this instance was installed from (the honest analog of the aspirational `game`).
    string Blueprint,
    // running | stopped | unknown — see ServerStatus. From Reading<InstanceRuntimeStatus>.
    string Status,
    // Installed version (InstanceRuntimeStatus.Version.Current). Null when the status is unknown
    // or kgsm reports no version. NOT an update check — `latest`/`updates_available` need the slow
    // per-instance network probe this read deliberately skips (fast mode).
    string? Version,
    // native | container — the supervision discriminator (Instance.Runtime), lower-cased.
    string Runtime,
    // The host this server runs on (architecture §4·a). Always this api's single host.
    string HostId,
    // Dedicated-server Steam App ID ("0" for non-Steam games). Static per-blueprint.
    string SteamAppId,
    // Client Steam App ID for launch/connect deeplinks ("0" for non-Steam games). Static per-blueprint.
    string ClientSteamAppId,
    // Whether a Steam account is required to download the server. Static per-blueprint.
    bool IsSteamAccountRequired,
    // Per-instance resource usage from the monitor, or null when the monitor is absent/unreachable
    // or has no sample for this instance (e.g. a stopped server has no cgroup/process tree). Null
    // here is the honest "not measurable now" — never a fabricated zero.
    ServerMetricsDto? Metrics,
    // The firewall/ports cross-reference (M6·b) — populated ONLY on the GET /servers/{id} detail view
    // (and the servers/{id}/network WS patch); omitted entirely on the list + the `servers` stream, so
    // those stay byte-identical to the frozen M1·b shape (detail ≠ list, the first such split). See
    // ServerNetwork for the honest-unknown + reserved-`reachable` semantics.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ServerNetwork? Network = null);

/// <summary>
/// One server's resource sample, mapped 1:1 from the monitor's <c>ServerMetrics</c> with its native
/// units preserved (see <see cref="Server"/>). Present only when the monitor produced a row for this
/// instance this tick.
/// </summary>
/// <param name="CpuPctCore">CPU as a percentage of one core (htop convention); a multi-core server
/// can exceed 100. Deliberately NOT the host's 0–100-across-all-cores figure.</param>
/// <param name="MemBytes">Charged memory in bytes (cgroup <c>memory.current</c> incl. page cache, or
/// summed process RSS for native) — honest, neither a plain <c>ps</c> RSS nor a capped fraction.</param>
/// <param name="IoReadBps">Block-IO read rate (bytes/sec), or <c>null</c> when the io controller is
/// not accounted for this kind (the monitor's own nullable — passed through, never coerced to 0).</param>
/// <param name="IoWriteBps">Block-IO write rate, or <c>null</c> (see <paramref name="IoReadBps"/>).</param>
/// <param name="Pids">Live process/thread count.</param>
public sealed record ServerMetricsDto(
    double CpuPctCore,
    long MemBytes,
    long? IoReadBps,
    long? IoWriteBps,
    int Pids);

/// <summary>
/// The honest run-state vocabulary (M1·b). Derived from kgsm-lib's
/// <c>Reading&lt;InstanceRuntimeStatus&gt;</c>: a measured reading maps its boolean
/// <c>Status</c> to <see cref="Running"/>/<see cref="Stopped"/>; any non-measured reading
/// (unavailable / unsupported / skipped, or a missing entry) is <see cref="Unknown"/> — the
/// status was not readable, distinct from a confident "stopped".
/// </summary>
public static class ServerStatus
{
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Unknown = "unknown";
}
