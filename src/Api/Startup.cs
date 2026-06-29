using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Infrastructure;
using TheKrystalShip.Api.Json;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.Api.Services.Files;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.Api.Services.Metrics;
using TheKrystalShip.KGSM.Extensions;

namespace TheKrystalShip.Api;

/// <summary>
/// Composition root for the API (classic ASP.NET Core <c>Startup</c> structure).
/// <see cref="ConfigureServices"/> registers DI; <see cref="Configure"/> builds the
/// middleware pipeline. The API is a per-host KGSM Control Panel aggregator on standard
/// JIT (controllers + EF Core — see PLAN.md §8 for the runtime/stack decision). It holds
/// NO domain DTOs yet; hosts/servers/metrics arrive in M1 behind the leaf wiring. M0's
/// job is the cross-team contract surface, frozen from architecture.html §6: the
/// <c>/api/v1</c> base path, the <c>{ "error": { code, message, details? } }</c> envelope,
/// camelCase + ISO-8601 UTC 'Z' JSON, a configurable CORS allowlist, and the auth
/// pipeline placeholder (filled at M4).
/// </summary>
public class Startup(IConfiguration configuration)
{
    private const string CorsPolicy = "frontend";

    public void ConfigureServices(IServiceCollection services)
    {
        // Controllers + the shared JSON conventions. ConfigureHttpJsonOptions applies the
        // same shaping to the HTTP path (WriteAsJsonAsync, used by the error writer) so
        // every response — from a controller or the pipeline — is camelCase / 'Z' identical.
        services.AddControllers()
            .AddJsonOptions(o => ApiJson.Configure(o.JsonSerializerOptions))
            // Suppress [ApiController]'s automatic mapping of client-error results
            // (NotFound(), BadRequest(), …) to RFC ProblemDetails, so they emit a bodyless
            // status that UseStatusCodePages renders as our frozen { error: … } envelope.
            // One error shape across the whole surface — never the framework's ProblemDetails.
            .ConfigureApiBehaviorOptions(o =>
            {
                o.SuppressMapClientErrors = true;
                // A model-binding/validation failure (malformed JSON, or a body field of the wrong type —
                // e.g. the M8 InstallRequest's typed reserved fields) is rejected by [ApiController] BEFORE
                // the action runs, and would otherwise emit the framework's ValidationProblemDetails. Route
                // it through the SAME frozen { error } envelope so every non-2xx is one shape (invariant #4 /
                // the CLAUDE.md typed-body gotcha). SuppressMapClientErrors lets this BadRequestObjectResult
                // pass through unmapped.
                o.InvalidModelStateResponseFactory = static _ =>
                    new BadRequestObjectResult(new ErrorEnvelope(new ErrorBody(
                        "bad_request",
                        "the request body is missing, malformed, or has a field of the wrong type")));
            });
        services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));

        // EF Core over SQLite — the API's own operational metadata (sessions M4, audit M5).
        // DB path env/config-driven; the file is created on first use. M0 uses EnsureCreated
        // via the _dbcheck probe; real schema evolution uses EF migrations from M5 on.
        string dbPath = configuration["KGSM_API_DB"] ?? "kgsm-api.db";
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

        // M1·a — leaf aggregation. ApiOptions consolidates the env config (host identity +
        // leaf endpoints). The monitor client owns the cached-latest scrape; the watchdog
        // client (kgsm-lib) is registered ONLY when provisioned so the capability probe can
        // resolve it optionally and report 'absent' when the leaf is not declared on this host.
        ApiOptions apiOptions = ApiOptions.FromConfiguration(configuration);
        services.AddSingleton(apiOptions);

        // M9 — metrics history (a SEPARATE metrics.db, D4). The MetricsDbContext is registered
        // alongside AppDbContext; its own EnsureCreated creates the sample+rollup tables. WAL +
        // auto_vacuum configured at creation time by MetricsHistoryStore.
        services.AddDbContext<MetricsDbContext>(options =>
            options.UseSqlite($"Data Source={apiOptions.MetricsHistoryDb}"));

        services.AddSingleton<MonitorClient>();
        services.AddSingleton<AssistantClient>();
        // Host identity: the static, runtime-derived card (OS/runtime/build/start-time), read once + cached;
        // and the editable overrides store (region/label) — its own EnsureCreated + CREATE TABLE IF NOT EXISTS
        // so the host_settings table appears on an existing DB without wiping the shared audit log.
        services.AddSingleton<HostIdentityProvider>();
        services.AddSingleton<HostSettingsStore>();
        services.AddSingleton<HostAggregator>();
        if (apiOptions.WatchdogProvisioned)
            services.AddKgsmWatchdogClient(apiOptions.WatchdogSocketPath);

        // M1·b — the servers join. kgsm-lib is the engine chokepoint (base, not a leaf): registered
        // when the engine is provisioned (it is, by default, at the packaged path). IInstanceService
        // is process-based — it shells KgsmPath — so the socket arg is only a registration formality
        // here (the event consumer that opens it lands at M5); the kgsm-lib singletons are lazy, so a
        // non-existent socket never blocks startup. The ServerAggregator resolves IInstanceService
        // per-request and degrades to an empty list (logged once) if the engine is unconfigured.
        if (apiOptions.KgsmProvisioned)
            services.AddKgsmServices(apiOptions.KgsmPath, apiOptions.KgsmSocketPath);

        // M6·b — ports. The firewall authority (kgsm-firewall) is OPT-IN like the assistant: its kgsm-lib
        // client is registered ONLY when its socket is configured (blank => firewall "absent"). It is
        // deliberately NOT added to the LeafHealthMonitor 2s poll — the daemon is socket-activated and
        // idle-exits, so a periodic probe would defeat that; NetworkAggregator probes it ON-DEMAND (detail
        // views + the open_ports verify), bounding each call, and reports liveness as the block-level
        // `firewall` status. A longer request timeout covers ufw mutation serialized behind the global lock.
        if (apiOptions.FirewallProvisioned)
            services.AddKgsmFirewallClient(o => { o.SocketPath = apiOptions.FirewallSocketPath; o.RequestTimeout = TimeSpan.FromSeconds(30); });
        // Always registered: it degrades to firewall:"absent"/null when the client isn't present, so the
        // server/host aggregators can depend on it unconditionally.
        services.AddSingleton<NetworkAggregator>();
        services.AddSingleton<ServerAggregator>();

        // M8·a — the installable-game catalog (GET /library). A blueprint scrape via kgsm-lib
        // IBlueprintService (resolved per-request, degrading to an empty catalog (logged once) when the engine
        // is unconfigured — the engine-is-base posture as ServerAggregator), joined with this host's cached
        // RAWG.io cover/metadata. RawgStore is the single reader/writer of the rawg_entry table (own DI scope
        // per op, like IntegrationStore); the LibraryAggregator reads it per-request, degrading cover/hero to
        // null INDEPENDENTLY of the blueprint read on a cache failure.
        services.AddSingleton<RawgStore>();
        services.AddSingleton<LibraryAggregator>();
        // The RAWG client is a typed HttpClient. RemoveAllLoggers() is load-bearing, NOT cosmetic (the same as
        // the Discord/Slack webhook clients): the request URL carries ?key=<RAWG api key> — a secret — and the
        // default IHttpClientFactory logging handler would write it to the app log on every request. Stripping
        // the loggers keeps the key off the log channel. ~10s timeout per the plan.
        services.AddHttpClient<IRawgClient, RawgClient>(c => c.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers();
        // The Steam cover client — a SEPARATE, decoupled typed HttpClient (the cover authority). No secret in
        // its URL (the appid is public) so its loggers stay intact. Keyless: it hydrates Steam covers regardless
        // of whether RAWG is configured; RAWG is only the cover fallback + the other-metadata authority.
        services.AddHttpClient<ISteamCoverClient, SteamCoverClient>(c => c.Timeout = TimeSpan.FromSeconds(10));
        // The hydration worker: boot sweep + a configurable periodic refresh (weekly by default, at a local
        // hour). Runs if EITHER source is on (Steam is on by default — keyless; RAWG is opt-in via
        // KGSM_API_RAWG_API_KEY). Off the request path; never blocks startup. Registered singleton + hosted
        // (same instance) like the other pumps, so the admin POST /library/refresh can force an immediate sweep.
        services.AddSingleton<RawgHydrationWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<RawgHydrationWorker>());

        // M8·c — outbound-notification integrations (§3·e). The store persists per-provider config (a
        // second EF entity in AppDbContext, created by the same EnsureCreated). Providers are a THIN seam
        // (INotificationProvider) resolved by id from the registered set — Discord is the first; Slack/
        // Telegram are just another AddHttpClient<INotificationProvider, X> later. One-way webhook delivery
        // only (the Discord view's `bot` is null — the two-way control bot stays kgsm-bot's). Increment A
        // is config + a real /test send; Increment B (below) is the delivery worker that fires on real events.
        services.AddSingleton<IntegrationStore>();
        // RemoveAllLoggers is load-bearing, NOT cosmetic: the provider POSTs to the webhook URL, and a
        // Discord webhook URL *is* the secret (.../webhooks/{id}/{token}). The default IHttpClientFactory
        // logging handler logs "Start processing HTTP request POST {uri}" at Information — i.e. it would
        // write the token to the app log on every send. Stripping the loggers for this client keeps the
        // "secret is never exposed" invariant on the log channel too (a regression test pins it).
        services.AddHttpClient<INotificationProvider, DiscordNotificationProvider>(
                c => c.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers();
        // M8·c Increment C — Slack, the second provider (validates the webhook-family abstraction). Same
        // INotificationProvider seam, registered the same way; the worker/catalog/controller are already
        // provider-agnostic, so it is picked up with no other change. RemoveAllLoggers for the same reason
        // (a Slack incoming-webhook URL is also the secret).
        services.AddHttpClient<INotificationProvider, SlackNotificationProvider>(
                c => c.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers();

        // M8·c Increment B — the delivery worker. The bus is the ALWAYS-ON tap: AuditService.AppendAsync
        // publishes every audit row to it (the bus keeps only catalog-mapped actions; the worker routes to
        // enabled providers at `every` cadence with a per-(provider,server,event) anti-spam window). NO new
        // event-socket consumer — it rides the existing audit flow. Singleton bus (a bounded channel) + a
        // hosted drain loop, the always-on-hosted-service shape of the audit consumer / alert engine.
        services.AddSingleton<INotificationBus, NotificationBus>();
        services.AddHostedService<NotificationDeliveryWorker>();

        // M2 — realtime. The hub is the per-host connection registry + fan-out; the three pumps poll
        // their sources (neither the monitor nor kgsm-lib pushes) and publish only while subscribed, so
        // an idle stream costs nothing. The /stream WebSocket endpoint lives in StreamController.
        services.AddSingleton<StreamHub>();
        services.AddHostedService<MetricsPump>();        // ~1s monitor scrape -> servers/{id}/metrics + hosts/{id}/metrics
        services.AddHostedService<DomainPump>();         // ~3s domain join diff -> servers (status/roster)
        // The leaf health monitor is ALWAYS-ON (not gated on subscribers): it polls each provisioned
        // leaf's /health every ~2s as the canonical liveness signal, serves the cached capability block
        // to GET /hosts (HostAggregator reads it), and publishes hosts/{id}/capabilities flips. It is one
        // instance exposed as both a singleton (the readable cache) and a hosted service (the poll loop).
        services.AddSingleton<LeafHealthMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<LeafHealthMonitor>());

        // #8 — the live console bridge (the follow-only servers/{id}/console topic). Always-running reconcile
        // loop (~2s, AlertEngine-shaped, NOT a per-source pump): while a console topic has subscribers it opens
        // exactly ONE shared watchdog tail-bridge per native instance and fans each appended line out as a
        // console.line; it closes a bridge when the last subscriber leaves / the instance vanishes / on
        // shutdown (cancelling the unbounded follow). The REST scrollback (GET /servers/{id}/console?tail=N,
        // ServerConsoleController) hydrates history; this streams the live tail. The watchdog client is resolved
        // optionally — absent => the loop logs once and stays silent (degrade gracefully, never a 500).
        services.AddSingleton<ConsoleBridgeManager>();
        services.AddHostedService(sp => sp.GetRequiredService<ConsoleBridgeManager>());

        // Host-log live tail — the resident piece behind the follow-only, operator-gated hosts/{id}/logs WS
        // topic. While that topic has subscribers it runs ONE shared `journalctl -f` across the configured leaf
        // units and fans each new line out as a log.line (the REST GET /hosts/{id}/logs hydrates history; this
        // streams the live tail). Idle when nobody is watching; degrades to silent if journalctl is unavailable.
        services.AddHostedService<JournalFollowBridge>();

        // M3 — commands (the first write path). The registry holds in-memory job state + the
        // one-in-flight-per-server guard; the runner executes admitted verbs off-request (its own DI
        // scope per job, since ILifecycleService is transient/process-based) and streams job.patch +
        // the verify server.patch. Both singletons.
        services.AddSingleton<JobRegistry>();
        services.AddSingleton<CommandRunner>();

        // M9 — metrics history (the durable tiered store behind KGSM_API_METRICS_HISTORY_DISABLED).
        // The store is always registered (the read endpoint needs it even to return empty); the sampler
        // and maintenance worker only start when history is enabled AND the monitor is provisioned.
        services.AddSingleton<MetricsHistoryStore>();
        if (apiOptions.MetricsHistoryEnabled && apiOptions.MetricsProvisioned)
        {
            services.AddHostedService<MetricsSampler>();
            services.AddHostedService<MetricsMaintenanceService>();
        }

        // M5 — audit log (append-only, downstream of the stateless engine). AuditService is the single
        // writer (own DI scope per write, serialized); the consumer subscribes to kgsm events via
        // kgsm-lib's IEventService and turns server.*/backup.* into audit rows (the engine owns those,
        // so the API records the echo, never double-writes a command). The consumer also EnsureCreates
        // the audit table at startup — so GET /audit + the API-internal (auth) writes work even with no
        // engine. Reads go straight to AppDbContext on the request scope (AuditController). No EF
        // migrations — the schema is EnsureCreated (greenfield/dev authority; PLAN M5).
        services.AddSingleton<AuditService>();
        services.AddHostedService<KgsmAuditConsumer>();

        // File browser (Tier 3 #12) — the jailed content I/O for GET/PUT /servers/{id}/files. No leaf, no
        // capability axis (engine-base, like config/backups): the jail root comes from kgsm-lib
        // (Instance.WorkingDir) and the read/write is host filesystem. Pure/stateless → singleton.
        services.AddSingleton<IInstanceFileService, InstanceFileService>();

        // Host logs — the GET /hosts/{id}/logs journald aggregator. No leaf, no capability axis (host-OS
        // introspection, like the file browser): it shells journalctl directly and is pure/stateless → singleton.
        services.AddSingleton<TheKrystalShip.Api.Services.Logs.JournalReader>();

        // M6·a — alerts (the condition-mirror). The engine is ALWAYS-ON (like LeafHealthMonitor, not gated
        // on WS subscribers): GET /alerts must serve fresh truth regardless of who is listening. It polls
        // the watchdog's supervision state (via kgsm-lib IWatchdogClient — the crash source) every ~5s,
        // raises/resolves/escalates/retracts, and serves the in-memory feed (no EF table — the durable
        // record is /audit). One instance, exposed as both the readable singleton (the controller) and the
        // poll loop (hosted service). With no watchdog provisioned it logs once and serves an empty feed.
        services.AddSingleton<AlertEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<AlertEngine>());

        // M4·a — auth (Discord per-host, Model A). Stateless JWT bearer (the M4 decision): no session
        // table, no user row — keeps M5 as the first EF migration. The Discord seam (IDiscordIdentityResolver)
        // keeps everything that talks to discord.com behind one interface, so the whole 401/403/tier matrix
        // is testable in-process with a fake. The token service mints/validates the host-scoped JWTs; the
        // tier handler grants a hierarchical viewer/operator/admin policy from the 'tier' claim.
        services.AddSingleton<ISessionTokenService, SessionTokenService>();
        services.AddHttpClient<IDiscordIdentityResolver, DiscordIdentityResolver>(
            c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IAuthorizationHandler, TierAuthorizationHandler>();

        // Auth is ON by default; KGSM_API_AUTH_DISABLED=1 swaps the default scheme for a synthetic-admin
        // handler so every policy passes (the explicit, loudly-logged dev/open window). When enabled, the
        // JwtBearer scheme validates the session JWTs with the SAME parameters the token service mints under
        // (shared via the post-configure below). The WS handshake can't set an Authorization header, so the
        // /stream path also accepts ?access_token=; a refresh token is never accepted as an access bearer.
        string defaultScheme = apiOptions.AuthEnabled
            ? JwtBearerDefaults.AuthenticationScheme
            : DisabledAuthHandler.SchemeName;
        AuthenticationBuilder authBuilder = services.AddAuthentication(defaultScheme);
        if (apiOptions.AuthEnabled)
        {
            authBuilder.AddJwtBearer(options =>
            {
                options.MapInboundClaims = false; // keep claim types verbatim ("sub", "tier", …)
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (string.IsNullOrEmpty(ctx.Token)
                            && ctx.Request.Path.StartsWithSegments("/api/v1/stream"))
                        {
                            string? qsToken = ctx.Request.Query["access_token"];
                            if (!string.IsNullOrEmpty(qsToken))
                                ctx.Token = qsToken;
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = ctx =>
                    {
                        // A refresh token authenticates ONLY /auth/session/refresh, never a protected call.
                        if (ctx.Principal?.FindFirst(AuthClaims.TokenKind)?.Value != TokenKind.Access)
                            ctx.Fail("not an access token");
                        return Task.CompletedTask;
                    },
                };
            });
            // The signing key lives in the token service (derived once); share its validation rules so
            // access tokens and refresh tokens validate identically.
            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<ISessionTokenService>((o, tokens) => o.TokenValidationParameters = tokens.ValidationParameters);
        }
        else
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, DisabledAuthHandler>(
                DisabledAuthHandler.SchemeName, _ => { });
        }

        // Hierarchical tier policies (admin ⊇ operator ⊇ viewer). An unauthenticated caller fails the
        // requirement → 401 challenge; an authenticated-but-too-low tier → 403 (the authorization
        // middleware picks challenge vs forbid). 401/403 already render the frozen {error} envelope below.
        services.AddAuthorization(o =>
        {
            o.AddPolicy(AuthPolicy.Viewer, p => p.Requirements.Add(new TierRequirement(AuthTier.Viewer)));
            o.AddPolicy(AuthPolicy.Operator, p => p.Requirements.Add(new TierRequirement(AuthTier.Operator)));
            o.AddPolicy(AuthPolicy.Admin, p => p.Requirements.Add(new TierRequirement(AuthTier.Admin)));
            // Secure-by-default: any endpoint without an explicit [Authorize]/[AllowAnonymous] still
            // requires an authenticated caller — so a future controller can't ship silently open. The
            // open probes (/health, /api/v1) opt out with [AllowAnonymous]; diagnostics are admin-gated.
            // (Under the disabled escape hatch the synthetic-admin scheme satisfies this too.)
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        // Error contract over the default ProblemDetails body. AddProblemDetails is
        // registered only to satisfy UseExceptionHandler's startup guard — ApiExceptionHandler
        // always handles, so the ProblemDetails fallback never fires.
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();

        // CORS allowlist is env/config-driven — the frontend's origin isn't known yet, and
        // the validation venue is still open. KGSM_API_CORS_ORIGINS is a comma-separated
        // list; when unset we allow any origin (dev only — no credentials to leak until M4).
        string[] corsOrigins = (configuration["KGSM_API_CORS_ORIGINS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
        {
            if (corsOrigins.Length > 0)
                policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            else
                policy.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod();
        }));
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory)
    {
        // Make the trust posture impossible to miss in the logs.
        ApiOptions options = app.ApplicationServices.GetRequiredService<ApiOptions>();
        ILogger startupLog = loggerFactory.CreateLogger("TheKrystalShip.Api.Startup");
        if (options.AuthDisabled)
            startupLog.LogWarning(
                "AUTH DISABLED (KGSM_API_AUTH_DISABLED) — every request is authenticated as admin. "
                + "This is the pre-M4 open trust window; never enable it on an exposed host.");
        else if (!options.DiscordConfigured)
            startupLog.LogWarning(
                "Auth is ON but Discord is not fully configured — the /auth/discord/* login endpoints "
                + "will 503 until KGSM_API_AUTH_DISCORD_* are set. Protected endpoints require a bearer (401).");

        // Same-origin SPA delivery: when the Control Panel SPA's built bundle is present in the web root
        // (the deploy drops kgsm-web's dist/ into wwwroot), Kestrel serves it at / on the SAME origin as
        // the API — one domain, no CORS. Gated on the bundle actually being there, so a dev run (no
        // wwwroot; the SPA on the Vite dev server) and an API-only deploy both no-op cleanly here.
        string? spaWebRoot = env.WebRootPath;
        bool serveSpa = !string.IsNullOrEmpty(spaWebRoot) && File.Exists(Path.Combine(spaWebRoot, "index.html"));
        if (serveSpa)
            startupLog.LogInformation("Serving the Control Panel SPA from {WebRoot} (same-origin).", spaWebRoot);

        app.UseExceptionHandler(); // unhandled -> 500 error envelope (ApiExceptionHandler)

        // HTTP → HTTPS upgrade (production posture: NO bare HTTP on the internet). Any plain-HTTP
        // request that arrives on a PUBLIC interface is permanently redirected (308 — it preserves the
        // method + body, so a POST upgrades cleanly instead of being silently turned into a GET) to its
        // https:// equivalent on the standard port (the inbound :80 is dropped → :443). The SOLE allowed
        // plain-HTTP surface is the LOOPBACK ops/health port (127.0.0.1:8097, never internet-exposed): the
        // deploy probe and a local `curl http://127.0.0.1:8097/health` don't speak TLS and the cert isn't
        // valid for 127.0.0.1, so those must pass through un-redirected. We gate on the RECEIVING interface
        // (a loopback local address ⇒ an ops call) rather than a hard-coded port, so it stays correct if the
        // ops port ever changes. For an external http:// to reach here at all, KGSM_API_URLS must include a
        // plain-http public bind (http://0.0.0.0:80); without one, bare http simply refuses the connection.
        app.Use(async (context, next) =>
        {
            System.Net.IPAddress local = context.Connection.LocalIpAddress ?? System.Net.IPAddress.Loopback;
            if (!context.Request.IsHttps && !System.Net.IPAddress.IsLoopback(local))
            {
                string target = $"https://{context.Request.Host.Host}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
                // permanent + preserveMethod => 308 (NOT 301): a 301 lets a client silently retry a POST as
                // GET; 308 keeps the method + body, so a bare-http POST upgrades to the identical https POST.
                context.Response.Redirect(target, permanent: true, preserveMethod: true);
                return;
            }
            await next();
        });

        // Status-only responses with no body (unmatched 404 now; 401/403 once M4 auth lands)
        // get the error envelope too, so the contract is uniform across the whole surface.
        app.UseStatusCodePages(async statusContext =>
        {
            HttpContext http = statusContext.HttpContext;
            (string code, string message) = http.Response.StatusCode switch
            {
                StatusCodes.Status404NotFound => ("not_found", "No such resource."),
                StatusCodes.Status401Unauthorized => ("unauthorized", "Authentication required."),
                StatusCodes.Status403Forbidden => ("forbidden", "Insufficient permissions."),
                _ => ("error", "Request failed."),
            };
            await ApiErrors.WriteAsync(http, http.Response.StatusCode, code, message);
        });

        // Serve the SPA's hashed static assets (JS/CSS/fonts) from the web root. Before routing so a
        // matching asset short-circuits; the API endpoints under /api/v1 are unaffected. Public by
        // design — the bundle (incl. the login page) must load before auth; the DATA stays [Authorize]-gated.
        if (serveSpa)
            app.UseStaticFiles();

        // Enable WebSocket upgrades before routing so the /api/v1/stream endpoint (M2) can accept them.
        app.UseWebSockets();

        app.UseRouting();
        app.UseCors(CorsPolicy);
        // M4·a — auth pipeline (the M0 placeholder, now filled). Authentication populates User from the
        // bearer (or the synthetic-admin scheme when disabled); authorization enforces the [Authorize]
        // tier policies. A 401/403 here flows through UseStatusCodePages above into the {error} envelope.
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();

            // SPA fallback: a client-routed GET (deep link / refresh — no file extension, matched no
            // controller) boots the app by returning index.html. EXCLUDE /api/* so a bogus API path stays
            // a JSON 404 ({error} envelope, invariant #4) rather than a 200 HTML page. Asset files (with
            // extensions) were already served by UseStaticFiles, so they never reach this :nonfile fallback.
            // .AllowAnonymous() is LOAD-BEARING: without it the endpoint inherits the global
            // RequireAuthenticatedUser fallback policy and returns 401 for the SPA shell — i.e. nobody could
            // even load the login page. The bundle is a PUBLIC static site; the DATA under /api/v1 stays gated.
            if (serveSpa)
            {
                string indexFile = Path.Combine(spaWebRoot!, "index.html");
                endpoints.MapFallback(async context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.SendFileAsync(indexFile);
                }).AllowAnonymous();
            }
        });
    }
}
