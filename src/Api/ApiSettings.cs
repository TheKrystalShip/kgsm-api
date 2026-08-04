namespace TheKrystalShip.Api;

/// <summary>
/// The API's configuration surface, shaped 1:1 to the <c>Api</c> section of
/// <c>kgsm-api.settings.json</c>. Every knob is a property here and a key there; nothing is read by
/// string lookup, so a knob cannot exist in one place and not the other. An environment variable
/// overrides one key by spelling its path with <c>__</c> (<c>Api__DomainPollMs</c>).
/// </summary>
/// <remarks>
/// This type holds what was <em>written</em>, not what the API runs on: values arrive unvalidated,
/// exactly as the file or the environment spelled them. <see cref="ApiOptions"/> is the validated
/// form, and <see cref="ApiOptions.FromSettings"/> is the one place clamping, blank-handling and
/// CSV splitting happen — so the raw configuration and the runtime view stay separable, and the
/// reasoning for each knob lives with the property that carries it there rather than here.
/// <para>
/// Strings are nullable and <b>null is distinct from empty</b>: a key left out of configuration
/// entirely means "use the default", while a key present but blank means "deliberately off" — which
/// is how a leaf endpoint declares its capability absent rather than merely unset. Numbers and
/// flags are nullable for a different reason: binding a blank value to a non-nullable
/// <see cref="int"/> throws, so one stray <c>Api__DomainPollMs=</c> line in an env file would take
/// the API down, and binding a null one yields <c>0</c>/<c>false</c>, silently discarding the coded
/// default. A value that is present but is not a number still fails loudly, which is the point of
/// typing it at all.
/// </para>
/// </remarks>
public sealed class ApiSettings
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string Section = "Api";

    // ── Identity ──────────────────────────────────────────────────────
    /// <summary>Stable identity of this host. Blank takes the OS machine name.</summary>
    public string? HostId { get; set; }

    /// <summary>Human-friendly label. Blank falls back to the host id.</summary>
    public string? HostLabel { get; set; }

    /// <summary>Deployment region, an arbitrary free string. Blank stays unset rather than guessing one.</summary>
    public string? Region { get; set; }

    /// <summary>This node's cluster identity. Blank takes the host id.</summary>
    public string? NodeId { get; set; }

    /// <summary>Public base URL absolute cover/hero URLs are built from. Blank derives it from the request.</summary>
    public string? PublicBaseUrl { get; set; }


    // ── Leaf connections ──────────────────────────────────────────────────────
    /// <summary>kgsm-monitor metrics socket. Blank reports the metrics capability absent.</summary>
    public string? MonitorSocketPath { get; set; }

    /// <summary>kgsm-watchdog control socket. Blank reports the watchdog capability absent.</summary>
    public string? WatchdogSocketPath { get; set; }

    /// <summary>Assistant base URL. Blank reports the assistant absent.</summary>
    public string? AssistantBaseUrl { get; set; }

    /// <summary>Shared secret for the assistant turn relay (X-Relay-Secret). Blank sends none.</summary>
    public string? AssistantRelaySecret { get; set; }

    /// <summary>kgsm-firewall control socket. Blank reports the ports surface absent.</summary>
    public string? FirewallSocketPath { get; set; }

    /// <summary>kgsm-scheduler status socket. Blank reports the scheduler absent.</summary>
    public string? SchedulerSocketPath { get; set; }


    // ── KGSM engine ──────────────────────────────────────────────────────
    /// <summary>Path to the host's kgsm entrypoint. Blank is a misconfiguration, not a capability toggle.</summary>
    public string? KgsmPath { get; set; }

    /// <summary>The engine's append-only event journal the audit consumer tails.</summary>
    public string? KgsmJournalDir { get; set; }

    /// <summary>systemctl binary the Services board shells. Resolved via PATH by default.</summary>
    public string? SystemctlPath { get; set; }

    /// <summary>journalctl binary the host-logs surface shells. Resolved via PATH by default.</summary>
    public string? JournalctlPath { get; set; }


    // ── Polling ──────────────────────────────────────────────────────
    /// <summary>How often the servers topic re-reads the roster from kgsm. Floor 1000.</summary>
    public int? DomainPollMs { get; set; }

    /// <summary>How often the metrics topics scrape the monitor for the live tick. Floor 250.</summary>
    public int? MetricsPollMs { get; set; }

    /// <summary>How often the Services board re-reads leaf liveness. Floor 2000.</summary>
    public int? ServicesPollMs { get; set; }

    /// <summary>How often the slow fleet-wide update probe runs. Floor 60000.</summary>
    public int? UpdateCheckPollMs { get; set; }

    /// <summary>How often each instance's backups are re-scanned. Floor 30000.</summary>
    public int? BackupScanPollMs { get; set; }

    /// <summary>Kill-switch for the always-on update-check probe.</summary>
    public bool? UpdateCheckDisabled { get; set; }

    /// <summary>Instance in-memory cache TTL. Floor 10.</summary>
    public int? InstanceCacheTtlSeconds { get; set; }

    /// <summary>Blueprint in-memory cache TTL. Floor 10.</summary>
    public int? BlueprintCacheTtlSeconds { get; set; }

    /// <summary>Kill-switch for the whole metric-threshold alert pass.</summary>
    public bool? MetricsThresholdsDisabled { get; set; }


    // ── Authentication ──────────────────────────────────────────────────────
    /// <summary>Dev escape hatch: authenticate every request as a synthetic admin.</summary>
    public bool? AuthDisabled { get; set; }

    /// <summary>HMAC signing key for session JWTs. Blank generates an ephemeral per-process key.</summary>
    public string? SigningKey { get; set; }

    /// <summary>Discord OAuth application id.</summary>
    public string? DiscordClientId { get; set; }

    /// <summary>Discord OAuth application secret.</summary>
    public string? DiscordClientSecret { get; set; }

    /// <summary>Bot token used to read guild member roles, the only path to them.</summary>
    public string? DiscordBotToken { get; set; }

    /// <summary>The guild whose roles authorize this host.</summary>
    public string? DiscordGuildId { get; set; }

    /// <summary>This host's OAuth redirect URI; must match the app registry.</summary>
    public string? DiscordRedirectUri { get; set; }

    /// <summary>SPA origin the OAuth callback hands the session back to. Blank returns JSON.</summary>
    public string? AuthFrontendUrl { get; set; }

    /// <summary>Comma-separated Discord role ids granting admin.</summary>
    public string? RoleAdminIds { get; set; }

    /// <summary>Comma-separated Discord role ids granting operator.</summary>
    public string? RoleOperatorIds { get; set; }

    /// <summary>Comma-separated Discord role ids granting viewer.</summary>
    public string? RoleViewerIds { get; set; }


    // ── Sessions ──────────────────────────────────────────────────────
    /// <summary>Turns the session registry inert, leaving revocation unenforceable.</summary>
    public bool? SessionsDisabled { get; set; }

    /// <summary>Per-request session validator cache TTL, the accepted revocation lag. Floor 500.</summary>
    public int? SessionsCacheTtlMs { get; set; }

    /// <summary>How often expired session rows are deleted. Floor 60000.</summary>
    public int? SessionsGcMs { get; set; }

    /// <summary>The sliding session refresh window in days. Floor 1.</summary>
    public int? SessionsRefreshAbsoluteDays { get; set; }


    // ── Game library & cover art ──────────────────────────────────────────────────────
    /// <summary>RAWG.io API key. Blank no-ops the hydration worker.</summary>
    public string? RawgApiKey { get; set; }

    /// <summary>Directory self-hosted cover/hero images are written to. Blank uses covers/ beside the DB.</summary>
    public string? RawgCacheDir { get; set; }

    /// <summary>Base URL for the keyless Steam library-capsule cover source.</summary>
    public string? SteamCdnBaseUrl { get; set; }

    /// <summary>Off switch for the keyless Steam cover source, leaving RAWG only.</summary>
    public bool? SteamCoversDisabled { get; set; }

    /// <summary>Re-fetch cover/metadata older than this many days. 0 disables the periodic wake.</summary>
    public int? LibraryRefreshIntervalDays { get; set; }

    /// <summary>Local hour the periodic library refresh wakes. Clamped to 0..23.</summary>
    public int? LibraryRefreshHour { get; set; }


    // ── File & blueprint editing ──────────────────────────────────────────────────────
    /// <summary>Max directory entries one file listing returns before truncating. Floor 1.</summary>
    public int? FilesMaxEntries { get; set; }

    /// <summary>Max file size the editor will open or save. Floor 1024.</summary>
    public long? FilesMaxEditBytes { get; set; }

    /// <summary>Max blueprint-file size the library editor will open or save. Floor 1024.</summary>
    public long? BlueprintMaxEditBytes { get; set; }


    // ── Log reading ──────────────────────────────────────────────────────
    /// <summary>Host-log source map as source:unit pairs. Blank derives it from the leaf catalog.</summary>
    public string? LogSources { get; set; }

    /// <summary>How long a journal read may take before giving up. Floor 500.</summary>
    public int? LogReadTimeoutMs { get; set; }


    // ── Leaf configuration ──────────────────────────────────────────────────────
    /// <summary>Directory the per-leaf override env files render to.</summary>
    public string? LeafOverridesDir { get; set; }

    /// <summary>Shared directory the leaf config descriptors are discovered in.</summary>
    public string? LeafDescriptorDir { get; set; }

    /// <summary>Directory the per-leaf systemd drop-ins live in.</summary>
    public string? LeafDropInDir { get; set; }

    /// <summary>How long a leaf's health is watched after a config restart before the change is declared good. Floor 2000.</summary>
    public int? LeafApplyCanaryMs { get; set; }


    // ── Cluster ──────────────────────────────────────────────────────
    /// <summary>The shared cluster HMAC secret. Blank means this host is not part of a cluster.</summary>
    public string? ClusterSecret { get; set; }

    /// <summary>The previous cluster secret, accepted during a rotation overlap.</summary>
    public string? ClusterSecretPrevious { get; set; }

    /// <summary>The client URL this node advertises to peers. Blank advertises none.</summary>
    public string? ClusterAdvertiseUrl { get; set; }

    /// <summary>The gossip URL this node advertises to peers. Blank advertises none.</summary>
    public string? ClusterGossipUrl { get; set; }

    /// <summary>How often membership gossip is pushed. Floor 250.</summary>
    public int? ClusterGossipMs { get; set; }

    /// <summary>How often peers are polled for membership. Floor 250.</summary>
    public int? ClusterPollMs { get; set; }

    /// <summary>Silence after which a peer is marked suspect. Floor 1000.</summary>
    public int? ClusterSuspectMs { get; set; }

    /// <summary>Silence after which a suspect peer is reaped. Floor 1000.</summary>
    public int? ClusterReapMs { get; set; }

    /// <summary>How often the cluster outbox drainer ticks. Floor 250.</summary>
    public int? ClusterDrainMs { get; set; }

    /// <summary>How often the cluster retention GC sweeps. Floor 60000.</summary>
    public int? ClusterGcMs { get; set; }

    /// <summary>Days a pending outbox row may keep retrying before it is dead-lettered. Floor 1.</summary>
    public int? ClusterRetryTtlDays { get; set; }

    /// <summary>Days a delivered or dead outbox row is kept. Clamped above the retry TTL so a late redelivery is still deduped.</summary>
    public int? ClusterRetentionDays { get; set; }


    // ── Storage ──────────────────────────────────────────────────────
    /// <summary>SQLite file for the API's own operational metadata.</summary>
    public string? DbPath { get; set; }


    // ── General ──────────────────────────────────────────────────────
    /// <summary>HTTP bind address(es), semicolon-separated. Read before the host exists, so it is bound separately in Program.</summary>
    public string? Urls { get; set; }

    /// <summary>Comma-separated CORS origin allowlist. Blank allows any origin, which is a dev-only posture.</summary>
    public string? CorsOrigins { get; set; }
}
