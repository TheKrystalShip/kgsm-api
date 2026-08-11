using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api;

/// <summary>
/// The validated configuration the API runs on, produced from <see cref="ApiSettings"/> — which is
/// bound 1:1 from the <c>Api</c> section of <c>kgsm-api.settings.json</c>, the file that declares the
/// whole configurable surface with its defaults. Resolved once at startup via
/// <see cref="FromConfiguration"/> and registered as a singleton, so every consumer reads one
/// interpretation of the configuration rather than re-deriving its own.
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
    /// HTTP bind address(es), semicolon-separated. Carried here so the whole configurable surface is
    /// one type, but <see cref="Program"/> reads the key directly as well: the bind address has to be
    /// known before the host that would resolve these options exists.
    /// </summary>
    public string Urls { get; init; } = "http://127.0.0.1:8080";

    /// <summary>
    /// CORS origin allowlist, already split. Empty means any-origin, which is safe only because
    /// bearers ride the Authorization header rather than cookies — set real origins on a deployed host.
    /// </summary>
    public IReadOnlyList<string> CorsOrigins { get; init; } = [];

    /// <summary>
    /// SQLite file for the API's own operational metadata. Also the anchor for
    /// <see cref="RawgCacheDir"/>'s default, so the image cache lands in the same state directory.
    /// </summary>
    public string DbPath { get; init; } = "kgsm-api.db";

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
    /// Deployment region (<c>Api__Region</c>) — an <strong>arbitrary free string</strong> (e.g.
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
    /// The public origin a browser reaches the assistant on (<c>Api__AssistantPublicUrl</c>), e.g.
    /// <c>https://assistant.example.com</c>. Reported in the assistant capability's <c>info.url</c>
    /// so the Control Panel's chat can address the leaf directly; <see cref="AssistantBaseUrl"/> is
    /// this API's own loopback route and is never a browser address. <see langword="null"/> when
    /// unset — the capability then carries no <c>info</c> and the chat says the assistant has no
    /// browser route, rather than inventing one from the loopback URL.
    /// </summary>
    public string? AssistantPublicUrl { get; init; }

    /// <summary>
    /// Shared secret for the M7 assistant turn relay (<c>Api__AssistantRelaySecret</c>) — the
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

    /// <summary>kgsm-bot's status socket; blank when this host serves no bot status surface.</summary>
    public required string BotSocketPath { get; init; }

    /// <summary>
    /// Path to the host's <c>kgsm.sh</c> entrypoint — the single C#↔engine chokepoint kgsm-lib
    /// shells (instances, run-state). Default: the AUR-packaged symlink <c>/usr/bin/kgsm</c>.
    /// Empty ⇒ the engine is not configured (a misconfiguration: <c>/servers</c> is empty + logged).
    /// </summary>
    public required string KgsmPath { get; init; }

    /// <summary>
    /// Directory holding the engine's append-only event journal, which the audit consumer tails
    /// for engine events. Read-only and shared: the engine is the sole writer, any number of
    /// consumers read the same files, and nothing here belongs to the API.
    /// Default: <c>/var/lib/kgsm/events</c>.
    /// </summary>
    public required string KgsmJournalDir { get; init; }

    // --- Library RAWG.io cover-art / metadata (the M8·a library increment) ------------------------

    /// <summary>
    /// RAWG.io API key (<c>Api__RawgApiKey</c>). <strong>Opt-in: blank by default</strong> → the
    /// hydration worker no-ops and the library's cover/hero stay null (the SPA's gradient fallback),
    /// genres/tags <c>[]</c>. Set it to enable cover-art/metadata hydration. <strong>A secret</strong>:
    /// the RAWG client's <c>HttpClient</c> uses <c>RemoveAllLoggers()</c> so the <c>?key=…</c> never logs.
    /// </summary>
    public required string RawgApiKey { get; init; }

    /// <summary>
    /// Directory the self-hosted cover/hero <c>.jpg</c>s are written to and served from
    /// (<c>Api__RawgCacheDir</c>). Default: a <c>covers/</c> dir beside the SQLite DB
    /// (<c>/var/lib/kgsm-api/covers</c> on a deployed host). The worker creates it on first write.
    /// </summary>
    public required string RawgCacheDir { get; init; }

    /// <summary>
    /// Optional public base URL (<c>Api__PublicBaseUrl</c>, e.g. <c>https://panel.example.com</c>) the
    /// absolute cover/hero URLs are built from for a reverse-proxy deployment. Blank (the default) ⇒ the URLs
    /// are derived from the incoming request (<c>{scheme}://{host}</c>), which resolves per-host for the
    /// multi-host SPA registry. Any trailing slash is trimmed.
    /// </summary>
    public required string PublicBaseUrl { get; init; }

    /// <summary>
    /// Base URL the Steam library-capsule cover (<c>{base}/{appId}/library_600x900.jpg</c> — the 2:3 portrait
    /// art Steam shows in the library view) is fetched from (<c>Api__SteamCdnBaseUrl</c>). Default: Steam's
    /// public store-asset CDN. Any trailing slash is trimmed. <strong>Steam is the cover authority</strong> —
    /// keyed by the blueprint's <c>client_steam_app_id</c>, fully <b>decoupled from RAWG</b> (no key needed);
    /// RAWG's <c>background_image</c> is only the fallback when a game isn't on Steam / has no capsule.
    /// </summary>
    public required string SteamCdnBaseUrl { get; init; }

    /// <summary>Kill-switch for the keyless Steam cover source (<c>Api__SteamCoversDisabled</c>). Off by
    /// default (Steam covers ON — they need no key); set to disable so the cover falls back to RAWG only (and,
    /// with no RAWG key either, the worker no-ops — the offline/test posture the smoke pins).</summary>
    public bool SteamCoversDisabled { get; init; }

    /// <summary>
    /// How stale (in days) a cached library row may get before the periodic worker re-fetches it from
    /// Steam/RAWG (<c>Api__LibraryRefreshIntervalDays</c>, default 7 = weekly). Cover/metadata for a
    /// fixed game catalog is near-static, so this is the per-game refresh cadence; <c>0</c> (or negative)
    /// disables the periodic wake entirely (boot sweep + the admin <c>POST /library/refresh</c> only). The
    /// boot sweep also honours it (a frequent restart doesn't re-hammer fresh rows).
    /// </summary>
    public required int LibraryRefreshIntervalDays { get; init; }

    /// <summary>The <b>local</b> hour-of-day (0–23) the periodic refresh wakes to check
    /// (<c>Api__LibraryRefreshHour</c>, default 6 = 06:00 local — a quiet window). The worker wakes at
    /// this hour each day and re-fetches any row older than <see cref="LibraryRefreshIntervalDays"/>; the wake
    /// itself is cheap (a DB read) when nothing is stale.</summary>
    public required int LibraryRefreshHour { get; init; }

    /// <summary>
    /// How long (seconds) the in-memory blueprint cache serves before a background refresh
    /// (<c>Api__BlueprintCacheTtlSeconds</c>, default 60). Blueprints change infrequently
    /// (install/uninstall), so a short staleness window is acceptable. Floor: 10s.
    /// </summary>
    public required int BlueprintCacheTtlSeconds { get; init; }

    /// <summary>
    /// How long (seconds) the in-memory instance cache serves before a background refresh
    /// (<c>Api__InstanceCacheTtlSeconds</c>, default 60). Instances change infrequently
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

    /// <summary>The bot publishes a status socket on this host (config-based, like the scheduler).</summary>
    public bool BotStatusProvisioned => !string.IsNullOrWhiteSpace(BotSocketPath);

    /// <summary>
    /// Whether the kgsm engine is configured (a non-empty <see cref="KgsmPath"/>). Unlike a leaf
    /// capability, the engine is assumed present — <c>false</c> is a surfaced misconfiguration.
    /// </summary>
    public bool KgsmProvisioned => !string.IsNullOrWhiteSpace(KgsmPath);

    // --- Realtime pump cadences (M2) — the background poll intervals the WS pumps tick at ----------

    /// <summary>
    /// How often the <see cref="Realtime.DomainPump"/> re-fetches the instance roster + run-state from
    /// kgsm (<c>Api__DomainPollMs</c>, default 5000 = 5s, floor 1000). This is the poll the
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
    /// resource tick out to the <c>*/metrics</c> topics (<c>Api__MetricsPollMs</c>, default 1000 =
    /// 1s, floor 250). This is the live performance feed (≈ the monitor's own self-tick), <b>not</b> the
    /// instance/blueprint poll — relaxing it makes the SPA's performance charts choppy, so it stays at 1s
    /// by default. Gated on subscribers. This is the live feed only; durable metrics history is owned by
    /// kgsm-monitor (the API relays its <c>/metrics/history</c>).
    /// </summary>
    public required int MetricsPollMs { get; init; }

    /// <summary>
    /// How often the <see cref="Realtime.ServicesPump"/> polls systemd for leaf-service state changes and
    /// emits <c>service.patch</c> on the <c>hosts/{id}/services</c> topic (<c>Api__ServicesPollMs</c>,
    /// default 5000 = 5s, floor 2000). Subscriber-gated — an idle stream costs nothing. Coarser than the
    /// metrics tick (1s) because systemd state changes are infrequent and the UI doesn't need sub-second
    /// granularity for service status. Distinct from the <see cref="Realtime.LeafHealthMonitor"/> 2s
    /// capability probe (which handles the deep-health axis independently).
    /// </summary>
    public required int ServicesPollMs { get; init; }

    /// <summary>
    /// How often the always-on <see cref="Services.Aggregation.BackupCache"/> re-scans each instance's
    /// backups (<c>Api__BackupScanPollMs</c>, default 300000 = 5min, floor 30000 = 30s). Listing
    /// backups is a kgsm process spawn per instance, so it cannot ride the roster refresh that serves
    /// <c>GET /servers</c>; this relaxed cadence carries the steady state while the kgsm
    /// <c>instance_backup_created</c>/<c>instance_backup_restored</c> event echo refreshes the one affected
    /// instance immediately, which is the case that actually needs to be prompt. A failed read keeps the
    /// prior reading (never wipes); the fields start honest-null until the first scan completes.
    /// </summary>
    public int BackupScanPollMs { get; init; } = 300_000;

    // Metrics history is owned by kgsm-monitor now (the API relays GET /metrics/history verbatim);
    // no history persistence config lives here.

    // --- File browser (Tier 3 #12) — the GET/PUT /servers/{id}/files surface ----------------------

    /// <summary>
    /// Max directory entries a single <c>GET /servers/{id}/files</c> returns before truncating with a
    /// <c>truncated:true</c> signal (<c>Api__FilesMaxEntries</c>, default 200). The constraint is
    /// FRONTEND rendering (one DOM node per entry, not virtualized) — a save subdir with thousands of map
    /// chunks janks the tree, not the API. Truncation is always signaled, never a silent refusal (plan §5).
    /// </summary>
    public required int FilesMaxEntries { get; init; }

    /// <summary>
    /// Max file size (bytes) the editor will open or save (<c>Api__FilesMaxEditBytes</c>, default
    /// ~2 MiB). A read past this returns <c>file_too_large</c> (the SPA shows "can't open" honestly rather
    /// than rendering megabytes of text); a save past it is refused. Hashing ≤ this for the etag is trivial.
    /// </summary>
    public required long FilesMaxEditBytes { get; init; }

    /// <summary>
    /// Max blueprint-file size (bytes) the library editor will open or save
    /// (<c>Api__BlueprintMaxEditBytes</c>, default 256 KiB). Separate from
    /// <see cref="FilesMaxEditBytes"/> because the two surfaces edit different things: an instance's
    /// working directory holds arbitrary game files, while a blueprint is a short hand-written YAML
    /// (~25 lines native, ~70 container). A ceiling three orders of magnitude above the largest real
    /// blueprint is generous and still refuses anything that plainly isn't one.
    /// </summary>
    public required long BlueprintMaxEditBytes { get; init; }

    // --- Host logs (the GET /hosts/{id}/logs journald aggregation surface) ------------------------

    /// <summary>
    /// The ordered source-id → systemd-unit map the host-log aggregator reads
    /// (<c>Api__LogSources</c>, e.g. <c>watchdog:kgsm-watchdog.service,monitor:kgsm-monitor.service</c>).
    /// Blank/unset ⇒ the default leaf set (assistant, monitor, watchdog, firewall, api, bot). Order is the
    /// order the frontend presents the sources. Reading the journal needs the api's user to have journal read
    /// access (the <c>systemd-journal</c>/<c>wheel</c> ACL); a host whose units are named differently overrides
    /// this map. NOT a §4·b capability — journald is always present locally; a read failure degrades to empty.
    /// </summary>
    public required IReadOnlyList<LogSourceMap> LogSources { get; init; }

    /// <summary>The <c>journalctl</c> binary the host-log reader shells (<c>Api__JournalctlPath</c>,
    /// default <c>journalctl</c> — resolved via PATH). The reader degrades to an empty page if it's missing.</summary>
    public required string JournalctlPath { get; init; }

    /// <summary>The <c>systemctl</c> binary the Services board (<c>GET /hosts/{id}/services</c>) shells to read
    /// each leaf unit's live state (<c>Api__SystemctlPath</c>, default <c>systemctl</c> — resolved via
    /// PATH). Reading unit state is unprivileged; the reader degrades each unit to <c>unknown</c> if it's
    /// missing. Same host-OS-introspection category as <see cref="JournalctlPath"/> — NOT a §4·b capability.</summary>
    public required string SystemctlPath { get; init; }

    /// <summary>Hard wall-clock budget (ms) for a single host-log read (<c>Api__LogReadTimeoutMs</c>,
    /// default 5000, floor 500). On timeout the reader returns the lines it gathered (honest partial), never a
    /// fabricated tail.</summary>
    public required int LogReadTimeoutMs { get; init; }

    // --- Leaf runtime config (the leaf-runtime-config feature, Phase 2) ----------------------------

    /// <summary>
    /// Directory the per-leaf override files (<c>&lt;leaf&gt;.env</c>) are rendered to
    /// (<c>Api__LeafOverridesDir</c>, default <c>/var/lib/kgsm-api/leaf-overrides</c> — the API's own
    /// state dir, so the API writes it <strong>unprivileged</strong>; a systemd drop-in the leaf is unaware of
    /// feeds it via <c>EnvironmentFile=-</c>). The renderer mkdirs it <c>0700</c> and writes each file
    /// <c>0600</c> (the overrides can hold secrets). The override file is a deterministic render of the
    /// <c>leaf_override</c> DB rows — never hand-edited.
    /// </summary>
    public required string LeafOverridesDir { get; init; }

    /// <summary>
    /// How long (ms) the apply broker watches a leaf's health after a config restart before declaring the
    /// change good (<c>Api__LeafApplyCanaryMs</c>, default 15000, floor 2000). If the leaf is not
    /// healthy within this window the override is restored and the leaf restarted again (auto-rollback) — so a
    /// bad value is a caught, reverted failure, not a downed leaf.
    /// </summary>
    public required int LeafApplyCanaryMs { get; init; }

    /// <summary>
    /// Directory the leaf config descriptors are read from (<c>Api__LeafDescriptorDir</c>, default
    /// <c>/var/lib/kgsm/leaves</c>). Each leaf's own <c>deploy.sh</c> installs <c>&lt;leaf&gt;.json</c> here
    /// declaring its full configurable surface; this API only ever <strong>scans and reads</strong> it, so a
    /// leaf that joins the ecosystem later becomes configurable with no rebuild here. Format:
    /// <c>tks/leaf-config-descriptor.md</c>.
    /// </summary>
    public required string LeafDescriptorDir { get; init; }

    /// <summary>
    /// Where systemd unit drop-ins live (<c>Api__LeafDropInDir</c>, default
    /// <c>/etc/systemd/system</c>). Read for two things: to tell whether a leaf is wired for config delivery
    /// at all (its <c>50-kgsm-api-override.conf</c> exists), and to resolve a leaf's floor values from the
    /// unit fragments that set them. Never written — the drop-ins are installed by
    /// <c>deploy/setup-leaf-config.sh</c>.
    /// </summary>
    public required string LeafDropInDir { get; init; }

    /// <summary>
    /// This API's own currently-resolved value for one of its settings, by environment-variable name — the
    /// other half of a descriptor field's <c>pairedApiKey</c>. Returns null when the name is not one this API
    /// resolves and the environment does not carry it, which means "cannot compare", not "they disagree".
    /// </summary>
    /// <remarks>
    /// Deliberately reads the <em>resolved</em> value rather than the raw environment for the settings it
    /// knows: an unset variable still has an effective value here (the coded default), and comparing against
    /// the raw environment would report a spurious disagreement whenever a host relies on defaults.
    /// </remarks>
    public string? ResolvedByEnvName(string envName) => envName switch
    {
        // Spelled from the settings property names, so a rename moves the case label with the
        // property rather than leaving a string here that resolves to nothing.
        $"{ApiSettings.Section}__{nameof(ApiSettings.HostId)}" => HostId,
        $"{ApiSettings.Section}__{nameof(ApiSettings.MonitorSocketPath)}" => MonitorSocketPath,
        $"{ApiSettings.Section}__{nameof(ApiSettings.WatchdogSocketPath)}" => WatchdogSocketPath,
        $"{ApiSettings.Section}__{nameof(ApiSettings.SchedulerSocketPath)}" => SchedulerSocketPath,
        $"{ApiSettings.Section}__{nameof(ApiSettings.BotSocketPath)}" => BotSocketPath,
        $"{ApiSettings.Section}__{nameof(ApiSettings.AssistantBaseUrl)}" => AssistantBaseUrl,
        $"{ApiSettings.Section}__{nameof(ApiSettings.AssistantPublicUrl)}" => AssistantPublicUrl,
        $"{ApiSettings.Section}__{nameof(ApiSettings.FirewallSocketPath)}" => FirewallSocketPath,
        $"{ApiSettings.Section}__{nameof(ApiSettings.KgsmPath)}" => KgsmPath,
        $"{ApiSettings.Section}__{nameof(ApiSettings.KgsmJournalDir)}" => KgsmJournalDir,
        _ => Environment.GetEnvironmentVariable(envName),
    };

    // --- Cluster message bus (docs/cluster-message-bus-plan.md, PLAN-peers.md §3) — the shared
    //     secret + node identity behind the cluster service token (node-to-node auth). Opt-in like
    //     the assistant/firewall: a blank secret means this host is not part of a cluster and the
    //     bus stays dormant (no inbox endpoint, no drainer — those land in later phases). -------------

    /// <summary>
    /// The shared cluster HMAC secret (<c>Api__ClusterSecret</c>) every node in the guild is
    /// configured with — distinct from <see cref="SigningKey"/> (leaking one never hands over the
    /// other's forgery). Blank ⇒ the cluster capability is not provisioned (<see cref="ClusterEnabled"/>
    /// is <see langword="false"/>): no service token can be minted or validated on this host.
    /// </summary>
    public required string ClusterSecret { get; init; }

    /// <summary>
    /// The previous cluster secret (<c>Api__ClusterSecretPrevious</c>), accepted alongside
    /// <see cref="ClusterSecret"/> during a rotation overlap window (<c>PLAN-peers.md §2</c> #9): roll
    /// one node at a time, then drop this once every node is on the new secret. Blank ⇒ no previous
    /// secret is accepted (the normal, non-rotating posture).
    /// </summary>
    public required string ClusterSecretPrevious { get; init; }

    /// <summary>
    /// This node's cluster identity (<c>Api__NodeId</c>) — the <c>sub</c>/<c>iss</c> a minted
    /// service token carries. Defaults to <see cref="HostId"/> (<c>PLAN-peers.md §2</c> #2: "config-driven
    /// nodeId, default: machine name, same as HostId") — a cluster node's identity is the same stable id
    /// already used for the host card, never a second independent name.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Whether this host is part of a cluster (a non-blank <see cref="ClusterSecret"/>). When
    /// <see langword="false"/> the cluster service token cannot be minted (<see
    /// cref="Services.Cluster.IClusterTokenService.Mint"/> throws) and never validates — the bus,
    /// the inbox endpoint, and every peer feature built on top of it (later phases) stay dormant.
    /// </summary>
    public bool ClusterEnabled => !string.IsNullOrWhiteSpace(ClusterSecret);

    // --- Cluster message bus, Phase 3 (the outbox drainer + GC — docs/cluster-message-bus-plan.md
    //     §6/§7). Not `required`: like the alert `Policy` above, these carry a sane default
    //     so the many existing test-built ApiOptions literals don't need updating for an opt-in feature
    //     that is inert whenever ClusterEnabled is false. -----------------------------------------------

    /// <summary>
    /// How often (ms) the outbox drainer ticks (<c>Api__ClusterDrainMs</c>, default 1000 = 1s,
    /// floor 250). Each tick pulls the due <c>pending</c> rows (<c>NextAttemptAt &lt;= now</c>) and
    /// attempts delivery; a row's own backoff — not this cadence — governs its individual retry spacing.
    /// </summary>
    public int ClusterDrainMs { get; init; } = 1000;

    /// <summary>
    /// Days a still-<c>pending</c> outbox row may keep retrying before the drainer dead-letters it
    /// (<c>Api__ClusterRetryTtlDays</c>, default 7, floor 1). Anchored on the row's
    /// <c>CreatedAt</c> — seven days covers any realistic node outage; a message still queued after
    /// that is an operational alarm (a loud log), not a silent loss (plan §6).
    /// </summary>
    public int ClusterRetryTtlDays { get; init; } = 7;

    /// <summary>
    /// Days a <c>delivered</c>/<c>dead</c> outbox row or an inbox dedupe-ledger row is kept before the
    /// GC worker prunes it (<c>Api__ClusterRetentionDays</c>, default 30). <b>Must exceed
    /// <see cref="ClusterRetryTtlDays"/></b> — <see cref="FromConfiguration"/> clamps it to at least
    /// <c>ClusterRetryTtlDays + 1</c> so a message redelivered right at the retry TTL boundary is still
    /// recognized as a duplicate by the (not-yet-pruned) inbox ledger, never re-applied (plan §7).
    /// </summary>
    public int ClusterRetentionDays { get; init; } = 30;

    /// <summary>
    /// How often (ms) the cluster bus GC worker sweeps (<c>Api__ClusterGcMs</c>, default 600000 =
    /// 10 min, floor 60000) — the same cadence family as <see cref="SessionsGcMs"/>.
    /// </summary>
    public int ClusterGcMs { get; init; } = 600000;

    // --- Cluster membership gossip (PLAN-peers.md §2·b, P0.5) — the masterless anti-entropy layer that
    //     converges the roster ("add one, join all"). All inert whenever ClusterEnabled is false; sane
    //     defaults so existing ApiOptions literals need no update. ------------------------------------------

    /// <summary>
    /// This node's <b>advertised, browser-reachable client URL</b> (<c>Api__ClusterAdvertiseUrl</c>,
    /// <c>PLAN-peers.md §2</c> #13a) — the address it puts in its own gossip self-entry so peers that learn
    /// it via gossip know where the SPA should reach it. Blank ⇒ this node omits its own URL from gossip
    /// (peers still learn it from whoever seeded it by handshake); a node discovered ONLY through this node's
    /// self-entry then has no client address and stays provisional. Honest default: unset.
    /// </summary>
    public string ClusterAdvertiseUrl { get; init; } = "";

    /// <summary>
    /// This node's <b>node-to-node gossip URL</b> (<c>Api__ClusterGossipUrl</c>, <c>PLAN-peers.md
    /// §2</c> #13a), when it differs from <see cref="ClusterAdvertiseUrl"/> (e.g. an internal/VPN address).
    /// Blank ⇒ falls back to <see cref="ClusterAdvertiseUrl"/> in the self-entry.
    /// </summary>
    public string ClusterGossipUrl { get; init; } = "";

    /// <summary>
    /// How often (ms) the gossip worker runs one random-peer push-pull sync round
    /// (<c>Api__ClusterGossipMs</c>, default 5000 = 5s, floor 250) — one peer per round, O(1) work
    /// per node (<c>PLAN-peers.md §2·b</c>, G1). Each round also advances the failure timers.
    /// </summary>
    public int ClusterGossipMs { get; init; } = 5000;

    /// <summary>
    /// How often (ms) the latency poller probes each enabled peer's <c>/identity</c> first-hand
    /// (<c>Api__ClusterPollMs</c>, default 10000 = 10s, floor 250; <c>PLAN-peers.md §4</c>). This is
    /// the failure detector's sampling rate: a peer must go unreachable across probes before the timers
    /// escalate it, so keep it comfortably below <see cref="ClusterSuspectMs"/> (several probes per suspect
    /// window). It is also the first-hand-auth cadence that promotes a gossip-discovered peer to <c>alive</c>.
    /// </summary>
    public int ClusterPollMs { get; init; } = 10000;

    /// <summary>
    /// How long (ms) a peer stays <c>suspect</c> — no successful first-hand probe — before the failure timer
    /// escalates it to <c>dead</c> (<c>Api__ClusterSuspectMs</c>, default 30000 = 30s, floor 1000;
    /// <c>PLAN-peers.md §2·b</c>, G5).
    /// </summary>
    public int ClusterSuspectMs { get; init; } = 30000;

    /// <summary>
    /// How long (ms) a peer stays <c>dead</c>/<c>left</c> before it is reaped (row deleted) from the roster
    /// (<c>Api__ClusterReapMs</c>, default 300000 = 5 min, floor 1000; <c>PLAN-peers.md §2·b</c>, G5 /
    /// §6 P0.5). Long enough that a briefly-bounced node refutes its own <c>dead</c> (via a higher
    /// incarnation) before it is forgotten.
    /// </summary>
    public int ClusterReapMs { get; init; } = 300000;

    // --- Auth (M4·a) — Discord per-host, Model A (architecture.html §3·f, keystone O5) -----------
    // Identity is a global Discord SSO anchor; authorization is a short-lived host-scoped bearer
    // this host mints after verifying identity once and resolving the role via the host's bot.
    // The Discord app/guild/bot-token/role-map are SHARED EXTERNAL CONFIG (same values the Discord
    // bot uses) — keystone §4: this is configuration, NOT a process dependency on kgsm-bot.

    /// <summary>
    /// Dev escape hatch (<c>Api__AuthDisabled=true</c>). When set, every request is authenticated
    /// as a synthetic <c>admin</c> and all tier policies pass — the pre-M4 unauthenticated trust
    /// window, now explicit and loudly logged. Off by default: <strong>auth is on by default</strong>.
    /// </summary>
    public bool AuthDisabled { get; init; }

    /// <summary>HMAC signing key for the host-scoped session JWTs (<c>Api__SigningKey</c>).
    /// Empty + auth enabled ⇒ an ephemeral per-process key is generated (tokens die on restart;
    /// logged loudly). Set a stable secret on a real host.</summary>
    public required string SigningKey { get; init; }

    /// <summary>
    /// The OAuth applications this host signs people in through, by provider name
    /// (<c>KgsmAuth__Providers__&lt;name&gt;__ClientId</c>). Shared with every other surface on the
    /// host, so one file points all of them at the same applications.
    /// </summary>
    public required KgsmAuthOptions OAuth { get; init; }

    /// <summary>
    /// This host's own sign-in callback (<c>Api__DiscordRedirectUri</c>) — the address a provider
    /// returns a browser to. Its <b>origin</b> is what every provider's two callbacks are built from
    /// (<see cref="LoginRedirectUri"/>, <see cref="LinkRedirectUri"/>): the paths belong to this
    /// API's own routes, so there is one place a host's public address is written and no way for two
    /// callbacks to name different origins.
    /// </summary>
    public required string DiscordRedirectUri { get; init; }

    /// <summary>
    /// The SPA origin/URL the OAuth callback hands the session back to
    /// (<c>Api__AuthFrontendUrl</c>). When set, <c>/auth/discord/callback</c> 302s the browser
    /// here with the result in the URL <b>fragment</b> (<c>#access=…&amp;refresh=…</c> on success,
    /// <c>#error=…</c> otherwise) instead of returning JSON — the SPA token handoff. The redirect target
    /// is THIS single configured value, never a request-supplied one (no open-redirect). Blank → the
    /// callback keeps returning JSON (API-only deployments, and the test default).
    /// </summary>
    public required string AuthFrontendUrl { get; init; }

    /// <summary>
    /// The host's shared KGSM account store (<c>Api__UsersDbPath</c>, default
    /// <c>/var/lib/kgsm/auth/users.db</c>) — where local accounts, their credentials and their tiers live.
    /// </summary>
    /// <remarks>
    /// Deliberately outside <see cref="DbPath"/>. This API's own database is its operational state and
    /// is wiped when its schema changes; accounts are the <em>host's</em>, shared with every other KGSM
    /// surface running beside it and never wiped. Pointing this at a file the assistant does not read
    /// gives the two surfaces different accounts.
    /// </remarks>
    /// <remarks>
    /// Not <c>required</c>, unlike the secrets beside it: this has one correct value on every host in
    /// the ecosystem, so a default here is the shared location rather than a guess. What must never
    /// default is a credential.
    /// </remarks>
    public string UsersDbPath { get; init; } = UserStoreOptions.DefaultPath;

    /// <summary>
    /// How long a resolved tier is reused before the account store is read again
    /// (<c>Api__AuthorityCacheSeconds</c>, default 5, floor 0 = never cache).
    /// </summary>
    /// <remarks>
    /// Authority is resolved on every request rather than read off the token, so this is the
    /// staleness bound on a demotion: how long after an admin lowers someone's tier that their next
    /// request can still pass at the old one. The read behind it is a local point query, so there is
    /// little to buy by making it long.
    /// </remarks>
    public int AuthorityCacheSeconds { get; init; } = 5;

    /// <summary>
    /// The most accounts awaiting approval to hold at once (<c>Api__PendingUserCap</c>, default 32,
    /// floor 1).
    /// </summary>
    /// <remarks>
    /// Signing in through an identity provider with no account here provisions one, unapproved and at
    /// no tier. That is reachable by anyone who can complete a login at that provider, so the table it
    /// grows needs a ceiling however little each row can do.
    /// </remarks>
    public int PendingUserCap { get; init; } = 32;

    /// <summary>
    /// How long an unapproved, self-provisioned account survives unattended
    /// (<c>Api__PendingUserTtlDays</c>, default 14, floor 1).
    /// </summary>
    /// <remarks>
    /// What keeps <see cref="PendingUserCap"/> from becoming a lockout: without expiry, one burst of
    /// arrivals fills the cap permanently and the next real person is refused. Only ever removes an
    /// account that arrived this way, is still unapproved, and has no password.
    /// </remarks>
    public int PendingUserTtlDays { get; init; } = 14;

    /// <summary>
    /// How long after proving a credential a session may attach or detach one
    /// (<c>Api__ReauthWindowMinutes</c>, default 5, floor 1).
    /// </summary>
    /// <remarks>
    /// Linking is the one write that outlives the session making it: afterwards, whoever holds that
    /// provider account can sign in as this one forever. A live session is not proof enough for that —
    /// it can be a borrowed unlocked laptop — so the credential has to have been proved recently, and
    /// this is how recently. Signing in counts as proving it, so the common path never sees a prompt.
    /// </remarks>
    public int ReauthWindowMinutes { get; init; } = 5;

    /// <summary>How this host bounds unapproved arrivals.</summary>
    public PendingPolicy PendingPolicy =>
        new(PendingUserCap, TimeSpan.FromDays(PendingUserTtlDays));

    /// <summary>Auth is on unless the dev escape hatch is set.</summary>
    public bool AuthEnabled => !AuthDisabled;

    // --- Sessions (M4·c) — the session registry + cached per-request validator
    // (re-opens the M4·a "no session table" lock — see docs/session-management-plan.md).
    // Sessions are operational state, NOT a user profile row; identity stays in the JWT claims.
    // The registry is the authority the cached validator reads to decide "is this session alive"
    // — what the stateless JWT alone cannot answer (close the 30d-refresh revocation gap).

    /// <summary>
    /// Master switch for the session registry + the cached per-request validator +
    /// the revocation surface (<c>Api__SessionsDisabled</c>, default <see langword="false"/>
    /// → sessions ON). When <see langword="true"/> the registry is inert — no per-request check,
    /// no <c>GET /auth/sessions</c>, no revoke endpoints (the M4·a stateless-JWT posture, an
    /// escape hatch for debugging). ⚠ In-flight tokens under <c>DISABLED</c> are always alive
    /// (no <c>sid</c> check); only set this for a deliberate debugging window, never on a real
    /// host. <b>Default ON</b> like <see cref="AuthEnabled"/>.
    /// </summary>
    public bool SessionsEnabled { get; init; } = true;

    /// <summary>
    /// The in-memory cache TTL (ms) for the per-request session validator
    /// (<c>Api__SessionsCacheTtlMs</c>, default 5000 = 5s, floor 500). The accepted
    /// revocation-lag bound (D2): a revoke evicts the cache entry immediately (best-effort), and
    /// the TTL is the backstop — worst case a revoked session lives up to this long before the
    /// next access → 401. Per-host single-instance so no cross-node coherence is needed. Lower
    /// values trade DB load for faster revoke; the access-token TTL (15min) is the hard ceiling
    /// regardless. Bumping to 0 effectively disables the cache (per-request DB read).
    /// </summary>
    public required int SessionsCacheTtlMs { get; init; }

    /// <summary>
    /// How often the session GC worker deletes expired rows
    /// (<c>Api__SessionsGcMs</c>, default 600000 = 10 min, floor 60000). Both revoked and
    /// non-revoked rows whose <c>Expires &lt; now</c> are deleted (expired is dead regardless of
    /// revocation) — keeps the table permanently bounded. Runs once at startup for catch-up after
    /// downtime. Inert when <see cref="SessionsEnabled"/> is <see langword="false"/>.
    /// </summary>
    public required int SessionsGcMs { get; init; }

    /// <summary>
    /// The session absolute-cap window in days (<c>Api__SessionsRefreshAbsoluteDays</c>,
    /// default 30, floor 1). A session row's <c>Expires = Created + this</c>. <b>No sliding</b>
    /// on refresh (the cap stays absolute, per the original M4·a lock rationale) — D8. ⚠ Must
    /// stay in lockstep with <see cref="Services.Auth.SessionTokenService"/>'s refresh-token TTL:
    /// if you change one, change both. A mismatch means the registry treats alive tokens as
    /// dead (or vice versa) — the registry is the revocation authority, the JWT TTL is the mint
    /// bound, and they must agree.
    /// </summary>
    public required int SessionsRefreshAbsoluteDays { get; init; }

    /// <summary>Sessions are on unless the master switch is unset. Mirrors
    /// <see cref="AuthEnabled"/>'s default-ON posture.</summary>
    public bool SessionsProvisioned => SessionsEnabled;

    /// <summary>
    /// Whether a sign-in through <paramref name="provider"/> can run here — an application for it,
    /// and this host's own public address. That is the whole of what signing someone in needs: no
    /// group and no role, because a login establishes who someone is and the account store alone
    /// says what they may do.
    /// </summary>
    /// <remarks>
    /// Auth (JWT validation, tier gates) is enforced regardless; this gates the <em>login</em> and
    /// <em>linking</em> endpoints, which 503 when a provider is not configured. A provider this host
    /// has no application for and one it has never heard of answer the same, so a caller asks this
    /// and never whether the name is known.
    /// </remarks>
    public bool ProviderConfigured(string provider) =>
        OAuth.For(provider).Configured && !string.IsNullOrWhiteSpace(DiscordRedirectUri);

    /// <summary>Where <paramref name="provider"/> returns a browser that is <em>signing in</em>.</summary>
    public string LoginRedirectUri(string provider) => CallbackUri($"/auth/{provider}/callback");

    /// <summary>
    /// Where <paramref name="provider"/> returns a browser that is <em>attaching</em> an account
    /// rather than signing in.
    /// </summary>
    /// <remarks>
    /// A separate address because the two flows end differently: one mints a session, the other
    /// attaches a credential to an account that already exists. ⚠ A provider accepts only redirect
    /// URIs registered on the application, so <b>this one has to be registered alongside the login
    /// callback</b> or a link is refused at the provider before it starts. That refusal is loud and
    /// names the URI.
    /// </remarks>
    public string LinkRedirectUri(string provider) =>
        CallbackUri($"/auth/identities/{provider}/callback");

    /// <summary>
    /// One of this host's callbacks, on the origin <see cref="DiscordRedirectUri"/> establishes. The
    /// path is this API's own route rather than anything configured, so a host's public address is
    /// written once and every provider's two callbacks agree on it by construction.
    /// </summary>
    private string CallbackUri(string path) =>
        Uri.TryCreate(DiscordRedirectUri, UriKind.Absolute, out Uri? configured)
            ? new Uri(configured, path).ToString()
            : string.Empty;

    /// <summary>Whether the OAuth callback redirects the session back to the SPA (fragment handoff)
    /// rather than returning JSON. True iff a frontend URL is configured.</summary>
    public bool FrontendRedirectEnabled => !string.IsNullOrWhiteSpace(AuthFrontendUrl);

    /// <summary>
    /// How this host mints session tokens. Projected here so <see cref="SessionsRefreshAbsoluteDays"/>
    /// is the ONE place the session lifetime is written: the token's expiry and the registry row's
    /// expiry are both derived from it, and a second copy would drift until a token outlived its own
    /// row or the reverse.
    /// </summary>
    public SessionTokenOptions ToSessionTokenOptions() => new(
        HostId,
        SigningKey,
        AccessLifetime: TimeSpan.FromMinutes(15),
        RefreshLifetime: TimeSpan.FromDays(SessionsRefreshAbsoluteDays),
        // "kgsm-api", not the package's neutral default: this host has been minting tokens under it,
        // and the issuer is validated. Adopting a tidier value would 401 every token already out
        // there and force every signed-in person to log in again.
        Issuer: "kgsm-api");

    public static ApiOptions FromConfiguration(IConfiguration configuration) =>
        FromSettings(
            configuration.GetSection(ApiSettings.Section).Get<ApiSettings>() ?? new ApiSettings(),
            configuration.GetSection(KgsmAuthOptions.Section).Get<KgsmAuthOptions>() ?? new KgsmAuthOptions());

    /// <summary>
    /// Validates what configuration supplied and produces the form the API runs on: clamps every
    /// cadence to its floor, resolves a blank path to the coded default where an empty one would
    /// throw, splits the CSV knobs, and inverts the two disable-flags into the enabled-by-default
    /// properties the code reads.
    /// </summary>
    /// <remarks>
    /// The distinction between <see langword="null"/> and <c>""</c> is load-bearing throughout, and
    /// is why the settings type holds nullable strings. A key absent from configuration means "use
    /// the default"; a key present but blank means "deliberately off", which is how a leaf endpoint
    /// declares its capability <c>absent</c> rather than merely unset. <see cref="Defaulted"/> keeps
    /// that difference; <see cref="BlankFallback"/> deliberately collapses it, for the few paths an
    /// empty string would make <c>Path.*</c> throw on.
    /// </remarks>
    public static ApiOptions FromSettings(
        ApiSettings s, KgsmAuthOptions? auth = null)
    {
        // The OAuth applications are the ECOSYSTEM's, not this API's — the assistant beside us signs
        // people in through the same ones — so they arrive from the shared KgsmAuth section. The
        // redirect URI is not among them: each surface has its own callback.
        auth ??= new KgsmAuthOptions();
        string hostId = Clean(s.HostId) ?? Environment.MachineName;

        // Computed ahead of the object initializer so ClusterRetentionDays's floor can reference it
        // (an initializer can't read a sibling property off the object being constructed).
        int clusterRetryTtlDays = Math.Max(1, s.ClusterRetryTtlDays ?? 7);

        return new ApiOptions
        {
            Urls = BlankFallback(s.Urls, "http://127.0.0.1:8080"),
            CorsOrigins = Csv(s.CorsOrigins),

            HostId = hostId,
            HostLabel = Clean(s.HostLabel) ?? hostId,
            // Region: an arbitrary free string. Null when unset (honest unknown) — Clean() collapses blank to null.
            Region = Clean(s.Region),
            // For socket/url defaults we distinguish "unset" (use the default) from
            // "set to empty" (deliberately mark the capability absent): a present-but-empty
            // value stays empty, an absent key falls back to the standard path.
            MonitorSocketPath = Defaulted(s.MonitorSocketPath, "/run/kgsm-monitor/metrics.sock"),
            WatchdogSocketPath = Defaulted(s.WatchdogSocketPath, "/run/kgsm-watchdog/control.sock"),
            AssistantBaseUrl = Defaulted(s.AssistantBaseUrl, ""),
            // The browser route is opt-in and has no sensible default — a loopback URL is not one.
            AssistantPublicUrl = Clean(s.AssistantPublicUrl),
            AssistantRelaySecret = Defaulted(s.AssistantRelaySecret, ""),
            // Opt-in (blank = absent): the firewall authority is a separate optional install.
            FirewallSocketPath = Defaulted(s.FirewallSocketPath, ""),
            // Opt-in (blank = absent): the scheduler is a separate optional leaf. Set it to
            // /run/kgsm-scheduler/status.sock on a host that runs kgsm-scheduler.
            SchedulerSocketPath = Defaulted(s.SchedulerSocketPath, ""),
            BotSocketPath = Defaulted(s.BotSocketPath, ""),
            KgsmPath = Defaulted(s.KgsmPath, "/usr/bin/kgsm"),
            KgsmJournalDir = Defaulted(s.KgsmJournalDir, "/var/lib/kgsm/events"),
            DbPath = BlankFallback(s.DbPath, "kgsm-api.db"),

            // Realtime pump cadences. The domain (instance) poll is relaxed by default (5s) — it
            // spawns kgsm.sh and the roster changes rarely (the SPA also has a manual refresh); floored at
            // 1s. The metrics tick stays at the monitor's ~1s self-tick (the live charts feed); floored at
            // 250ms. Blueprints have no poll — GET /library reads them live per request.
            DomainPollMs = Math.Max(1000, s.DomainPollMs ?? 5000),
            MetricsPollMs = Math.Max(250, s.MetricsPollMs ?? 1000),
            ServicesPollMs = Math.Max(2000, s.ServicesPollMs ?? 5000),
            // Update-check poll — the slow (networked) fleet-wide probe's cadence. Relaxed (10min default,
            // 1min floor): the per-game upstream API hit makes it the slowest surface, so it runs on its own
            // dedicated cadence — independent of the fast-mode 60s instance cache. NOT subscriber-gated.
            BackupScanPollMs = Math.Max(30_000, s.BackupScanPollMs ?? 300_000),
            // Library RAWG cover/metadata. Opt-in (blank key => worker no-ops). The cache dir always resolves
            // to a concrete path: an explicit RawgCacheDir wins, else a covers/ subdir beside the SQLite DB,
            // so it lands in the StateDirectory the deployed unit sets. (BlankFallback, not Defaulted — a
            // blank must NOT stay blank here: Path.* would throw on an empty cache dir.)
            RawgApiKey = Defaulted(s.RawgApiKey, ""),
            RawgCacheDir = BlankFallback(s.RawgCacheDir, DefaultCacheDir(s.DbPath)),
            PublicBaseUrl = Defaulted(s.PublicBaseUrl, "").TrimEnd('/'),

            // Steam library-capsule cover (the 2:3 portrait). The cover AUTHORITY, decoupled from RAWG: keyless,
            // so it defaults ON (BlankFallback keeps a concrete CDN base even if the declared default is blank).
            // SteamCoversDisabled forces RAWG-only (the offline smoke sets it so cover stays null).
            SteamCdnBaseUrl = BlankFallback(
                s.SteamCdnBaseUrl,
                "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps").TrimEnd('/'),
            SteamCoversDisabled = s.SteamCoversDisabled ?? false,
            // Periodic refresh of the cover/metadata cache (in-process, off the request path). Weekly by
            // default; runs at a configurable local hour (a quiet window). Clamped: interval >= 0 (0 disables
            // the periodic wake), hour into 0..23.
            LibraryRefreshIntervalDays = Math.Max(0, s.LibraryRefreshIntervalDays ?? 7),
            LibraryRefreshHour = Math.Clamp(s.LibraryRefreshHour ?? 6, 0, 23),
            // Blueprint in-memory cache TTL (background refresh interval). Floor 10s.
            BlueprintCacheTtlSeconds = Math.Max(10, s.BlueprintCacheTtlSeconds ?? 60),
            // Instance in-memory cache TTL (background refresh interval). Floor 10s.
            InstanceCacheTtlSeconds = Math.Max(10, s.InstanceCacheTtlSeconds ?? 60),

            // File browser. Entry cap is a frontend-render bound; edit ceiling guards the
            // editor against megabyte blobs. Clamped sane: at least 1 entry, at least 1 KiB.
            FilesMaxEntries = Math.Max(1, s.FilesMaxEntries ?? 200),
            FilesMaxEditBytes = Math.Max(1024, s.FilesMaxEditBytes ?? 2 * 1024 * 1024),
            BlueprintMaxEditBytes = Math.Max(1024, s.BlueprintMaxEditBytes ?? 256 * 1024),

            // Host logs (GET /hosts/{id}/logs). The unit map defaults to the host's leaf services; override
            // LogSources on a host whose units are named differently. journalctl resolves via PATH.
            LogSources = ParseLogSources(s.LogSources),
            JournalctlPath = BlankFallback(s.JournalctlPath, "journalctl"),
            SystemctlPath = BlankFallback(s.SystemctlPath, "systemctl"),
            LogReadTimeoutMs = Math.Max(500, s.LogReadTimeoutMs ?? 5000),

            // Leaf runtime config. The override dir lives in the API's StateDirectory by default
            // (unprivileged write); the canary window is floored at 2s so a bad value can't be declared good
            // before the leaf has even restarted.
            LeafOverridesDir = BlankFallback(s.LeafOverridesDir, "/var/lib/kgsm-api/leaf-overrides"),
            LeafApplyCanaryMs = Math.Max(2000, s.LeafApplyCanaryMs ?? 15000),
            LeafDescriptorDir = BlankFallback(s.LeafDescriptorDir, "/var/lib/kgsm/leaves"),
            LeafDropInDir = BlankFallback(s.LeafDropInDir, "/etc/systemd/system"),

            // Cluster message bus foundation. Blank secret => ClusterEnabled false => the cluster
            // service token seam stays dormant. NodeId defaults to the already-resolved HostId,
            // not Environment.MachineName a second time.
            ClusterSecret = Defaulted(s.ClusterSecret, ""),
            ClusterSecretPrevious = Defaulted(s.ClusterSecretPrevious, ""),
            NodeId = Clean(s.NodeId) ?? hostId,

            // The outbox drainer + GC cadence/TTL. The retention floor is computed from the
            // just-parsed retry TTL (Math.Max(ClusterRetryTtlDays + 1, …)) so a custom TTL still gets a
            // sane retention margin, not a fixed constant that could undercut it.
            ClusterDrainMs = Math.Max(250, s.ClusterDrainMs ?? 1000),
            ClusterRetryTtlDays = clusterRetryTtlDays,
            ClusterRetentionDays = Math.Max(clusterRetryTtlDays + 1, s.ClusterRetentionDays ?? 30),
            ClusterGcMs = Math.Max(60000, s.ClusterGcMs ?? 600000),

            // Membership gossip. Advertised/gossip URLs default blank (honest: a node that doesn't
            // know its own client address just doesn't advertise it). Intervals share the drainer's
            // clamp-to-a-floor posture so a fat-fingered tiny value can't spin the loop.
            ClusterAdvertiseUrl = Defaulted(s.ClusterAdvertiseUrl, ""),
            ClusterGossipUrl = Defaulted(s.ClusterGossipUrl, ""),
            ClusterGossipMs = Math.Max(250, s.ClusterGossipMs ?? 5000),
            ClusterPollMs = Math.Max(250, s.ClusterPollMs ?? 10000),
            ClusterSuspectMs = Math.Max(1000, s.ClusterSuspectMs ?? 30000),
            ClusterReapMs = Math.Max(1000, s.ClusterReapMs ?? 300000),

            // Auth. On by default; the dev escape hatch is the only way to the old open window.
            AuthDisabled = s.AuthDisabled ?? false,
            SigningKey = Defaulted(s.SigningKey, ""),
            OAuth = auth,
            DiscordRedirectUri = Defaulted(s.DiscordRedirectUri, ""),
            AuthFrontendUrl = Defaulted(s.AuthFrontendUrl, ""),
            // Blank falls back to the shared host location rather than to a file beside this API's
            // own database: accounts belong to the host, and a private copy would be a second set of
            // users the assistant beside us cannot see.
            UsersDbPath = BlankFallback(s.UsersDbPath, UserStoreOptions.DefaultPath),
            AuthorityCacheSeconds = Math.Max(0, s.AuthorityCacheSeconds ?? 5),
            PendingUserCap = Math.Max(1, s.PendingUserCap ?? 32),
            PendingUserTtlDays = Math.Max(1, s.PendingUserTtlDays ?? 14),
            ReauthWindowMinutes = Math.Max(1, s.ReauthWindowMinutes ?? 5),

            // Sessions. SessionsEnabled is the default-ON twin of the written SessionsDisabled
            // (a disable-flag with inverted polarity). The cache TTL bounds the revocation lag;
            // the GC cadence bounds the table; the refresh-absolute-days mirrors the JWT refresh
            // TTL (the two must stay in lockstep — see the property's doc).
            SessionsEnabled = !(s.SessionsDisabled ?? false),
            SessionsCacheTtlMs = Math.Max(500, s.SessionsCacheTtlMs ?? 5000),
            SessionsGcMs = Math.Max(60000, s.SessionsGcMs ?? 600000),
            SessionsRefreshAbsoluteDays = Math.Max(1, s.SessionsRefreshAbsoluteDays ?? 30),
        };
    }

    // The default RAWG image cache dir: a covers/ subdir beside the SQLite DB (so it inherits the
    // StateDirectory the systemd unit sets via Api__DbPath). With no DB path (the bare default
    // "kgsm-api.db" — relative, no dir) it falls back to a relative "covers" dir in the cwd.
    private static string DefaultCacheDir(string? dbPath)
    {
        string? dir = string.IsNullOrWhiteSpace(dbPath) ? null : Path.GetDirectoryName(dbPath.Trim());
        return string.IsNullOrEmpty(dir) ? "covers" : Path.Combine(dir, "covers");
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
    // (e.g. a filesystem path Path.* will throw on), where the declared default is a blank "".
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
    // never drift on the unit set. Api__LogSources still overrides this map for a host whose units are
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
