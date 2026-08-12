using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Integrations;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Warns that a scheduled restart is nearly due, while there is still time to do something about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The point is the lead time.</b> Telling somebody a server restarted is an event; telling them it
/// is about to is the only version of the fact they can act on, and the action — pushing it back an hour
/// without leaving the game — is what the scheduler's control socket exists for.
/// </para>
/// <para>
/// <b>Only for a server that is actually running.</b> A restart due on a stopped server changes nothing
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
    /// How far ahead of a scheduled restart the warning goes out. Long enough to finish what you are
    /// doing or to push it back, short enough that it is still about tonight.
    /// </summary>
    internal static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // instance -> the fire time already warned about. Keyed on the instant, not just the instance, so a
    // postponement re-arms the warning for the new time instead of silencing it forever.
    private readonly Dictionary<string, DateTimeOffset> _warned = new(StringComparer.Ordinal);

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
                    logger.LogDebug(ex, "scheduled-restart watch tick failed");
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

        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (SchedulerInstanceStatus status in snapshot.Instances)
        {
            if (status.NextFireUtc is not { } fire) continue;
            if (!IsMeasuredRunning(status.Name)) continue;

            live.Add(status.Name);

            TimeSpan until = fire - now;
            if (until <= TimeSpan.Zero || until > LeadTime) continue;
            if (_warned.TryGetValue(status.Name, out DateTimeOffset already) && already == fire) continue;

            _warned[status.Name] = fire;
            Announce(status.Name, fire, until, now, scheduler.CanControl);
        }

        // A server that stopped, lost its schedule, or left the host forgets what it was warned about.
        foreach (string gone in _warned.Keys.Where(k => !live.Contains(k)).ToList())
            _warned.Remove(gone);
    }

    private void Announce(string instance, DateTimeOffset fire, TimeSpan until, DateTimeOffset now, bool canPostpone)
    {
        int minutes = Math.Max(1, (int)Math.Round(until.TotalMinutes));
        string summary = $"{instance} is scheduled to restart in {minutes} minute(s)";

        logger.LogInformation("scheduled restart: {Summary} (at {Fire:o})", summary, fire);

        bus.PublishDerived(new NotificationEvent(
            CatalogId: "restart_soon",
            Action: DerivedNotificationAction.RestartSoon,
            ServerId: instance,
            Severity: AuditSeverity.Warn,
            Summary: summary,
            Ts: now,
            AuditId: "",
            // Keyed on the fire it is about, so a warning for a postponed-to time is its own fact rather
            // than a repeat the coalesce window would swallow.
            SubjectKey: $"restart/{instance}/{fire:O}",
            // Nothing to act on where this host cannot reach the scheduler's control socket — a button
            // offered there would be an offer to fail.
            ActionSubject: canPostpone ? instance : null));
    }

    private bool IsMeasuredRunning(string instance) =>
        instances.Statuses.TryGetValue(instance, out KGSM.Core.Models.Reading<KGSM.Core.Models.InstanceRuntimeStatus>? reading)
        && reading is { IsMeasured: true, Value.Status: true };
}
