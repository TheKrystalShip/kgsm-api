using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Infrastructure;
using TheKrystalShip.Api.Json;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Library;
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
            .ConfigureApiBehaviorOptions(o => o.SuppressMapClientErrors = true);
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
        services.AddSingleton<MonitorClient>();
        services.AddSingleton<AssistantClient>();
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

        // M8·a — the installable-game catalog (GET /library). A pure blueprint scrape via kgsm-lib
        // IBlueprintService, resolved per-request and degrading to an empty catalog (logged once) when
        // the engine is unconfigured — the same engine-is-base posture as ServerAggregator.
        services.AddSingleton<LibraryAggregator>();

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

        // M3 — commands (the first write path). The registry holds in-memory job state + the
        // one-in-flight-per-server guard; the runner executes admitted verbs off-request (its own DI
        // scope per job, since ILifecycleService is transient/process-based) and streams job.patch +
        // the verify server.patch. Both singletons.
        services.AddSingleton<JobRegistry>();
        services.AddSingleton<CommandRunner>();

        // M5 — audit log (append-only, downstream of the stateless engine). AuditService is the single
        // writer (own DI scope per write, serialized); the consumer subscribes to kgsm events via
        // kgsm-lib's IEventService and turns server.*/backup.* into audit rows (the engine owns those,
        // so the API records the echo, never double-writes a command). The consumer also EnsureCreates
        // the audit table at startup — so GET /audit + the API-internal (auth) writes work even with no
        // engine. Reads go straight to AppDbContext on the request scope (AuditController). No EF
        // migrations — the schema is EnsureCreated (greenfield/dev authority; PLAN M5).
        services.AddSingleton<AuditService>();
        services.AddHostedService<KgsmAuditConsumer>();

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

        app.UseExceptionHandler(); // unhandled -> 500 error envelope (ApiExceptionHandler)

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

        // Enable WebSocket upgrades before routing so the /api/v1/stream endpoint (M2) can accept them.
        app.UseWebSockets();

        app.UseRouting();
        app.UseCors(CorsPolicy);
        // M4·a — auth pipeline (the M0 placeholder, now filled). Authentication populates User from the
        // bearer (or the synthetic-admin scheme when disabled); authorization enforces the [Authorize]
        // tier policies. A 401/403 here flows through UseStatusCodePages above into the {error} envelope.
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => endpoints.MapControllers());
    }
}
