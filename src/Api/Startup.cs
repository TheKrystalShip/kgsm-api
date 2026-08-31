using TheKrystalShip.KGSM.WebPush;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Infrastructure;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.Api.Services.Files;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Integrations.WebPush;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.Api.Services.Preferences;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Services;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Extensions;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Lifecycle;

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
    /// <summary>
    /// Whether an address is one a stranger on the internet could be calling from. Loopback, the private
    /// ranges, link-local and unique-local addresses are all reachable only from inside the operator's own
    /// network, so a plain-HTTP request from one crosses nothing that needs protecting.
    /// </summary>
    internal static bool IsOutOnTheInternet(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] switch
            {
                10 => false,
                127 => false,
                169 => b[1] != 254,
                172 => b[1] < 16 || b[1] > 31,
                192 => b[1] != 168,
                _ => true,
            };
        }

        return !address.IsIPv6LinkLocal
               && !address.IsIPv6SiteLocal
               && (address.GetAddressBytes()[0] & 0xFE) != 0xFC;   // fc00::/7 unique-local
    }

    private const string CorsPolicy = "frontend";

    // The CORS policy is built in ConfigureServices, before any service exists to resolve, but it has to
    // consult the panel origins this node learns at runtime. The store is dropped in here once the provider
    // is up (Configure), and the policy predicate reads it per request. Null until then, which reads as
    // "nothing learned yet" — the same answer the node gave before it learned anything.
    private SelfIdentityStore? _corsPanelOrigins;

    public void ConfigureServices(IServiceCollection services)
    {
        // Resolve the whole configurable surface once, up front. Everything below reads it from here
        // rather than by string key, so there is exactly one place a knob is interpreted and one place
        // its default lives — the binding chain is ApiSettings (what was written) -> ApiOptions (what
        // we run on), and nothing short-circuits it.
        ApiOptions apiOptions = ApiOptions.FromConfiguration(configuration);
        services.AddSingleton(apiOptions);

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
        // The file is created on first use. M0 uses EnsureCreated via the _dbcheck probe; real
        // schema evolution uses EF migrations from M5 on.
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={apiOptions.DbPath}"));

        // M1·a — leaf aggregation. ApiOptions consolidates host identity + leaf endpoints. The
        // monitor client owns the cached-latest scrape; the watchdog client (kgsm-lib) is registered
        // ONLY when provisioned so the capability probe can resolve it optionally and report 'absent'
        // when the leaf is not declared on this host.

        // Leaf runtime-provisioning registry (the leaf-runtime-provisioning feature): the DB-backed,
        // runtime-mutable source of truth for which leaves are connected, replacing the immutable
        // ApiOptions.*Provisioned flags. Registered as a hosted service EARLY (before the other hosted
        // services below) so it reconciles the persisted provisioning with the config seed before the
        // LeafHealthMonitor's first poll; also a singleton (the synchronous cache the leaf clients,
        // LeafHealthMonitor, ServicesAggregator and NetworkAggregator gate on).
        services.AddSingleton<LeafRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<LeafRegistry>());

        services.AddSingleton<MonitorClient>();
        // The metrics-history read seam: the same MonitorClient singleton, exposed for the history
        // proxy controller (the monitor owns history now; the API relays GET /metrics/history verbatim).
        services.AddSingleton<IMonitorHistoryClient>(sp => sp.GetRequiredService<MonitorClient>());
        services.AddSingleton<AssistantClient>();
        // Host identity: the static, runtime-derived card (OS/runtime/build/start-time), read once + cached;
        // and the editable overrides store (region/label) — its own EnsureCreated + CREATE TABLE IF NOT EXISTS
        // so the host_settings table appears on an existing DB without wiping the shared audit log.
        services.AddSingleton<HostIdentityProvider>();
        services.AddSingleton<HostSettingsStore>();
        services.AddSingleton<HostAggregator>();
        // Always register the watchdog client (lazy, configured-or-default socket) so a runtime "connect
        // watchdog" arms it without a restart — provisioning is now the registry's flag, not the client's
        // presence. A blank configured socket falls back to the standard path; consumers (LeafHealthMonitor,
        // AlertEngine, ConsoleBridgeManager) gate on provisioning before using it, so a down socket is inert.
        services.AddKgsmWatchdogClient(o =>
            o.SocketPath = string.IsNullOrWhiteSpace(apiOptions.WatchdogSocketPath)
                ? "/run/kgsm-watchdog/control.sock"
                : apiOptions.WatchdogSocketPath);

        // M1·b — the servers join. kgsm-lib is the engine chokepoint (base, not a leaf): registered
        // when the engine is provisioned (it is, by default, at the packaged path). IInstanceService
        // is process-based — it shells KgsmPath; engine events come from the journal, a file the API
        // reads with nothing to bind and nothing to reserve. The kgsm-lib singletons are lazy, so a
        // journal directory that does not exist yet never blocks startup. The ServerAggregator
        // resolves IInstanceService per-request and degrades to an empty list (logged once) if the
        // engine is unconfigured.
        //
        // Tail, with no cursor: the API never PERSISTS an engine event. It shapes each one into a
        // live audit row, fans it out over SSE, and hands it to the notification bus — so replaying
        // history on restart would re-announce to Discord/Slack events that were already announced.
        // Nothing is lost by starting at the tail, because the journal IS the record and GET /audit
        // reads it back from there (IEventJournalHistory, registered by AddKgsmServices below).
        // This API's own journal — the write half. NOT conditional on the engine: signing somebody in
        // works on a host with no kgsm installed, and so must recording that it happened. The producer
        // id is this state directory's own name, which is what a reader scans for, so writer and
        // readers agree on the location without either being told.
        services.AddKgsmJournal(
            ApiOptions.ApiJournalProducer,
            typeof(Startup).Assembly,
            stateRoot: apiOptions.JournalStateRoot,
            // Named explicitly because this API's journal path is configurable: the state root places
            // it by default, and a host that points Api__EventJournalDir somewhere else must still
            // have the writer land where this process's own reader is told to look.
            configure: o => o.Directory = apiOptions.EventJournalDir);
        services.AddSingleton<ApiJournal>();

        // What this API says about ITSELF, as opposed to about the people using it. Separate from
        // ApiJournal because the two answer to different identities: an audit line carries whoever
        // acted, and a lifecycle line is this process reporting on its own state with nobody behind it.
        services.AddSingleton(sp => new LeafLifecycle(
            sp.GetRequiredService<IEventJournalWriter>(),
            sp.GetRequiredService<ILogger<LeafLifecycle>>()));

        services.AddHostedService<ApiLifecycleReporter>();

        if (apiOptions.KgsmProvisioned)
        {
            services.AddKgsmServices(new KgsmOptions
            {
                KgsmPath = apiOptions.KgsmPath,
                EventJournalDirectory = apiOptions.KgsmJournalDir,
                EventStartPosition = EventStartPosition.Tail
            });

            // File browser (Tier 3 #12) — the jailed content I/O for GET/PUT /servers/{id}/files. No
            // capability axis of its own (engine-base, like config/backups): it's a thin status-mapping
            // wrapper around kgsm-lib's IInstanceFiles (registered by AddKgsmServices above), so it is
            // gated the SAME as the engine itself and registered transient (its dependency is). The
            // controller resolves it lazily from RequestServices — mirroring the IInstanceService
            // null-check pattern below — so an unprovisioned engine degrades to the existing 503, not a
            // DI construction failure.
            services.AddTransient<IInstanceFileService, InstanceFileService>();

            // The library blueprint editor (GET/PUT/DELETE /library/{id}/file). Same posture as the file
            // browser above: a thin status-mapping wrapper over kgsm-lib's IBlueprintFiles +
            // IBlueprintService (both transient, so this is too), gated on the engine, resolved lazily
            // from RequestServices so an unprovisioned engine degrades to 503 rather than failing DI.
            services.AddTransient<IBlueprintFileService, BlueprintFileService>();
        }

        // Read EVERY producer's journal, not only the engine's. Each component records what it did in
        // its own journal, and this API is the surface that serves the merge — it aggregates rather
        // than owns, so the merging itself lives in kgsm-lib and every other consumer gets it too.
        // Which journals exist is discovered on disk, so no list is held here and a leaf that starts
        // writing one later needs no rebuild.
        //
        // Must stay AFTER AddKgsmServices: that call registers an IEventSource reading the engine's
        // journal alone, and this one replaces it by being registered last. Moving this above it leaves
        // every consumer tailing one journal while believing it tails all of them.
        //
        // Registered whatever the engine's state, and that is load-bearing rather than tidy: reading a
        // journal is reading files, and THIS API's own journal is one of them. Gating the reader on
        // kgsm being installed would leave a host with no engine writing its sign-ins to a record it
        // then refused to read back.
        //
        // No cursor path, deliberately: same reasoning as the Tail start position above. This API
        // persists no event it reads — it shapes each into a live audit row, fans it out over SSE and
        // hands it to the notification bus — so resuming a journal would re-announce to Discord/Slack
        // things that were already announced when they happened. GET /audit reads history back off the
        // record itself, so nothing is lost by starting every journal at its tail.
        services.AddKgsmJournalFederation(
            cursorPath: null,
            startPosition: EventStartPosition.Tail,
            engineJournalDirectory: apiOptions.KgsmJournalDir,
            stateRoot: apiOptions.JournalStateRoot,
            // This API's own journal, named rather than left to the scan. The scan finds a producer at
            // its DEFAULT state directory, and this one's path is configurable — so a host that points
            // it elsewhere would have this API writing a record it could not then read back.
            namedJournals: [new JournalSource(ApiOptions.ApiJournalProducer, apiOptions.EventJournalDir)]);

        // M6·b — ports. The firewall authority (kgsm-firewall) is OPT-IN like the assistant: its kgsm-lib
        // client is registered ONLY when its socket is configured (blank => firewall "absent"). It is
        // deliberately NOT added to the LeafHealthMonitor 2s poll — the daemon is socket-activated and
        // idle-exits, so a periodic probe would defeat that; NetworkAggregator probes it ON-DEMAND on a
        // detail view, bounding each call, and reports liveness as the block-level `firewall` status.
        // Always register the firewall client too (lazy, configured-or-default socket): the runtime registry
        // flag — NOT the client's presence — decides the firewall surface now (NetworkAggregator gates
        // on LeafRegistry.IsProvisioned("firewall"), seeded from config so the default is unchanged).
        // A runtime "connect firewall" arms the ports surface without a restart.
        services.AddKgsmFirewallClient(o =>
        {
            o.SocketPath = string.IsNullOrWhiteSpace(apiOptions.FirewallSocketPath)
                ? "/run/kgsm-firewall/firewall.sock"
                : apiOptions.FirewallSocketPath;
            o.RequestTimeout = TimeSpan.FromSeconds(30);
        });
        // Settings Phase 3 — the kgsm-scheduler leaf (NDJSON-over-unix-socket status). OPT-IN like the
        // firewall/assistant: registered ONLY when Api__SchedulerSocketPath is configured, so a host with
        // no scheduler resolves the client as null → the capability is 'absent' and nextFireUtc is null
        // (never a perpetually-'down' row). Consumers (LeafHealthMonitor, ServerSettingsController) resolve
        // it optionally. Config-provisioned, NOT the runtime DB LeafRegistry (not one of the four
        // runtime-flippable leaves) — the socket path is fixed by config for this leaf.
        if (apiOptions.SchedulerProvisioned)
            services.AddSingleton<SchedulerClient>();

        // The kgsm-reactor leaf. Always registered, like the monitor and unlike the scheduler: its
        // provisioning is the runtime registry (seeded from Api__ReactorSocketPath), so the client builds
        // its transport from the configured-or-default socket and a "connect reactor" arms the probe
        // without a restart. Every call gates itself on the registry, so an unconnected reactor is never
        // dialed.
        services.AddSingleton<ReactorClient>();

        // The Discord bot's status socket, opt-in on exactly the same terms as the scheduler's: a host
        // that configures no path registers no client, and the Services page falls back to systemd
        // liveness alone rather than reporting a bot that is perpetually unreachable.
        if (apiOptions.BotStatusProvisioned)
            services.AddSingleton<BotClient>();

        // The speech leaf, always registered and never polled. Unlike its neighbours there is nothing to
        // opt into: systemd binds the leaf's socket whether or not the daemon is running, so the client
        // answers "is this installed here" from the socket file itself and a host without kgsm-speech
        // simply 404s the surface. It is deliberately absent from the LeafHealthMonitor poll — the daemon
        // idle-exits to give back the ~1.6GB its models cost, and connecting is what starts it, so a
        // periodic probe would keep a process alive purely to be asked whether it is alive.
        services.AddSingleton<SpeechLeafClient>();

        // Always registered: it degrades to firewall:"absent"/null when not provisioned, so the
        // server/host aggregators can depend on it unconditionally.
        services.AddSingleton<NetworkAggregator>();

        // How long each instance has been running an out-of-date build, read from the engine's journal on
        // a slow loop. Registered ahead of the aggregator that joins it onto the roster, and always — a
        // host with no engine simply never populates it, and every server then reports the honest null.
        services.AddSingleton<Services.Availability.UpdateLagIndex>();
        services.AddHostedService(sp => sp.GetRequiredService<Services.Availability.UpdateLagIndex>());
        // The watchdog's run clock (when the current run began, when the last one ended). Registered
        // unconditionally: it resolves IWatchdogClient optionally and simply reports nothing on a host
        // with no watchdog, so the roster join needs no branch.
        services.AddSingleton<Services.Availability.RunTimesIndex>();
        services.AddHostedService(sp => sp.GetRequiredService<Services.Availability.RunTimesIndex>());

        // The watchdog's supervision phase, for the one state a run-state boolean cannot carry: an
        // instance parked for a maintenance window is stopped on purpose, and reading that as an outage
        // would report a server as down when nothing is wrong.
        services.AddSingleton<Services.Availability.SupervisionPhaseIndex>();
        services.AddHostedService(sp => sp.GetRequiredService<Services.Availability.SupervisionPhaseIndex>());

        services.AddSingleton<ServerAggregator>();

        // Instance in-memory cache: sits between consumers (ServerAggregator, DomainPump,
        // NetworkAggregator) and the kgsm engine's IInstanceService. Background refresh every
        // InstanceCacheTtlSeconds; kgsm lifecycle events update runtime state between refreshes.
        services.AddSingleton<InstanceCache>();
        services.AddHostedService(sp => sp.GetRequiredService<InstanceCache>());

        // Backup cache: each instance's newest snapshot + how many it holds, populating the
        // Server.LastBackup/BackupCount contract fields. Listing backups is a kgsm process spawn per
        // instance, so like the update check it runs on its own relaxed BackupScanPollMs cadence rather than
        // on the roster refresh; the kgsm backup created/restored event echo refreshes the one affected
        // instance immediately so an operator sees their own backup land. Reads InstanceCache.Roster for the
        // authoritative id set, and both fields are honest-null until the first scan completes.
        services.AddSingleton<BackupCache>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupCache>());

        // M8·a — the installable-game catalog (GET /library). A blueprint scrape via kgsm-lib
        // IBlueprintService (resolved per-request, degrading to an empty catalog (logged once) when the engine
        // is unconfigured — the engine-is-base posture as ServerAggregator), joined with this host's cached
        // RAWG.io cover/metadata. RawgStore is the single reader/writer of the rawg_entry table (own DI scope
        // per op, like IntegrationStore); the LibraryAggregator reads it per-request, degrading cover/hero to
        // null INDEPENDENTLY of the blueprint read on a cache failure.
        // Blueprint in-memory cache: sits between consumers (LibraryAggregator, LibraryHydrationWorker) and
        // the kgsm engine's IBlueprintService. Background refresh every BlueprintCacheTtlSeconds; degrades
        // to empty when the engine is unconfigured.
        services.AddSingleton<BlueprintCache>();
        services.AddHostedService(sp => sp.GetRequiredService<BlueprintCache>());
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
        // Api__RawgApiKey). Off the request path; never blocks startup. Registered singleton + hosted
        // (same instance) like the other pumps, so the admin POST /library/refresh can force an immediate sweep.
        services.AddSingleton<LibraryHydrationWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<LibraryHydrationWorker>());

        // Outbound-notification integrations (§3·e). The store persists per-provider config (a second EF
        // entity in AppDbContext, created by the same EnsureCreated). Providers are a THIN seam
        // (INotificationProvider) resolved by id from the registered set, so a new one — Telegram, a
        // generic webhook — is another AddHttpClient<INotificationProvider, X> and nothing else.
        //
        // There is deliberately NO Discord provider here. Discord is the one channel this ecosystem ships
        // a real bot for, and kgsm-bot is the component that holds the connection, the per-server channels
        // and the announcement switches. A second Discord path from the API would post the same events
        // twice and split the configuration across two components.
        services.AddSingleton<IntegrationStore>();
        // RemoveAllLoggers is load-bearing, NOT cosmetic: the provider POSTs to the webhook URL, and an
        // incoming-webhook URL *is* the secret. The default IHttpClientFactory logging handler logs
        // "Start processing HTTP request POST {uri}" at Information — i.e. it would write the token to the
        // app log on every send. Stripping the loggers for this client keeps the "secret is never exposed"
        // invariant on the log channel too (a regression test pins it).
        services.AddHttpClient<INotificationProvider, SlackNotificationProvider>(
                c => c.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers();

        // Web Push — the third provider. It fans out over per-device subscriptions instead of posting to
        // one webhook, so the subscription table and the sender are their own services and the provider
        // itself takes no HttpClient.
        //
        // RemoveAllLoggers here for the same reason as the webhook client above: a push endpoint URL is a
        // capability to notify somebody's phone, and the default factory logging handler would write one
        // per send into the service log.
        services.AddHttpClient<WebPushSender>(c => c.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers();
        // The general per-account preference store — what a person has set, per device, plus the
        // account switch that makes one device's set authoritative. A singleton for the same reason the
        // push stores are: it owns a scope per operation and a write gate, and the version bump behind
        // that gate is what keeps the merge counter monotonic. Keys are opaque to it, so the dashboard
        // layout and the UI theme are tenants rather than features here.
        services.AddSingleton<UserPreferenceStore>();

        services.AddSingleton<PushSubscriptionStore>();
        services.AddSingleton<PushPreferenceStore>();
        services.AddSingleton<PushSnoozeStore>();
        services.AddSingleton<PushQuietHoursStore>();
        services.AddSingleton<PushActionStore>();
        services.AddSingleton<VapidKeyStore>();
        services.AddTransient<INotificationProvider, WebPushNotificationProvider>();

        // M8·c Increment B — the delivery worker. The bus is the ALWAYS-ON tap: AuditService.AppendAsync
        // publishes every audit row to it (the bus keeps only catalog-mapped actions; the worker routes to
        // enabled providers at `every` cadence with a per-(provider,server,event) anti-spam window). NO new
        // event-socket consumer — it rides the existing audit flow. Singleton bus (a bounded channel) + a
        // hosted drain loop, the always-on-hosted-service shape of the audit consumer / alert engine.
        services.AddSingleton<INotificationBus, NotificationBus>();
        services.AddSingleton<NotificationDigestStore>();
        services.AddHostedService<NotificationDeliveryWorker>();

        // The digest's own loop. The delivery worker is blocked on the bus for the life of the process,
        // which is right for it and useless here: a summary becomes due because time passed, and on a
        // quiet host nothing will happen to wake it.
        services.AddHostedService<NotificationDigestWorker>();

        // The one notifiable fact with no event behind it: a server left running with nobody on it. It is a
        // reading taken from the engine and the supervisor agreeing over a dwell, published straight onto
        // the bus — nothing is written to the audit log, which records actions rather than observations.
        services.AddHostedService<IdleServerWatcher>();

        // And the other always-on watcher: a leaf that stops answering its health check. The Services board
        // shows the same flips, but its pump goes idle when nobody is watching the panel — which is the
        // situation a notification exists for.
        services.AddHostedService<LeafHealthWatcher>();

        // And the third: a scheduled restart about to fire on a server somebody is playing on. Its whole
        // value is the lead time, so it reads the scheduler on a short interval rather than reacting to
        // anything — nothing happens fifteen minutes before a restart.
        services.AddHostedService<ScheduledRestartWatcher>();

        // M2 — realtime. The hub is the per-host connection registry + fan-out; the three pumps poll
        // their sources (neither the monitor nor kgsm-lib pushes) and publish only while subscribed, so
        // an idle stream costs nothing. The /stream SSE endpoint lives in StreamController.
        services.AddSingleton<StreamHub>();
        services.AddHostedService<MetricsPump>();        // ~1s monitor scrape -> servers/{id}/metrics + hosts/{id}/metrics
        // Singleton + hosted-service (the InstanceCache pattern): the engine's event consumer holds a
        // reference so it can ask for a diff pass the moment a run-state change is announced, instead of
        // leaving the panel on the previous state until the next tick.
        services.AddSingleton<DomainPump>();
        services.AddHostedService(sp => sp.GetRequiredService<DomainPump>());  // cache-backed diff -> servers (status/roster)
        services.AddHostedService<ServicesPump>();        // ~5s systemd poll -> hosts/{id}/services (service.patch)
        // The leaf health monitor is ALWAYS-ON (not gated on subscribers): it polls each provisioned
        // leaf's /health every ~2s as the canonical liveness signal, serves the cached capability block
        // to GET /hosts (HostAggregator reads it), and publishes hosts/{id}/capabilities flips. It is one
        // instance exposed as both a singleton (the readable cache) and a hosted service (the poll loop).
        // What each leaf says is broken about ITSELF, read from its own journal.
        //
        // The half the capability probe cannot see. /health answers yes or no, so a leaf answering
        // perfectly while unable to do part of its job reads as operational — and the two
        // socket-activated leaves cannot be probed at all, because connecting to the socket is what
        // starts them. Registered before the monitor that consumes it.
        services.AddSingleton<LeafDegradationTracker>();
        services.AddHostedService(sp => sp.GetRequiredService<LeafDegradationTracker>());

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

        // Batches — one verb across a set of this host's servers. The store is the durable half (the work
        // outlives the request AND this process); the worker holds the host's concurrency window, which
        // exists nowhere else: the registry caps in-flight work per SERVER and nothing caps it per host.
        // Registered as a singleton and hosted from that same instance so the controller can signal it on
        // accept — otherwise the first members of a batch wait for the next idle poll.
        services.AddSingleton<BatchStore>();
        services.AddSingleton<BatchWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<BatchWorker>());

        // Backup-download tickets: a singleton because the mint and the redemption are two separate
        // requests, and the second one carries no identity of its own — the ticket is what connects them.
        services.AddSingleton<Services.Backups.BackupDownloadTickets>();
        services.AddSingleton<CommandRunner>();
        // The batch worker takes the runner through its interface so its concurrency window is provable
        // without executing anything — the same instance, named by the one method the worker uses.
        services.AddSingleton<ICommandExecutor>(sp => sp.GetRequiredService<CommandRunner>());

        // M5 — audit log (append-only, downstream of the stateless engine). AuditService is the single
        // writer (own DI scope per write, serialized); the consumer subscribes to kgsm events via
        // kgsm-lib's IEventService and turns server.*/backup.* into audit rows (the engine owns those,
        // so the API records the echo, never double-writes a command). The consumer also EnsureCreates
        // the audit table at startup — so GET /audit + the API-internal (auth) writes work even with no
        // engine. Reads go straight to AppDbContext on the request scope (AuditController). No EF
        // migrations — the schema is EnsureCreated (greenfield/dev authority; PLAN M5).
        services.AddSingleton<AuditService>();

        // M4·c — the session registry's single writer (one row per login × device, keyed by the JWT
        // `sid` claim). Own DI scope per write + a write gate (SQLite single-writer), the same
        // posture as AuditService above. Reads (the per-request validator, GET /auth/sessions) go
        // through AppDbContext on the request scope directly, NOT through this store. The table is
        // created by EnsureCreated on a fresh DB and by a one-shot sqlite3 command on the existing
        // prod DB (D11) — this store assumes the table exists.
        services.AddSingleton<SessionStore>();

        // M4·c — the per-request session validator (cached): an IMemoryCache keyed by sid → bool,
        // backed by a DB query on cache miss. The 5s TTL (SessionsCacheTtlMs) is the accepted
        // revocation-lag bound (D2); the Evict call in the revoke path (Increment 5/6) makes a revoke
        // ~instant, the TTL is the backstop. MemoryCache is process-local (per-host single-instance —
        // D2, no cross-node coherence). Registered as a singleton + the IMemoryCache it depends on
        // (AddMemoryCache is the standard Microsoft.Extensions.Caching.Memory registration).
        services.AddMemoryCache();
        services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<SessionStore>());

        // Api__SessionsDisabled makes the whole registry inert — the stateless-JWT posture, a debugging
        // escape hatch. That switch is THIS API's, not the session package's: rather than teaching the
        // shared validator and GC worker about a flag only one surface has, the switch decides what
        // gets composed. Disabled means a validator that answers "alive" without asking anyone, and no
        // GC worker at all — a genuinely inert registry rather than a live one that skips its work.
        if (apiOptions.SessionsEnabled)
        {
            services.AddSingleton<ISessionValidator>(sp => new SessionValidator(
                sp.GetRequiredService<ISessionRegistry>(),
                sp.GetRequiredService<IMemoryCache>(),
                TimeSpan.FromMilliseconds(apiOptions.SessionsCacheTtlMs)));

            // Deletes expired rows (revoked or not) on a timer so the table stays permanently bounded.
            services.AddHostedService(sp => new SessionCleanupWorker(
                sp.GetRequiredService<ISessionRegistry>(),
                TimeSpan.FromMilliseconds(apiOptions.SessionsGcMs),
                sp.GetRequiredService<ILogger<SessionCleanupWorker>>()));
        }
        else
        {
            services.AddSingleton<ISessionValidator, InertSessionValidator>();
        }

        // Player-presence live roster — an in-memory projection driven
        // FROM KgsmAuditConsumer's own player.join/player.leave (+ start/stop reset) handlers, never via a
        // second IEventService registration for the same event types (kgsm-lib keeps one handler per type;
        // see PlayerRosterService's remarks). GET /servers/{id}/players reads it directly.
        services.AddSingleton<PlayerRosterService>();

        // Player-presence permanent roster — the DB-backed authority for
        // GET /servers/{id}/players and all roster WS frames. Maintains an in-memory cache for fast reads,
        // persists to SQLite for durability, publishes WS frames on every status change. Called FROM
        // KgsmAuditConsumer alongside PlayerRosterService (session-level dedup). On startup, marks stale
        // online entries as unknown (honest — we missed events during downtime).
        services.AddSingleton<PlayerHistoryService>();

        // Whether presence is observable at all, for every surface that asks — the roster endpoint's
        // `detection` field and the per-server online count carried on every server element. One cached
        // reading of the supervisor's answer, so the two can never disagree and the roster build costs no
        // socket round trip. Resolves IWatchdogClient lazily (it is absent on a host with no watchdog).
        services.AddSingleton<PlayerObservability>();

        services.AddHostedService<KgsmAuditConsumer>();

        // Host logs — the GET /hosts/{id}/logs journald aggregator. No leaf, no capability axis (host-OS
        // introspection, like the file browser): it shells journalctl directly and is pure/stateless → singleton.
        services.AddSingleton<TheKrystalShip.Api.Services.Logs.JournalReader>();

        // Services board — the GET /hosts/{id}/services leaf control center. Same host-OS-introspection
        // category as the host logs: SystemdReader shells `systemctl show` (the unit manager's own state),
        // and ServicesAggregator joins that liveness with the LeafHealthMonitor deep-health cache (resolved
        // as the singleton above). Pure/stateless readers → singletons.
        services.AddSingleton<SystemdReader>();
        services.AddSingleton<ServicesAggregator>();

        // The engine's identity probe (kgsm --version / --paths through kgsm-lib, cached). Two readers:
        // the Services board's engine pseudo-leaf row, and GET /hosts/{id}/engine. Registered
        // unconditionally — it answers null itself when the engine isn't provisioned.
        services.AddSingleton<TheKrystalShip.Api.Services.Engine.EngineInfoService>();

        // Leaf runtime config (Phase 2 — the privileged broker). The override store persists the per-leaf
        // KEY=value overrides (the leaf_override table, idempotent CREATE TABLE IF NOT EXISTS like the
        // registry); the renderer materializes them into the API-owned <LeafOverridesDir>/<leaf>.env file a
        // systemd drop-in feeds the leaf; the apply broker (LeafConfigService) writes → renders → restarts via
        // IUnitController → polls ILeafProbe (health canary) → auto-rolls-back on failure. IUnitController +
        // ILeafProbe are seams (real impls shell systemctl / read systemd liveness; faked in tests).
        //
        // The config SURFACE comes from the descriptors each leaf's own deploy installs (scanned, never
        // listed here — a new leaf needs no rebuild), falling back to the built-in LeafConfigManifest for a
        // leaf that has not shipped one yet. LeafFloorReader reads the leaf's own config so each field can
        // report where its live value actually comes from, and ILeafReachability is the second, separate
        // verdict a wiring change needs: the liveness canary passes when a leaf restarts cleanly on a socket
        // path this API can no longer reach.
        services.AddSingleton<LeafDescriptorStore>();
        services.AddSingleton<LeafCommandStore>();
        services.AddSingleton<LeafConfigCatalog>();
        services.AddSingleton<LeafOverrideStore>();
        services.AddSingleton<LeafOverrideRenderer>();
        services.AddSingleton<LeafFloorReader>();
        services.AddSingleton<IUnitController, SystemctlUnitController>();
        services.AddSingleton<ILeafProbe, LeafProbe>();
        services.AddSingleton<ILeafReachability, LeafReachability>();
        services.AddSingleton<LeafConfigService>();

        // The write half of the reactor's rule editor. The leaf's socket is read-only, so storing a rule
        // is this API's half: it writes the file and restarts the unit through the grant above.

        // M6·a — alerts (the condition-mirror). The engine is ALWAYS-ON (like LeafHealthMonitor, not gated
        // on WS subscribers): GET /alerts must serve fresh truth regardless of who is listening. It polls
        // the watchdog's supervision state (via kgsm-lib IWatchdogClient — the crash source) every ~5s,
        // raises/resolves/escalates/retracts, and serves the in-memory feed (no EF table — the durable
        // record is /audit). One instance, exposed as both the readable singleton (the controller) and the
        // poll loop (hosted service). With no watchdog provisioned it logs once and serves an empty feed.
        services.AddSingleton<AlertEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<AlertEngine>());

        // The durable half of the same conditions needs nothing registered here. kgsm-monitor writes a
        // host.threshold.breached / _cleared event to its own journal the moment an episode opens or
        // closes, and this API reads that journal like every other producer's — so a breach reaches the
        // audit trail because the component that measured it recorded it, not because this one polled a
        // database and copied rows into its own store. The row is still shaped here at read time
        // (AuditMapping.FromThresholdBreachedEvent), because the wording and severity are a reader's
        // business and the record holds only what was measured.

        // Cluster membership and durable member-to-member messaging (TheKrystalShip.KGSM.Cluster). The
        // outbox, the inbox, the member service token, the SQLite store behind them and the
        // member-to-member wire all live in the package, so this API takes part in a cluster as one
        // member rather than being the thing a cluster is made of.
        //
        // The gate is registered FIRST, deliberately: the package registers its own accept-anything gate
        // with TryAddSingleton, so whichever gate is already there wins. This API has a roster, so it
        // brings the roster-backed one, keyed on the member id a token's iss carries.
        //
        // Everything below is inert on a host with no cluster secret — the token service validates
        // nothing, so the inbox endpoint rejects every call before a handler is reached, and neither
        // background worker starts a timer.
        // Registered BEFORE the package, deliberately: it registers its own default card source with
        // TryAddSingleton, so whichever is already there wins. This API is a node, so it brings one that
        // adds the node block — a route version, a build, and the leaves provisioned here — over the
        // package's own, which states only what the package itself knows. The enabled-member gate needs
        // nothing here: the package's is roster-backed and this API's roster is that roster.
        services.AddSingleton<SelfMemberCardSource>();
        services.AddSingleton<IMemberCardSource, NodeCardSource>();
        services.AddKgsmCluster(new ClusterOptions
        {
            MemberId = apiOptions.NodeId,
            // Host-level, from /etc/kgsm/kgsm-cluster.env, which this unit loads before its own env
            // file. Read through the package so every member of a cluster on this machine spells the
            // key the same way — one that spells it differently reads a blank and concludes, silently,
            // that it is not clustered.
            Secret = ClusterConfiguration.Secret(configuration),
            SecretPrevious = ClusterConfiguration.SecretPrevious(configuration),
            // Beside this API's own database rather than inside it: the roster and the queues are
            // cluster state, and this API's database is operational state that is wiped whenever its
            // schema changes. Named FROM that database rather than fixed within its directory, so the
            // cluster store belongs to this member rather than to the directory it sits in — two members
            // sharing a directory would otherwise share one outbox.
            StorePath = Path.ChangeExtension(apiOptions.DbPath, ".cluster.db"),
            DrainMs = apiOptions.ClusterDrainMs,
            RetryTtlDays = apiOptions.ClusterRetryTtlDays,
            RetentionDays = apiOptions.ClusterRetentionDays,
            GcMs = apiOptions.ClusterGcMs,
            // This API is a node: it runs the engine and game servers and hosts leaves.
            Kind = MemberKind.Node,
            PublicBaseUrl = apiOptions.PublicBaseUrl,
            GossipUrl = apiOptions.ClusterGossipUrl,
            GossipMs = apiOptions.ClusterGossipMs,
            PollMs = apiOptions.ClusterPollMs,
            SuspectMs = apiOptions.ClusterSuspectMs,
            ReapMs = apiOptions.ClusterReapMs,
        });

        // session.revoke is registered rather than built in: the transport dispatches by type and
        // knows nothing about sessions, and the handler is every member's rather than this API's. The
        // retention is how long a record of an ended session is worth keeping — the longest a bearer
        // for it could still be presented, which is a refresh token's life.
        services.AddSingleton<IClusterSessionAuthority>(sp => new ClusterSessionStore(
            sp.GetRequiredService<SessionStore>(), sp.GetRequiredService<ApiOptions>()));

        services.AddSingleton<IClusterMessageHandler>(sp => new SessionRevokeHandler(
            sp.GetRequiredService<IClusterSessionAuthority>(),
            sp.GetRequiredService<ISessionValidator>(),
            sp.GetRequiredService<ClusterSessionRevocations>(),
            TimeSpan.FromDays(sp.GetRequiredService<ApiOptions>().SessionsRefreshAbsoluteDays),
            sp.GetRequiredService<ILogger<SessionRevokeHandler>>()));

        // This node's own copy of the cluster's accounts. It is what lets authority be resolved here
        // rather than by asking the member that holds them — so a demotion lands on the next request
        // and an outage over there costs this node nothing it serves. The replica itself lives on
        // UserDirectory, which already owns this host's answer to a store it cannot read.
        // The replica the handlers apply to is this host's account store, which UserDirectory already
        // owns along with its whole answer to a store it cannot read.
        services.AddSingleton<IReplicatedAccounts>(sp => sp.GetRequiredService<UserDirectory>());

        services.AddSingleton<IClusterMessageHandler, AccountReplicationHandler>();
        services.AddSingleton<IClusterMessageHandler, AccountRemovalHandler>();

        // The first full copy. The stream alone would leave a node holding only what changed after it
        // joined, resolving everybody who existed before that as a stranger.
        services.AddHostedService<AccountSnapshotWorker>();

        // The roster-backed fan-out target list. A durable, identity-carrying message goes only to members
        // this node has authenticated first-hand — never to one it has merely heard about — or the outbox
        // would retry a secret-bearing message at a phantom for the full retry window.
        services.AddSingleton<RosterClusterTargetProvider>();

        // Cluster resource visibility (PLAN-peers.md P2). The server-side node-proxy that fans a resource read
        // out to a peer's self/* surface — reuses the OutboxDrainer named HttpClient (mint-authed, 10s bound),
        // so no new client. The self/* exposing endpoints live on PeersController (no extra service).
        services.AddSingleton<ClusterPeerRelay>();

        // Auth — per-host, Model A. The sign-in seam (ISignInService, from TheKrystalShip.KGSM.Auth)
        // keeps the login behind one interface shared with every other KGSM surface, so the whole
        // 401/403/tier matrix is testable in-process with a fake and no two surfaces can resolve a
        // person differently. The token service mints/validates the host-scoped JWTs; the tier handler
        // grants a hierarchical viewer/operator/admin policy from the 'tier' claim.
        // What tokens are signed with is resolved once, here, and never re-read: a configured key wins,
        // and a host given none generates one and keeps it, so sessions outlive a restart on a machine
        // nobody handed a secret to.
        services.AddSingleton<HostSigningKey>();

        // The other kind of session this host accepts: one the cluster's auth anchor minted, verified
        // against the key it publishes. The keys are read through the capability's holder and refreshed
        // at the gossip cadence, so a reassignment or a rotation needs no restart here. A host that is
        // not in a cluster learns nothing and accepts only its own sessions, which is the whole of what
        // a standalone install has ever done.
        // Whether this node still answers for its own accounts, or its cluster has an anchor that
        // does. Read from cluster state rather than configured, so it follows an anchor joining or
        // being reassigned with nothing to change here.
        services.AddSingleton<AnchorHeldGate>();

        services.AddSingleton<ClusterSessionKeys>();
        services.AddSingleton<IClusterSessionKeys>(sp => sp.GetRequiredService<ClusterSessionKeys>());
        services.AddHostedService(sp => sp.GetRequiredService<ClusterSessionKeys>());

        // A cluster session has no row here, so the only thing worth storing about one is that it has
        // been ended. Same cache bound as the validator beside it, for the same reason.
        services.AddSingleton(sp => new ClusterSessionRevocations(
            sp.GetRequiredService<IClusterSessionAuthority>(),
            sp.GetRequiredService<IMemoryCache>(),
            TimeSpan.FromMilliseconds(sp.GetRequiredService<ApiOptions>().SessionsCacheTtlMs)));

        services.AddSingleton<ISessionTokenService>(sp => new SessionTokenService(
            sp.GetRequiredService<ApiOptions>().ToSessionTokenOptions()
                with { SigningKey = sp.GetRequiredService<HostSigningKey>().Value },
            sp.GetRequiredService<ILogger<SessionTokenService>>()));
        // The callback URL is this surface's own; the application is the host's. Both are projected
        // from ApiOptions rather than re-read from configuration: ApiOptions is the single place any
        // key is interpreted, and a second reader is how two halves of one setting drift apart.
        services.AddSingleton(sp => sp.GetRequiredService<ApiOptions>().OAuth);
        // This host's own accounts. A singleton because it wraps one SQLite file that every request
        // reads — the store opens connections per operation and pools them, so nothing is held. It is
        // NOT on AppDbContext: this API's database is operational state and is wiped whenever its
        // schema changes, and accounts cannot be. Opening it can fail (a permission problem, or a file
        // written by a newer sibling on this host); UserDirectory captures that as a capability rather
        // than letting it decide whether the Control Panel starts.
        services.AddSingleton<UserDirectory>();

        // The first start on a host with no accounts creates the administrator that start-of-life
        // needs, and leaves its password in the state directory. Every start after that finds accounts
        // and does nothing.
        services.AddHostedService<HostBootstrapper>();

        // The two halves of a sign-in come from two different places, which is the whole reason they
        // are separate seams. A provider says WHO someone is (IIdentityProvider) and contributes
        // nothing else; the account store says what they may DO (IAuthorityProvider), and is the only
        // thing that ever does. A group or a role is not an answer to the second question and is not
        // read here at all: any provider account can be attached to any KGSM account, and where else
        // it can get in says nothing about either.
        services.AddTransient<IAuthorityProvider, DirectoryAuthority>();

        // One registration per provider, and the ONLY place in this API a provider is named.
        // Everything above takes the name off the route and asks the catalog, so wiring up another is
        // a line here and nothing anywhere else.
        //
        // Each is built per resolution, like the typed client it wraps: holding one in a singleton
        // pins one handler for the process lifetime, so the factory's rotation — and with it DNS
        // refresh — silently stops. The redirect URI is handed in rather than read, because the same
        // provider serves two flows that end differently and must name different callbacks.
        //
        // "identify guilds", not the package's leaner "identify" default: the granted scopes are
        // surfaced on GET /auth/session and /me and the SPA reads them, so narrowing the set would be
        // a visible contract change. Neither scope contributes to authority — nothing Discord grants
        // does.
        services.AddHttpClient(nameof(DiscordDirectory), c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton(new AuthProviderRegistration(
            KgsmActorProvider.Discord,
            (sp, application, redirectUri) => new DiscordDirectory(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DiscordDirectory)),
                application,
                new DiscordOAuthEndpoints(redirectUri, "identify guilds"))));
        services.AddSingleton<IAuthProviderCatalog, AuthProviderCatalog>();

        // Authority on every request, from the store, replacing the tier the token was minted with.
        services.AddSingleton<LiveAuthority>();

        // Changing what proves an account needs the credential proved again, and a link in flight has
        // to remember whose account it is without telling the browser. Both are per-process on purpose
        // (see LinkFlow.cs): a restart makes everyone prove themselves again and drops links in flight,
        // which costs a click and cannot grant anything.
        services.AddSingleton(sp => new ReauthGate(
            TimeSpan.FromMinutes(sp.GetRequiredService<ApiOptions>().ReauthWindowMinutes)));
        services.AddSingleton<LinkTicketStore>();
        services.AddSingleton<IAuthorizationHandler, TierAuthorizationHandler>();

        // Auth is ON by default; Api__AuthDisabled=true swaps the default scheme for a synthetic-admin
        // handler so every policy passes (the explicit, loudly-logged dev/open window). When enabled, the
        // JwtBearer scheme validates the session JWTs with the SAME parameters the token service mints under
        // (shared via the post-configure below). SSE streams carry the bearer as an Authorization header;
        // a refresh token is never accepted as an access bearer.
        // Opening the door is allowed; opening it anonymously is not. Every request that comes through
        // it is attributed to Api__DisabledAuthActor and lands in the audit log under that name, so the
        // host refuses to start until it has been told a real one. Failing here rather than at the first
        // request is deliberate: the alternative is a host that runs fine and mis-attributes everything.
        if (apiOptions.AuthDisabled && !KgsmActor.TryParse(apiOptions.DisabledAuthActor, out _, out _))
        {
            throw new InvalidOperationException(
                "Api__AuthDisabled is set but Api__DisabledAuthActor is not a 'provider:name' actor "
                + $"(got '{apiOptions.DisabledAuthActor}'). Every request on an auth-disabled host is "
                + "attributed to it, so it must name somebody — e.g. 'local:claude'.");
        }

        string defaultScheme = apiOptions.AuthEnabled
            ? JwtBearerDefaults.AuthenticationScheme
            : DisabledAuthHandler.SchemeName;
        // One door with two kinds of caller behind it. A person presents a session; another member of
        // this cluster presents its own service token and names the person it is acting for, because
        // that person may never have signed in here at all — they asked the cluster's assistant
        // something in Discord, and the assistant runs on a different machine.
        //
        // Which handler answers is decided by the acting header, not by trying one and falling back:
        // the two establish trust in completely different ways, and a fallback would mean a failed
        // member-acting call quietly re-examined as a session.
        const string routingScheme = "KgsmCaller";
        AuthenticationBuilder authBuilder = services.AddAuthentication(routingScheme);

        authBuilder.AddPolicyScheme(routingScheme, routingScheme, o =>
            o.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey(MemberActing.ActingHandleHeader)
                    ? MemberActing.Scheme
                    : defaultScheme);

        // Registered whether or not this host is clustered. Without a cluster secret the token check
        // refuses every caller, which is the correct answer for a member-to-member call on a machine
        // that is in no cluster.
        authBuilder.AddScheme<AuthenticationSchemeOptions, MemberActingHandler>(
            MemberActing.Scheme, displayName: null, configureOptions: null);
        if (apiOptions.AuthEnabled)
        {
            authBuilder.AddJwtBearer(options =>
            {
                options.MapInboundClaims = false; // keep claim types verbatim ("sub", "tier", …)
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        // A refresh token authenticates ONLY /auth/session/refresh, never a protected call.
                        if (ctx.Principal?.FindFirst(KgsmAuthClaims.TokenKind)?.Value != KgsmTokenKind.Access)
                        {
                            ctx.Fail("not an access token");
                            return;
                        }

                        // The per-request session check, cached. It runs AFTER the access-kind gate (a
                        // refresh token never reaches here) and AFTER the signature, issuer, audience
                        // and lifetime have all been confirmed — so what is left to establish is only
                        // whether the session behind a genuine token is still live. The SSE path rides
                        // the same event, its bearer set by OnMessageReceived for /stream.
                        //
                        // Api__SessionsDisabled bypasses the whole block, which is the stateless-JWT
                        // posture kept for debugging. A token carrying no sid is refused: a session
                        // nothing can revoke is not one this host is willing to hold open.
                        var svc = ctx.HttpContext.RequestServices;
                        var opts = svc.GetRequiredService<ApiOptions>();
                        if (!opts.SessionsEnabled)
                            return;

                        string? sid = ctx.Principal?.FindFirst(KgsmAuthClaims.SessionId)?.Value;
                        if (string.IsNullOrEmpty(sid))
                        {
                            ctx.Fail("no session id");
                            return;
                        }

                        if (ctx.Principal?.Identity is not ClaimsIdentity claims)
                        {
                            ctx.Fail("the bearer carries no claims identity");
                            return;
                        }

                        // Two kinds of session, held to opposite questions about the same table.
                        //
                        // One this host minted has a row, so the row IS the session: no live row means
                        // no session, and the check is an allow-list.
                        //
                        // One the cluster's auth anchor minted has no row here — the sign-in happened
                        // on another machine — and is accepted because its signature verifies against
                        // the key that member publishes. There is nothing to look up, so the only
                        // thing worth storing is that somebody ended it, and the check is a deny-list.
                        // Running the allow-list against a cluster session would refuse every one of
                        // them, which is "sign in once" failing on every member but the anchor.
                        if (ClusterSessionValidation.IsClusterSession(claims, opts.HostId))
                        {
                            if (await svc.GetRequiredService<ClusterSessionRevocations>()
                                .IsRevokedAsync(sid, ctx.HttpContext.RequestAborted).ConfigureAwait(false))
                            {
                                ctx.Fail("cluster session ended");
                                return;
                            }
                        }
                        else if (!await svc.GetRequiredService<ISessionValidator>()
                            .IsValidAsync(sid, ctx.HttpContext.RequestAborted).ConfigureAwait(false))
                        {
                            ctx.Fail("session revoked or expired");
                            return;
                        }

                        // Authority, resolved now rather than read off the token. The `tier` claim
                        // the token was minted with is replaced with what the account store says
                        // today, so a demotion lands within the authority cache TTL instead of
                        // whenever the token happens to rotate, and this API and the assistant beside
                        // it — which re-derives per request — cannot disagree about the same person.
                        // A disabled account fails here, which is what makes the switch cut live
                        // sessions on every surface with no cross-service call.
                        if (await svc.GetRequiredService<LiveAuthority>()
                            .ApplyAsync(claims, ctx.HttpContext.RequestAborted).ConfigureAwait(false) is { } refusal)
                        {
                            ctx.Fail(refusal);
                        }
                    },

                    // An unreachable account store is not a bad token. Answering the standard 401
                    // would send a browser back to a sign-in that reads the same file and fails the
                    // same way, so the challenge for that one failure is a 502 naming it — the same
                    // posture as an unreachable identity provider at the callback.
                    OnChallenge = ctx =>
                    {
                        if (ctx.AuthenticateFailure is not AuthorityUnavailableException failure)
                            return Task.CompletedTask;

                        ctx.HandleResponse();
                        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                        ctx.Response.ContentType = "application/json";
                        return ctx.Response.WriteAsJsonAsync(
                            new ErrorEnvelope(new ErrorBody("authority_unavailable", failure.Message)));
                    },
                };
            });
            // The signing key lives in the token service (derived once); its rules are shared so this
            // host's access and refresh tokens validate identically, and widened so the anchor's
            // sessions validate beside them under rules of their own.
            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<ISessionTokenService, IClusterSessionKeys>((o, tokens, clusterKeys) =>
                    o.TokenValidationParameters =
                        ClusterSessionValidation.Accepting(tokens.ValidationParameters, clusterKeys));
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
            o.AddPolicy(AuthPolicy.Viewer, p => p.Requirements.Add(new TierRequirement(KgsmTier.Viewer)));
            o.AddPolicy(AuthPolicy.Operator, p => p.Requirements.Add(new TierRequirement(KgsmTier.Operator)));
            o.AddPolicy(AuthPolicy.Admin, p => p.Requirements.Add(new TierRequirement(KgsmTier.Admin)));
            // Secure-by-default: any endpoint without an explicit [Authorize]/[AllowAnonymous] still
            // requires an authenticated caller — so a future controller can't ship silently open. The
            // open probes (/health, /api/v1) opt out with [AllowAnonymous]; diagnostics are admin-gated.
            // (Under the disabled escape hatch the synthetic-admin scheme satisfies this too.)
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        // Per-caller throttling for the anonymous doors that touch credentials — sign in, and sign
        // up. The account store's own lockout is exponential and keyed on the account being guessed
        // at, which is the right shape for protecting one person and the wrong shape for two things:
        // one password sprayed across many usernames locks nobody out, and registration has no
        // account to lock. Both throttles stay; they answer different questions.
        //
        // Partitioned on the client address, which is the forwarded one — UseForwardedHeaders runs
        // first and only trusts a proxy on this machine, so this cannot be widened by a header a
        // stranger appended. A caller with no address at all (a unit test's in-memory transport)
        // falls into one shared bucket rather than escaping the limiter.
        //
        // Ten a minute: a person who mistypes a password several times and then registers never
        // meets it, and a script trying to enumerate does so on its first breath.
        services.AddRateLimiter(o =>
        {
            o.AddPolicy(RateLimitPolicy.Anonymous, http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = apiOptions.AnonymousRateLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            // The frozen {error} envelope, with the same code the account lockout uses — a caller
            // being throttled and a caller being locked out are one thing to whoever is typing, and
            // a client that handles one handles both.
            o.OnRejected = async (context, ct) =>
            {
                int seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : 60;
                context.HttpContext.Response.Headers.RetryAfter =
                    seconds.ToString(CultureInfo.InvariantCulture);
                await ApiErrors.WriteAsync(
                    context.HttpContext, StatusCodes.Status429TooManyRequests, "too_many_attempts",
                    $"Too many attempts. Try again in {seconds}s.");
            };
        });

        // Behind a reverse proxy the request this app sees is the PROXY's: plain http, from 127.0.0.1.
        // Without translating the forwarded headers, `Request.IsHttps` is false on every request — and
        // the OAuth CSRF state cookie is written `Secure = Request.IsHttps`, so a browser login would
        // quietly downgrade to a non-Secure cookie while continuing to work. Client addresses would
        // likewise all read as loopback, making the audit log's actor-vs-origin story meaningless.
        //
        // Trust is restricted to a proxy on this machine. The middleware honours these headers only
        // when the IMMEDIATE PEER is a known proxy, so a request arriving from the internet carrying a
        // forged X-Forwarded-Proto is ignored — which is also what makes this safe to run with no proxy
        // in front at all. ForwardLimit 1: exactly one hop is expected, and a longer chain would mean
        // trusting a header a stranger appended.
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            // Replace the framework defaults rather than adding to them, so the trusted set is exactly
            // what is written here.
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Add(IPAddress.Loopback);
            o.KnownProxies.Add(IPAddress.IPv6Loopback);
            // X-Forwarded-Host is deliberately NOT trusted: the proxy passes the original Host through
            // untouched, so Request.Host is already right and there is one fewer header to believe.
        });

        // Error contract over the default ProblemDetails body. AddProblemDetails is
        // registered only to satisfy UseExceptionHandler's startup guard — ApiExceptionHandler
        // always handles, so the ProblemDetails fallback never fires.
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();

        // CORS answers from two places: the configured allowlist, and the panel origins this node has
        // learned — an origin an admin signed in from here, or one a peer carried over in an introduce
        // exchange (PLAN-peers.md P0.6). That is what lets a panel served from somewhere that is not a node
        // reach every node in a cluster without a per-node allowlist. When neither names anything we allow
        // any origin (dev only — safe because bearers ride the Authorization header, not cookies).
        IReadOnlyList<string> corsOrigins = apiOptions.CorsOrigins;
        services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
            policy.SetIsOriginAllowed(origin =>
                  {
                      IReadOnlyList<string>? learned = _corsPanelOrigins?.CachedPanelOrigins();
                      if (corsOrigins.Count == 0 && (learned is null || learned.Count == 0))
                          return true;

                      string? normalized = SelfIdentityStore.Normalize(origin);
                      if (normalized is null) return false;

                      return corsOrigins.Any(o => string.Equals(
                                 SelfIdentityStore.Normalize(o), normalized, StringComparison.OrdinalIgnoreCase))
                             || (learned?.Any(o => string.Equals(o, normalized, StringComparison.OrdinalIgnoreCase))
                                 ?? false);
                  })
                  .AllowAnyHeader()
                  .AllowAnyMethod()));
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory)
    {
        // Make the trust posture impossible to miss in the logs.
        ApiOptions options = app.ApplicationServices.GetRequiredService<ApiOptions>();
        _corsPanelOrigins = app.ApplicationServices.GetRequiredService<SelfIdentityStore>();
        // Warm it before the first request: the CORS predicate reads this cache synchronously, and an
        // unloaded cache is indistinguishable from an empty one — which would silently widen a node that
        // has learned exactly which origin its panel is served from. A store that cannot be read leaves the
        // configured allowlist in charge rather than taking the host down.
        try { _corsPanelOrigins.PrimeAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TheKrystalShip.Api.Startup")
                .LogWarning(ex, "could not read this node's learned addresses and panel origins at startup");
        }
        ILogger startupLog = loggerFactory.CreateLogger("TheKrystalShip.Api.Startup");
        if (options.AuthDisabled)
            startupLog.LogWarning(
                "AUTH DISABLED (Api__AuthDisabled) — every request is authenticated as admin and "
                + "attributed to {Actor}. Never enable this on an exposed host.",
                options.DisabledAuthActor);
        else if (app.ApplicationServices.GetRequiredService<IAuthProviderCatalog>().Configured is { Count: 0 })
            startupLog.LogWarning(
                "Auth is ON but this host is wired to no identity provider — the /auth/{{provider}}/* "
                + "login endpoints and identity linking will 503 until an application "
                + "(KgsmAuth__Providers__<name>__ClientId) and this host's redirect URI are set. A KGSM "
                + "password still signs anyone in; protected endpoints require a bearer (401).");

        // Same-origin SPA delivery: when the Control Panel SPA's built bundle is present in the web root
        // (the deploy drops kgsm-web's dist/ into wwwroot), Kestrel serves it at / on the SAME origin as
        // the API — one domain, no CORS. Gated on the bundle actually being there, so a dev run (no
        // wwwroot; the SPA on the Vite dev server) and an API-only deploy both no-op cleanly here.
        string? spaWebRoot = env.WebRootPath;
        bool serveSpa = !string.IsNullOrEmpty(spaWebRoot) && File.Exists(Path.Combine(spaWebRoot, "index.html"));
        if (serveSpa)
            startupLog.LogInformation("Serving the Control Panel SPA from {WebRoot} (same-origin).", spaWebRoot);

        // FIRST, before anything reads the scheme or the caller's address: rewrite the request from
        // what the proxy sent us into what the client actually asked for. Everything downstream — the
        // https upgrade below, cookie Secure flags, audit origins — reads the corrected values.
        app.UseForwardedHeaders();

        app.UseExceptionHandler(); // unhandled -> 500 error envelope (ApiExceptionHandler)

        // HTTP → HTTPS upgrade (production posture: NO bare HTTP on the internet). A plain-HTTP request
        // from a client OUT ON THE INTERNET is permanently redirected (308 — it preserves the method and
        // body, so a POST upgrades cleanly instead of being silently turned into a GET) to its https://
        // equivalent on the standard port (the inbound :80 is dropped → :443).
        //
        // The gate is the CALLER's address, because "on the internet" is a fact about the caller and
        // nothing else. A caller on loopback or a private network has no internet hop to protect: the
        // deploy's `curl http://127.0.0.1:8097/health` doesn't speak TLS and the cert isn't valid for
        // 127.0.0.1, and a reverse proxy on another machine in the same network has already terminated TLS
        // for the real client and is talking to us over the operator's own wire. Redirecting either would
        // send them to an address that lands right back here — a loop, not an upgrade.
        //
        // The receiving interface cannot answer this question: behind NAT a request from the internet
        // arrives on a private LAN address exactly as a request from the next room does, so reading the
        // local address would exempt genuine internet traffic on every home-network node and refuse the
        // proxy on none. For an external http:// to reach here at all, Api__Urls must include a plain-http
        // public bind (http://0.0.0.0:80); without one, bare http simply refuses the connection.
        app.Use(async (context, next) =>
        {
            System.Net.IPAddress caller = context.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback;
            if (!context.Request.IsHttps && IsOutOnTheInternet(caller))
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

        app.UseRouting();
        app.UseCors(CorsPolicy);
        // After UseRouting, or the endpoint carrying [EnableRateLimiting] is not known yet and the
        // policy silently never applies. Before authentication, so a throttled caller is refused
        // without the store being read at all.
        app.UseRateLimiter();
        // M4·a — auth pipeline (the M0 placeholder, now filled). Authentication populates User from the
        // bearer (or the synthetic-admin scheme when disabled); authorization enforces the [Authorize]
        // tier policies. A 401/403 here flows through UseStatusCodePages above into the {error} envelope.
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();

            // The member-to-member wire, served by the cluster package beside this API's own routes:
            // one implementation of the status codes, the size cap, the token check and the spoof
            // guard, so two members cannot come to disagree about what the protocol is.
            endpoints.MapClusterEndpoints();

            // SPA fallback: a client-routed GET (deep link / refresh — no file extension, matched no
            // controller) boots the app by returning index.html. Asset files (with extensions) were
            // already served by UseStaticFiles, so they never reach this :nonfile fallback.
            // .AllowAnonymous() is LOAD-BEARING: without it the endpoint inherits the global
            // RequireAuthenticatedUser fallback policy and returns 401 for the SPA shell — i.e. nobody could
            // even load the login page. The bundle is a PUBLIC static site; the DATA under /api/v1 stays gated.
            //
            // Two things are never the SPA shell, and both are here because the shell is a 200: a
            // caller cannot tell "this route is gone" from "here is a web page" by status alone, and
            // will conclude the route still exists.
            //
            //  - Anything under an API prefix. /auth/* is one of them: it sits at the root beside /api
            //    rather than under it, so naming only /api leaves the whole auth surface answering 200
            //    HTML to a path that does not exist.
            //  - Anything that is not a GET or a HEAD. A deep link is a navigation; nothing client-routed
            //    arrives as a POST, so a POST that matched no controller is a caller in error and is
            //    owed an answer that says so.
            //
            // Both fall through to the {error} envelope (invariant #4).
            if (serveSpa)
            {
                string indexFile = Path.Combine(spaWebRoot!, "index.html");
                endpoints.MapFallback(async context =>
                {
                    bool apiPath =
                        context.Request.Path.StartsWithSegments("/api")
                        || context.Request.Path.StartsWithSegments("/auth");

                    bool navigation =
                        HttpMethods.IsGet(context.Request.Method)
                        || HttpMethods.IsHead(context.Request.Method);

                    // Re-checked per request, not only at startup. The bundle is a directory a
                    // deploy replaces and an operator can empty, and a shell that has gone since this
                    // host started would otherwise throw out of SendFileAsync — an unhandled 500 and
                    // a stack trace per request, for a host that simply has no panel any more.
                    if (apiPath || !navigation || !File.Exists(indexFile))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.SendFileAsync(indexFile);
                }).AllowAnonymous();
            }
            else
            {
                // No panel here, and saying so is the whole job. Without this an unmatched path meets
                // the global RequireAuthenticatedUser fallback policy — which applies to a request
                // with no endpoint at all — and answers 401: "sign in and you will see it", about a
                // path that does not exist. On a node whose cluster has an anchor that is doubly
                // wrong, because signing in here is exactly what it refuses to let anybody do.
                //
                // A node that serves no panel is an ordinary node. It should read as one.
                //
                // The explicit "{*path}" matters: MapFallback's default pattern is {*path:nonfile},
                // which skips anything whose last segment looks like a file — so /index.html would
                // match no endpoint at all and answer 401 through the same policy, which is the exact
                // path somebody types when they wonder whether a panel is there.
                endpoints.MapFallback("{*path}", context =>
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }).AllowAnonymous();
            }
        });
    }
}
