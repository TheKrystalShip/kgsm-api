using System.Net.Sockets;

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

    public AssistantClient(ApiOptions options, ILogger<AssistantClient> logger)
        : base(NewHandler(), disposeHandler: true)
    {
        _logger = logger;

        if (options.AssistantProvisioned
            && Uri.TryCreate(options.AssistantBaseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            BaseAddress = baseUri;
            IsProvisioned = true;
        }
    }

    /// <summary>True when an assistant base URL is configured on this host (capability is declared).</summary>
    public bool IsProvisioned { get; }

    /// <summary>
    /// Provisional liveness probe for the §4·b assistant capability: any HTTP response means the
    /// assistant process is reachable. Returns <c>false</c> on timeout, unreachable, or any error
    /// — it never throws, and never blocks longer than <see cref="ProbeTimeout"/>. The real
    /// readiness/health contract is defined with the SSE relay at M7.
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        if (!IsProvisioned)
            return false;

        // Self-bound to the probe budget, independent of the client's (default) Timeout, while
        // still honoring caller cancellation through the linked token.
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ProbeTimeout);
        try
        {
            // Headers-only: we only need "did it respond", not the body.
            using HttpResponseMessage _ = await this
                .GetAsync("", HttpCompletionOption.ResponseHeadersRead, timed.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("assistant probe timed out after {Timeout}", ProbeTimeout);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "assistant probe failed");
            return false;
        }
    }

    // Recycle pooled connections so a process-lifetime singleton never pins a stale one (the
    // documented long-lived-HttpClient alternative to IHttpClientFactory). Largely moot for a
    // localhost leaf, but correct and explicit about intent.
    private static SocketsHttpHandler NewHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };
}
