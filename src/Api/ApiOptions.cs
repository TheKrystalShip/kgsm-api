using TheKrystalShip.Api.Services.Alerts;

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

    /// <summary>Human-friendly host label (default: the host id). The deploy-time default; an admin
    /// <c>PATCH /hosts/{id}</c> override (stored in <c>host_settings</c>) wins at runtime.</summary>
    public required string HostLabel { get; init; }

    /// <summary>
    /// Deployment region (<c>KGSM_API_REGION</c>) — an <strong>arbitrary free string</strong> (e.g.
    /// <c>eu-west</c>, <c>us-east</c>, <c>homelab</c>), NOT a restricted enum. The deploy-time default for the
    /// host identity card; an admin <c>PATCH /hosts/{id}</c> override (stored in <c>host_settings</c>) wins at
    /// runtime. <see langword="null"/> when unset — surfaced as honest unknown, never a fabricated region.
    /// </summary>
    public string? Region { get; init; }

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
    /// Shared secret for the M7 assistant turn relay (<c>KGSM_API_ASSISTANT_RELAY_SECRET</c>) — the
    /// API presents it as <c>X-Relay-Secret</c> so the co-located assistant trusts the forwarded
    /// end-user identity (it must match the assistant's <c>Assistant:Relay:Secret</c>). Empty ⇒ no
    /// secret is sent; if the assistant requires one the relay is refused (its 401 → our 502).
    /// <strong>Shared external config</strong>, like the Discord app — not a process dependency.
    /// </summary>
    public required string AssistantRelaySecret { get; init; }

    /// <summary>
    /// kgsm-firewall control socket (M6·b). Empty ⇒ the firewall/ports surface is not provisioned
    /// (the per-server <c>network</c> block reports <c>firewall:"absent"</c>, the host
    /// <c>network</c> is null). <strong>Opt-in like the assistant</strong>: the host-firewall
    /// authority is a separate, optional install (kgsm-firewall) — set this to
    /// <c>/run/kgsm-firewall/firewall.sock</c> to enable the ports surface. Deliberately NOT
    /// default-provisioned: a host with no firewall authority should report <c>absent</c>, not a
    /// perpetually-<c>down</c> capability. NOT polled by the <see cref="Services.Leaves.LeafHealthMonitor"/>
    /// — kgsm-firewall is socket-activated + idle-exits, so a 2s poll would defeat that; liveness is
    /// reported per-probe as the block-level <c>firewall</c> status instead.
    /// </summary>
    public required string FirewallSocketPath { get; init; }

    /// <summary>
    /// kgsm-scheduler status socket (Settings Phase 3). The scheduler exposes an NDJSON-over-unix-socket
    /// status snapshot at this path (default standard install: <c>/run/kgsm-scheduler/status.sock</c>).
    /// Empty ⇒ the scheduler leaf is not provisioned (absent): <c>GET /servers/{id}/settings</c> still
    /// returns 200 with <c>nextFireUtc:null</c>, and the scheduler capability renders absent — never a
    /// perpetually-<c>down</c> row. <strong>Opt-in like the assistant/firewall</strong>: the scheduler is a
    /// separate optional leaf (built in parallel, not yet deployed), so a host without it reports absent.
    /// </summary>
    public required string SchedulerSocketPath { get; init; }

    /// <summary>
    /// Path to the host's <c>kgsm.sh</c> entrypoint — the single C#↔engine chokepoint kgsm-lib
    /// shells (instances, run-state). Default: the AUR-packaged symlink <c>/usr/bin/kgsm</c>.
    /// Empty ⇒ the engine is not configured (a misconfiguration: <c>/servers</c> is empty + logged).
    /// </summary>
    public required string KgsmPath { get; init; }

    /// <summary>
    /// Path to the kgsm event socket. A <em>registration formality</em> for M1·b — kgsm-lib's
    /// <c>IInstanceService</c> is process-based (it shells <see cref="KgsmPath"/>); only the
    /// event consumer (M5) opens this socket. Default: <c>/run/kgsm-api/kgsm-events.sock</c>
    /// (the API's own systemd <c>RuntimeDirectory=kgsm-api</c> — a DEDICATED path the listener
    /// owns, matching the deployed unit).
    /// </summary>
    public required string KgsmSocketPath { get; init; }

    // --- Library RAWG.io cover-art / metadata (the M8·a library increment) ------------------------

    /// <summary>
    /// RAWG.io API key (<c>KGSM_API_RAWG_API_KEY</c>). <strong>Opt-in: blank by default</strong> → the
    /// hydration worker no-ops and the library's cover/hero stay null (the SPA's gradient fallback),
    /// genres/tags <c>[]</c>. Set it to enable cover-art/metadata hydration. <strong>A secret</strong>:
    /// the RAWG client's <c>HttpClient</c> uses <c>RemoveAllLoggers()</c> so the <c>?key=…</c> never logs.
    /// </summary>
    public required string RawgApiKey { get; init; }

    /// <summary>
    /// Directory the self-hosted cover/hero <c>.jpg</c>s are written to and served from
    /// (<c>KGSM_API_RAWG_CACHE_DIR</c>). Default: a <c>covers/</c> dir beside the SQLite DB
    /// (<c>/var/lib/kgsm-api/covers</c> on a deployed host). The worker creates it on first write.
    /// </summary>
    public required string RawgCacheDir { get; init; }

    /// <summary>
    /// Optional public base URL (<c>KGSM_API_PUBLIC_BASE_URL</c>, e.g. <c>https://panel.example.com</c>) the
    /// absolute cover/hero URLs are built from for a reverse-proxy deployment. Blank (the default) ⇒ the URLs
    /// are derived from the incoming request (<c>{scheme}://{host}</c>), which resolves per-host for the
    /// multi-host SPA registry. Any trailing slash is trimmed.
    /// </summary>
    public required string PublicBaseUrl { get; init; }

    /// <summary>
    /// Base URL the Steam library-capsule cover (<c>{base}/{appId}/library_600x900.jpg</c> — the 2:3 portrait
    /// art Steam shows in the library view) is fetched from (<c>KGSM_API_STEAM_CDN_BASE</c>). Default: Steam's
    /// public store-asset CDN. Any trailing slash is trimmed. <strong>Steam is the cover authority</strong> —
    /// keyed by the blueprint's <c>client_steam_app_id</c>, fully <b>decoupled from RAWG</b> (no key needed);
    /// RAWG's <c>background_image</c> is only the fallback when a game isn't on Steam / has no capsule.
    /// </summary>
    public required string SteamCdnBaseUrl { get; init; }

    /// <summary>Kill-switch for the keyless Steam cover source (<c>KGSM_API_STEAM_COVERS_DISABLED</c>). Off by
    /// default (Steam covers ON — they need no key); set to disable so the cover falls back to RAWG only (and,
    /// with no RAWG key either, the worker no-ops — the offline/test posture the smoke pins).</summary>
    public bool SteamCoversDisabled { get; init; }

    /// <summary>
    /// How stale (in days) a cached library row may get before the periodic worker re-fetches it from
    /// Steam/RAWG (<c>KGSM_API_LIBRARY_REFRESH_INTERVAL_DAYS</c>, default 7 = weekly). Cover/metadata for a
    /// fixed game catalog is near-static, so this is the per-game refresh cadence; <c>0</c> (or negative)
    /// disables the periodic wake entirely (boot sweep + the admin <c>POST /library/refresh</c> only). The
    /// boot sweep also honours it (a frequent restart doesn't re-hammer fresh rows).
    /// </summary>
    public required int LibraryRefreshIntervalDays { get; init; }

    /// <summary>The <b>local</b> hour-of-day (0–23) the periodic refresh wakes to check
    /// (<c>KGSM_API_LIBRARY_REFRESH_HOUR</c>, default 6 = 06:00 local — a quiet window). The worker wakes at
    /// this hour each day and re-fetches any row older than <see cref="LibraryRefreshIntervalDays"/>; the wake
    /// itself is cheap (a DB read) when nothing is stale.</summary>
    public required int LibraryRefreshHour { get; init; }

    /// <summary>
    /// How long (seconds) the in-memory blueprint cache serves before a background refresh
    /// (<c>KGSM_API_BLUEPRINT_CACHE_TTL_SECONDS</c>, default 60). Blueprints change infrequently
    /// (install/uninstall), so a short staleness window is acceptable. Floor: 10s.
    /// </summary>
    public required int BlueprintCacheTtlSeconds { get; init; }

    /// <summary>
    /// How long (seconds) the in-memory instance cache serves before a background refresh
    /// (<c>KGSM_API_INSTANCE_CACHE_TTL_SECONDS</c>, default 60). Instances change infrequently
    /// (start/stop/install/uninstall), and kgsm events update runtime state between refreshes.
    /// Floor: 10s.
    /// </summary>
    public required int InstanceCacheTtlSeconds { get; init; }

    /// <summary>Whether RAWG hydration is enabled (a non-blank <see cref="RawgApiKey"/>). When false the
    /// worker skips RAWG (hero/description/genres/tags + the cover fallback); Steam covers are unaffected.</summary>
    public bool RawgProvisioned => !string.IsNullOrWhiteSpace(RawgApiKey);

    /// <summary>Whether the Steam cover source is active (not disabled and a non-blank CDN base). Independent of
    /// <see cref="RawgProvisioned"/> — Steam covers hydrate even with no RAWG key (Steam is the cover authority).</summary>
    public bool SteamCoversProvisioned => !SteamCoversDisabled && !string.IsNullOrWhiteSpace(SteamCdnBaseUrl);

    public bool MetricsProvisioned => !string.IsNullOrWhiteSpace(MonitorSocketPath);
    public bool WatchdogProvisioned => !string.IsNullOrWhiteSpace(WatchdogSocketPath);
    public bool AssistantProvisioned => !string.IsNullOrWhiteSpace(AssistantBaseUrl);

    /// <summary>Whether the kgsm-firewall authority is configured (a non-empty
    /// <see cref="FirewallSocketPath"/>). When false the ports surface degrades to
    /// <c>firewall:"absent"</c> (server) / null (host) — never an error.</summary>
    public bool FirewallProvisioned => !string.IsNullOrWhiteSpace(FirewallSocketPath);

    /// <summary>Whether the kgsm-scheduler status socket is configured (a non-empty
    /// <see cref="SchedulerSocketPath"/>). When false the scheduler leaf is absent — its capability renders
    /// absent and <c>nextFireUtc</c> is null on the settings surface, never an error.</summary>
    public bool SchedulerProvisioned => !string.IsNullOrWhiteSpace(SchedulerSocketPath);

    /// <summary>
    /// Whether the kgsm engine is configured (a non-empty <see cref="KgsmPath"/>). Unlike a leaf
    /// capability, the engine is assumed present — <c>false</c> is a surfaced misconfiguration.
    /// </summary>
    public bool KgsmProvisioned => !string.IsNullOrWhiteSpace(KgsmPath);

    // --- Realtime pump cadences (M2) — the background poll intervals the WS pumps tick at ----------

    /// <summary>
    /// How often the <see cref="Realtime.DomainPump"/> re-fetches the instance roster + run-state from
    /// kgsm (<c>KGSM_API_DOMAIN_POLL_MS</c>, default 5000 = 5s, floor 1000). This is the poll the
    /// <c>servers</c> WS topic rides — each tick spawns <c>kgsm.sh</c> (a process), so it is deliberately
    /// relaxed: instances change rarely, the SPA has a manual refresh, and every operator-initiated
    /// start/stop/install already pushes an immediate verify <c>server.patch</c> off the command path, so
    /// this poll only catches out-of-band changes (a crash, an external edit). Gated on subscribers — an
    /// idle stream never spawns kgsm. <strong>Blueprints have no separate poll</strong>: the library
    /// catalog (<c>GET /library</c>) is read live per request, not on a timer.
    /// </summary>
    public required int DomainPollMs { get; init; }

    /// <summary>
    /// How often the <see cref="Realtime.MetricsPump"/> scrapes the monitor socket and fans the live
    /// resource tick out to the <c>*/metrics</c> topics (<c>KGSM_API_METRICS_POLL_MS</c>, default 1000 =
    /// 1s, floor 250). This is the live performance feed (≈ the monitor's own self-tick), <b>not</b> the
    /// instance/blueprint poll — relaxing it makes the SPA's performance charts choppy, so it stays at 1s
    /// by default. Gated on subscribers. Distinct from <see cref="MetricsPersistMs"/> (the durable-history
    /// sampler cadence).
    /// </summary>
    public required int MetricsPollMs { get; init; }

    // --- Metrics history (M9) — durable tiered metrics store (metrics.db) -------------------------

    /// <summary>Master switch for the metrics history store (<c>KGSM_API_METRICS_HISTORY_ENABLED</c>,
    /// default true). When false the sampler is inert and the read endpoint returns empty series.</summary>
    public bool MetricsHistoryEnabled { get; init; } = true;

    /// <summary>Path to the dedicated metrics SQLite file (<c>KGSM_API_METRICS_HISTORY_DB</c>, default
    /// <c>metrics.db</c> beside the audit DB). Separate from the audit DB (D4).</summary>
    public required string MetricsHistoryDb { get; init; }

    /// <summary>Tier-1 persist cadence in ms (<c>KGSM_API_METRICS_PERSIST_MS</c>, default 15000, floor
    /// 5000). Decoupled from the 1 Hz live stream.</summary>
    public required int MetricsPersistMs { get; init; }

    /// <summary>Tier-1 raw retention in hours (<c>KGSM_API_METRICS_RAW_RETENTION_HOURS</c>, default 24).</summary>
    public required int MetricsRawRetentionHours { get; init; }

    /// <summary>Tier-2 rollup bucket width in minutes (<c>KGSM_API_METRICS_ROLLUP_STEP_MIN</c>, default 5).</summary>
    public required int MetricsRollupStepMin { get; init; }

    /// <summary>Tier-2 rollup retention in days (<c>KGSM_API_METRICS_ROLLUP_RETENTION_DAYS</c>, default 30).</summary>
    public required int MetricsRollupRetentionDays { get; init; }

    /// <summary>How often the maintenance job rolls up + prunes, in ms
    /// (<c>KGSM_API_METRICS_MAINT_MS</c>, default 60000).</summary>
    public required int MetricsMaintenanceMs { get; init; }

    // --- Metric-threshold alerts (the alerts `metrics`/`host-monitor` source, increment 1 of 2 — see
    //     metrics-threshold-alerts-plan.md). Policy storage is appsettings/env ONLY this increment: a
    //     baked-in Default, optionally wholesale-overridden by the MetricsThresholds:Rules config section.
    //     DB-backed/panel-editable policy is increment 2, out of scope. ----------------------------------

    /// <summary>
    /// The active metric-threshold policy. Defaults to <see cref="MetricsThresholdPolicy.Default"/>; a
    /// present, non-empty <c>MetricsThresholds:Rules</c> config section overrides it WHOLESALE (not
    /// merged field-by-field). Tuning a threshold or enabling a per-server rule means editing
    /// <c>appsettings.json</c>/the env file and restarting the API — the expected increment-1 UX.
    /// </summary>
    public MetricsThresholdPolicy Policy { get; init; } = MetricsThresholdPolicy.Default;

    /// <summary>Kill-switch for the whole metric-threshold alert pass
    /// (<c>KGSM_API_METRICS_THRESHOLDS_DISABLED</c>), independent of each rule's own <c>enabled</c> flag —
    /// flips the entire source off without touching the rule config.</summary>
    public bool MetricsThresholdsDisabled { get; init; }

    /// <summary>
    /// Whether the metric-threshold alert source is active: the monitor capability is provisioned (metrics
    /// are actually reachable to threshold), the kill-switch is off, and at least one rule is enabled. The
    /// <c>AlertEngine</c> guards its metrics reconcile pass on this, mirroring
    /// <see cref="WatchdogProvisioned"/> for the existing crash pass.
    /// </summary>
    public bool MetricsThresholdProvisioned =>
        MetricsProvisioned && !MetricsThresholdsDisabled && Policy.AnyEnabled;

    // --- File browser (Tier 3 #12) — the GET/PUT /servers/{id}/files surface ----------------------

    /// <summary>
    /// Max directory entries a single <c>GET /servers/{id}/files</c> returns before truncating with a
    /// <c>truncated:true</c> signal (<c>KGSM_API_FILES_MAX_ENTRIES</c>, default 200). The constraint is
    /// FRONTEND rendering (one DOM node per entry, not virtualized) — a save subdir with thousands of map
    /// chunks janks the tree, not the API. Truncation is always signaled, never a silent refusal (plan §5).
    /// </summary>
    public required int FilesMaxEntries { get; init; }

    /// <summary>
    /// Max file size (bytes) the editor will open or save (<c>KGSM_API_FILES_MAX_EDIT_BYTES</c>, default
    /// ~2 MiB). A read past this returns <c>file_too_large</c> (the SPA shows "can't open" honestly rather
    /// than rendering megabytes of text); a save past it is refused. Hashing ≤ this for the etag is trivial.
    /// </summary>
    public required long FilesMaxEditBytes { get; init; }

    // --- Host logs (the GET /hosts/{id}/logs journald aggregation surface) ------------------------

    /// <summary>
    /// The ordered source-id → systemd-unit map the host-log aggregator reads
    /// (<c>KGSM_API_LOG_SOURCES</c>, e.g. <c>watchdog:kgsm-watchdog.service,monitor:kgsm-monitor.service</c>).
    /// Blank/unset ⇒ the default leaf set (assistant, monitor, watchdog, firewall, api, bot). Order is the
    /// order the frontend presents the sources. Reading the journal needs the api's user to have journal read
    /// access (the <c>systemd-journal</c>/<c>wheel</c> ACL); a host whose units are named differently overrides
    /// this map. NOT a §4·b capability — journald is always present locally; a read failure degrades to empty.
    /// </summary>
    public required IReadOnlyList<LogSourceMap> LogSources { get; init; }

    /// <summary>The <c>journalctl</c> binary the host-log reader shells (<c>KGSM_API_JOURNALCTL_PATH</c>,
    /// default <c>journalctl</c> — resolved via PATH). The reader degrades to an empty page if it's missing.</summary>
    public required string JournalctlPath { get; init; }

    /// <summary>The <c>systemctl</c> binary the Services board (<c>GET /hosts/{id}/services</c>) shells to read
    /// each leaf unit's live state (<c>KGSM_API_SYSTEMCTL_PATH</c>, default <c>systemctl</c> — resolved via
    /// PATH). Reading unit state is unprivileged; the reader degrades each unit to <c>unknown</c> if it's
    /// missing. Same host-OS-introspection category as <see cref="JournalctlPath"/> — NOT a §4·b capability.</summary>
    public required string SystemctlPath { get; init; }

    /// <summary>Hard wall-clock budget (ms) for a single host-log read (<c>KGSM_API_LOG_READ_TIMEOUT_MS</c>,
    /// default 5000, floor 500). On timeout the reader returns the lines it gathered (honest partial), never a
    /// fabricated tail.</summary>
    public required int LogReadTimeoutMs { get; init; }

    // --- Leaf runtime config (the leaf-runtime-config feature, Phase 2) ----------------------------

    /// <summary>
    /// Directory the per-leaf override files (<c>&lt;leaf&gt;.env</c>) are rendered to
    /// (<c>KGSM_API_LEAF_OVERRIDES_DIR</c>, default <c>/var/lib/kgsm-api/leaf-overrides</c> — the API's own
    /// state dir, so the API writes it <strong>unprivileged</strong>; a systemd drop-in the leaf is unaware of
    /// feeds it via <c>EnvironmentFile=-</c>). The renderer mkdirs it <c>0700</c> and writes each file
    /// <c>0600</c> (the overrides can hold secrets). The override file is a deterministic render of the
    /// <c>leaf_override</c> DB rows — never hand-edited.
    /// </summary>
    public required string LeafOverridesDir { get; init; }

    /// <summary>
    /// How long (ms) the apply broker watches a leaf's health after a config restart before declaring the
    /// change good (<c>KGSM_API_LEAF_APPLY_CANARY_MS</c>, default 15000, floor 2000). If the leaf is not
    /// healthy within this window the override is restored and the leaf restarted again (auto-rollback) — so a
    /// bad value is a caught, reverted failure, not a downed leaf.
    /// </summary>
    public required int LeafApplyCanaryMs { get; init; }

    // --- Auth (M4·a) — Discord per-host, Model A (architecture.html §3·f, keystone O5) -----------
    // Identity is a global Discord SSO anchor; authorization is a short-lived host-scoped bearer
    // this host mints after verifying identity once and resolving the role via the host's bot.
    // The Discord app/guild/bot-token/role-map are SHARED EXTERNAL CONFIG (same values the Discord
    // bot uses) — keystone §4: this is configuration, NOT a process dependency on kgsm-bot.

    /// <summary>
    /// Dev escape hatch (<c>KGSM_API_AUTH_DISABLED=1</c>). When set, every request is authenticated
    /// as a synthetic <c>admin</c> and all tier policies pass — the pre-M4 unauthenticated trust
    /// window, now explicit and loudly logged. Off by default: <strong>auth is on by default</strong>.
    /// </summary>
    public bool AuthDisabled { get; init; }

    /// <summary>HMAC signing key for the host-scoped session JWTs (<c>KGSM_API_AUTH_SIGNING_KEY</c>).
    /// Empty + auth enabled ⇒ an ephemeral per-process key is generated (tokens die on restart;
    /// logged loudly). Set a stable secret on a real host.</summary>
    public required string SigningKey { get; init; }

    /// <summary>Discord OAuth application client id (<c>KGSM_API_AUTH_DISCORD_CLIENT_ID</c>).</summary>
    public required string DiscordClientId { get; init; }
    /// <summary>Discord OAuth application client secret (<c>KGSM_API_AUTH_DISCORD_CLIENT_SECRET</c>).</summary>
    public required string DiscordClientSecret { get; init; }
    /// <summary>The host's OAuth redirect URI — this host's <c>/auth/discord/callback</c>
    /// (<c>KGSM_API_AUTH_DISCORD_REDIRECT_URI</c>).</summary>
    public required string DiscordRedirectUri { get; init; }
    /// <summary>Bot token used to read guild member roles via the Discord REST API
    /// (<c>KGSM_API_AUTH_DISCORD_BOT_TOKEN</c>) — the only path to roles, since the
    /// <c>identify guilds</c> user scopes don't carry them. Same token the host's bot uses.</summary>
    public required string DiscordBotToken { get; init; }
    /// <summary>The Discord guild whose roles authorize this host (<c>KGSM_API_AUTH_DISCORD_GUILD_ID</c>).</summary>
    public required string DiscordGuildId { get; init; }

    /// <summary>
    /// The SPA origin/URL the OAuth callback hands the session back to
    /// (<c>KGSM_API_AUTH_FRONTEND_URL</c>). When set, <c>/auth/discord/callback</c> 302s the browser
    /// here with the result in the URL <b>fragment</b> (<c>#access=…&amp;refresh=…</c> on success,
    /// <c>#error=…</c> otherwise) instead of returning JSON — the SPA token handoff. The redirect target
    /// is THIS single configured value, never a request-supplied one (no open-redirect). Blank → the
    /// callback keeps returning JSON (API-only deployments, and the test default).
    /// </summary>
    public required string AuthFrontendUrl { get; init; }

    /// <summary>Discord role ids granting the <c>admin</c> tier (comma-separated;
    /// <c>KGSM_API_AUTH_ROLE_ADMIN</c>).</summary>
    public required IReadOnlyList<string> RoleAdminIds { get; init; }
    /// <summary>Discord role ids granting the <c>operator</c> tier (<c>KGSM_API_AUTH_ROLE_OPERATOR</c>);
    /// the natural mapping for the bot's existing Ops <c>ActionRoleId</c>.</summary>
    public required IReadOnlyList<string> RoleOperatorIds { get; init; }
    /// <summary>Discord role ids granting the <c>viewer</c> tier (<c>KGSM_API_AUTH_ROLE_VIEWER</c>).</summary>
    public required IReadOnlyList<string> RoleViewerIds { get; init; }

    /// <summary>Auth is on unless the dev escape hatch is set.</summary>
    public bool AuthEnabled => !AuthDisabled;

    /// <summary>
    /// Whether the Discord OAuth login flow can run — all of client id/secret, redirect URI, bot
    /// token and guild id are configured. Auth (JWT validation, tier gates) is enforced regardless;
    /// this only gates the <em>login</em> endpoints (the M4·b live half), which 503 when unconfigured.
    /// </summary>
    public bool DiscordConfigured =>
        !string.IsNullOrWhiteSpace(DiscordClientId)
        && !string.IsNullOrWhiteSpace(DiscordClientSecret)
        && !string.IsNullOrWhiteSpace(DiscordRedirectUri)
        && !string.IsNullOrWhiteSpace(DiscordBotToken)
        && !string.IsNullOrWhiteSpace(DiscordGuildId);

    /// <summary>Whether the OAuth callback redirects the session back to the SPA (fragment handoff)
    /// rather than returning JSON. True iff a frontend URL is configured.</summary>
    public bool FrontendRedirectEnabled => !string.IsNullOrWhiteSpace(AuthFrontendUrl);

    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        string? hostId = Clean(configuration["KGSM_API_HOST_ID"]);
        hostId ??= Environment.MachineName;

        return new ApiOptions
        {
            HostId = hostId,
            HostLabel = Clean(configuration["KGSM_API_HOST_LABEL"]) ?? hostId,
            // Region: an arbitrary free string. Null when unset (honest unknown) — Clean() collapses blank to null.
            Region = Clean(configuration["KGSM_API_REGION"]),
            // For socket/url defaults we distinguish "unset" (use the default) from
            // "set to empty" (deliberately mark the capability absent): a present-but-empty
            // value stays empty, an absent key falls back to the standard path.
            MonitorSocketPath = Defaulted(configuration["KGSM_API_MONITOR_SOCKET"], "/run/kgsm-monitor/metrics.sock"),
            WatchdogSocketPath = Defaulted(configuration["KGSM_API_WATCHDOG_SOCKET"], "/run/kgsm-watchdog/control.sock"),
            AssistantBaseUrl = Defaulted(configuration["KGSM_API_ASSISTANT_URL"], ""),
            AssistantRelaySecret = Defaulted(configuration["KGSM_API_ASSISTANT_RELAY_SECRET"], ""),
            // Opt-in (blank = absent): the firewall authority is a separate optional install.
            FirewallSocketPath = Defaulted(configuration["KGSM_API_FIREWALL_SOCKET"], ""),
            // Opt-in (blank = absent): the scheduler is a separate optional leaf (Settings Phase 3). Set it
            // to /run/kgsm-scheduler/status.sock on a host that runs kgsm-scheduler.
            SchedulerSocketPath = Defaulted(configuration["KGSM_API_SCHEDULER_SOCKET"], ""),
            KgsmPath = Defaulted(configuration["KGSM_API_KGSM_PATH"], "/usr/bin/kgsm"),
            KgsmSocketPath = Defaulted(configuration["KGSM_API_KGSM_SOCKET"], "/run/kgsm-api/kgsm-events.sock"),

            // Realtime pump cadences (M2). The domain (instance) poll is relaxed by default (5s) — it
            // spawns kgsm.sh and the roster changes rarely (the SPA also has a manual refresh); floored at
            // 1s. The metrics tick stays at the monitor's ~1s self-tick (the live charts feed); floored at
            // 250ms. Blueprints have no poll — GET /library reads them live per request.
            DomainPollMs = Math.Max(1000, IntOr(configuration["KGSM_API_DOMAIN_POLL_MS"], 5000)),
            MetricsPollMs = Math.Max(250, IntOr(configuration["KGSM_API_METRICS_POLL_MS"], 1000)),

            // Library RAWG cover/metadata. Opt-in (blank key => worker no-ops). The cache dir always resolves
            // to a concrete path: an explicit KGSM_API_RAWG_CACHE_DIR wins, else (unset OR blank — the
            // appsettings.json default is "") a covers/ subdir beside the SQLite DB, so it lands in the
            // StateDirectory the deployed unit sets. (BlankFallback, not Defaulted — a blank must NOT stay
            // blank here: Path.* would throw on an empty cache dir.)
            RawgApiKey = Defaulted(configuration["KGSM_API_RAWG_API_KEY"], ""),
            RawgCacheDir = BlankFallback(
                configuration["KGSM_API_RAWG_CACHE_DIR"],
                DefaultCacheDir(configuration["KGSM_API_DB"])),
            PublicBaseUrl = Defaulted(configuration["KGSM_API_PUBLIC_BASE_URL"], "").TrimEnd('/'),

            // Steam library-capsule cover (the 2:3 portrait). The cover AUTHORITY, decoupled from RAWG: keyless,
            // so it defaults ON (BlankFallback keeps a concrete CDN base even if the appsettings default is "").
            // KGSM_API_STEAM_COVERS_DISABLED forces RAWG-only (the offline smoke sets it so cover stays null).
            SteamCdnBaseUrl = BlankFallback(
                configuration["KGSM_API_STEAM_CDN_BASE"],
                "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps").TrimEnd('/'),
            SteamCoversDisabled = Flag(configuration["KGSM_API_STEAM_COVERS_DISABLED"]),
            // Periodic refresh of the cover/metadata cache (in-process, off the request path). Weekly by
            // default; runs at a configurable local hour (a quiet window). Clamped: interval >= 0 (0 disables
            // the periodic wake), hour into 0..23.
            LibraryRefreshIntervalDays = Math.Max(0, IntOr(configuration["KGSM_API_LIBRARY_REFRESH_INTERVAL_DAYS"], 7)),
            LibraryRefreshHour = Math.Clamp(IntOr(configuration["KGSM_API_LIBRARY_REFRESH_HOUR"], 6), 0, 23),
            // Blueprint in-memory cache TTL (background refresh interval). Floor 10s.
            BlueprintCacheTtlSeconds = Math.Max(10, IntOr(configuration["KGSM_API_BLUEPRINT_CACHE_TTL_SECONDS"], 60)),
            // Instance in-memory cache TTL (background refresh interval). Floor 10s.
            InstanceCacheTtlSeconds = Math.Max(10, IntOr(configuration["KGSM_API_INSTANCE_CACHE_TTL_SECONDS"], 60)),

            // Metrics history (M9). The dedicated DB beside the audit DB; persist cadence floored at 5s;
            // retention/step clamped sane.
            MetricsHistoryEnabled = !Flag(configuration["KGSM_API_METRICS_HISTORY_DISABLED"]),
            MetricsHistoryDb = BlankFallback(
                configuration["KGSM_API_METRICS_HISTORY_DB"],
                DefaultMetricsDb(configuration["KGSM_API_DB"])),
            MetricsPersistMs = Math.Max(5000, IntOr(configuration["KGSM_API_METRICS_PERSIST_MS"], 15000)),
            MetricsRawRetentionHours = Math.Max(1, IntOr(configuration["KGSM_API_METRICS_RAW_RETENTION_HOURS"], 24)),
            MetricsRollupStepMin = Math.Max(1, IntOr(configuration["KGSM_API_METRICS_ROLLUP_STEP_MIN"], 5)),
            MetricsRollupRetentionDays = Math.Max(1, IntOr(configuration["KGSM_API_METRICS_ROLLUP_RETENTION_DAYS"], 30)),
            MetricsMaintenanceMs = Math.Max(10000, IntOr(configuration["KGSM_API_METRICS_MAINT_MS"], 60000)),

            // Metric-threshold alerts (increment 1 — appsettings/env only). The kill-switch is independent
            // of each rule's own enabled flag; the policy itself wholesale-overrides the baked-in Default
            // only when MetricsThresholds:Rules is present and non-empty (see LoadThresholdPolicy).
            MetricsThresholdsDisabled = Flag(configuration["KGSM_API_METRICS_THRESHOLDS_DISABLED"]),
            Policy = LoadThresholdPolicy(configuration),

            // File browser (Tier 3 #12). Entry cap is a frontend-render bound; edit ceiling guards the
            // editor against megabyte blobs. Clamped sane: at least 1 entry, at least 1 KiB.
            FilesMaxEntries = Math.Max(1, IntOr(configuration["KGSM_API_FILES_MAX_ENTRIES"], 200)),
            FilesMaxEditBytes = Math.Max(1024, LongOr(configuration["KGSM_API_FILES_MAX_EDIT_BYTES"], 2 * 1024 * 1024)),

            // Host logs (GET /hosts/{id}/logs). The unit map defaults to the host's leaf services; override
            // KGSM_API_LOG_SOURCES on a host whose units are named differently. journalctl resolves via PATH.
            LogSources = ParseLogSources(configuration["KGSM_API_LOG_SOURCES"]),
            JournalctlPath = BlankFallback(configuration["KGSM_API_JOURNALCTL_PATH"], "journalctl"),
            SystemctlPath = BlankFallback(configuration["KGSM_API_SYSTEMCTL_PATH"], "systemctl"),
            LogReadTimeoutMs = Math.Max(500, IntOr(configuration["KGSM_API_LOG_READ_TIMEOUT_MS"], 5000)),

            // Leaf runtime config (Phase 2). The override dir lives in the API's StateDirectory by default
            // (unprivileged write); the canary window is floored at 2s so a bad value can't be declared good
            // before the leaf has even restarted.
            LeafOverridesDir = BlankFallback(configuration["KGSM_API_LEAF_OVERRIDES_DIR"], "/var/lib/kgsm-api/leaf-overrides"),
            LeafApplyCanaryMs = Math.Max(2000, IntOr(configuration["KGSM_API_LEAF_APPLY_CANARY_MS"], 15000)),

            // Auth (M4·a). On by default; the dev escape hatch is the only way to the old open window.
            AuthDisabled = Flag(configuration["KGSM_API_AUTH_DISABLED"]),
            SigningKey = Defaulted(configuration["KGSM_API_AUTH_SIGNING_KEY"], ""),
            DiscordClientId = Defaulted(configuration["KGSM_API_AUTH_DISCORD_CLIENT_ID"], ""),
            DiscordClientSecret = Defaulted(configuration["KGSM_API_AUTH_DISCORD_CLIENT_SECRET"], ""),
            DiscordRedirectUri = Defaulted(configuration["KGSM_API_AUTH_DISCORD_REDIRECT_URI"], ""),
            DiscordBotToken = Defaulted(configuration["KGSM_API_AUTH_DISCORD_BOT_TOKEN"], ""),
            DiscordGuildId = Defaulted(configuration["KGSM_API_AUTH_DISCORD_GUILD_ID"], ""),
            AuthFrontendUrl = Defaulted(configuration["KGSM_API_AUTH_FRONTEND_URL"], ""),
            RoleAdminIds = Csv(configuration["KGSM_API_AUTH_ROLE_ADMIN"]),
            RoleOperatorIds = Csv(configuration["KGSM_API_AUTH_ROLE_OPERATOR"]),
            RoleViewerIds = Csv(configuration["KGSM_API_AUTH_ROLE_VIEWER"]),
        };
    }

    // The default metrics DB path: metrics.db beside the audit DB (same StateDirectory reasoning).
    private static string DefaultMetricsDb(string? dbPath)
    {
        string? dir = string.IsNullOrWhiteSpace(dbPath) ? null : Path.GetDirectoryName(dbPath.Trim());
        return string.IsNullOrEmpty(dir) ? "metrics.db" : Path.Combine(dir, "metrics.db");
    }

    // The default RAWG image cache dir: a covers/ subdir beside the SQLite DB (so it inherits the
    // StateDirectory the systemd unit sets via KGSM_API_DB). With no DB path (the bare default
    // "kgsm-api.db" — relative, no dir) it falls back to a relative "covers" dir in the cwd.
    private static string DefaultCacheDir(string? dbPath)
    {
        string? dir = string.IsNullOrWhiteSpace(dbPath) ? null : Path.GetDirectoryName(dbPath.Trim());
        return string.IsNullOrEmpty(dir) ? "covers" : Path.Combine(dir, "covers");
    }

    // MetricsThresholds:Rules binds via reflection-based config binding (JIT runtime — fine here); a
    // present, non-empty section wins WHOLESALE over the baked-in Default (no field-by-field merge), per
    // the locked decision in metrics-threshold-alerts-plan.md. Null/empty/absent -> Default.
    // NB: bind to the mutable ThresholdRuleBinding, NOT the positional ThresholdRule — the binder cannot
    // construct a record whose `double? Danger` ctor param has no value, and silently returns an empty list,
    // so a single warn-only rule would drop the whole custom policy back to Default. A rule missing its Key
    // is skipped (malformed), never materialized with a null id.
    private static MetricsThresholdPolicy LoadThresholdPolicy(IConfiguration configuration)
    {
        List<ThresholdRuleBinding>? bound =
            configuration.GetSection("MetricsThresholds:Rules").Get<List<ThresholdRuleBinding>>();
        List<ThresholdRule>? rules = bound?
            .Where(b => !string.IsNullOrWhiteSpace(b.Key))
            .Select(b => b.ToRule())
            .ToList();
        return rules is { Count: > 0 } ? new MetricsThresholdPolicy(rules) : MetricsThresholdPolicy.Default;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Parse an integer config value; blank/unset/garbage -> the fallback (callers clamp the range).
    private static int IntOr(string? value, int fallback) =>
        int.TryParse(value?.Trim(), out int n) ? n : fallback;

    // Parse a long config value; blank/unset/garbage -> the fallback (callers clamp the range).
    private static long LongOr(string? value, long fallback) =>
        long.TryParse(value?.Trim(), out long n) ? n : fallback;

    // null key (unset) -> fallback; present key (even empty) -> the given value, trimmed.
    private static string Defaulted(string? value, string fallback) => value is null ? fallback : value.Trim();

    // null OR blank/whitespace -> fallback; otherwise the trimmed value. For a value that must never be empty
    // (e.g. a filesystem path Path.* will throw on), where the appsettings.json default is a blank "".
    private static string BlankFallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    // Truthy env flag: "1"/"true"/"yes"/"on" (case-insensitive) -> true; anything else -> false.
    private static bool Flag(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    // Comma-separated list (role ids), trimmed and de-blanked. Empty/unset -> empty list.
    private static IReadOnlyList<string> Csv(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // The default host-log source map is DERIVED from the canonical leaf catalog (LeafCatalog) — the single
    // source of truth for "which units make up a host" — so the host-log sources and the Services board can
    // never drift on the unit set. KGSM_API_LOG_SOURCES still overrides this map for a host whose units are
    // named differently (logs only; the Services board reads the catalog directly). Order = catalog order.
    private static readonly IReadOnlyList<LogSourceMap> DefaultLogSources =
        Services.Leaves.LeafCatalog.Default.Select(l => new LogSourceMap(l.Id, l.Unit)).ToArray();

    // Parse `source:unit,source:unit,…` -> the ordered map; blank/unset -> the default leaf set. A malformed
    // entry (no ':' or a blank half) is skipped; if nothing parses we fall back to the default (never an empty
    // map, which would silently disable the whole surface).
    private static IReadOnlyList<LogSourceMap> ParseLogSources(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultLogSources;
        var list = new List<LogSourceMap>();
        foreach (string pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = pair.IndexOf(':');
            if (i <= 0 || i >= pair.Length - 1) continue;
            string src = pair[..i].Trim();
            string unit = pair[(i + 1)..].Trim();
            if (src.Length > 0 && unit.Length > 0) list.Add(new LogSourceMap(src, unit));
        }
        return list.Count > 0 ? list : DefaultLogSources;
    }
}

/// <summary>A configured mapping of a friendly log source id (<c>watchdog</c>) to the systemd unit whose
/// journal carries it (<c>kgsm-watchdog.service</c>). Ordered as the frontend should present the sources.</summary>
public sealed record LogSourceMap(string Source, string Unit);
