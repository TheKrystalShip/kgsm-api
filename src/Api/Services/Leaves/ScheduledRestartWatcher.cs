using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Scheduling;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Warns that a maintenance window is nearly due, while there is still time to do something about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The point is the lead time.</b> Telling somebody a server restarted is an event; telling them it
/// is about to is the only version of the fact they can act on, and the action — pushing that one window
/// back an hour without leaving the game — is what the scheduler's control socket exists for.
/// </para>
/// <para>
/// <b>Only a window that interrupts somebody.</b> A nightly archive runs against a live server and takes
/// nobody offline, so there is no true sentence to warn about; only a window carrying an update or a
/// restart is announced.
/// </para>
/// <para>
/// <b>Only for a server that is actually running.</b> Maintenance due on a stopped server changes nothing
/// anybody is in the middle of, and a warning about it is noise dressed as urgency.
/// </para>
/// <para>
/// <b>It re-reads the scheduler on every tick rather than remembering the time it warned about.</b> That
/// is what makes a postponement work: the deferred fire is outside the window again on the next pass, so
/// the same warning is not re-sent — and it re-arms honestly if the new time comes round with the person
/// still playing. The scheduler's snapshot is rebuilt on its own tick, so the moved time is visible
/// within a minute of the tap.
/// </para>
/// </remarks>
public sealed class ScheduledRestartWatcher(
    IServiceProvider services,
    Aggregation.InstanceCache instances,
    INotificationBus bus,
    ILogger<ScheduledRestartWatcher> logger) : BackgroundService
{
    /// <summary>
    /// How far ahead of a window the warning goes out. Long enough to finish what you are doing or to push
    /// it back, short enough that it is still about tonight.
    /// </summary>
    internal static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // (instance, window) -> the fire time already warned about. One instance can hold several windows
    // counting down, so the window is half the key; the fire time is the other half, so a postponement
    // re-arms the warning for the new time instead of silencing it forever.
    private readonly Dictionary<(string Instance, string Window), DateTimeOffset> _warned = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await TickAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "maintenance-window watch tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    internal async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (services.GetService(typeof(SchedulerClient)) is not SchedulerClient scheduler) return;

        SchedulerStatusResponse? snapshot = await scheduler.GetStatusAsync(ct).ConfigureAwait(false);
        // Not knowing what is scheduled is not the same as nothing being scheduled: say nothing, and
        // keep what was already warned about so a blip does not produce a second warning.
        if (snapshot is null) return;

        var live = new HashSet<(string, string)>();

        foreach (SchedulerInstanceStatus status in snapshot.Instances ?? [])
        {
            if (!IsMeasuredRunning(status.Name)) continue;

            foreach (SchedulerWindowStatus row in status.Windows ?? [])
            {
                if (!row.Valid) continue;
                if (row.NextFireUtc is not { } fire) continue;

                MaintenanceWindow window = Read(row);
                if (!Warnable(window)) continue;

                var key = (status.Name, row.Id);
                live.Add(key);

                TimeSpan until = fire - now;
                if (until <= TimeSpan.Zero || until > LeadTime) continue;
                if (_warned.TryGetValue(key, out DateTimeOffset already) && already == fire) continue;

                _warned[key] = fire;
                Announce(status.Name, window, fire, until, now, scheduler.CanControl);
            }
        }

        // A server that stopped, or a window that was edited away, forgets what it was warned about.
        foreach ((string, string) gone in _warned.Keys.Where(k => !live.Contains(k)).ToList())
            _warned.Remove(gone);
    }

    /// <summary>
    /// Whether a window is worth counting down at all.
    /// </summary>
    /// <remarks>
    /// <b>A window that comes round faster than the lead is never warned about.</b> The warning says "in
    /// fifteen minutes", and on a ten-minute window every tick from the moment one fire lands is inside
    /// the lead of the next — so the sentence would be false and it would be pushed to a phone every
    /// cycle. Below the lead, the fire is closer than the notice could ever be useful.
    /// </remarks>
    internal static bool Warnable(MaintenanceWindow window) =>
        window.IsValid
        && MaintenanceWindows.IsDisruptive(window)
        && MaintenanceWindows.PeriodOf(window) is { } period
        && period > LeadTime;

    // The leaf reports a window as its id (the schedule expression) plus its tasks, which is exactly the
    // grammar — so the one parser reads it back rather than this file learning to tell an interval from an
    // appointment on its own.
    private static MaintenanceWindow Read(SchedulerWindowStatus row) =>
        MaintenanceWindowParser.ParseWindow($"{row.Id}/{string.Join(',', row.Tasks ?? [])}");

    private void Announce(
        string instance, MaintenanceWindow window, DateTimeOffset fire, TimeSpan until,
        DateTimeOffset now, bool canPostpone)
    {
        int minutes = Math.Max(1, (int)Math.Round(until.TotalMinutes));
        string reason = MaintenanceWindows.DisruptionReason(window) ?? "restarting";
        string summary = $"{instance} is {reason} in {minutes} minute(s)";

        logger.LogInformation("maintenance window {Window} on {Instance}: {Summary} (at {Fire:o})",
            window.Id, instance, summary, fire);

        bus.PublishDerived(new NotificationEvent(
            CatalogId: "restart_soon",
            Action: DerivedNotificationAction.RestartSoon,
            ServerId: instance,
            Severity: AuditSeverity.Warn,
            Summary: summary,
            Ts: now,
            AuditId: "",
            // Keyed on the window and the fire it is about, so a second window on the same server, and a
            // warning for a postponed-to time, are each their own fact rather than a repeat the coalesce
            // window would swallow.
            SubjectKey: $"maintenance/{instance}/{window.Id}/{fire:O}",
            // Nothing to act on where this host cannot reach the scheduler's control socket — a button
            // offered there would be an offer to fail.
            ActionSubject: canPostpone ? instance : null,
            // The verb moves one window, so the button has to name which. An instruction naming none is
            // refused by the daemon, and moving the wrong appointment is worse than refusing.
            ActionQualifier: window.Id));
    }

    private bool IsMeasuredRunning(string instance) =>
        instances.Statuses.TryGetValue(instance, out KGSM.Core.Models.Reading<KGSM.Core.Models.InstanceRuntimeStatus>? reading)
        && reading is { IsMeasured: true, Value.Status: true };
}
