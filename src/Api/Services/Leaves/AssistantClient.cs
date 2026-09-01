using System.Net.Http.Json;
using System.Net.Sockets;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.Kgsm.Assistant.Relay;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The kgsm-assistant leaf client: a typed <see cref="HttpClient"/> onto the assistant's HTTP
/// surface (a co-located leaf reached over plain TCP, unlike the monitor's unix socket). Today
/// it exposes only a liveness <see cref="ProbeAsync"/> used to report the architecture §4·b
/// assistant capability, but it is the deliberate home for the assistant's real surface as it
/// lands — the tool catalog, capability discovery, and the SSE turn relay (M7) — so callers
/// depend on typed methods here rather than raw HTTP scattered across the aggregator.
/// </summary>
/// <remarks>
/// When the assistant is not provisioned (no base URL configured) the client is constructed in a
/// disabled state (<see cref="IsProvisioned"/> false) and every call short-circuits — the §4·b
/// capability renders <c>absent</c>, never a broken <c>down</c>. Registered as a singleton; the
/// recycling connection pool (<see cref="SocketsHttpHandler.PooledConnectionLifetime"/>) is the
/// documented way to keep a process-lifetime <see cref="HttpClient"/> from pinning a stale
/// connection without IHttpClientFactory. Note the client's <see cref="HttpClient.Timeout"/> is
/// left at its default on purpose: the 2s budget is the <em>liveness probe's</em>, applied per
/// call via a linked token — it must not become a class-wide ceiling on the slower calls (tool
/// fetch, SSE connect) this client will grow.
/// </remarks>
public sealed class AssistantClient : HttpClient
{
    // Liveness-probe budget only — bound the probe so a hung assistant can never stall a /hosts
    // request. Applied per call (NOT as HttpClient.Timeout) so future, slower calls are free to
    // set their own budget. Aligned with the other leaf probes (HostAggregator.ProbeTimeout).
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<AssistantClient> _logger;
    private readonly LeafRegistry _registry;
    // Identity and authority go on the wire through the assistant's own relay contract, so this API
    // cannot drift from the filter that reads them — or from the bot that writes them too.
    private readonly bool _hasBaseUrl;

    public AssistantClient(ApiOptions options, LeafRegistry registry, ILogger<AssistantClient> logger)
        : base(NewHandler(), disposeHandler: true)
    {
        _logger = logger;
        _registry = registry;

        // Unbounded class Timeout — the probe sets its OWN budget via a linked token, which is the
        // pattern the type remarks ask for rather than a class-wide ceiling every future call inherits.
        Timeout = System.Threading.Timeout.InfiniteTimeSpan;

        // Set the base address from the configured URL whenever one is present (independent of the runtime
        // provisioning flag) so a connect/disconnect arms/disarms the client live without a restart. Without
        // a configured URL there is no endpoint to flip to (no universal default), so the runtime flip can
        // only ever report the capability down — the honest limit, noted in the feature plan.
        if (Uri.TryCreate(options.AssistantBaseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            BaseAddress = baseUri;
            _hasBaseUrl = true;
        }

    }

    /// <summary>True when the assistant is provisioned (connected) on this host at runtime AND a base URL is
    /// configured to reach it. The capability/relay calls all gate on this, so disconnecting the assistant
    /// disarms them live.</summary>
    public bool IsProvisioned => _hasBaseUrl && _registry.IsProvisioned(ProvisionableLeaf.Assistant);

    /// <summary>
    /// Liveness probe for the §4·b assistant capability, run through the assistant's own shared probe
    /// so every leaf that consumes it agrees on what "up" means: <c>GET /health</c>, a 2xx, within
    /// <see cref="ProbeTimeout"/>. Never throws. An unprovisioned assistant is short-circuited here
    /// rather than probed, so the capability renders <c>absent</c> instead of a broken <c>down</c>.
    /// </summary>
    public Task<bool> CheckHealthAsync(CancellationToken ct) =>
        IsProvisioned
            ? AssistantHealthProbe.CheckAsync(this, ct, _logger, ProbeTimeout)
            : Task.FromResult(false);

    // Recycle pooled connections so a process-lifetime singleton never pins a stale one (the
    // documented long-lived-HttpClient alternative to IHttpClientFactory). Largely moot for a
    // localhost leaf, but correct and explicit about intent.
    private static SocketsHttpHandler NewHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };
}
