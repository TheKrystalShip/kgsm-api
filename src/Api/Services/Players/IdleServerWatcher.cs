using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Services.Players;

/// <summary>
/// Notices a server that is running with nobody on it, and says so once.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a reading, not an event.</b> Nothing happens at the moment a server becomes idle — the last
/// person simply left a while ago — so there is no engine event to echo and nothing to tap. This watcher
/// takes the reading instead: two authorities have to agree, the engine that the server is running and the
/// supervisor that no session is open on it, and it has to keep agreeing for <see cref="EmptyDwell"/>.
/// </para>
/// <para>
/// <b>The dwell is what makes it worth sending.</b> Without one this fires every time the last player
/// signs off for a minute, which is most evenings on most servers. With one it fires when a server has
/// genuinely been left running for nothing — which is the only version of this fact anybody wants a phone
/// to buzz about, and the only one where the Stop button it carries is the right answer.
/// </para>
/// <para>
/// <b>Unobservable presence is never empty.</b> A game this host cannot watch players on reports no
/// sessions for exactly the same reason a deserted one does, and the two are not the same fact. Only an
/// instance the supervisor says it is actually detecting is considered at all, so a server nobody can see
/// into is silently skipped rather than declared abandoned.
/// </para>
/// <para>
/// <b>Once per emptying.</b> The announcement latches until somebody joins, the server stops, or presence
/// stops being observable — so a server left down for a fortnight is one notification, not one every tick.
/// </para>
/// </remarks>
public sealed class IdleServerWatcher(
    IServiceProvider services,
    InstanceCache instances,
    INotificationBus bus,
    ILogger<IdleServerWatcher> logger) : BackgroundService
{
    /// <summary>How long a server has to have been empty before it is worth telling somebody. Long enough
    /// that a lull between sessions passes unremarked, short enough that the hours it would otherwise sit
    /// there are still ahead of you when the notification arrives.</summary>
    internal static readonly TimeSpan EmptyDwell = TimeSpan.FromMinutes(30);

    /// <summary>How often the reading is taken. Coarse on purpose — this measures a condition that only
    /// means anything after half an hour, so polling it faster buys nothing.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IdleTracker _tracker = new(EmptyDwell);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad reading must never end the watch.
                    logger.LogDebug(ex, "idle-server watch tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    internal async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (services.GetService(typeof(IWatchdogClient)) is not IWatchdogClient watchdog) return;

        IReadOnlyDictionary<string, WatchdogInstancePresence>? presence;
        using (var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probe.Token))
            presence = await watchdog.GetPlayerPresenceAsync(linked.Token).ConfigureAwait(false);

        // No answer from the supervisor is not "everything is empty" — it is not knowing. Every arming
        // clock is dropped, so a server that really is idle re-arms from zero once presence is readable
        // again rather than announcing on the strength of a gap in the record.
        if (presence is null)
        {
            _tracker.Forget();
            return;
        }

        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string instance, WatchdogInstancePresence entry) in presence)
        {
            if (!entry.IsDetected) continue;            // this host cannot see who is on it
            if (!IsMeasuredRunning(instance)) continue; // stopped, or the engine could not be read
            live.Add(instance);

            if (_tracker.Observe(instance, entry.Players.Count, now) is { } been)
                Announce(instance, been, now);
        }

        // Anything that dropped out of the observable-and-running set this tick loses its clock, so a
        // stop-then-start or a supervisor that stops detecting a game starts the measurement over.
        _tracker.Retain(live);
    }

    private void Announce(string instance, TimeSpan been, DateTimeOffset now)
    {
        int minutes = (int)Math.Round(been.TotalMinutes);
        logger.LogInformation("idle: {Server} has had nobody connected for {Minutes} minutes", instance, minutes);

        bus.PublishDerived(new NotificationEvent(
            CatalogId: "server_empty",
            Action: DerivedNotificationAction.ServerEmpty,
            ServerId: instance,
            Severity: AuditSeverity.Info,
            Summary: $"nobody has been on {instance} for {minutes} minutes",
            Ts: now,
            // No audit row stands behind this, and an id pointing at nothing would be worse than none.
            AuditId: ""));
    }

    private bool IsMeasuredRunning(string instance) =>
        instances.Statuses.TryGetValue(instance, out Reading<InstanceRuntimeStatus>? reading)
        && reading is { IsMeasured: true, Value.Status: true };
}

/// <summary>
/// The dwell and the latch, with no I/O — how long each server has been observed empty and whether that
/// has already been said. Separated from the watcher so the rule can be exercised against a clock instead
/// of against a running fleet.
/// </summary>
/// <remarks>Single-threaded by construction: only the watcher's timer loop touches it.</remarks>
internal sealed class IdleTracker(TimeSpan dwell)
{
    private readonly Dictionary<string, DateTimeOffset> _emptySince = new(StringComparer.Ordinal);
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);

    /// <summary>
    /// Record one observation of a running, observable server.
    /// </summary>
    /// <returns>How long it has been empty, the one time that crosses the dwell; <see langword="null"/>
    /// on every other observation — somebody is on it, it has not been empty long enough, or this has
    /// already been announced for the current stretch.</returns>
    public TimeSpan? Observe(string instance, int players, DateTimeOffset now)
    {
        if (players > 0)
        {
            Rearm(instance);
            return null;
        }

        if (!_emptySince.TryGetValue(instance, out DateTimeOffset since))
        {
            // First sight of it empty. The clock starts now rather than at whenever the last person left:
            // this process may have just started, and claiming a duration nobody measured is the one thing
            // the summary must not do.
            _emptySince[instance] = now;
            return null;
        }

        TimeSpan been = now - since;
        return been >= dwell && _announced.Add(instance) ? been : null;
    }

    /// <summary>Drop the clock for every server not in <paramref name="live"/> — stopped, no longer
    /// observable, or gone from the host. Each is a reason the measurement has to start over.</summary>
    public void Retain(IReadOnlySet<string> live)
    {
        foreach (string gone in _emptySince.Keys.Where(k => !live.Contains(k)).ToList())
            Rearm(gone);
    }

    /// <summary>Drop everything — the answer to not knowing, which is not the same as knowing nothing
    /// is connected.</summary>
    public void Forget()
    {
        _emptySince.Clear();
        _announced.Clear();
    }

    private void Rearm(string instance)
    {
        _emptySince.Remove(instance);
        _announced.Remove(instance);
    }
}
