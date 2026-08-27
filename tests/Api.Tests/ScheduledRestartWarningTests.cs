using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Integrations.WebPush;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The button on a maintenance-window warning, and the conditions under which it is not offered.
/// <para>
/// The warning is only worth sending because something can be done about it, so the cases that matter are
/// the ones where nothing can: a host that cannot reach the scheduler's control socket, and a warning that
/// cannot say which of a server's windows to move. In both the notification still goes out and carries
/// nothing to press, rather than offering a button that would fail.
/// </para>
/// </summary>
public sealed class ScheduledRestartWarningTests
{
    private static NotificationEvent Warning(string? actionSubject, string? window = "weekly.sun@04:00") =>
        new("restart_soon", DerivedNotificationAction.RestartSoon, "romestead", AuditSeverity.Warn,
            "romestead is restarting in 15 minute(s)", DateTimeOffset.UtcNow, "",
            SubjectKey: "maintenance/romestead/weekly.sun@04:00/2026-08-13T04:00:00Z",
            ActionSubject: actionSubject, ActionQualifier: window);

    [Fact]
    public void A_warning_offers_to_push_that_window_back()
    {
        PushActionOffer only = Assert.Single(PushActionCatalog.For(Warning("romestead")));
        Assert.Equal(PushActionKind.SchedulePostpone, only.Kind);
        Assert.Equal("romestead", only.Target);
        Assert.Equal("Postpone 1h", only.Label);
        // The scheduler moves one window and refuses an instruction that names none, so the button carries
        // which — on a server holding two appointments, moving the wrong one is worse than refusing.
        Assert.Equal("weekly.sun@04:00", only.Subject);
    }

    [Fact]
    public void A_host_that_cannot_reach_the_scheduler_offers_nothing() =>
        // The watcher clears the subject when the control socket is unconfigured, and a button offered
        // there would be an offer to fail — which on a lock screen is worse than no button at all.
        Assert.Empty(PushActionCatalog.For(Warning(null)));

    [Fact]
    public void A_warning_that_cannot_name_its_window_offers_nothing() =>
        Assert.Empty(PushActionCatalog.For(Warning("romestead", window: null)));

    [Fact]
    public void An_hour_is_what_the_button_says_and_what_it_does() =>
        Assert.Equal(60, PushActionCatalog.PostponeBy);

    [Fact]
    public void The_warning_is_its_own_subject_per_window_per_fire_time()
    {
        // Keyed on the window and the instant rather than the server, so a second window on the same
        // server, and the warning for a postponed-to time, are each a new fact instead of a repeat the
        // coalesce window would swallow.
        NotificationEvent first = Warning("romestead");
        NotificationEvent laterFire = first with { SubjectKey = "maintenance/romestead/weekly.sun@04:00/2026-08-13T05:00:00Z" };
        NotificationEvent otherWindow = first with { SubjectKey = "maintenance/romestead/daily@05:00/2026-08-13T04:00:00Z" };

        Assert.NotEqual(first.SubjectKey, laterFire.SubjectKey);
        Assert.NotEqual(first.SubjectKey, otherWindow.SubjectKey);
    }
}

/// <summary>
/// Which windows are worth a countdown at all — the rule the watcher applies before it ever looks at a
/// clock.
/// </summary>
public sealed class MaintenanceWarningRuleTests
{
    private static bool Warnable(string expression) =>
        ScheduledRestartWatcher.Warnable(MaintenanceWindowParser.ParseWindow(expression));

    [Fact]
    public void A_window_that_bounces_the_server_is_warned_about()
    {
        Assert.True(Warnable("weekly.sun@04:00/backup,restart"));
        Assert.True(Warnable("daily@05:00/update,restart"));
        Assert.True(Warnable("6h/restart"));
    }

    [Fact]
    public void A_nightly_archive_interrupts_nobody_and_is_not_warned_about() =>
        // A backup runs against a live server — kgsm records the state an archive was captured in — so
        // there is no true sentence to say about it to the people playing.
        Assert.False(Warnable("daily@05:00/backup"));

    [Fact]
    public void A_window_that_comes_round_faster_than_the_lead_is_not_warned_about()
    {
        // On a ten-minute window every tick after a fire is inside the fifteen-minute lead of the next, so
        // "in fifteen minutes" would be false and a phone would be pushed every cycle.
        Assert.False(Warnable("10m/restart"));
        Assert.False(Warnable("15m/restart"));
        Assert.True(Warnable("16m/restart"));
    }

    [Fact]
    public void An_unreadable_window_is_not_warned_about() =>
        Assert.False(Warnable("weekly.funday@04:00/restart"));
}
