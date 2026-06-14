using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Infrastructure;
using TheKrystalShip.Api.Json;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Leaves;
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
        services.AddSingleton<ServerAggregator>();

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

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
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
        // M4 auth slots in here: app.UseAuthentication(); app.UseAuthorization();
        app.UseEndpoints(endpoints => endpoints.MapControllers());
    }
}
