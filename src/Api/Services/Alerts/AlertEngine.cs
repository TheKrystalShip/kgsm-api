using System.Collections.Concurrent;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Services.Alerts;

/// <summary>
/// The condition-mirror alert engine (M6·a, architecture.html §3·c). It maintains the live "needs
/// attention" set as a mirror of <em>conditions</em> the host can honestly measure, raises while a
/// condition is true and resolves it (after a probation dwell) when it clears — never a task, never
/// client-writable. It is the single source for both the REST <c>GET /alerts</c> read and the live
/// <c>alerts</c> WS topic (<c>alert.raise</c>/<c>resolve</c>/<c>retract</c>), exactly the
/// always-on-singleton-plus-hosted-service shape as <see cref="Leaves.LeafHealthMonitor"/>.
/// </summary>
/// <remarks>
/// <para><b>Crash source only (M6·a).</b> The only producer wired today is the watchdog's supervision
/// state, polled via kgsm-lib's <see cref="IWatchdogClient"/> (the C#↔engine chokepoint — never a raw
/// socket). A <c>Desired="running"</c> instance whose <c>Phase</c> is <c>restart-pending</c> is a firing
/// <c>warn</c> crash; <c>Phase="failed"</c> (the supervisor exhausted its retries and gave up) is an
/// <c>escalated</c> <c>danger</c> that never auto-resolves. Every field is measured from the kernel
/// (<c>cgroup.events</c>) — never fabricated. <b>Honest boundary:</b> the watchdog supervises NATIVE
/// instances only, so container-instance crashes are out of scope until a Docker event source exists;
/// metric thresholds, leaf-down, and port-unreachable are deferred (no honest source at M6·a).</para>
/// <para><b>The poll is the authority; it doubles as the raise debounce.</b> A crash that recovers
/// faster than one poll interval is never seen down — so it never fires, which is exactly §3·c's "don't
/// fire on a blip". We deliberately do NOT event-fast-path a raise (that would fire on every transient
/// crash). The clear is probation-gated: a cleared condition is only resolved once it stays clear for
/// <see cref="ResolveProbation"/>, so a crash-loop (crash→start→crash) never flaps the feed.</para>
/// <para><b>Rebuilds on restart; never fabricates on a blind tick.</b> The firing set is in-memory (no
/// EF table — the durable record is <c>/audit</c>); on an API restart it is reconstructed from the next
/// poll because the watchdog state is queryable, not an unreplayable event. If a poll <em>fails</em>
/// (watchdog unreachable / timeout) the tick is skipped — the firing set persists; we never resolve or
/// retract on the absence of an answer (honest-unknown). A condition that fired-and-resolved while the
/// API was down is simply absent — the transition still lives in <c>/audit</c>.</para>
/// <para><b>The alert↔audit bridge (and its honest limit).</b> <see cref="NoteRecoveryAction"/> stashes
/// the audit <c>evt_</c> id of each <c>server.start</c>/<c>server.restart</c> row (handed off by
/// <see cref="Audit.KgsmAuditConsumer"/> AFTER it writes the row, so the id exists). When a crash later
/// resolves because an OPERATOR/api start|restart brought the server back, that id becomes the
/// resolution's <c>actionId</c> — the one-way link to the fix. The poll can't learn an audit id on its
/// own, so this is the sole event integration; it is lock-free (a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// read by the poll thread). The watchdog's autonomous crash-restart now emits <c>instance_restarted</c>
/// (<c>system</c>/<c>system</c>, kgsm-watchdog <c>d4b453f</c>) → a <c>server.restart</c> row, so a pure
/// auto-heal bridges its <c>actionId</c> once that row is consumed (within the resolve probation). The
/// bridge is <b>episode-scoped</b>: a stashed action stamps a resolution only if it post-dates that crash's
/// raise (see <see cref="BuildResolution"/>), so a dropped recovery event can never let a stale action from
/// a PRIOR crash episode stand in — the resolution is an honest <see langword="null"/> instead. Not every
/// <c>server.start</c> row is even eligible: <see cref="Audit.KgsmAuditConsumer.IsRecoveryAction"/> also
/// excludes the watchdog's BOOT-AUTOSTART (a system-origin start — a boot bring-up is not a crash recovery;
/// belt-and-braces now, since episode-scoping would reject its pre-crash timestamp regardless). <b>Limit:</b>
/// a crash cleared by a STOP, or whose own recovery event dropped, resolves with <c>actionId</c>
/// <see langword="null"/> — never a fabricated link.</para>
/// <para><b>The metrics-threshold source (increment 1, <c>metrics-threshold-alerts-plan.md</c>).</b> A second,
/// independent producer folded into this same engine: <see cref="TickMetrics"/> reconciles the monitor
/// <see cref="Snap.Snapshot"/> against <see cref="ApiOptions.Policy"/>'s <see cref="ThresholdRule"/>s, raising
/// <c>metric:&lt;ruleKey&gt;[:&lt;ref-or-serverId&gt;]</c> alerts on a sustained host/per-server breach. Unlike
/// crash, a metric value can spike — so this pass needs its OWN fire-dwell (<see cref="_breachSince"/>,
/// <see cref="ThresholdRule.FireForSec"/>) on top of the shared clear-dwell (<see cref="_clearSince"/>,
/// <see cref="ThresholdRule.ClearForSec"/>) plus a hysteresis deadband (<see cref="ThresholdRule.ClearMargin"/>)
/// so a value hovering at the threshold can't flap the feed. <c>snap == null</c> (monitor down) holds every
/// metric alert unchanged — the same honest-unknown posture as a failed watchdog poll. A metric alert's
/// <c>resolution.actionId</c> is always <see langword="null"/> (the bridge is crash-specific) and
/// <c>Escalated</c> is always <see langword="false"/> (a metric in the danger band still auto-resolves).</para>
/// <para><b>Threading.</b> The alert state (<see cref="_firing"/>/<see cref="_resolved"/>/<see cref="_clearSince"/>/
/// <see cref="_breachSince"/>) is mutated ONLY by <see cref="Tick"/> and <see cref="TickMetrics"/>, both on the
/// single poll-loop thread (sequentially, never concurrently); the controller reads the volatile immutable
/// <see cref="_snapshot"/>; <see cref="_lastStartAction"/> is concurrent. No locks.</para>
/// </remarks>
public sealed class AlertEngine : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a cleared crash condition must stay clear before it resolves — the §3·c
    /// "verify the clear" dwell that stops a crash-loop flapping raise/resolve. Comfortably longer than
    /// the watchdog's restart backoff so a re-crash lands inside the window and cancels the resolve.</summary>
    internal static readonly TimeSpan ResolveProbation = TimeSpan.FromSeconds(30);

    /// <summary>How long a resolved record lingers in the rear-view before ageing off (§3·c "24h
    /// rear-view"); nothing is lost — the firing→resolved transition lives in <c>/audit</c>.</summary>
    internal static readonly TimeSpan ResolvedRetention = TimeSpan.FromHours(24);

    // Watchdog vocabulary (WatchdogInstanceState.Desired / .Phase) — matched case-insensitively.
    private const string DesiredRunning = "running";
    private const string PhaseRestartPending = "restart-pending";
    private const string PhaseFailed = "failed";
    private const string PhaseRunning = "running";

    private readonly ApiOptions _options;
    private readonly IServiceProvider _services;
    private readonly MonitorClient _monitor;
    private readonly StreamHub _hub;
    private readonly ILogger<AlertEngine> _logger;

    // Mutated only by Tick / TickMetrics (single loop thread).
    private readonly Dictionary<string, Alert> _firing = new();          // id -> live firing record
    private readonly List<Alert> _resolved = new();                      // resolved, within retention
    private readonly Dictionary<string, DateTimeOffset> _clearSince = new(); // id -> when first read clear

    /// <summary>Metric fire-dwell: id -> when the value was FIRST observed breaching (warn or danger). Crash
    /// never needed this — its poll interval is its own debounce; a metric can spike, so a rule must hold a
    /// breach for <see cref="ThresholdRule.FireForSec"/> before it raises. Namespaced by id (<c>metric:…</c>),
    /// so it can never collide with a crash id (<c>crash:…</c>).</summary>
    private readonly Dictionary<string, DateTimeOffset> _breachSince = new();

    // Written by the event thread (NoteRecoveryAction), read by the poll thread (Tick). Lock-free.
    // Episode-scoped at read time by timestamp (see BuildResolution) — a stale action never bridges.
    private readonly ConcurrentDictionary<string, RecoveryAction> _lastStartAction = new();

    private volatile Snapshot _snapshot = Snapshot.Empty;

    public AlertEngine(ApiOptions options, IServiceProvider services, MonitorClient monitor, StreamHub hub, ILogger<AlertEngine> logger)
    {
        _options = options;
        _services = services;
        _monitor = monitor;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>The live firing set (thread-safe read), oldest-first.</summary>
    public IReadOnlyList<Alert> Firing => _snapshot.Firing;

    /// <summary>Resolved records that cleared at or after <paramref name="cutoff"/> (the 24h rear-view),
    /// newest-first.</summary>
    public IReadOnlyList<Alert> ResolvedSince(DateTimeOffset cutoff) =>
        _snapshot.Resolved.Where(a => a.ResolvedAt is { } r && r >= cutoff).ToList();

    /// <summary>Stash the audit <c>evt_</c> id of a <c>server.start</c>/<c>server.restart</c> (a "bring it
    /// up" recovery action) together with <paramref name="at"/> (the action's audit-row timestamp) so a
    /// later crash resolution can reference it as <c>resolution.actionId</c> (the alert↔audit bridge).
    /// Called by the audit consumer AFTER the row is written. Lock-free; the latest action for a server
    /// wins. The stash is <b>episode-scoped at read time</b>: <see cref="BuildResolution"/> honors it only
    /// if it post-dates the firing record's raise, so a stale action from a PRIOR crash episode (or a fast
    /// auto-heal blip that never fired) can never stamp a later resolution — honest null over a stale link.
    /// The watchdog's autonomous crash-restart emits <c>instance_restarted</c> (system/system) → a
    /// <c>server.restart</c> row that lands here too, so a real auto-heal still bridges its recovery.</summary>
    public void NoteRecoveryAction(string serverId, string actionId, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(actionId)) return;
        _lastStartAction[serverId] = new RecoveryAction(actionId, at);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WatchdogProvisioned && !_options.MetricsThresholdProvisioned)
        {
            // Neither a crash source nor a metrics-threshold source on this host — GET /alerts serves an
            // empty feed, the WS topic stays silent.
            _logger.LogInformation(
                "Alerts: no crash or metrics-threshold source provisioned — empty feed.");
            return;
        }

        await PollAsync(stoppingToken).ConfigureAwait(false); // warm immediately
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await PollAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "alert poll failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Gather whatever is provisioned, then reconcile each pass — independently, so a metrics-only host
        // never touches the watchdog and a watchdog-only host never scrapes the monitor.
        if (_options.WatchdogProvisioned)
        {
            IReadOnlyList<WatchdogInstanceState>? states = await PollWatchdogStatesAsync(ct).ConfigureAwait(false);
            if (states is not null) Tick(states, now); // null = blind cycle (timeout/unreachable) — skip, never fabricate
        }

        if (_options.MetricsThresholdProvisioned)
        {
            Snap.Snapshot? snapshot = await _monitor.GetLatestAsync(ct).ConfigureAwait(false); // null when monitor down
            TickMetrics(snapshot, now);
        }
    }

    // The watchdog crash-source scrape, split out of PollAsync so it can be skipped entirely when the
    // watchdog isn't provisioned (a metrics-only host must never call the watchdog client). Behavior
    // unchanged from the original PollAsync body: a timeout/unreachable watchdog returns null (skip the
    // tick — honest-unknown, never resolve/retract on a blind cycle).
    private async Task<IReadOnlyList<WatchdogInstanceState>?> PollWatchdogStatesAsync(CancellationToken ct)
    {
        // Registered only when provisioned (see Startup); resolve optionally to stay safe.
        var watchdog = _services.GetService(typeof(IWatchdogClient)) as IWatchdogClient;
        if (watchdog is null) return null;

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ProbeTimeout);
        try
        {
            return await watchdog.ListAsync(timed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("watchdog list timed out after {Timeout} — skipping alert tick", ProbeTimeout);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "watchdog list failed — skipping alert tick");
            return null;
        }
    }

    /// <summary>
    /// One reconcile of the firing set against the watchdog's current supervision state. Pure over the
    /// engine's in-memory state (no I/O beyond the hub publish) and single-threaded — the unit-test seam.
    /// Raises new crash conditions, re-pushes on an escalation change, resolves cleared ones once they
    /// hold past <see cref="ResolveProbation"/>, retracts a vanished instance, and ages off the rear-view.
    /// </summary>
    internal void Tick(IReadOnlyList<WatchdogInstanceState> states, DateTimeOffset now)
    {
        var present = new HashSet<string>();
        var firingNow = new Dictionary<string, Observed>();

        foreach (WatchdogInstanceState ws in states)
        {
            if (string.IsNullOrEmpty(ws.Name)) continue;
            present.Add(ws.Name);

            bool desiredRunning = string.Equals(ws.Desired, DesiredRunning, StringComparison.OrdinalIgnoreCase);
            bool crashing = string.Equals(ws.Phase, PhaseRestartPending, StringComparison.OrdinalIgnoreCase);
            bool failed = string.Equals(ws.Phase, PhaseFailed, StringComparison.OrdinalIgnoreCase);
            if (desiredRunning && (crashing || failed))
                firingNow[ws.Name] = new Observed(failed, ws.Restarts, ws.Reason ?? "");
        }

        // 1) raise new / re-push on escalation change / cancel any pending resolve.
        foreach ((string serverId, Observed obs) in firingNow)
        {
            string id = AlertId(serverId);
            _clearSince.Remove(id); // condition true → it is not clearing
            if (_firing.TryGetValue(id, out Alert? existing))
            {
                if (existing.Escalated != obs.Escalated || existing.Attempts != obs.Attempts)
                {
                    Alert updated = BuildFiring(serverId, obs, existing.RaisedAt);
                    _firing[id] = updated;
                    Publish(StreamProtocol.AlertRaise, id, updated); // re-push the full record (upsert)
                }
            }
            else
            {
                Alert raised = BuildFiring(serverId, obs, now);
                _firing[id] = raised;
                Publish(StreamProtocol.AlertRaise, id, raised);
            }
        }

        // 2) resolve (probation-gated) the cleared, retract the vanished. ONLY crash: ids — _firing now also
        // holds metric: alerts (the threshold source shares this dict), and those are TickMetrics's to
        // reconcile, never this watchdog pass's. Without this guard a crash poll would see a metric alert's
        // serverId absent from its watchdog `present`/`firingNow` sets and wrongly retract/resolve a live
        // metric condition.
        foreach (string id in _firing.Keys.ToList())
        {
            if (!id.StartsWith(CrashIdPrefix, StringComparison.Ordinal)) continue;
            Alert record = _firing[id];
            string serverId = record.ServerId!;
            if (firingNow.ContainsKey(serverId)) continue; // still firing

            if (!present.Contains(serverId))
            {
                // The instance is gone entirely (uninstalled) — never an actionable condition now.
                _firing.Remove(id);
                _clearSince.Remove(id);
                Publish(StreamProtocol.AlertRetract, id, new AlertRetracted(id));
                continue;
            }

            // Condition cleared but the server still exists — start/observe the probation window.
            if (!_clearSince.TryGetValue(id, out DateTimeOffset since))
            {
                _clearSince[id] = now;
                continue;
            }
            if (now - since < ResolveProbation) continue; // not yet stable — hold

            _firing.Remove(id);
            _clearSince.Remove(id);
            AlertResolution resolution = BuildResolution(serverId, record.RaisedAt, states);
            Alert resolved = record with { Status = AlertStatus.Resolved, ResolvedAt = now, Resolution = resolution };
            _resolved.Add(resolved);
            Publish(StreamProtocol.AlertResolve, id, new AlertResolved(id, resolution));
        }

        RebuildSnapshot(now);
    }

    /// <summary>
    /// One reconcile of the metric-threshold rules (<see cref="ApiOptions.Policy"/>) against the monitor's
    /// latest <paramref name="snap"/> — the new producer for the <c>host-monitor</c>/<c>metrics</c> sources
    /// (<c>metrics-threshold-alerts-plan.md</c> §E). Pure over the engine's in-memory state (no I/O beyond the
    /// hub publish) and single-threaded — the unit-test seam, mirroring <see cref="Tick"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Honest-unknown.</b> <paramref name="snap"/> <see langword="null"/> (the monitor is down) holds
    /// every metric alert unchanged — never resolves/retracts on the absence of an answer, exactly like a
    /// failed watchdog poll. A field that isn't evaluable this tick (a null nullable, no swap, no cpu-info, no
    /// sensors) is already filtered out by <see cref="ThresholdMetrics.Observe"/> — never reached here.</para>
    /// <para><b>Two dwells, not one.</b> A breach must hold <see cref="ThresholdRule.FireForSec"/> before it
    /// raises (<see cref="_breachSince"/> — kills a spike); a clear must hold <see cref="ThresholdRule.ClearForSec"/>
    /// AND have dropped <see cref="ThresholdRule.ClearMargin"/> below <see cref="ThresholdRule.Warn"/> before it
    /// resolves (reuses <see cref="_clearSince"/>, namespaced by id so it never collides with a crash entry). A
    /// value sitting in the deadband between the two neither advances nor flaps — it just stays firing.</para>
    /// <para><b>Vanished server row.</b> A firing <c>metric:&lt;ruleKey&gt;:&lt;serverId&gt;</c> whose server no
    /// longer appears in a <em>non-null</em> <paramref name="snap"/> is treated as cleared and resolves after the
    /// same <see cref="ThresholdRule.ClearForSec"/> dwell — distinct from a monitor blackout, which (per the
    /// honest-unknown rule above) never resolves anything.</para>
    /// </remarks>
    internal void TickMetrics(Snap.Snapshot? snap, DateTimeOffset now)
    {
        if (snap is null) return; // honest-unknown: change nothing, hold every metric alert (no rebuild either)

        foreach (ThresholdRule rule in _options.Policy.Rules)
        {
            if (!rule.Enabled) continue;

            bool hostScope = ThresholdMetrics.IsHostScope(rule.Metric);
            // Only server-scope rules need vanished-row tracking — a host-scope rule's targets (a singleton,
            // or the mount/sensor fan-out) don't correspond to a Snapshot.Servers row.
            HashSet<string>? observedIds = hostScope ? null : new HashSet<string>();

            foreach (MetricObservation obs in ThresholdMetrics.Observe(rule, snap))
            {
                observedIds?.Add(obs.Id);
                ReconcileMetricObservation(rule, obs, now);
            }

            if (observedIds is not null)
                ResolveVanishedServerAlerts(rule, observedIds, now);
        }

        RebuildSnapshot(now);
    }

    // One id's reconcile against its rule for this tick's observation — the fire-dwell / hysteresis-clear /
    // deadband state machine from metrics-threshold-alerts-plan.md §E, applied per <see cref="MetricObservation"/>.
    private void ReconcileMetricObservation(ThresholdRule rule, MetricObservation obs, DateTimeOffset now)
    {
        string id = obs.Id;
        string? band = rule.Danger is { } danger && obs.Value >= danger ? AlertSeverity.Danger
            : obs.Value >= rule.Warn ? AlertSeverity.Warn
            : null;

        if (band is not null)
        {
            // Breaching (warn or danger) — not clearing, so cancel any pending resolve and arm/hold the
            // fire-dwell clock.
            _clearSince.Remove(id);
            if (!_breachSince.ContainsKey(id)) _breachSince[id] = now;

            if (now - _breachSince[id] < TimeSpan.FromSeconds(rule.FireForSec))
                return; // dwell not met yet — pending, no raise (kills a one-tick spike)

            if (_firing.TryGetValue(id, out Alert? existing))
            {
                if (existing.Severity != band)
                {
                    Alert updated = BuildMetricFiring(rule, obs, band, existing.RaisedAt);
                    _firing[id] = updated;
                    Publish(StreamProtocol.AlertRaise, id, updated); // re-push the full record (severity change)
                }
            }
            else
            {
                Alert raised = BuildMetricFiring(rule, obs, band, now);
                _firing[id] = raised;
                Publish(StreamProtocol.AlertRaise, id, raised);
            }
            return;
        }

        if (obs.Value <= rule.Warn - rule.ClearMargin)
        {
            // Truly cleared — past the hysteresis deadband. Start/observe the clear dwell.
            _breachSince.Remove(id);
            if (!_firing.TryGetValue(id, out Alert? firingRecord)) return; // not firing — nothing to clear

            if (!_clearSince.TryGetValue(id, out DateTimeOffset since))
            {
                _clearSince[id] = now;
                return; // the arming tick itself never resolves
            }
            if (now - since < TimeSpan.FromSeconds(rule.ClearForSec)) return; // not yet stable — hold

            _firing.Remove(id);
            _clearSince.Remove(id);
            AlertResolution resolution = BuildMetricResolution(rule, obs);
            Alert resolved = firingRecord with { Status = AlertStatus.Resolved, ResolvedAt = now, Resolution = resolution };
            _resolved.Add(resolved);
            Publish(StreamProtocol.AlertResolve, id, new AlertResolved(id, resolution));
            return;
        }

        // Deadband: elevated but neither at the fire threshold nor past the clear margin. Honest middle
        // ground — never starts the clear dwell, so a value hovering right at Warn can't flap the feed.
        // (If it WAS firing it stays firing; if it wasn't, it still isn't.)
        _breachSince.Remove(id);
    }

    // A firing metric:<ruleKey>:<serverId> whose server vanished from a NON-NULL snapshot this tick (the
    // monitor is up, the row is just gone — a real stop/uninstall, not a blackout). Runs the same clear-dwell
    // as a true value-clear, then resolves with an honest "no longer reporting" reason.
    private void ResolveVanishedServerAlerts(ThresholdRule rule, HashSet<string> observedIds, DateTimeOffset now)
    {
        string prefix = ThresholdMetrics.AlertId(rule.Key, null) + ":"; // "metric:<ruleKey>:"
        foreach (string id in _firing.Keys.ToList())
        {
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (observedIds.Contains(id)) continue; // still reporting — handled by the observation pass above

            Alert record = _firing[id];
            if (!_clearSince.TryGetValue(id, out DateTimeOffset since))
            {
                _clearSince[id] = now;
                continue; // the arming tick itself never resolves
            }
            if (now - since < TimeSpan.FromSeconds(rule.ClearForSec)) continue; // not yet stable — hold

            _firing.Remove(id);
            _clearSince.Remove(id);
            _breachSince.Remove(id);
            var resolution = new AlertResolution(
                AlertResolvedBy.System,
                ThresholdMetrics.IsHostScope(rule.Metric) ? AlertSource.HostMonitor : AlertSource.Metrics,
                "Recovered — no longer reporting metrics.",
                ActionId: null);
            Alert resolved = record with { Status = AlertStatus.Resolved, ResolvedAt = now, Resolution = resolution };
            _resolved.Add(resolved);
            Publish(StreamProtocol.AlertResolve, id, new AlertResolved(id, resolution));
        }
    }

    private Alert BuildFiring(string serverId, Observed obs, DateTimeOffset raisedAt)
    {
        string severity = obs.Escalated ? AlertSeverity.Danger : AlertSeverity.Warn;

        // Escalated = the supervisor is in its terminal "gave up" state. Distinguish a start that NEVER
        // succeeded (0 restarts — it never ran, so it never "crashed") from a crash-loop whose retries the
        // supervisor exhausted. Framing a failed first start as "keeps crashing … after 0 restart(s)" is the
        // self-contradictory wording we refuse to ship: it never crashed and it never restarted.
        string title;
        string detail;
        if (obs.Escalated)
        {
            bool neverStarted = obs.Attempts == 0;
            title = neverStarted ? $"{serverId} failed to start" : $"{serverId} keeps crashing";
            string lead = neverStarted
                ? "Supervisor could not start it."
                : $"Supervisor gave up after {obs.Attempts} restart(s).";
            detail = lead + (string.IsNullOrEmpty(obs.Reason) ? "" : $" Last: {obs.Reason}");
        }
        else
        {
            title = $"{serverId} crashed";
            detail = string.IsNullOrEmpty(obs.Reason) ? "Auto-restarting." : obs.Reason;
        }

        return new Alert(
            Id: AlertId(serverId),
            Severity: severity,
            Source: AlertSource.Watchdog,
            Title: title,
            Detail: detail,
            ServerId: serverId,
            HostId: _options.HostId,
            Anchor: new AlertAnchor(AlertSurface.Server, _options.HostId, Tab: null, Ref: serverId),
            Status: AlertStatus.Firing,
            RaisedAt: raisedAt,
            Escalated: obs.Escalated,
            Attempts: obs.Attempts);
    }

    private AlertResolution BuildResolution(string serverId, DateTimeOffset raisedAt, IReadOnlyList<WatchdogInstanceState> states)
    {
        WatchdogInstanceState? ws = states.FirstOrDefault(s => string.Equals(s.Name, serverId, StringComparison.Ordinal));
        bool running = ws is not null && string.Equals(ws.Phase, PhaseRunning, StringComparison.OrdinalIgnoreCase);
        bool stopped = ws is not null && !string.Equals(ws.Desired, DesiredRunning, StringComparison.OrdinalIgnoreCase);

        string reason = running ? "Recovered — running and stable."
            : stopped ? "Server was stopped — no longer supervised as running."
            : "No longer in a crash state.";

        // actionId is the bridge: set only when a start|restart (operator/api OR the watchdog's own
        // autonomous crash-restart) brought it back to running. EPISODE-SCOPED: the stashed action must
        // post-date THIS crash's raise (action.At >= raisedAt), so a stale id from a prior episode — or a
        // dropped recovery event that left an older action in the map — can never stamp this resolution; we
        // emit honest null instead. Soundness rests on ONE invariant: kgsm/watchdog emit lifecycle events at
        // operation COMPLETION (server up), never at initiation, so a genuine recovery's timestamp is always
        // at/after the poll that observed the server DOWN (RaisedAt). Single-host → action.At and raisedAt
        // share a wall clock. (A stop-cleared crash resolves null regardless — running is false below.)
        string? actionId = running
            && _lastStartAction.TryGetValue(serverId, out RecoveryAction action)
            && action.At >= raisedAt
            ? action.Id : null;
        return new AlertResolution(AlertResolvedBy.System, AlertSource.Watchdog, reason, actionId);
    }

    /// <summary>The firing record for a metric-threshold breach (<c>host-monitor</c>/<c>metrics</c> source).
    /// <see cref="Alert.Escalated"/> is ALWAYS <see langword="false"/> — a metric in the danger band still
    /// auto-resolves once it recedes, so severity alone (never <c>escalated</c>) carries how bad it is.</summary>
    private Alert BuildMetricFiring(ThresholdRule rule, MetricObservation obs, string severity, DateTimeOffset raisedAt)
    {
        bool hostScope = ThresholdMetrics.IsHostScope(rule.Metric);
        string noun = MetricNoun(rule.Metric);
        // Subject: a host rule names its target (mount/sensor ref, else the host itself); a server rule names
        // the instance (carried as serverId). The measured value rides in obs.Display (already unit-formatted).
        string subject = hostScope
            ? (obs.RefKey is { Length: > 0 } refKey ? refKey : _options.HostId)
            : (obs.ServerId ?? "server");
        string sev = severity == AlertSeverity.Danger ? "critical" : "high";

        // Deep-link hints to the tab where the operator would act: a host-scope alert points at the host's
        // resources view (host CPU/mem/disk); a server-scope alert at that server's performance tab.
        AlertAnchor anchor = hostScope
            ? new AlertAnchor(AlertSurface.Host, _options.HostId, Tab: "resources", Ref: obs.RefKey)
            : new AlertAnchor(AlertSurface.Server, _options.HostId, Tab: "performance", Ref: obs.ServerId);

        return new Alert(
            Id: obs.Id,
            Severity: severity,
            Source: hostScope ? AlertSource.HostMonitor : AlertSource.Metrics,
            Title: $"{subject} {noun} at {obs.Display}",
            Detail: $"Sustained {sev} {noun} — held for at least {FormatDwell(rule.FireForSec)}.",
            ServerId: obs.ServerId,
            HostId: _options.HostId,
            Anchor: anchor,
            Status: AlertStatus.Firing,
            RaisedAt: raisedAt,
            Escalated: false,
            Attempts: 0);
    }

    /// <summary>The resolution for a cleared metric-threshold breach. <see cref="AlertResolution.ActionId"/>
    /// is ALWAYS <see langword="null"/> — a threshold clears because the measured value receded, never via an
    /// operator/system action; the actionId↔audit bridge is crash-specific. <see cref="AlertResolution.By"/>
    /// stays <c>system</c> (the server observed the clear).</summary>
    private AlertResolution BuildMetricResolution(ThresholdRule rule, MetricObservation obs)
    {
        string source = ThresholdMetrics.IsHostScope(rule.Metric) ? AlertSource.HostMonitor : AlertSource.Metrics;
        return new AlertResolution(
            AlertResolvedBy.System, source, $"Recovered — {MetricNoun(rule.Metric)} back to {obs.Display}.", ActionId: null);
    }

    private static string MetricNoun(ThresholdMetric metric) => metric switch
    {
        ThresholdMetric.HostMemUsedPct => "memory",
        ThresholdMetric.HostSwapUsedPct => "swap",
        ThresholdMetric.HostDiskUsedPct => "disk",
        ThresholdMetric.HostLoadPerCore => "load",
        ThresholdMetric.HostTempC => "temperature",
        ThresholdMetric.ServerMemBytes => "memory",
        ThresholdMetric.ServerCpuPctCore => "CPU",
        ThresholdMetric.ServerPids => "processes",
        _ => "metric",
    };

    private static string FormatDwell(int seconds) =>
        seconds > 0 && seconds % 60 == 0 ? $"{seconds / 60}m" : $"{seconds}s";

    /// <summary>Age off the rear-view, then republish the immutable snapshot the REST read serves. Called by
    /// BOTH <see cref="Tick"/> (crash) and <see cref="TickMetrics"/> (threshold) so <see cref="_snapshot"/>
    /// always projects the FULL firing/resolved set — both sources together, order-independent.</summary>
    private void RebuildSnapshot(DateTimeOffset now)
    {
        _resolved.RemoveAll(a => a.ResolvedAt is { } r && now - r > ResolvedRetention);
        _snapshot = new Snapshot(
            _firing.Values.OrderBy(a => a.RaisedAt).ToList(),
            _resolved.OrderByDescending(a => a.ResolvedAt).ToList());
    }

    private void Publish(string type, string id, object data) =>
        _hub.Publish(StreamProtocol.AlertsTopic, StreamProtocol.AlertEntityKey(id),
            new StreamMessage(StreamProtocol.AlertsTopic, type, data));

    private const string CrashIdPrefix = "crash:";
    private static string AlertId(string serverId) => $"{CrashIdPrefix}{serverId}";

    // The watchdog-observed crash condition for one instance (the inputs that shape the firing record).
    private readonly record struct Observed(bool Escalated, int Attempts, string Reason);

    // A stashed recovery action: the audit evt_ id + when it happened (the row Ts). Episode-scoped at read.
    private readonly record struct RecoveryAction(string Id, DateTimeOffset At);

    // The immutable read view the controller serves (republished each tick).
    private sealed record Snapshot(IReadOnlyList<Alert> Firing, IReadOnlyList<Alert> Resolved)
    {
        public static readonly Snapshot Empty = new(Array.Empty<Alert>(), Array.Empty<Alert>());
    }
}
