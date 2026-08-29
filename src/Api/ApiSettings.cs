using TheKrystalShip.KGSM.LeafConfig;

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
[LeafSection(Section)]
public sealed class ApiSettings
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string Section = "Api";

    // ── Identity ──────────────────────────────────────────────────────
    /// <summary>Stable identity of this host. Blank takes the OS machine name.</summary>
    /// <panel>Identity this host is known by across the whole ecosystem — the audience its tokens are
    /// minted for, and the key its stored metrics and history hang off. Empty uses the machine
    /// name.</panel>
    [LeafField("hostId", "Host id", Group = "identity", Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? HostId { get; set; }

    /// <summary>Human-friendly label. Blank falls back to the host id.</summary>
    /// <panel>Friendly name shown for this host in the panel. Empty falls back to the host id.</panel>
    [LeafField("hostLabel", "Display name", Group = "identity", NoDefault = true)]
    public string? HostLabel { get; set; }

    /// <summary>Deployment region, an arbitrary free string. Blank stays unset rather than guessing one.</summary>
    /// <panel>Where this host is, shown alongside it in a multi-host view. Free text, purely descriptive.</panel>
    [LeafField("region", "Region", Group = "identity", NoDefault = true)]
    public string? Region { get; set; }

    /// <summary>This node's cluster identity. Blank takes the host id.</summary>
    /// <panel>Identity this host uses inside a cluster. Empty uses the host id.</panel>
    [LeafField("nodeId", "Cluster node id", Group = "identity", Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? NodeId { get; set; }

    /// <summary>Public base URL absolute cover/hero URLs are built from. Blank derives it from the request.</summary>
    /// <panel>Externally reachable address of this panel, used where a link has to work from outside the
    /// host. Empty means links are built from the incoming request instead.</panel>
    [LeafField("publicBaseUrl", "Public base URL", Group = "identity", NoDefault = true)]
    public string? PublicBaseUrl { get; set; }


    // ── Leaf connections ──────────────────────────────────────────────────────
    /// <summary>kgsm-monitor metrics socket. Blank reports the metrics capability absent.</summary>
    /// <panel>The metrics daemon's socket. Wrong and the metrics capability reports unavailable rather
    /// than guessing at numbers.</panel>
    [LeafField("monitorSocket", "Monitor socket", Group = "leaves", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? MonitorSocketPath { get; set; }

    /// <summary>kgsm-watchdog control socket. Blank reports the watchdog capability absent.</summary>
    /// <panel>The supervisor's control socket, which every start and stop of a native server goes through.</panel>
    [LeafField("watchdogSocket", "Watchdog socket", Group = "leaves", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? WatchdogSocketPath { get; set; }

    /// <summary>Assistant base URL. Blank reports the assistant absent.</summary>
    /// <panel>Where the assistant serves. Empty means this host has no assistant and the chat surface
    /// reports it absent.</panel>
    [LeafField("assistantUrl", "Assistant URL", Group = "leaves", Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? AssistantBaseUrl { get; set; }

    /// <summary>Public origin browsers reach the assistant on. Blank reports no browser route.</summary>
    /// <panel>The assistant's own public address, e.g. <c>https://assistant.example.com</c>. The Control
    /// Panel's chat talks to the assistant directly on it. Empty means the chat reports the assistant
    /// unreachable from the browser rather than routing through this API.</panel>
    [LeafField("assistantPublicUrl", "Assistant public URL", Group = "leaves", Risk = LeafRisk.Wiring,
        NoDefault = true)]
    public string? AssistantPublicUrl { get; set; }

    /// <summary>Shared secret for the assistant turn relay (X-Relay-Secret). Blank sends none.</summary>
    /// <panel>Shared secret letting this API ask the assistant on a signed-in user's behalf. It has to
    /// match the assistant's own relay secret.</panel>
    [LeafField("assistantRelaySecret", "Assistant relay secret", Group = "leaves",
        Type = LeafType.Secret, Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? AssistantRelaySecret { get; set; }

    /// <summary>File the host's relay secret is kept in, read when no secret is set here.</summary>
    /// <panel>Where this host keeps the secret its surfaces prove themselves to each other with. The
    /// first surface to look for it creates it, so nothing has to be typed in; point this elsewhere on
    /// a host that keeps its state somewhere other than /var/lib.</panel>
    [LeafField("relaySecretPath", "Relay secret file", Group = "leaves", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? RelaySecretPath { get; set; }

    /// <summary>kgsm-firewall control socket. Blank reports the ports surface absent.</summary>
    /// <panel>The firewall authority's socket. Empty means port state is reported as unknown rather than
    /// assumed closed.</panel>
    [LeafField("firewallSocket", "Firewall socket", Group = "leaves", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? FirewallSocketPath { get; set; }

    /// <summary>kgsm-scheduler status socket. Blank reports the scheduler absent.</summary>
    /// <panel>The scheduler's status socket. Empty means the next scheduled restart shows as unknown.</panel>
    [LeafField("schedulerSocket", "Scheduler socket", Group = "leaves", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? SchedulerSocketPath { get; set; }

    /// <summary>kgsm-scheduler control socket. Blank means a scheduled restart cannot be postponed from
    /// here — the panel offers nothing rather than offering something that fails.</summary>
    /// <panel>The scheduler's control socket, which postponing a scheduled restart goes through. Empty
    /// means restarts can be seen but not deferred.</panel>
    [LeafField("schedulerControlSocket", "Scheduler control socket", Group = "leaves", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? SchedulerControlSocketPath { get; set; }

    /// <summary>kgsm-reactor status socket. Blank reports the reactor absent.</summary>
    /// <panel>The reactor's status socket, which its health is read from. Empty means the Services page
    /// shows systemd liveness only.</panel>
    [LeafField("reactorSocket", "Reactor socket", Group = "leaves", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? ReactorSocketPath { get; set; }

    /// <summary>kgsm-speech control socket. The leaf's own default is the standard path, and the socket
    /// file's presence — not this value — is what says the leaf is installed.</summary>
    /// <panel>The speech engine's socket, which the Services page reads its engine state from. The socket
    /// is bound whether or not the daemon is running, so a host without kgsm-speech simply has no file
    /// there and the page says so.</panel>
    [LeafField("speechSocket", "Speech socket", Group = "leaves", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? SpeechSocketPath { get; set; }

    /// <summary>kgsm-bot status socket. Blank reports the bot's status surface absent.</summary>
    /// <panel>The Discord bot's status socket, which the Services page reads its gateway and channel
    /// state from. Empty means the bot page shows systemd liveness only.</panel>
    [LeafField("botSocket", "Discord bot socket", Group = "leaves", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? BotSocketPath { get; set; }


    // ── KGSM engine ──────────────────────────────────────────────────────
    /// <summary>Path to the host's kgsm entrypoint. Blank is a misconfiguration, not a capability toggle.</summary>
    /// <panel>Path to the KGSM executable. Everything this API knows about servers, blueprints and
    /// configuration is read through it.</panel>
    [LeafField("kgsmPath", "KGSM executable", Group = "engine", Type = LeafType.Path, Risk = LeafRisk.Wiring)]
    public string? KgsmPath { get; set; }

    /// <summary>The engine's append-only event journal the audit consumer tails.</summary>
    /// <panel>Directory holding the engine's append-only event journal, which the API reads to see what
    /// the engine did. Read-only and shared with every other consumer — nothing needs configuring
    /// on the engine side.</panel>
    [LeafField("kgsmJournalDir", "KGSM event journal", Group = "engine", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? KgsmJournalDir { get; set; }

    /// <summary>This API's OWN append-only event journal. Blank uses events/ beside the DB.</summary>
    /// <panel>Where this API records what it did itself — signing people in, changing an account's
    /// authority, editing a file. Its own journal, not the engine's: every component on this host
    /// records its own actions, and the audit page is the merge of all of them. Moving it leaves the
    /// existing history behind at the old path.</panel>
    [LeafField("eventJournalDir", "Panel event journal", Group = "engine", Type = LeafType.Path,
        Risk = LeafRisk.Destructive, NoDefault = true)]
    public string? EventJournalDir { get; set; }

    /// <summary>Where per-service state directories live, scanned to find each producer's journal.</summary>
    /// <panel>The directory holding each KGSM service's own state directory. The audit page is the merge
    /// of every event journal found under it, so pointing it somewhere else changes which components'
    /// history this host can show.</panel>
    [LeafField("journalStateRoot", "Service state root", Group = "engine", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? JournalStateRoot { get; set; }

    /// <summary>systemctl binary the Services board shells. Resolved via PATH by default.</summary>
    /// <panel>Which systemctl to run when reading a leaf's state or restarting it.</panel>
    [LeafField("systemctlPath", "systemctl path", Group = "engine", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? SystemctlPath { get; set; }

    /// <summary>journalctl binary the host-logs surface shells. Resolved via PATH by default.</summary>
    /// <panel>Which journalctl to run when reading a leaf's logs.</panel>
    [LeafField("journalctlPath", "journalctl path", Group = "engine", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? JournalctlPath { get; set; }


    // ── Polling ──────────────────────────────────────────────────────
    /// <summary>How often the servers topic re-reads the roster from kgsm. Floor 1000.</summary>
    /// <panel>How often the list of servers and their run state is re-read from the engine.</panel>
    [LeafField("domainPollMs", "Server list refresh", Group = "polling", Min = 1000, Unit = "ms")]
    public int? DomainPollMs { get; set; }

    /// <summary>How often the metrics topics scrape the monitor for the live tick. Floor 250.</summary>
    /// <panel>How often metrics are scraped from the monitor and pushed to connected panels.</panel>
    [LeafField("metricsPollMs", "Metrics refresh", Group = "polling", Min = 250, Unit = "ms")]
    public int? MetricsPollMs { get; set; }

    /// <summary>How often the Services board re-reads leaf liveness. Floor 2000.</summary>
    /// <panel>How often each leaf's systemd state and health are re-read for the Services board.</panel>
    [LeafField("servicesPollMs", "Services refresh", Group = "polling", Min = 2000, Unit = "ms")]
    public int? ServicesPollMs { get; set; }

    /// <summary>How often each instance's backups are re-scanned. Floor 30000.</summary>
    /// <panel>How often each server's backups are re-scanned for the panel's backup KPIs. A backup taken
    /// or restored through KGSM refreshes that server immediately, so this only bounds how quickly
    /// other changes are noticed.</panel>
    [LeafField("backupScanPollMs", "Backup scan interval", Group = "polling", Min = 30000, Unit = "ms")]
    public int? BackupScanPollMs { get; set; }

    /// <summary>Instance in-memory cache TTL. Floor 10.</summary>
    /// <panel>How long a server's detail is reused before being re-read from the engine.</panel>
    [LeafField("instanceCacheTtlSec", "Server detail cache", Group = "polling", Min = 10, Unit = "s")]
    public int? InstanceCacheTtlSeconds { get; set; }

    /// <summary>Blueprint in-memory cache TTL. Floor 10.</summary>
    /// <panel>How long the blueprint catalog is reused before being re-read from the engine.</panel>
    [LeafField("blueprintCacheTtlSec", "Blueprint cache", Group = "polling", Min = 10, Unit = "s")]
    public int? BlueprintCacheTtlSeconds { get; set; }


    // ── Authentication ──────────────────────────────────────────────────────
    /// <summary>Dev escape hatch: authenticate every request as a synthetic admin.</summary>
    /// <panel>Turns off every authentication check and treats each caller as an administrator. Intended
    /// for local development only — on a reachable host it hands full control of every server to
    /// anyone who can connect.</panel>
    [LeafField("authDisabled", "Disable authentication", Group = "auth", Risk = LeafRisk.Destructive)]
    public bool? AuthDisabled { get; set; }

    /// <summary>The principal every request is attributed to while authentication is off.</summary>
    /// <panel>Who the audit log names for work done while authentication is disabled, written
    /// <c>provider:name</c> (for example <c>local:claude</c>). Required whenever authentication is off:
    /// without it the host refuses to start, because every action taken through an open door would
    /// otherwise land in the record under a name nobody chose.</panel>
    [LeafField("disabledAuthActor", "Actor while auth is disabled", Group = "auth", NoDefault = true)]
    public string? DisabledAuthActor { get; set; }

    /// <summary>HMAC signing key for session JWTs. Blank means the host generates and keeps its own.</summary>
    /// <panel>Secret every session token is signed with. Leave it blank and this host generates one for
    /// itself on first start and reuses it forever after. Changing it signs everyone out at once, which
    /// is also how you revoke every token in a hurry.</panel>
    [LeafField("signingKey", "Token signing key", Group = "auth", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? SigningKey { get; set; }

    /// <summary>This host's OAuth redirect URI; must match the app registry.</summary>
    /// <panel>Where Discord sends someone back to after they approve. It has to match the redirect
    /// registered on the Discord application exactly.</panel>
    [LeafField("discordRedirectUri", "Sign-in redirect", Group = "auth", Risk = LeafRisk.Wiring,
        NoDefault = true)]
    public string? DiscordRedirectUri { get; set; }

    /// <summary>SPA origin the OAuth callback hands the session back to. Blank returns JSON.</summary>
    /// <panel>Where to send someone once sign-in completes. Empty returns them to where they started.</panel>
    [LeafField("authFrontendUrl", "Panel URL", Group = "auth", NoDefault = true)]
    public string? AuthFrontendUrl { get; set; }

    /// <summary>The host's shared KGSM account store. Blank falls back to /var/lib/kgsm/auth/users.db.</summary>
    /// <panel>The file this host keeps its KGSM accounts and passwords in. Every KGSM service on the
    /// host reads the same file, so pointing this somewhere else signs people out of the panel while
    /// leaving the assistant on the old one.</panel>
    [LeafField("usersDbPath", "Account store", Group = "auth", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? UsersDbPath { get; set; }

    /// <summary>How long a resolved tier is reused before the store is read again. Floor 0.</summary>
    /// <panel>How long someone's authority is reused before it is looked up again. This is how long
    /// after an admin changes what a person may do that their next request can still go through at the
    /// old level.</panel>
    [LeafField("authorityCacheSeconds", "Authority lookup cache", Group = "auth", Min = 0, Unit = "s")]
    public int? AuthorityCacheSeconds { get; set; }

    /// <summary>The most accounts awaiting approval this host will hold at once. Floor 1.</summary>
    /// <panel>How many people can be waiting for approval at the same time. Anyone who signs in through
    /// an identity provider and has no account here yet becomes one of them, so this is the ceiling on
    /// what a stranger can add to this host.</panel>
    [LeafField("pendingUserCap", "Accounts awaiting approval", Group = "auth", Min = 1)]
    public int? PendingUserCap { get; set; }

    /// <summary>How long an unapproved, self-provisioned account survives unattended, in days. Floor 1.</summary>
    /// <panel>How long someone waiting for approval stays on the list before being forgotten. Only ever
    /// removes an account that arrived on its own and was never approved — one an administrator created
    /// by hand waits as long as it waits. Set this to however long approving somebody may realistically
    /// take here, because past it they are gone and have to sign up again.</panel>
    [LeafField("pendingUserTtlDays", "Approval request lifetime", Group = "auth", Min = 1, Unit = "days")]
    public int? PendingUserTtlDays { get; set; }

    /// <summary>Whether anonymous callers may create their own account through <c>POST /auth/register</c>.</summary>
    /// <panel>Lets people create their own account on this host instead of waiting for an administrator
    /// to make one. A new account still holds nothing until it is approved, so this decides who may
    /// join the queue — not who gets in. Off unless you turn it on: a host reachable from the internet
    /// with sign-up open is a host strangers can fill the approval list on.</panel>
    [LeafField("allowSelfRegistration", "Allow people to sign themselves up", Group = "auth")]
    public bool? AllowSelfRegistration { get; set; }

    /// <summary>Sign-in and sign-up attempts one caller may make per minute. Floor 1.</summary>
    /// <panel>How many times a minute one address may try to sign in or sign up before this host stops
    /// answering it. Raise it if several people here share one internet connection, since to this host
    /// they all look like the same caller.</panel>
    [LeafField("anonymousRateLimit", "Sign-in attempts per minute", Group = "auth", Min = 1)]
    public int? AnonymousRateLimit { get; set; }

    /// <summary>How long a proved credential keeps a session allowed to change what proves it. Floor 1.</summary>
    /// <panel>How long after entering their password someone may attach or detach a sign-in method.
    /// Past it they are asked for it again — a borrowed unlocked laptop should not be able to attach a
    /// permanent way back in.</panel>
    [LeafField("reauthWindowMinutes", "Re-authentication window", Group = "auth", Min = 1, Unit = "min")]
    public int? ReauthWindowMinutes { get; set; }


    // ── Sessions ──────────────────────────────────────────────────────
    /// <summary>Turns the session registry inert, leaving revocation unenforceable.</summary>
    /// <panel>Stops tracking live sessions. Signing in still works, but a token can no longer be revoked
    /// before it expires and the Active Sessions list goes empty.</panel>
    [LeafField("sessionsDisabled", "Disable the session registry", Group = "sessions",
        Risk = LeafRisk.Destructive)]
    public bool? SessionsDisabled { get; set; }

    /// <summary>Per-request session validator cache TTL, the accepted revocation lag. Floor 500.</summary>
    /// <panel>How long a session's validity is reused before it is checked again. Longer means fewer
    /// lookups and a longer wait before a revoked session actually stops working.</panel>
    [LeafField("sessionsCacheTtlMs", "Session lookup cache", Group = "sessions", Min = 500, Unit = "ms")]
    public int? SessionsCacheTtlMs { get; set; }

    /// <summary>How often expired session rows are deleted. Floor 60000.</summary>
    /// <panel>How often expired sessions are cleared out of the registry.</panel>
    [LeafField("sessionsGcMs", "Expired session cleanup", Group = "sessions", Min = 60000, Unit = "ms")]
    public int? SessionsGcMs { get; set; }

    /// <summary>The sliding session refresh window in days. Floor 1.</summary>
    /// <panel>How long a session can keep renewing itself before the user has to sign in again, however
    /// active they are.</panel>
    [LeafField("sessionsRefreshAbsoluteDays", "Maximum session age", Group = "sessions", Min = 1, Unit = "days")]
    public int? SessionsRefreshAbsoluteDays { get; set; }


    // ── Game library & cover art ──────────────────────────────────────────────────────
    /// <summary>RAWG.io API key. Blank no-ops the hydration worker.</summary>
    /// <panel>Key for the game-metadata service used for descriptions and artwork. Empty means the panel
    /// shows what the blueprints carry and nothing more.</panel>
    [LeafField("rawgApiKey", "RAWG API key", Group = "library", Type = LeafType.Secret, NoDefault = true)]
    public string? RawgApiKey { get; set; }

    /// <summary>Directory self-hosted cover/hero images are written to. Blank uses covers/ beside the DB.</summary>
    /// <panel>Where downloaded artwork is kept. Pointing it elsewhere abandons what is already downloaded
    /// and fetches everything again.</panel>
    [LeafField("rawgCacheDir", "Artwork cache directory", Group = "library", Type = LeafType.Path,
        Risk = LeafRisk.Destructive, NoDefault = true)]
    public string? RawgCacheDir { get; set; }

    /// <summary>Base URL for the keyless Steam library-capsule cover source.</summary>
    /// <panel>Where Steam cover art is fetched from.</panel>
    [LeafField("steamCdnBase", "Steam art base URL", Group = "library")]
    public string? SteamCdnBaseUrl { get; set; }

    /// <summary>Off switch for the keyless Steam cover source, leaving RAWG only.</summary>
    /// <panel>Stops using Steam for cover art, leaving the metadata service as the only source.</panel>
    [LeafField("steamCoversDisabled", "Disable Steam cover art", Group = "library")]
    public bool? SteamCoversDisabled { get; set; }

    /// <summary>Re-fetch cover/metadata older than this many days. 0 disables the periodic wake.</summary>
    /// <panel>How often the game library's metadata and artwork are refreshed. Zero turns the scheduled
    /// refresh off.</panel>
    [LeafField("libraryRefreshIntervalDays", "Library refresh interval", Group = "library", Min = 0,
        Unit = "days")]
    public int? LibraryRefreshIntervalDays { get; set; }

    /// <summary>Local hour the periodic library refresh wakes. Clamped to 0..23.</summary>
    /// <panel>Hour of the day the refresh runs, in this host's local time.</panel>
    [LeafField("libraryRefreshHour", "Library refresh hour", Group = "library", Min = 0, Max = 23,
        DependsOn = "libraryRefreshIntervalDays")]
    public int? LibraryRefreshHour { get; set; }


    // ── File & blueprint editing ──────────────────────────────────────────────────────
    /// <summary>Max directory entries one file listing returns before truncating. Floor 1.</summary>
    /// <panel>How many entries one directory listing returns before it is cut short.</panel>
    [LeafField("filesMaxEntries", "Directory listing limit", Group = "files", Min = 1)]
    public int? FilesMaxEntries { get; set; }

    /// <summary>Max file size the editor will open or save. Floor 1024.</summary>
    /// <panel>How large a server file may be and still be opened in the editor.</panel>
    [LeafField("filesMaxEditBytes", "Editable file size limit", Group = "files", Min = 1024, Unit = "bytes")]
    public long? FilesMaxEditBytes { get; set; }

    /// <summary>Max blueprint-file size the library editor will open or save. Floor 1024.</summary>
    /// <panel>How large a blueprint may be and still be opened in the editor.</panel>
    [LeafField("blueprintMaxEditBytes", "Editable blueprint size limit", Group = "files", Min = 1024,
        Unit = "bytes")]
    public long? BlueprintMaxEditBytes { get; set; }


    // ── Log reading ──────────────────────────────────────────────────────
    /// <summary>Host-log source map as source:unit pairs. Blank derives it from the leaf catalog.</summary>
    /// <panel>Which journal units the host's Logs tab reads. Empty uses the known leaf units.</panel>
    [LeafField("logSources", "Log sources", Group = "logs", Type = LeafType.Csv, NoDefault = true)]
    public string? LogSources { get; set; }

    /// <summary>How long a journal read may take before giving up. Floor 500.</summary>
    /// <panel>How long to wait for the journal before giving up on a log request.</panel>
    [LeafField("logReadTimeoutMs", "Log read timeout", Group = "logs", Min = 500, Unit = "ms")]
    public int? LogReadTimeoutMs { get; set; }


    // ── Leaf configuration ──────────────────────────────────────────────────────
    /// <summary>Directory the per-leaf override env files render to.</summary>
    /// <panel>Where this API writes each leaf's configuration override. It has to match what the leaves'
    /// drop-ins load, or every change is written and never read.</panel>
    [LeafField("leafOverridesDir", "Override directory", Group = "leafconfig", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? LeafOverridesDir { get; set; }

    /// <summary>Shared directory the leaf config descriptors are discovered in.</summary>
    /// <panel>Where leaves publish what they can be configured with. Pointing it elsewhere leaves every
    /// leaf's configuration page empty.</panel>
    [LeafField("leafDescriptorDir", "Descriptor directory", Group = "leafconfig", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string? LeafDescriptorDir { get; set; }

    /// <summary>One unit-file directory to search instead of systemd's own roots. Blank searches all
    /// of them, in systemd's order.</summary>
    /// <panel>One directory to look for unit files and their drop-ins in, instead of everywhere systemd
    /// looks. Empty searches all of them, which is what finds a leaf's unit whether its package installed
    /// it or a deploy script did.</panel>
    [LeafField("leafDropInDir", "Drop-in directory", Group = "leafconfig", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? LeafDropInDir { get; set; }

    /// <summary>How long a leaf's health is watched after a config restart before the change is declared good. Floor 2000.</summary>
    /// <panel>How long a leaf is watched after a configuration change before the change is rolled back as
    /// unhealthy.</panel>
    [LeafField("leafApplyCanaryMs", "Post-change health window", Group = "leafconfig", Min = 2000, Unit = "ms")]
    public int? LeafApplyCanaryMs { get; set; }


    // ── Cluster ──────────────────────────────────────────────────────
    /// <summary>The shared cluster HMAC secret. Blank means this host is not part of a cluster.</summary>
    /// <panel>Shared secret every host in the cluster authenticates to the others with. Empty means this
    /// host is standalone.</panel>
    [LeafField("clusterSecret", "Cluster secret", Group = "cluster", Type = LeafType.Secret,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? ClusterSecret { get; set; }

    /// <summary>The previous cluster secret, accepted during a rotation overlap.</summary>
    /// <panel>The secret being rotated away from, still accepted so the cluster does not split apart
    /// mid-rotation.</panel>
    [LeafField("clusterSecretPrevious", "Previous cluster secret", Group = "cluster",
        Type = LeafType.Secret, Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? ClusterSecretPrevious { get; set; }

    /// <summary>The client URL this node advertises to peers. Blank advertises none.</summary>
    /// <panel>Address other hosts should reach this one at. Empty means this host does not advertise
    /// itself.</panel>
    [LeafField("clusterAdvertiseUrl", "Advertised URL", Group = "cluster", Risk = LeafRisk.Wiring,
        NoDefault = true)]
    public string? ClusterAdvertiseUrl { get; set; }

    /// <summary>The gossip URL this node advertises to peers. Blank advertises none.</summary>
    /// <panel>A peer to learn the rest of the cluster from. Empty means this host waits to be contacted.</panel>
    [LeafField("clusterGossipUrl", "Peer URL", Group = "cluster", Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? ClusterGossipUrl { get; set; }

    /// <summary>How often membership gossip is pushed. Floor 250.</summary>
    /// <panel>How often this host exchanges what it knows with a peer.</panel>
    [LeafField("clusterGossipMs", "Peer exchange interval", Group = "cluster", Min = 250, Unit = "ms")]
    public int? ClusterGossipMs { get; set; }

    /// <summary>How often peers are polled for membership. Floor 250.</summary>
    /// <panel>How often each peer's state is re-read.</panel>
    [LeafField("clusterPollMs", "Peer refresh interval", Group = "cluster", Min = 250, Unit = "ms")]
    public int? ClusterPollMs { get; set; }

    /// <summary>Silence after which a peer is marked suspect. Floor 1000.</summary>
    /// <panel>How long a silent peer is tolerated before it is treated as suspect.</panel>
    [LeafField("clusterSuspectMs", "Peer suspect timeout", Group = "cluster", Min = 1000, Unit = "ms")]
    public int? ClusterSuspectMs { get; set; }

    /// <summary>Silence after which a suspect peer is reaped. Floor 1000.</summary>
    /// <panel>How long a suspect peer is kept before it is dropped from the cluster.</panel>
    [LeafField("clusterReapMs", "Peer removal timeout", Group = "cluster", Min = 1000, Unit = "ms")]
    public int? ClusterReapMs { get; set; }

    /// <summary>How often the cluster outbox drainer ticks. Floor 250.</summary>
    /// <panel>How often queued messages for other hosts are sent.</panel>
    [LeafField("clusterDrainMs", "Outbox drain interval", Group = "cluster", Min = 250, Unit = "ms")]
    public int? ClusterDrainMs { get; set; }

    /// <summary>How often the cluster retention GC sweeps. Floor 60000.</summary>
    /// <panel>How often delivered and expired cluster messages are cleared out.</panel>
    [LeafField("clusterGcMs", "Cluster cleanup interval", Group = "cluster", Min = 60000, Unit = "ms")]
    public int? ClusterGcMs { get; set; }

    /// <summary>Days a pending outbox row may keep retrying before it is dead-lettered. Floor 1.</summary>
    /// <panel>How long a message for an unreachable host keeps being retried before it is given up on.</panel>
    [LeafField("clusterRetryTtlDays", "Undelivered message lifetime", Group = "cluster", Min = 1, Unit = "days")]
    public int? ClusterRetryTtlDays { get; set; }

    /// <summary>Days a delivered or dead outbox row is kept. Clamped above the retry TTL so a late redelivery is still deduped.</summary>
    /// <panel>How long delivered cluster messages are kept before being deleted. Must outlast the retry
    /// window, or a message could be deleted while still being retried.</panel>
    [LeafField("clusterRetentionDays", "Cluster message retention", Group = "cluster", Min = 2,
        Unit = "days", Risk = LeafRisk.Destructive)]
    public int? ClusterRetentionDays { get; set; }


    // ── Storage ──────────────────────────────────────────────────────
    /// <summary>SQLite file for the API's own operational metadata.</summary>
    /// <panel>File holding this API's own records — the audit trail, live sessions, and per-host settings.
    /// Pointing it elsewhere starts empty and leaves the existing history behind.</panel>
    [LeafField("dbPath", "Database file", Group = "storage", Type = LeafType.Path, Risk = LeafRisk.Destructive)]
    public string? DbPath { get; set; }


    // ── General ──────────────────────────────────────────────────────
    /// <summary>HTTP bind address(es), semicolon-separated. Read before the host exists, so it is bound separately in Program.</summary>
    /// <panel>Addresses this API serves on. The Control Panel and every other surface reach it here.</panel>
    [LeafField("bindAddress", "Listen address", Group = "general", Risk = LeafRisk.Wiring)]
    public string? Urls { get; set; }

    /// <summary>Comma-separated CORS origin allowlist. Blank allows any origin, which is a dev-only posture.</summary>
    /// <panel>Exact origins a browser may call this API from. A panel served from an origin that is not
    /// listed is refused by the browser before the request arrives.</panel>
    [LeafField("corsOrigins", "Allowed browser origins", Group = "general", Type = LeafType.Csv,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string? CorsOrigins { get; set; }
}
