using System.Collections.Concurrent;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

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
/// read by the poll thread). <b>Limit:</b> an AUTONOMOUS watchdog crash-restart emits no start/restart
/// event (the watchdog emits only crash/failed — it is not an audited action today), so a pure auto-heal
/// resolves with <c>actionId</c> <see langword="null"/> — honest, never a fabricated link. Auditing the
/// watchdog's autonomous restart (a future kgsm-watchdog/kgsm-lib change) would bridge that case too.</para>
/// <para><b>Threading.</b> The alert state (<see cref="_firing"/>/<see cref="_resolved"/>/<see cref="_clearSince"/>)
/// is mutated ONLY by <see cref="Tick"/> on the single poll-loop thread; the controller reads the volatile
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

    private readonly ApiOptions _options;
    private readonly IServiceProvider _services;
    private readonly StreamHub _hub;
    private readonly ILogger<AlertEngine> _logger;

    // Mutated only by Tick (single loop thread).
    private readonly Dictionary<string, Alert> _firing = new();          // id -> live firing record
    private readonly List<Alert> _resolved = new();                      // resolved, within retention
    private readonly Dictionary<string, DateTimeOffset> _clearSince = new(); // id -> when first read clear

    // Written by the event thread (NoteStart), read by the poll thread (Tick). Lock-free.
    private readonly ConcurrentDictionary<string, string> _lastStartAction = new();

    private volatile Snapshot _snapshot = Snapshot.Empty;

    public AlertEngine(ApiOptions options, IServiceProvider services, StreamHub hub, ILogger<AlertEngine> logger)
    {
        _options = options;
        _services = services;
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
    /// up" recovery action) so a later crash resolution can reference it as <c>resolution.actionId</c> (the
    /// alert↔audit bridge). Called by the audit consumer AFTER the row is written. Lock-free; the last
    /// recovery for a server wins (the action that held). An autonomous watchdog restart is NOT a recovery
    /// action here (it emits no event) — so an auto-heal keeps <c>actionId</c> null, never fabricated.</summary>
    public void NoteRecoveryAction(string serverId, string actionId)
    {
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(actionId)) return;
        _lastStartAction[serverId] = actionId;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WatchdogProvisioned)
        {
            // No crash source on this host — GET /alerts serves an empty feed, the WS topic stays silent.
            _logger.LogInformation(
                "Alerts: watchdog not provisioned — crash alerts are off (GET /alerts serves an empty feed).");
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
        // Registered only when provisioned (see Startup); resolve optionally to stay safe.
        var watchdog = _services.GetService(typeof(IWatchdogClient)) as IWatchdogClient;
        if (watchdog is null) return;

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ProbeTimeout);
        IReadOnlyList<WatchdogInstanceState> states;
        try
        {
            states = await watchdog.ListAsync(timed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("watchdog list timed out after {Timeout} — skipping alert tick", ProbeTimeout);
            return; // honest-unknown: skip the tick, never resolve/retract on a blind cycle
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "watchdog list failed — skipping alert tick");
            return;
        }

        Tick(states, DateTimeOffset.UtcNow);
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

        // 2) resolve (probation-gated) the cleared, retract the vanished.
        foreach (string id in _firing.Keys.ToList())
        {
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
            AlertResolution resolution = BuildResolution(serverId, states);
            Alert resolved = record with { Status = AlertStatus.Resolved, ResolvedAt = now, Resolution = resolution };
            _resolved.Add(resolved);
            Publish(StreamProtocol.AlertResolve, id, new AlertResolved(id, resolution));
        }

        // 3) age off the rear-view.
        _resolved.RemoveAll(a => a.ResolvedAt is { } r && now - r > ResolvedRetention);

        // 4) republish the immutable snapshot the REST read serves.
        _snapshot = new Snapshot(
            _firing.Values.OrderBy(a => a.RaisedAt).ToList(),
            _resolved.OrderByDescending(a => a.ResolvedAt).ToList());
    }

    private Alert BuildFiring(string serverId, Observed obs, DateTimeOffset raisedAt)
    {
        string severity = obs.Escalated ? AlertSeverity.Danger : AlertSeverity.Warn;
        string title = obs.Escalated ? $"{serverId} keeps crashing" : $"{serverId} crashed";
        string detail = obs.Escalated
            ? $"Supervisor gave up after {obs.Attempts} restart(s)."
                + (string.IsNullOrEmpty(obs.Reason) ? "" : $" Last: {obs.Reason}")
            : (string.IsNullOrEmpty(obs.Reason) ? "Auto-restarting." : obs.Reason);

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

    private AlertResolution BuildResolution(string serverId, IReadOnlyList<WatchdogInstanceState> states)
    {
        WatchdogInstanceState? ws = states.FirstOrDefault(s => string.Equals(s.Name, serverId, StringComparison.Ordinal));
        bool running = ws is not null && string.Equals(ws.Phase, PhaseRunning, StringComparison.OrdinalIgnoreCase);
        bool stopped = ws is not null && !string.Equals(ws.Desired, DesiredRunning, StringComparison.OrdinalIgnoreCase);

        string reason = running ? "Recovered — running and stable."
            : stopped ? "Server was stopped — no longer supervised as running."
            : "No longer in a crash state.";

        // actionId is the bridge: set only when an operator/api start|restart (an audited recovery action,
        // stashed by NoteRecoveryAction) brought it back. A pure autonomous auto-heal (no audited action)
        // or a stop-cleared crash resolves with actionId null — honest, never a fabricated link.
        string? actionId = running && _lastStartAction.TryGetValue(serverId, out string? a) ? a : null;
        return new AlertResolution(AlertResolvedBy.System, AlertSource.Watchdog, reason, actionId);
    }

    private void Publish(string type, string id, object data) =>
        _hub.Publish(StreamProtocol.AlertsTopic, StreamProtocol.AlertEntityKey(id),
            new StreamMessage(StreamProtocol.AlertsTopic, type, data));

    private static string AlertId(string serverId) => $"crash:{serverId}";

    // The watchdog-observed crash condition for one instance (the inputs that shape the firing record).
    private readonly record struct Observed(bool Escalated, int Attempts, string Reason);

    // The immutable read view the controller serves (republished each tick).
    private sealed record Snapshot(IReadOnlyList<Alert> Firing, IReadOnlyList<Alert> Resolved)
    {
        public static readonly Snapshot Empty = new(Array.Empty<Alert>(), Array.Empty<Alert>());
    }
}
