namespace TheKrystalShip.Api;

/// <summary>
/// Consolidated environment configuration for the api (introduced at M1, replacing the
/// inline env reads of M0). All values come from environment variables (systemd-friendly),
/// resolved once at startup via <see cref="FromConfiguration"/> and registered as a singleton.
/// </summary>
/// <remarks>
/// A leaf's <c>*Provisioned</c> flag is derived from whether its endpoint is configured:
/// a non-empty path/URL means the capability is declared on this host, an empty one means
/// it is absent (the §4·b capability renders <c>absent</c>, not a broken <c>down</c>). The
/// defaults provision the engine-side leaves (monitor, watchdog) at their standard sockets;
/// the assistant is opt-in. (True host-registration provisioning arrives with the host
/// registry later; config is the honest stand-in.)
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

    public bool MetricsProvisioned => !string.IsNullOrWhiteSpace(MonitorSocketPath);
    public bool WatchdogProvisioned => !string.IsNullOrWhiteSpace(WatchdogSocketPath);
    public bool AssistantProvisioned => !string.IsNullOrWhiteSpace(AssistantBaseUrl);

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
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // null key (unset) -> fallback; present key (even empty) -> the given value, trimmed.
    private static string Defaulted(string? value, string fallback) => value is null ? fallback : value.Trim();
}
