namespace TheKrystalShip.Api;

/// <summary>
/// Consolidated configuration for the api (introduced at M1, replacing the inline env reads
/// of M0). Values are read through <see cref="IConfiguration"/>, so each key is documented
/// in <c>appsettings.json</c> (the schema + defaults) and overridable by an environment
/// variable of the same name (systemd-friendly). Resolved once at startup via
/// <see cref="FromConfiguration"/> and registered as a singleton.
/// </summary>
/// <remarks>
/// A leaf's <c>*Provisioned</c> flag is derived from whether its endpoint is configured:
/// a non-empty path/URL means the capability is declared on this host, an empty one means
/// it is absent (the §4·b capability renders <c>absent</c>, not a broken <c>down</c>). The
/// defaults provision the engine-side pieces (the kgsm engine, monitor, watchdog) at their
/// standard install paths; the assistant is opt-in. (True host-registration provisioning
/// arrives with the host registry later; config is the honest stand-in.)
/// <para>
/// The kgsm engine is <strong>base, not a leaf</strong> — the api is meaningless without
/// the host's kgsm — so it is provisioned-by-default at its packaged path. Blanking
/// <see cref="KgsmPath"/> is a misconfiguration the api surfaces (an empty <c>/servers</c>
/// plus a loud log), never a normal "capability absent" — there is no §4·b engine capability.
/// </para>
/// </remarks>
public sealed class ApiOptions
{
    /// <summary>
    /// Stable identity of THIS host. Config-driven (default: machine name) and deliberately
    /// NOT derived from a leaf snapshot — identity must not flap when the monitor blips.
    /// Every server/alert this host reports carries it as <c>hostId</c> (architecture §4·a).
    /// </summary>
    public required string HostId { get; init; }

    /// <summary>Human-friendly host label (default: the host id).</summary>
    public required string HostLabel { get; init; }

    /// <summary>kgsm-monitor metrics socket. Empty ⇒ metrics capability not provisioned (absent).</summary>
    public required string MonitorSocketPath { get; init; }

    /// <summary>kgsm-watchdog control socket. Empty ⇒ watchdog capability not provisioned (absent).</summary>
    public required string WatchdogSocketPath { get; init; }

    /// <summary>
    /// Assistant base URL (the SSE relay lands at M7). Empty ⇒ assistant capability not
    /// provisioned (absent). In M1 it is only probed for liveness to report the capability.
    /// </summary>
    public required string AssistantBaseUrl { get; init; }

    /// <summary>
    /// Path to the host's <c>kgsm.sh</c> entrypoint — the single C#↔engine chokepoint kgsm-lib
    /// shells (instances, run-state). Default: the AUR-packaged symlink <c>/usr/bin/kgsm</c>.
    /// Empty ⇒ the engine is not configured (a misconfiguration: <c>/servers</c> is empty + logged).
    /// </summary>
    public required string KgsmPath { get; init; }

    /// <summary>
    /// Path to the kgsm event socket. A <em>registration formality</em> for M1·b — kgsm-lib's
    /// <c>IInstanceService</c> is process-based (it shells <see cref="KgsmPath"/>); only the
    /// event consumer (M5) opens this socket. Default: <c>/usr/share/kgsm/kgsm.sock</c>.
    /// </summary>
    public required string KgsmSocketPath { get; init; }

    public bool MetricsProvisioned => !string.IsNullOrWhiteSpace(MonitorSocketPath);
    public bool WatchdogProvisioned => !string.IsNullOrWhiteSpace(WatchdogSocketPath);
    public bool AssistantProvisioned => !string.IsNullOrWhiteSpace(AssistantBaseUrl);

    /// <summary>
    /// Whether the kgsm engine is configured (a non-empty <see cref="KgsmPath"/>). Unlike a leaf
    /// capability, the engine is assumed present — <c>false</c> is a surfaced misconfiguration.
    /// </summary>
    public bool KgsmProvisioned => !string.IsNullOrWhiteSpace(KgsmPath);

    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        string? hostId = Clean(configuration["KGSM_API_HOST_ID"]);
        hostId ??= Environment.MachineName;

        return new ApiOptions
        {
            HostId = hostId,
            HostLabel = Clean(configuration["KGSM_API_HOST_LABEL"]) ?? hostId,
            // For socket/url defaults we distinguish "unset" (use the default) from
            // "set to empty" (deliberately mark the capability absent): a present-but-empty
            // value stays empty, an absent key falls back to the standard path.
            MonitorSocketPath = Defaulted(configuration["KGSM_API_MONITOR_SOCKET"], "/run/kgsm-monitor.sock"),
            WatchdogSocketPath = Defaulted(configuration["KGSM_API_WATCHDOG_SOCKET"], "/run/kgsm-watchdog/control.sock"),
            AssistantBaseUrl = Defaulted(configuration["KGSM_API_ASSISTANT_URL"], ""),
            KgsmPath = Defaulted(configuration["KGSM_API_KGSM_PATH"], "/usr/bin/kgsm"),
            KgsmSocketPath = Defaulted(configuration["KGSM_API_KGSM_SOCKET"], "/usr/share/kgsm/kgsm.sock"),
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // null key (unset) -> fallback; present key (even empty) -> the given value, trimmed.
    private static string Defaulted(string? value, string fallback) => value is null ? fallback : value.Trim();
}
