using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Integrations.WebPush;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Quiet hours: when a window is open, and what still gets through it.
/// <para>
/// The two halves fail in opposite directions and both matter. A window that reads as closed when it
/// should be open wakes somebody at 3am, which is the thing this feature exists to stop. A window that
/// reads as open when it should be closed swallows an outage, which is worse — so everything uncertain
/// here resolves towards delivering.
/// </para>
/// </summary>
public sealed class QuietHoursTests
{
    private static PushQuietHoursStore Store() =>
        // Only IsQuiet is exercised here; it is pure and touches no scope.
        new(null!, NullLogger<PushQuietHoursStore>.Instance);

    private static PushQuietHoursEntity Window(
        int startMinute, int endMinute, string zone = "UTC", bool enabled = true,
        string floor = PushQuietFloor.Danger) =>
        new()
        {
            UserSubject = "someone",
            Enabled = enabled,
            StartMinute = startMinute,
            EndMinute = endMinute,
            TimeZoneId = zone,
            MinSeverity = floor,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 12, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(22, 30, false)]  // before it opens
    [InlineData(23, 00, true)]   // the moment it opens
    [InlineData(2, 00, true)]    // the middle of the night, on the far side of midnight
    [InlineData(7, 59, true)]    // the last minute
    [InlineData(8, 00, false)]   // the moment it closes — exclusive, so the alarm hour is not quiet
    [InlineData(12, 00, false)]
    public void A_window_that_wraps_midnight_is_the_normal_one(int hour, int minute, bool expected) =>
        Assert.Equal(expected, Store().IsQuiet(Window(23 * 60, 8 * 60), At(hour, minute)));

    [Theory]
    [InlineData(12, 00, false)]
    [InlineData(13, 00, true)]
    [InlineData(14, 30, true)]
    [InlineData(15, 00, false)]
    public void A_window_inside_one_day_works_too(int hour, int minute, bool expected) =>
        Assert.Equal(expected, Store().IsQuiet(Window(13 * 60, 15 * 60), At(hour, minute)));

    [Fact]
    public void A_window_that_is_switched_off_is_never_open() =>
        Assert.False(Store().IsQuiet(Window(23 * 60, 8 * 60, enabled: false), At(2)));

    [Fact]
    public void The_times_are_read_where_the_PERSON_is_not_where_the_host_is()
    {
        // 02:00 UTC is 05:00 in Bucharest, which is inside a 23:00–08:00 night either way; 22:00 UTC is
        // 01:00 there — quiet — and would read as awake if the host used its own clock.
        PushQuietHoursEntity window = Window(23 * 60, 8 * 60, "Europe/Bucharest");

        Assert.True(Store().IsQuiet(window, At(2)));
        Assert.True(Store().IsQuiet(window, At(22)));
        // And 07:00 UTC is 10:00 there, well past the alarm.
        Assert.False(Store().IsQuiet(window, At(7)));
    }

    [Fact]
    public void A_zone_this_host_cannot_resolve_holds_nothing_back()
    {
        // The failure has to be the direction that delivers: being wrong this way costs a buzz at a bad
        // hour, and the other way costs an outage nobody was told about.
        Assert.False(Store().IsQuiet(Window(0, 23 * 60 + 59, "Mars/Olympus_Mons"), At(3)));
        Assert.False(Store().IsQuiet(Window(0, 23 * 60 + 59, ""), At(3)));
    }

    [Theory]
    [InlineData(PushQuietFloor.Everything, AuditSeverity.Info, true)]
    [InlineData(PushQuietFloor.Everything, AuditSeverity.Danger, true)]
    [InlineData(PushQuietFloor.Warn, AuditSeverity.Info, false)]
    [InlineData(PushQuietFloor.Warn, AuditSeverity.Success, false)]
    [InlineData(PushQuietFloor.Warn, AuditSeverity.Warn, true)]
    [InlineData(PushQuietFloor.Warn, AuditSeverity.Danger, true)]
    [InlineData(PushQuietFloor.Danger, AuditSeverity.Warn, false)]
    [InlineData(PushQuietFloor.Danger, AuditSeverity.Danger, true)]
    [InlineData(PushQuietFloor.Nothing, AuditSeverity.Danger, false)]
    public void The_floor_is_read_off_the_severity_the_row_already_carries(
        string floor, string severity, bool passes) =>
        Assert.Equal(passes, PushQuietFloor.Passes(severity, floor));

    [Fact]
    public void Success_ranks_with_info_because_it_means_something_finished_well() =>
        Assert.False(PushQuietFloor.Passes(AuditSeverity.Success, PushQuietFloor.Warn));

    [Fact]
    public void A_severity_this_build_does_not_know_does_not_earn_an_exception() =>
        // The floor's value is that it holds things back; an unrecognised spelling ranks as low as it
        // can rather than sailing through because nothing matched.
        Assert.False(PushQuietFloor.Passes("catastrophe", PushQuietFloor.Danger));

    [Theory]
    [InlineData(PushQuietFloor.Everything)]
    [InlineData(PushQuietFloor.Warn)]
    [InlineData(PushQuietFloor.Danger)]
    [InlineData(PushQuietFloor.Nothing)]
    public void Every_floor_the_vocabulary_names_is_accepted(string floor) =>
        Assert.True(PushQuietFloor.IsKnown(floor));

    [Fact]
    public void Nothing_is_a_floor_of_its_own_and_not_a_severity() =>
        // Spelled as a word precisely so it can never be misread as "no floor" — the two are opposites.
        Assert.False(PushQuietFloor.Passes(AuditSeverity.Danger, PushQuietFloor.Nothing));
}
