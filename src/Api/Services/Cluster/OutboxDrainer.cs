using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Json;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The cluster bus's outbox drainer (<c>docs/cluster-message-bus-plan.md §6</c>) — a
/// <see cref="BackgroundService"/> in the mould of <see cref="Auth.SessionCleanupWorker"/>: inert
/// (early return, no timer at all) when <see cref="ApiOptions.ClusterEnabled"/> is false, a startup
/// catch-up pass, then a <see cref="PeriodicTimer"/> loop (<see cref="ApiOptions.ClusterDrainMs"/>) with a
/// per-tick <c>try/catch</c> that swallows non-cancellation exceptions — one bad tick (a DB hiccup, an
/// unexpected exception) must never kill the drainer; the next tick tries again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per drain pass</b> (<see cref="RunDrainPassAsync"/>): pull up to <see cref="DrainCap"/> due
/// (<c>pending</c>, <c>NextAttemptAt &lt;= now</c>) rows oldest-first, and process each sequentially — the
/// plan notes bounded per-target parallelism as a future optimization; sequential is correct and simple
/// for the message volumes this bus targets today.
/// </para>
/// <para>
/// <b>Per-row status→action</b> (<see cref="ProcessRowAsync"/>):
/// <list type="bullet">
/// <item>TTL check FIRST — a row older than <see cref="ApiOptions.ClusterRetryTtlDays"/> (anchored on
/// <see cref="OutboxMessage.CreatedAt"/>) is dead-lettered with a loud log, without ever being sent.</item>
/// <item><c>2xx</c> → delivered.</item>
/// <item><c>400</c>/<c>401</c>/<c>403</c>/<c>413</c> (a permanent reject) → dead-lettered with a loud log
/// — this is a LOCAL misconfiguration signal (wrong secret / a peer that disabled us), not a lost
/// message, so it is surfaced rather than silently retried forever.</item>
/// <item>Anything else — a thrown exception (connect refused, DNS failure, client-side timeout), a
/// <c>5xx</c>, or any other non-2xx/non-permanent status — is treated as transient:
/// <see cref="OutboxMessage.Attempts"/> increments, <see cref="OutboxMessage.NextAttemptAt"/> moves out by
/// <see cref="ComputeBackoff"/>, the row stays <c>pending</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Backoff:</b> capped exponential with jitter — <c>min(cap, base · 2^(attempts-1))</c> plus a random
/// jitter, <c>base=1s</c>, <c>cap=5min</c> (plan §6). Jitter avoids a thundering herd when many rows
/// targeting the same recovered peer all become due at once.
/// </para>
/// <para>
/// <b>Deliberately NOT built here</b> (plan §6/§12, M-bus·b): the latency-poller / <c>node.online</c>
/// immediate-flush coupling. The durable retry loop above is the correctness mechanism regardless; that
/// coupling is a pure latency optimization for a later phase.
/// </para>
/// </remarks>
public sealed class OutboxDrainer : BackgroundService
{
    /// <summary>The named <see cref="HttpClient"/> (via <see cref="IHttpClientFactory"/>) this drainer
    /// posts envelopes with — registered in <c>Startup</c> with a short (~10s) timeout.</summary>
    public const string HttpClientName = "cluster-outbox-drainer";

    /// <summary>Max due rows processed in a single drain pass — a sane per-tick ceiling so one tick can
    /// never unboundedly balloon; the next tick picks up whatever remains due.</summary>
    private const int DrainCap = 100;

    private static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromMinutes(5);

