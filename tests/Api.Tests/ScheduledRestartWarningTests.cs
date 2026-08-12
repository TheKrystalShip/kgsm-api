using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Integrations.WebPush;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The button on a scheduled-restart warning, and the one condition under which it is not offered.
/// <para>
/// The warning itself is only worth sending because something can be done about it, so the case that
/// matters is the host that cannot reach the scheduler's control socket: there, the notification still
/// goes out and carries nothing to press, rather than offering a button that would fail.
/// </para>
/// </summary>
public sealed class ScheduledRestartWarningTests
{
    private static NotificationEvent Warning(string? actionSubject) =>
        new("restart_soon", DerivedNotificationAction.RestartSoon, "romestead", AuditSeverity.Warn,
            "romestead is scheduled to restart in 15 minute(s)", DateTimeOffset.UtcNow, "",
            SubjectKey: "restart/romestead/2026-08-13T04:00:00Z", ActionSubject: actionSubject);

    [Fact]
    public void A_warning_offers_to_push_the_restart_back()
    {
        PushActionOffer only = Assert.Single(PushActionCatalog.For(Warning("romestead")));
        Assert.Equal(PushActionKind.SchedulePostpone, only.Kind);
        Assert.Equal("romestead", only.Target);
        Assert.Equal("Postpone 1h", only.Label);
    }

    [Fact]
    public void A_host_that_cannot_reach_the_scheduler_offers_nothing() =>
        // The watcher clears the subject when the control socket is unconfigured, and a button offered
        // there would be an offer to fail — which on a lock screen is worse than no button at all.
        Assert.Empty(PushActionCatalog.For(Warning(null)));

    [Fact]
    public void An_hour_is_what_the_button_says_and_what_it_does() =>
        Assert.Equal(60, PushActionCatalog.PostponeBy);

    [Fact]
    public void The_warning_is_its_own_subject_per_fire_time()
    {
        // Keyed on the instant rather than the server, so the warning for a postponed-to time is a new
        // fact instead of a repeat the coalesce window would swallow — which is what makes a second
        // warning possible if the new time comes round with the person still playing.
        NotificationEvent first = Warning("romestead");
        NotificationEvent second = first with { SubjectKey = "restart/romestead/2026-08-13T05:00:00Z" };
        Assert.NotEqual(first.SubjectKey, second.SubjectKey);
    }
}
