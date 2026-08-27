using System.Collections.Concurrent;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
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
/// <c>escalated</c> <c>danger</c> that never auto-resolves. <c>Phase="maintenance"</c> is neither: the
/// daemon drained the instance for a window somebody scheduled, so nothing failed and nothing is owed. Every field is measured from the kernel
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
/// read by the poll thread). The watchdog's autonomous crash-restart now emits <c>server.restarted</c>
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
/// <para><b>The metrics-threshold source.</b> A second producer, <see cref="TickConditions"/>, mirrors the
/// threshold conditions kgsm-monitor publishes on its <see cref="Snap.Snapshot"/> into
/// <c>metric:&lt;ruleKey&gt;[:&lt;ref-or-serverId&gt;]</c> alerts. <b>It decides nothing about whether a
/// value is over its line, and it holds no dwell.</b> The monitor evaluates the rules against every sample
/// it takes and publishes the verdict; this API scrapes on its own slower cadence, so a dwell computed here
/// would be a claim about a window it saw a fraction of. What this pass adds is everything the leaf
/// deliberately does not know: severity, source, anchor, and the words on the card. <c>snap == null</c>
/// (monitor down) holds every metric alert unchanged — the same honest-unknown posture as a failed watchdog
/// poll. A metric alert's <c>resolution.actionId</c> is always <see langword="null"/> (the bridge is
/// crash-specific) and <c>Escalated</c> is always <see langword="false"/> (a metric in the danger band still
/// auto-resolves).</para>
/// <para><b>The engine source.</b> A third producer, <see cref="TickUpdates"/>, mirrors update availability
/// into the feed as <c>update:&lt;serverId&gt;</c> <c>info</c> alerts. It is the one source that measures
/// nothing: kgsm records what the scheduler's networked check found beside each instance, so the condition is
/// read off the same fast status the roster is built from — no probe, no network, no extra call. It needs
/// nothing provisioned (kgsm is this API's base dependency), which is why the loop runs even on a host with
/// neither a watchdog nor a threshold policy.</para>
/// <para><b>Threading.</b> The alert state (<see cref="_firing"/>/<see cref="_resolved"/>/<see cref="_clearSince"/>)
/// is mutated ONLY by <see cref="Tick"/>, <see cref="TickConditions"/> and <see cref="TickUpdates"/>, all on
/// the single poll-loop thread (sequentially, never concurrently); the controller reads the volatile
/// immutable <see cref="_snapshot"/>; <see cref="_lastStartAction"/> is concurrent. No locks.</para>
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

    /// <summary>
    /// The daemon is holding this instance out of service for a maintenance window: drained on purpose,
    /// desired state still running, crash detection suppressed for the duration.
    /// </summary>
    /// <remarks>
    /// <b>Never a crash condition.</b> The pairing this pass raises on is a desired-running instance in a
    /// restart-pending or failed phase, and a park is neither — but it is a desired-running instance that
    /// is measurably down, which is exactly the shape a future producer would be tempted to read as an
    /// outage. Naming it here is what makes "somebody asked for this" a fact this file states rather than
    /// one it happens not to trip over.
    /// </remarks>
    private const string PhaseMaintenance = "maintenance";

    private readonly ApiOptions _options;
    private readonly IServiceProvider _services;
    private readonly MonitorClient _monitor;
    private readonly InstanceCache _instances;
    private readonly StreamHub _hub;
    private readonly ILogger<AlertEngine> _logger;

    // Mutated only by Tick / TickConditions / TickUpdates (single loop thread).
    private readonly Dictionary<string, Alert> _firing = new();          // id -> live firing record
    private readonly List<Alert> _resolved = new();                      // resolved, within retention
    private readonly Dictionary<string, DateTimeOffset> _clearSince = new(); // id -> when first read clear

    // Written by the event thread (NoteRecoveryAction), read by the poll thread (Tick). Lock-free.
    // Episode-scoped at read time by timestamp (see BuildResolution) — a stale action never bridges.
    private readonly ConcurrentDictionary<string, RecoveryAction> _lastStartAction = new();

    private volatile Snapshot _snapshot = Snapshot.Empty;

    public AlertEngine(ApiOptions options, IServiceProvider services, MonitorClient monitor, InstanceCache instances, StreamHub hub, ILogger<AlertEngine> logger)
    {
        _options = options;
        _services = services;
        _monitor = monitor;
        _instances = instances;
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
    /// The watchdog's autonomous crash-restart emits <c>server.restarted</c> (system/system) → a
    /// <c>server.restart</c> row that lands here too, so a real auto-heal still bridges its recovery.</summary>
    public void NoteRecoveryAction(string serverId, string actionId, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(actionId)) return;
        _lastStartAction[serverId] = new RecoveryAction(actionId, at);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WatchdogProvisioned && !_options.MetricsProvisioned)
        {
            // Neither optional source on this host. The loop still runs: the engine source needs nothing
            // provisioned — kgsm is this API's base dependency, and a host with no engine configured simply
            // leaves the instance cache empty, which produces no alerts rather than a wrong feed.
            _logger.LogInformation(
                "Alerts: no crash or metrics-threshold source provisioned — update availability is the only producer.");
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

        if (_options.MetricsProvisioned)
        {
            Snap.Snapshot? snapshot = await _monitor.GetLatestAsync(ct).ConfigureAwait(false); // null when monitor down
            TickConditions(snapshot, now);
        }

        // No I/O of its own: the instance cache already holds the fast status read every other surface is
        // served from, and update availability rides on it. A cache whose last engine read FAILED is holding
        // stale rows, so the pass is skipped rather than reconciled against them — the same honest-unknown
        // posture as a blind watchdog poll.
        if (_instances.EngineRead) TickUpdates(_instances.Statuses, now);
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
            bool parked = string.Equals(ws.Phase, PhaseMaintenance, StringComparison.OrdinalIgnoreCase);
            bool crashing = !parked && string.Equals(ws.Phase, PhaseRestartPending, StringComparison.OrdinalIgnoreCase);
            bool failed = !parked && string.Equals(ws.Phase, PhaseFailed, StringComparison.OrdinalIgnoreCase);
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
        // holds metric: alerts (the threshold source shares this dict), and those are TickConditions's to
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
            Alert resolved = record with { Status = AlertStatus.Resolved, ResolvedAt = now, Resolution = resolution, Actions = null };
            _resolved.Add(resolved);
            Publish(StreamProtocol.AlertResolve, id, new AlertResolved(id, resolution));
        }

        RebuildSnapshot(now);
    }

    /// <summary>
    /// One reconcile of the metric-threshold alerts against the conditions kgsm-monitor published on
    /// <paramref name="snap"/> — the <c>host-monitor</c>/<c>metrics</c> sources. Pure over the engine's
    /// in-memory state (no I/O beyond the hub publish) and single-threaded — the unit-test seam, mirroring
    /// <see cref="Tick"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>This decides nothing.</b> Whether a value is over its line, and whether it has been for long
    /// enough to count, is settled by the monitor against every sample it took. This API scrapes on a slower
    /// cadence than the monitor samples on, so a dwell evaluated here would be a claim about a window it saw
    /// a fraction of. What this pass contributes is the half a leaf has no business knowing: which
    /// <see cref="AlertSource"/> a condition belongs to, how loud it is, where in the panel it points, and
    /// the sentence a person reads.</para>
    /// <para><b>Present means firing; absent means resolved.</b> The monitor publishes breaching conditions
    /// only, having already run the clear dwell, so a condition that stops appearing has genuinely cleared
    /// and resolves at once. Re-running a probation here would delay a recovery the monitor already
    /// verified.</para>
    /// <para><b>Honest-unknown.</b> <paramref name="snap"/> <see langword="null"/> (the monitor is down)
    /// holds every metric alert unchanged — never resolves or retracts on the absence of an answer, exactly
    /// like a failed watchdog poll. The distinction the monitor being down would otherwise erase is the
    /// whole point: no conditions in a frame means all clear, and no frame at all means nobody knows.</para>
    /// </remarks>
    internal void TickConditions(Snap.Snapshot? snap, DateTimeOffset now)
    {
        if (snap is null) return; // honest-unknown: change nothing, hold every metric alert (no rebuild either)

        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (Snap.ConditionReading condition in snap.Conditions ?? [])
        {
            string id = MetricAlertId(condition);
            present.Add(id);

            Alert candidate = BuildMetricFiring(condition, id,
                _firing.TryGetValue(id, out Alert? held) ? held.RaisedAt : now);

            // Re-push only on a changed record. A condition worsening from warn to danger has to reach the
            // operator; the same condition restated every five seconds with a value that ticked by a tenth
            // of a percent does not, and would push a frame per scrape to every open browser.
            if (held is null || held.Severity != candidate.Severity || held.Title != candidate.Title
                || held.Detail != candidate.Detail)
            {
                _firing[id] = candidate;
                Publish(StreamProtocol.AlertRaise, id, candidate);
            }
        }

        // Scoped to metric: ids — _firing is shared with the crash and update sources, and their rows are
        // theirs to reconcile.
        foreach (string id in _firing.Keys.ToList())
        {
            if (!id.StartsWith(MetricIdPrefix, StringComparison.Ordinal)) continue;
            if (present.Contains(id)) continue;

            Alert record = _firing[id];
            _firing.Remove(id);
            var resolution = new AlertResolution(
                AlertResolvedBy.System, record.Source, "Recovered.", ActionId: null);
            _resolved.Add(record with { Status = AlertStatus.Resolved, ResolvedAt = now, Resolution = resolution, Actions = null });
            Publish(StreamProtocol.AlertResolve, id, new AlertResolved(id, resolution));
        }

        RebuildSnapshot(now);
    }

    /// <summary>
    /// The alert id for one condition: <c>metric:&lt;ruleKey&gt;</c> for a single-target rule, or
    /// <c>metric:&lt;ruleKey&gt;:&lt;ref-or-serverId&gt;</c> for one that fans out. Derived from the rule and
    /// target rather than from the monitor's episode id, deliberately: an episode id changes every time a
    /// condition clears and recurs, and this feed's contract is that one condition keeps one id so a re-fire
    /// upserts the record an operator is already looking at.
    /// </summary>
    private static string MetricAlertId(Snap.ConditionReading condition)
    {
        string? target = condition.Ref ?? condition.ServerId;
        return string.IsNullOrEmpty(target)
            ? $"{MetricIdPrefix}{condition.RuleKey}"
            : $"{MetricIdPrefix}{condition.RuleKey}:{target}";
    }

    /// <summary>The firing record for a threshold condition. <see cref="Alert.Escalated"/> is ALWAYS
    /// <see langword="false"/> — a metric in the danger band still auto-resolves once it recedes, so severity
    /// alone (never <c>escalated</c>) carries how bad it is.</summary>
    private Alert BuildMetricFiring(Snap.ConditionReading condition, string id, DateTimeOffset raisedAt)
    {
        bool hostScope = !string.Equals(condition.Scope, ConditionScope.Server, StringComparison.Ordinal);
        string severity = string.Equals(condition.Band, ConditionBandDanger, StringComparison.Ordinal)
            ? AlertSeverity.Danger
            : AlertSeverity.Warn;

        string noun = ConditionDisplay.Noun(condition.Metric);

        // Subject: a host rule names its target (mount/sensor ref, else the host itself); a server rule names
        // the instance. The measured value rides alongside, already unit-formatted.
        string subject = hostScope
            ? (condition.Ref is { Length: > 0 } refKey ? refKey : _options.HostId)
            : (condition.ServerId ?? "server");

        // Deep-link hints to the tab where the operator would act: a host-scope alert points at the host's
        // resources view; a server-scope alert at that server's performance tab.
        AlertAnchor anchor = hostScope
            ? new AlertAnchor(AlertSurface.Host, _options.HostId, Tab: "resources", Ref: condition.Ref)
            : new AlertAnchor(AlertSurface.Server, _options.HostId, Tab: "performance", Ref: condition.ServerId);

        return new Alert(
            Id: id,
            Severity: severity,
            Source: hostScope ? AlertSource.HostMonitor : AlertSource.Metrics,
            Title: $"{subject} {noun} at {ConditionDisplay.Format(condition.Metric, condition.Value)}",
            Detail: MetricDetail(condition, noun, severity, raisedAt),
            ServerId: condition.ServerId,
            HostId: _options.HostId,
            Anchor: anchor,
            Status: AlertStatus.Firing,
            RaisedAt: raisedAt,
            Escalated: false,
            Attempts: 0,
            Actions: AlertActionCatalog.For(
                hostScope ? AlertSource.HostMonitor : AlertSource.Metrics, condition.ServerId, escalated: false));
    }

    /// <summary>
    /// The detail line. It reports the peak the monitor actually recorded across the breach, which is the
    /// honest justification for the alert existing — the headline value is whatever the metric read when the
    /// frame was built, and on a value that moves those are not the same number. The peak is only worth
    /// saying when it differs from what the headline already shows.
    /// </summary>
    private static string MetricDetail(Snap.ConditionReading condition, string noun, string severity, DateTimeOffset raisedAt)
    {
        string band = severity == AlertSeverity.Danger ? "critical" : "high";
        long heldSec = (long)Math.Max(0, (DateTimeOffset.UtcNow - raisedAt).TotalSeconds);
        string held = $"Sustained {band} {noun} — {ConditionDisplay.Duration(heldSec)} so far";

        string current = ConditionDisplay.Format(condition.Metric, condition.Value);
        string peak = ConditionDisplay.Format(condition.Metric, condition.WindowMax);
        return string.Equals(current, peak, StringComparison.Ordinal)
            ? held + "."
            : held + $", peaking at {peak}.";
    }

    /// <summary>
    /// One reconcile of the update-available conditions against the instance cache's latest fast status
    /// read — the <c>engine</c> source. Pure over the engine's in-memory state (no I/O beyond the hub
    /// publish) and single-threaded, the unit-test seam, mirroring <see cref="Tick"/> and
    /// <see cref="TickConditions"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>This measures nothing.</b> kgsm establishes update availability — the scheduler's sweep runs
    /// the networked check and the engine records what it found beside the instance — so this pass reads a
    /// fact off a status read that touches no network, and its whole job is to mirror that fact into the
    /// feed. Nothing here compares versions or asks an upstream.</para>
    /// <para><b>Neither dwell applies.</b> The metric dwells exist because a measured value can spike; an
    /// update record cannot. It is written once, by a check that already completed, and stays written until
    /// the update is applied — so there is no blip to debounce on the way in, and the clear is a real
    /// transition (the game was updated) that an operator should see reflected at once rather than 30
    /// seconds later.</para>
    /// <para><b>Three states, not two.</b> <c>UpdatesAvailable</c> is <see langword="null"/> until something
    /// has checked, and that is not "no update" — it holds, exactly like a non-measured reading does. Only a
    /// measured <see langword="false"/> clears. An instance that disappears from the roster entirely was
    /// uninstalled, and a pending update on something that no longer exists is retracted, not resolved.</para>
    /// </remarks>
    internal void TickUpdates(IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> statuses, DateTimeOffset now)
    {
        foreach ((string serverId, Reading<InstanceRuntimeStatus> reading) in statuses)
        {
            if (reading is not { IsMeasured: true, Value: { } runtime }) continue; // honest-unknown: hold
            bool? available = runtime.Version.UpdatesAvailable;
            if (available is null) continue;                                       // never checked: hold

            string id = UpdateAlertId(serverId);
            if (available is true)
            {
                Alert candidate = BuildUpdateFiring(serverId, runtime.Version,
                    _firing.TryGetValue(id, out Alert? held) ? held.RaisedAt : now);

                // Re-push only on a changed record: a second build landing upstream before the operator
                // applies the first moves the target version, and the card would otherwise keep naming the
                // build they already read about.
                if (held is null || held.Detail != candidate.Detail || held.Title != candidate.Title)
                {
                    _firing[id] = candidate;
                    Publish(StreamProtocol.AlertRaise, id, candidate);
                }
                continue;
            }

            if (!_firing.TryGetValue(id, out Alert? firingRecord)) continue;
            _firing.Remove(id);
            var resolution = new AlertResolution(
                AlertResolvedBy.System, AlertSource.Engine,
                string.IsNullOrWhiteSpace(runtime.Version.Current)
                    ? "Up to date."
                    : $"Up to date — running {runtime.Version.Current}.",
                ActionId: null);
            _resolved.Add(firingRecord with { Status = AlertStatus.Resolved, ResolvedAt = now, Resolution = resolution, Actions = null });
            Publish(StreamProtocol.AlertResolve, id, new AlertResolved(id, resolution));
        }

        // Uninstalled while an update was pending. Scoped to update: ids — _firing is shared with the crash
        // and metric sources, and their rows are theirs to reconcile.
        foreach (string id in _firing.Keys.ToList())
        {
            if (!id.StartsWith(UpdateIdPrefix, StringComparison.Ordinal)) continue;
            if (statuses.ContainsKey(_firing[id].ServerId!)) continue;
            _firing.Remove(id);
            Publish(StreamProtocol.AlertRetract, id, new AlertRetracted(id));
        }

        RebuildSnapshot(now);
    }

    /// <summary>The firing record for an available game update (<c>engine</c> source). <see cref="Alert.Escalated"/>
    /// is always <see langword="false"/> and <see cref="Alert.Attempts"/> always 0 — nothing is retrying, and
    /// nothing gives up: the condition simply waits for someone to apply the update.</summary>
    private Alert BuildUpdateFiring(string serverId, VersionInfo version, DateTimeOffset raisedAt)
    {
        // Both halves are separately unknown-able. The engine records a version string it read from upstream,
        // and an instance whose installed version it cannot determine still gets an honest headline rather
        // than one built around an empty string.
        string installed = string.IsNullOrWhiteSpace(version.Current) ? "unknown" : version.Current;
        string latest = string.IsNullOrWhiteSpace(version.Latest) ? "unknown" : version.Latest!;

        return new Alert(
            Id: UpdateAlertId(serverId),
            Severity: AlertSeverity.Info,
            Source: AlertSource.Engine,
            Title: $"{serverId} has an update available",
            Detail: $"Installed {installed} · latest {latest}.",
            ServerId: serverId,
            HostId: _options.HostId,
            Anchor: new AlertAnchor(AlertSurface.Server, _options.HostId, Tab: null, Ref: serverId),
            Status: AlertStatus.Firing,
            RaisedAt: raisedAt,
            Escalated: false,
            Attempts: 0,
            Actions: AlertActionCatalog.For(AlertSource.Engine, serverId, escalated: false));
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
            Attempts: obs.Attempts,
            Actions: AlertActionCatalog.For(AlertSource.Watchdog, serverId, obs.Escalated));
    }

    private AlertResolution BuildResolution(string serverId, DateTimeOffset raisedAt, IReadOnlyList<WatchdogInstanceState> states)
    {
        WatchdogInstanceState? ws = states.FirstOrDefault(s => string.Equals(s.Name, serverId, StringComparison.Ordinal));
        bool running = ws is not null && string.Equals(ws.Phase, PhaseRunning, StringComparison.OrdinalIgnoreCase);
        bool stopped = ws is not null && !string.Equals(ws.Desired, DesiredRunning, StringComparison.OrdinalIgnoreCase);
        bool parked = ws is not null && string.Equals(ws.Phase, PhaseMaintenance, StringComparison.OrdinalIgnoreCase);

        // A park says why the crash condition stopped being true without claiming the server is back:
        // it is down, deliberately, and it will come back on its own. "Recovered" would be a claim about
        // a process nobody has seen yet.
        string reason = running ? "Recovered — running and stable."
            : parked ? "Held out of service for maintenance."
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

    /// <summary>Age off the rear-view, then republish the immutable snapshot the REST read serves. Called by
    /// BOTH <see cref="Tick"/> (crash) and <see cref="TickConditions"/> (threshold) so <see cref="_snapshot"/>
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

    private const string UpdateIdPrefix = "update:";
    private static string UpdateAlertId(string serverId) => $"{UpdateIdPrefix}{serverId}";

    private const string MetricIdPrefix = "metric:";

    // The monitor's vocabulary, as this API reads it. Two words and one of them is a band name, which is the
    // measure of how little of its own language a leaf is asked to speak.
    private const string ConditionBandDanger = "danger";
    private static class ConditionScope
    {
        public const string Server = "server";
    }

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