    private static readonly HttpStatusCode[] PermanentRejectCodes =
    [
        HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden,
        HttpStatusCode.RequestEntityTooLarge,
    ];

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = BuildJsonOptions();

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var options = new JsonSerializerOptions();
        ApiJson.Configure(options);
        return options;
    }

    private readonly ClusterBus _bus;
    private readonly IClusterTokenService _tokens;
    private readonly ApiOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OutboxDrainer> _logger;

    public OutboxDrainer(
        ClusterBus bus,
        IClusterTokenService tokens,
        ApiOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<OutboxDrainer> logger)
    {
        _bus = bus;
        _tokens = tokens;
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ClusterEnabled)
        {
            _logger.LogInformation("outbox drainer inert — cluster not configured");
            return;
        }

        _logger.LogInformation(
            "outbox drainer: started (interval={IntervalMs}ms, retryTtl={RetryTtlDays}d)",
            _options.ClusterDrainMs, _options.ClusterRetryTtlDays);

        // Startup catch-up pass — a host that was down for a while shouldn't wait a full interval
        // before it starts working through whatever backlog accumulated.
        await RunDrainPassAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.ClusterDrainMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunDrainPassAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "outbox drainer: tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    /// <summary>One drain pass: due-scan, then process each row in turn. Public so tests can drive a
    /// pass deterministically without the <see cref="PeriodicTimer"/> loop's timing.</summary>
    public async Task RunDrainPassAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<OutboxMessage> due = await _bus.ListDueAsync(now, DrainCap, ct).ConfigureAwait(false);
        foreach (OutboxMessage row in due)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessRowAsync(row, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessRowAsync(OutboxMessage row, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // TTL check first — a row this old will never be given a fresh chance; don't even try.
        if (now - row.CreatedAt > TimeSpan.FromDays(_options.ClusterRetryTtlDays))
        {
            await _bus.MarkDeadAsync(row.Id, "retry TTL exceeded", ct).ConfigureAwait(false);
            _logger.LogError(
                "cluster outbox: {Id} (target={Target} type={Type}) exceeded the {Days}-day retry TTL " +
                "— dead-lettered without being sent",
                row.Id, row.TargetNodeId, row.Type, _options.ClusterRetryTtlDays);
            return;
        }

        string body;
        try
        {
            var envelope = new ClusterEnvelope(
                row.MessageId, row.Type, _options.NodeId, row.CreatedAt,
                JsonSerializer.Deserialize<JsonElement>(row.Payload));
            body = JsonSerializer.Serialize(envelope, EnvelopeJsonOptions);
        }
        catch (JsonException ex)
        {
            // The stored payload itself is unparseable — this can never succeed no matter how many
            // times we retry it. Dead-letter rather than loop forever on a message that will never send.
            await _bus.MarkDeadAsync(row.Id, $"malformed stored payload: {ex.Message}", ct)
                .ConfigureAwait(false);
            _logger.LogError(ex,
                "cluster outbox: {Id} has an unparseable stored payload — dead-lettered", row.Id);
            return;
        }

        string url = $"{row.TargetUrl.TrimEnd('/')}/api/v1/peers/inbox";
        HttpResponseMessage? response = null;
        string? transientError = null;
        try
        {
            MintedClusterToken token = _tokens.Mint();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            HttpClient http = _httpClientFactory.CreateClient(HttpClientName);
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real shutdown — propagate, don't treat this as a delivery failure
        }
        catch (Exception ex)
        {
            // Connect refused, DNS failure, the client's own request timeout (a TaskCanceledException
            // whose token is NOT ours), or any other transport-level failure — all transient.
            transientError = ex.Message;
        }

        try
        {
            if (response is not null && response.IsSuccessStatusCode)
            {
                await _bus.MarkDeliveredAsync(row.Id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                return;
            }

            if (response is not null && PermanentRejectCodes.Contains(response.StatusCode))
            {
                await _bus.MarkDeadAsync(row.Id, $"peer rejected: {(int)response.StatusCode}", ct)
                    .ConfigureAwait(false);
                _logger.LogError(
                    "cluster outbox: {Id} (target={Target}) permanently rejected by the peer " +
                    "(HTTP {Status}) — dead-lettered; this is a LOCAL misconfiguration signal (wrong " +
                    "cluster secret, or the peer disabled us), not a lost message",
                    row.Id, row.TargetNodeId, (int)response.StatusCode);
                return;
            }

            // Transient: an exception, a 5xx, or any other non-2xx/non-permanent status.
            string error = transientError ?? $"HTTP {(int)response!.StatusCode}";
            int attempts = row.Attempts + 1;
            TimeSpan backoff = ComputeBackoff(attempts);
            await _bus.MarkTransientFailureAsync(
                row.Id, attempts, DateTimeOffset.UtcNow.Add(backoff), error, ct).ConfigureAwait(false);
        }
        finally
        {
            response?.Dispose();
        }
    }

    // Capped exponential with jitter: min(cap, base * 2^(attempts-1)) + a random jitter up to 20% of the
    // delay (avoids many rows recovering toward the same peer retrying in exact lockstep).
    private static TimeSpan ComputeBackoff(int attempts)
    {
        double multiplier = Math.Pow(2, Math.Max(0, attempts - 1));
        double delayMs = Math.Min(BackoffCap.TotalMilliseconds, BackoffBase.TotalMilliseconds * multiplier);
        double jitterMs = Random.Shared.NextDouble() * delayMs * 0.2;
        return TimeSpan.FromMilliseconds(delayMs + jitterMs);
    }
}
