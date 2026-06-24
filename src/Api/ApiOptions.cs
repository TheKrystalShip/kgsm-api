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

    /// <summary>
    /// Whether the kgsm engine is configured (a non-empty <see cref="KgsmPath"/>). Unlike a leaf
    /// capability, the engine is assumed present — <c>false</c> is a surfaced misconfiguration.
    /// </summary>
    public bool KgsmProvisioned => !string.IsNullOrWhiteSpace(KgsmPath);

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
            // For socket/url defaults we distinguish "unset" (use the default) from
            // "set to empty" (deliberately mark the capability absent): a present-but-empty
            // value stays empty, an absent key falls back to the standard path.
            MonitorSocketPath = Defaulted(configuration["KGSM_API_MONITOR_SOCKET"], "/run/kgsm-monitor.sock"),
            WatchdogSocketPath = Defaulted(configuration["KGSM_API_WATCHDOG_SOCKET"], "/run/kgsm-watchdog/control.sock"),
            AssistantBaseUrl = Defaulted(configuration["KGSM_API_ASSISTANT_URL"], ""),
            AssistantRelaySecret = Defaulted(configuration["KGSM_API_ASSISTANT_RELAY_SECRET"], ""),
            // Opt-in (blank = absent): the firewall authority is a separate optional install.
            FirewallSocketPath = Defaulted(configuration["KGSM_API_FIREWALL_SOCKET"], ""),
            KgsmPath = Defaulted(configuration["KGSM_API_KGSM_PATH"], "/usr/bin/kgsm"),
            KgsmSocketPath = Defaulted(configuration["KGSM_API_KGSM_SOCKET"], "/usr/share/kgsm/kgsm.sock"),

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

            // File browser (Tier 3 #12). Entry cap is a frontend-render bound; edit ceiling guards the
            // editor against megabyte blobs. Clamped sane: at least 1 entry, at least 1 KiB.
            FilesMaxEntries = Math.Max(1, IntOr(configuration["KGSM_API_FILES_MAX_ENTRIES"], 200)),
            FilesMaxEditBytes = Math.Max(1024, LongOr(configuration["KGSM_API_FILES_MAX_EDIT_BYTES"], 2 * 1024 * 1024)),

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

    // The default RAWG image cache dir: a covers/ subdir beside the SQLite DB (so it inherits the
    // StateDirectory the systemd unit sets via KGSM_API_DB). With no DB path (the bare default
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
    // (e.g. a filesystem path Path.* will throw on), where the appsettings.json default is a blank "".
    private static string BlankFallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    // Truthy env flag: "1"/"true"/"yes"/"on" (case-insensitive) -> true; anything else -> false.
    private static bool Flag(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    // Comma-separated list (role ids), trimmed and de-blanked. Empty/unset -> empty list.
    private static IReadOnlyList<string> Csv(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
