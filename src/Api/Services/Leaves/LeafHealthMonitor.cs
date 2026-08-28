using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.KGSM.Core.Interfaces;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The single source of truth for capability <em>availability</em> (architecture §4·b). It polls each
/// provisioned leaf's <c>/health</c> frequently (the canonical "is this leaf able to provide its
/// capability" signal), caches the resulting <see cref="HostCapabilities"/> block for the REST
/// <c>GET /hosts</c> read, and publishes a <c>capabilities.patch</c> on the <c>hosts/{id}/capabilities</c>
/// WS topic whenever a status flips — so the frontend learns when a leaf dies and recovers, gracefully.
/// </summary>
/// <remarks>
/// <para><b>Two axes, never conflated.</b> <c>provisioned</c> is the fixed "what leaves this host has",
/// resolved once from config — it is the one-time contract the frontend negotiates at connect and it
/// <strong>never</strong> flips at runtime. A leaf failing flips its <c>status</c>
/// (operational→down→operational), <strong>never</strong> its <c>provisioned</c>: the capability is
/// "temporarily unavailable, still there", never "lost". <c>down</c> with <c>provisioned:true</c> IS the
/// notification — we never invent a softer status nor suppress the flip.</para>
/// <para><b>Always-on, not gated.</b> Unlike the metric pumps (which only run while subscribed), this
/// polls regardless of WS subscribers, because the capability set is negotiated at connect over REST and
/// its truth must always be fresh. A detected flip is published via the hub (a no-op when nobody is
/// subscribed); a flip-only emitter never replays current state to a new subscriber — they hydrate via
/// REST (§3·j) — so no prime-on-subscribe is needed.</para>
/// <para><b>Liveness ≠ data.</b> Metrics availability is the monitor's <c>/health</c>, deliberately
/// decoupled from whether <c>/metrics</c> produced a frame (a warming monitor is operational with null
/// capacity, not down) — honoring the "metric-presence ≠ status" invariant the M1·a code technically
/// bent. <c>since</c> is when THIS api observed the flip (not an authoritative leaf-change time).</para>
/// </remarks>
public sealed class LeafHealthMonitor : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);   // "query frequently"
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly MonitorClient _monitor;
    private readonly AssistantClient _assistant;
    private readonly ReactorClient _reactor;
    private readonly LeafRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly StreamHub _hub;
    private readonly ILogger<LeafHealthMonitor> _logger;
    private readonly LeafDegradationTracker? _degradation;

    private readonly string _topic;
    // Scheduler provisioning is CONFIG-based (the socket is configured ⇒ SchedulerClient is registered),
    // NOT the runtime DB registry (the four provisionable leaves): the scheduler is an opt-in leaf resolved
    // optionally from DI. Captured once at construction, same source as the conditional registration.
    private readonly bool _schedulerProvisioned;
    // The assistant's PUBLIC origin (config, fixed for the process) — reported as the capability's info so
    // the Control Panel's chat can address the leaf itself. Null when unconfigured: no info, no browser route.
    private readonly string? _assistantPublicUrl;
    // Serializes the poll body so the always-on 2s loop and an out-of-band PollNowAsync (a connect/disconnect
    // forcing an immediate publish) never race two writers onto _current.
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    /// <summary>
    /// What a leaf says is broken about itself, or nothing when this API is not tracking that.
    /// </summary>
    /// <remarks>
    /// Optional so the capability model still works on a host where the tracker is not registered.
    /// Absent means no self-report was read, which is not the same as a leaf reporting it is fine —
    /// and it therefore leaves the probe's answer alone rather than downgrading it.
    /// </remarks>
    private IReadOnlyCollection<string> Degraded(string leaf) => _degradation?.For(leaf) ?? [];
    private volatile HostCapabilities _current; // written only under _pollGate; volatile for reader threads

    public LeafHealthMonitor(
        ApiOptions options,
        MonitorClient monitor,
        AssistantClient assistant,
        ReactorClient reactor,
        LeafRegistry registry,
        IServiceProvider services,
        StreamHub hub,
        ILogger<LeafHealthMonitor> logger,
        LeafDegradationTracker? degradation = null)
    {
        _degradation = degradation;
        _monitor = monitor;
        _assistant = assistant;
        _reactor = reactor;
        _registry = registry;
        _services = services;
        _hub = hub;
        _logger = logger;
        _topic = StreamProtocol.HostCapabilitiesTopic(options.HostId);
        _schedulerProvisioned = options.SchedulerProvisioned;
        _assistantPublicUrl = options.AssistantPublicUrl;
        _current = ColdBlock(); // provisioned -> unknown (declared, not yet probed); unprovisioned -> absent
    }

    /// <summary>The latest capability block (thread-safe read). Never null; cold-start reads as <c>unknown</c>.</summary>
    public HostCapabilities Current => _current;

    /// <summary>
    /// Force an immediate, out-of-band poll + publish (used by the connect/disconnect endpoints so a runtime
    /// provisioning flip lights up / tears down the SPA's capability-gated UI without waiting for the next 2s
    /// tick). Serialized with the background loop; emits a <c>capabilities.patch</c> only if the block changed.
    /// </summary>
    public Task PollNowAsync(CancellationToken ct = default) => PollAndPublishAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PollAndPublishAsync(stoppingToken).ConfigureAwait(false); // warm the cache immediately

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await PollAndPublishAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "leaf health poll failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task PollAndPublishAsync(CancellationToken ct)
    {
        // Serialize the body: the 2s loop and an out-of-band PollNowAsync must not both write _current.
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Provisioning comes from the runtime registry (NOT the immutable ApiOptions), so a connect/
            // disconnect flips the capability set live. A non-provisioned leaf is never probed (Absent).
            bool metricsProvisioned = _registry.IsProvisioned(ProvisionableLeaf.Monitor);
            bool assistantProvisioned = _registry.IsProvisioned(ProvisionableLeaf.Assistant);
            bool watchdogProvisioned = _registry.IsProvisioned(ProvisionableLeaf.Watchdog);
            bool reactorProvisioned = _registry.IsProvisioned(ProvisionableLeaf.Reactor);

            // Probe all provisioned leaves concurrently; each call self-bounds (HttpClient timeout / linked
            // CTS) so one hung leaf can never stall the cycle. The leaf clients own their transport (the
            // chokepoint invariant) and gate themselves on the same registry, so an unprovisioned leaf
            // returns false/null without a wire call.
            // The scheduler client is registered only when its socket is configured; resolve it optionally
            // and gate on presence (the config-based provisioning flag), mirroring the watchdog probe.
            SchedulerClient? scheduler = _schedulerProvisioned
                ? _services.GetService(typeof(SchedulerClient)) as SchedulerClient
                : null;

            Task<bool> metricsTask = metricsProvisioned ? _monitor.CheckHealthAsync(ct) : Task.FromResult(false);
            Task<bool> assistantTask = assistantProvisioned ? _assistant.CheckHealthAsync(ct) : Task.FromResult(false);
            Task<bool> watchdogTask = watchdogProvisioned ? ProbeWatchdogAsync(ct) : Task.FromResult(false);
            Task<bool> schedulerTask = scheduler is not null ? scheduler.CheckHealthAsync(ct) : Task.FromResult(false);
            Task<bool> reactorTask = reactorProvisioned ? _reactor.CheckHealthAsync(ct) : Task.FromResult(false);
            await Task.WhenAll(metricsTask, assistantTask, watchdogTask, schedulerTask, reactorTask)
                .ConfigureAwait(false);

            // info.intervalMs stays honestly sourced from the metrics snapshot (cached), only when up.
            Snap.Snapshot? snap = metricsTask.Result ? await _monitor.GetLatestAsync(ct).ConfigureAwait(false) : null;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            HostCapabilities prev = _current;
            var next = new HostCapabilities(
                Metrics: BuildMetrics(
                    prev.Metrics, metricsProvisioned, metricsTask.Result, snap, now,
                    Degraded(ProvisionableLeaf.Monitor)),
                Assistant: BuildAssistant(
                    prev.Assistant, assistantProvisioned, assistantTask.Result, _assistantPublicUrl, now,
                    Degraded(ProvisionableLeaf.Assistant)),
                Watchdog: BuildLeaf(
                    prev.Watchdog, watchdogProvisioned, watchdogTask.Result, now, "Watchdog is not ready.",
                    Degraded(ProvisionableLeaf.Watchdog)),
                Scheduler: BuildLeaf(
                    prev.Scheduler, _schedulerProvisioned, schedulerTask.Result, now,
                    "Scheduler status check failed.", Degraded(ProvisionableLeaf.Scheduler)),
                Reactor: BuildLeaf(
                    prev.Reactor, reactorProvisioned, reactorTask.Result, now,
                    "Reactor health check failed.", Degraded(ProvisionableLeaf.Reactor)));

            if (next.Equals(prev))
                return; // no flip -> no emit (and provisioned/since are stable, so the block is value-equal)

            _current = next;
            _hub.Publish(_topic, _topic, new StreamMessage(_topic, StreamProtocol.CapabilitiesPatch, next));
        }
        finally { _pollGate.Release(); }
    }

    private async Task<bool> ProbeWatchdogAsync(CancellationToken ct)
    {
        // Caller already gated on the registry; resolve the (now always-registered, lazy) client optionally.
        var watchdog = _services.GetService(typeof(IWatchdogClient)) as IWatchdogClient;
        if (watchdog is null)
            return false;

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ProbeTimeout);
        try
        {
            // The watchdog is reached ONLY via kgsm-lib (the C#<->engine chokepoint); IsReadyAsync is the
            // readiness call. Its underlying path is now the unified /health (kgsm-lib, 2026-06-15).
            return await watchdog.IsReadyAsync(timed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("watchdog readiness probe timed out after {Timeout}", ProbeTimeout);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "watchdog readiness probe failed");
            return false;
        }
    }

    /// <summary>
    /// The status a leaf that answered its probe should carry, given what it says about itself.
    /// </summary>
    /// <remarks>
    /// <b>A probe answers yes or no, and a leaf can be answering perfectly while unable to do part of
    /// its job.</b> An assistant with a dead backend, a monitor serving a frozen frame and a scheduler
    /// that cannot reach the watchdog all pass their health check. <c>Degraded</c> is the state the
    /// contract has always had and nothing produced.
    /// </remarks>
    /// <param name="healthy">Whether the leaf answered its probe.</param>
    /// <param name="degraded">The components it reports broken, from its own journal.</param>
    /// <returns>The capability status.</returns>
    private static string StatusFor(bool healthy, IReadOnlyCollection<string> degraded)
    {
        if (!healthy)
            return CapabilityStatus.Down;

        return degraded.Count > 0 ? CapabilityStatus.Degraded : CapabilityStatus.Operational;
    }

    /// <summary>
    /// What to say about a leaf that is up and impaired, naming the components rather than summarising.
    /// </summary>
    /// <remarks>
    /// The component is the actionable part: "the assistant is degraded" sends somebody reading logs
    /// where "the assistant's llm-backend is unreachable" does not.
    /// </remarks>
    private static string? MessageFor(bool healthy, IReadOnlyCollection<string> degraded, string downMessage)
    {
        if (!healthy)
            return downMessage;

        return degraded.Count == 0 ? null : $"Reports {string.Join(", ", degraded.Order())} not working.";
    }

    private static Capability BuildMetrics(
        Capability prev, bool provisioned, bool healthy, Snap.Snapshot? snap, DateTimeOffset now,
        IReadOnlyCollection<string> degraded)
    {
        if (!provisioned)
            return Capability.Absent; // not connected -> client renders 'absent'

        string status = StatusFor(healthy, degraded);
        object? info = healthy && snap is not null ? new MetricsCapabilityInfo(snap.IntervalMs) : null;
        return new Capability(
            Provisioned: true,
            Status: status,
            Since: SinceFor(prev, status, now),
            Message: MessageFor(healthy, degraded, "Monitor health check failed."),
            Info: info);
    }

    // The assistant carries its public origin in `info` so the chat surface can reach the leaf directly.
    // The origin is config, not health, so it rides along on every status — a leaf that is momentarily down
    // still has the same address, and withholding it would make a transient outage look like a missing route.
    private static Capability BuildAssistant(
        Capability prev, bool provisioned, bool healthy, string? publicUrl, DateTimeOffset now,
        IReadOnlyCollection<string> degraded)
    {
        if (!provisioned)
            return Capability.Absent;

        string status = StatusFor(healthy, degraded);
        return new Capability(
            Provisioned: true,
            Status: status,
            Since: SinceFor(prev, status, now),
            Message: MessageFor(healthy, degraded, "Assistant health check failed."),
            Info: publicUrl is null ? null : new AssistantCapabilityInfo(publicUrl));
    }

    private static Capability BuildLeaf(
        Capability prev, bool provisioned, bool healthy, DateTimeOffset now, string downMessage,
        IReadOnlyCollection<string> degraded)
    {
        if (!provisioned)
            return Capability.Absent;

        string status = StatusFor(healthy, degraded);
        return new Capability(
            Provisioned: true,
            Status: status,
            Since: SinceFor(prev, status, now),
            Message: MessageFor(healthy, degraded, downMessage));
    }

    // Carry the prior flip timestamp when the status is unchanged; stamp 'now' on a transition. Keeping
    // 'since' stable between flips is what makes the block value-equal poll-to-poll (no emit storm).
    private static DateTimeOffset SinceFor(Capability prev, string status, DateTimeOffset now) =>
        prev.Status == status && prev.Since is { } s ? s : now;

    private HostCapabilities ColdBlock()
    {
        static Capability Cold(bool provisioned) => provisioned
            ? new Capability(Provisioned: true, Status: CapabilityStatus.Unknown) // declared, not yet probed
            : Capability.Absent;
        return new HostCapabilities(
            Metrics: Cold(_registry.IsProvisioned(ProvisionableLeaf.Monitor)),
            Assistant: _registry.IsProvisioned(ProvisionableLeaf.Assistant)
                ? new Capability(Provisioned: true, Status: CapabilityStatus.Unknown,
                    Info: _assistantPublicUrl is null ? null : new AssistantCapabilityInfo(_assistantPublicUrl))
                : Capability.Absent,
            Watchdog: Cold(_registry.IsProvisioned(ProvisionableLeaf.Watchdog)),
            Scheduler: Cold(_schedulerProvisioned),
            Reactor: Cold(_registry.IsProvisioned(ProvisionableLeaf.Reactor)));
    }
}
