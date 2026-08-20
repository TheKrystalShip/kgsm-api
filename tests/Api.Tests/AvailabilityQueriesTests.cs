using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Availability;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>AvailabilityQueries.Fold</c> — the replay that turns engine lifecycle events into uptime.
/// </summary>
/// <remarks>
/// The whole design rests on one distinction the tests below exercise from both sides: a server that
/// was <em>stopped</em> is not down, and a server that <em>crashed</em> is. Getting that backwards
/// would either score a fleet by how much of it happens to be running, or hide every outage behind a
/// denominator that grew to match.
/// </remarks>
public sealed class AvailabilityQueriesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static EventHistoryEntry Ev(string type, TimeSpan at, string instance = "srv") =>
        new($"evt_{type}_{at.Ticks}", T0 + at, type, instance, null, null, null, null, null);

    private static ServerAvailability Fold(TimeSpan windowFrom, TimeSpan windowTo, params EventHistoryEntry[] events) =>
        AvailabilityQueries.Fold("srv", [.. events], T0 + windowFrom, T0 + windowTo);

    [Fact]
    public void RunningTheWholeWindow_IsFullyAvailable()
    {
        // Started before the window and never touched again: the window opens on a known state.
        ServerAvailability a = Fold(TimeSpan.FromHours(1), TimeSpan.FromHours(3),
            Ev("instance_started", TimeSpan.Zero));

        Assert.Equal(1.0, a.Availability);
        Assert.Equal((long)TimeSpan.FromHours(2).TotalSeconds, a.IntendedSeconds);
        Assert.Equal(0, a.DowntimeSeconds);
        Assert.Equal(0, a.Outages);
        Assert.True(a.Seeded);
    }

    [Fact]
    public void DeliberatelyStopped_IsNotDowntime_AndLowersTheDenominator()
    {
        // Up for the first hour of a two-hour window, then stopped on purpose.
        ServerAvailability a = Fold(TimeSpan.FromHours(1), TimeSpan.FromHours(3),
            Ev("instance_started", TimeSpan.Zero),
            Ev("instance_stopped", TimeSpan.FromHours(2)));

        Assert.Equal(1.0, a.Availability);
        Assert.Equal((long)TimeSpan.FromHours(1).TotalSeconds, a.IntendedSeconds);
        Assert.Equal(0, a.DowntimeSeconds);
        Assert.Equal(0, a.Outages);
    }

    [Fact]
    public void Crash_IsDowntime_UntilItComesBack()
    {
        // Two-hour window, crashed for the middle 30 minutes.
        ServerAvailability a = Fold(TimeSpan.FromHours(1), TimeSpan.FromHours(3),
            Ev("instance_started", TimeSpan.Zero),
            Ev("instance_crashed", TimeSpan.FromHours(1.5)),
            Ev("instance_started", TimeSpan.FromHours(2)));

        Assert.Equal((long)TimeSpan.FromHours(2).TotalSeconds, a.IntendedSeconds);
        Assert.Equal((long)TimeSpan.FromMinutes(30).TotalSeconds, a.DowntimeSeconds);
        Assert.Equal(0.75, a.Availability);
        Assert.Equal(1, a.Outages);
        Assert.Equal(T0 + TimeSpan.FromHours(1.5), a.LastOutage);
    }

    [Fact]
    public void StillDownAtTheEnd_AccruesToTheWindowClose()
    {
        ServerAvailability a = Fold(TimeSpan.FromHours(1), TimeSpan.FromHours(3),
            Ev("instance_started", TimeSpan.Zero),
            Ev("instance_crashed", TimeSpan.FromHours(2)));

        Assert.Equal((long)TimeSpan.FromHours(1).TotalSeconds, a.DowntimeSeconds);
        Assert.Equal(0.5, a.Availability);
    }

    [Fact]
    public void NothingWantedItUp_HasNoFigureAtAll()
    {
        // Stopped before the window and left alone. Not 100% — there is no denominator.
        ServerAvailability a = Fold(TimeSpan.FromHours(1), TimeSpan.FromHours(3),
            Ev("instance_stopped", TimeSpan.Zero));

        Assert.Null(a.Availability);
        Assert.Equal(0, a.IntendedSeconds);
        Assert.True(a.Seeded);
    }

    [Fact]
    public void NoEventsAtAll_IsUnmeasured_NotPerfect()
    {
        ServerAvailability a = Fold(TimeSpan.FromHours(1), TimeSpan.FromHours(3));

        Assert.Null(a.Availability);
        Assert.Equal(0, a.IntendedSeconds);
        Assert.False(a.Seeded);
    }

    [Fact]
    public void StateBeforeTheWindow_DoesNotAccrue()
    {
        // A full day of uptime before the window must not leak into a two-hour window's denominator.
        ServerAvailability a = Fold(TimeSpan.FromHours(24), TimeSpan.FromHours(26),
            Ev("instance_started", TimeSpan.Zero));

        Assert.Equal((long)TimeSpan.FromHours(2).TotalSeconds, a.IntendedSeconds);
    }

    [Fact]
    public void FirstInWindowEvent_SeedsWithoutBackdating()
    {
        // Nothing before the window says what the state was, so the span before the first event
        // accrues nothing rather than being assumed running.
        ServerAvailability a = Fold(TimeSpan.Zero, TimeSpan.FromHours(4),
            Ev("instance_started", TimeSpan.FromHours(3)));

        Assert.Equal((long)TimeSpan.FromHours(1).TotalSeconds, a.IntendedSeconds);
        Assert.False(a.Seeded);
    }

    [Fact]
    public void Uninstalled_StopsAccruingEntirely()
    {
        ServerAvailability a = Fold(TimeSpan.FromHours(1), TimeSpan.FromHours(5),
            Ev("instance_started", TimeSpan.Zero),
            Ev("instance_uninstalled", TimeSpan.FromHours(2)));

        Assert.Equal((long)TimeSpan.FromHours(1).TotalSeconds, a.IntendedSeconds);
        Assert.Equal(0, a.DowntimeSeconds);
    }

    [Fact]
    public void RepeatedCrashes_CountAsSeparateOutages()
    {
        ServerAvailability a = Fold(TimeSpan.Zero, TimeSpan.FromHours(6),
            Ev("instance_started", TimeSpan.FromHours(1)),
            Ev("instance_crashed", TimeSpan.FromHours(2)),
            Ev("instance_started", TimeSpan.FromHours(3)),
            Ev("instance_crashed", TimeSpan.FromHours(4)),
            Ev("instance_started", TimeSpan.FromHours(5)));

        Assert.Equal(2, a.Outages);
        Assert.Equal((long)TimeSpan.FromHours(2).TotalSeconds, a.DowntimeSeconds);
    }

    [Fact]
    public void OutageOpenBeforeTheWindow_IsNotRecounted()
    {
        // The gap still costs availability; the outage itself began earlier and is not a new one.
        ServerAvailability a = Fold(TimeSpan.FromHours(2), TimeSpan.FromHours(4),
            Ev("instance_started", TimeSpan.Zero),
            Ev("instance_crashed", TimeSpan.FromHours(1)));

        Assert.Equal(0, a.Outages);
        Assert.Equal((long)TimeSpan.FromHours(2).TotalSeconds, a.DowntimeSeconds);
        Assert.Equal(0.0, a.Availability);
    }

    [Fact]
    public void ReadyAfterCrash_ClosesTheOutage()
    {
        ServerAvailability a = Fold(TimeSpan.Zero, TimeSpan.FromHours(4),
            Ev("instance_started", TimeSpan.FromHours(1)),
            Ev("instance_crashed", TimeSpan.FromHours(2)),
            Ev("instance_ready", TimeSpan.FromHours(3)));

        Assert.Equal((long)TimeSpan.FromHours(1).TotalSeconds, a.DowntimeSeconds);
        Assert.Equal(1, a.Outages);
    }

    [Fact]
    public void PlannedRestart_IsDowntime_ButNotAnOutage()
    {
        // The shutdown half of a restart: unreachable for ten minutes, and nobody should be paged.
        ServerAvailability a = Fold(TimeSpan.Zero, TimeSpan.FromHours(2),
            Ev("instance_started", TimeSpan.Zero),
            Ev("instance_restart_stopped", TimeSpan.FromMinutes(30)),
            Ev("instance_restarted", TimeSpan.FromMinutes(40)));

        Assert.Equal((long)TimeSpan.FromMinutes(10).TotalSeconds, a.DowntimeSeconds);
        Assert.Equal(0, a.Outages);
        Assert.Null(a.LastOutage);
    }

    [Fact]
    public void CrashDuringAPlannedRestartWindow_StillCountsAsAnOutage()
    {
        ServerAvailability a = Fold(TimeSpan.Zero, TimeSpan.FromHours(2),
            Ev("instance_started", TimeSpan.Zero),
            Ev("instance_restart_stopped", TimeSpan.FromMinutes(30)),
            Ev("instance_restarted", TimeSpan.FromMinutes(31)),
            Ev("instance_crashed", TimeSpan.FromMinutes(40)),
            Ev("instance_started", TimeSpan.FromMinutes(50)));

        Assert.Equal(1, a.Outages);
        Assert.Equal((long)TimeSpan.FromMinutes(11).TotalSeconds, a.DowntimeSeconds);
    }

    // ---- The rollup ----

    [Fact]
    public void FleetRollup_IsTimeWeighted_NotAnAverageOfPercentages()
    {
        // One server barely ran and was fully down; one ran all week and was perfect. Averaging the
        // percentages would report 50%; weighting by time reports the ~1% of intended time lost.
        List<ServerAvailability> rows =
        [
            new("tiny", 0.0, IntendedSeconds: 100, DowntimeSeconds: 100, Outages: 1, LastOutage: T0, Seeded: true),
            new("big", 1.0, IntendedSeconds: 9900, DowntimeSeconds: 0, Outages: 0, LastOutage: null, Seeded: true),
        ];

        FleetAvailability f = AvailabilityQueries.Roll(rows);

        Assert.Equal(0.99, f.Availability);
        Assert.Equal(10000, f.IntendedSeconds);
        Assert.Equal(2, f.ServersCounted);
        Assert.Equal(1, f.Outages);
    }

    [Fact]
    public void ServersWithNoDenominator_AreExcludedFromTheRollup()
    {
        List<ServerAvailability> rows =
        [
            new("idle", null, 0, 0, 0, null, Seeded: true),
            new("live", 1.0, 3600, 0, 0, null, Seeded: true),
        ];

        FleetAvailability f = AvailabilityQueries.Roll(rows);

        Assert.Equal(1, f.ServersCounted);
        Assert.Equal(3600, f.IntendedSeconds);
    }

    [Fact]
    public void AnEntirelyIdleFleet_HasNoFigure()
    {
        FleetAvailability f = AvailabilityQueries.Roll(
            [new("idle", null, 0, 0, 0, null, Seeded: false)]);

        Assert.Null(f.Availability);
        Assert.Equal(0, f.ServersCounted);
    }

    // ---- Window parsing ----

    [Theory]
    [InlineData("24h", 24)]
    [InlineData("7d", 24 * 7)]
    [InlineData("30d", 24 * 30)]
    public void ParseWindow_ReadsHoursAndDays(string label, int expectedHours) =>
        Assert.Equal(TimeSpan.FromHours(expectedHours), AvailabilityQueries.ParseWindow(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("0d")]
    [InlineData("-5d")]
    public void ParseWindow_FallsBackToTheDefault(string? label) =>
        Assert.Equal(AvailabilityQueries.DefaultWindow, AvailabilityQueries.ParseWindow(label));

    [Fact]
    public void ParseWindow_ClampsToTheMaximum() =>
        Assert.Equal(AvailabilityQueries.MaxWindow, AvailabilityQueries.ParseWindow("999d"));

    // ---- No journal ----

    [Fact]
    public async Task NoJournal_ReportsDegraded_WithNullFigures()
    {
        AvailabilityReport r = await AvailabilityQueries.BuildAsync(
            journal: null, ["a", "b"], TimeSpan.FromDays(7), "7d", T0, CancellationToken.None);

        Assert.True(r.EngineHistoryDegraded);
        Assert.Null(r.Fleet.Availability);
        Assert.Equal(2, r.Servers.Count);
        Assert.All(r.Servers, s => Assert.Null(s.Availability));
    }
}

/// <summary>
/// <c>UpdateLagIndex.Select</c> — which "update available" notice still stands for each instance.
/// </summary>
public sealed class UpdateLagIndexTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static EventHistoryEntry Ev(string type, TimeSpan at, string instance) =>
        new($"evt_{instance}_{at.Ticks}", T0 + at, type, instance, null, null, null, null, null);

    private static EventHistoryEntry Notice(TimeSpan at, string instance = "srv") =>
        Ev("instance_update_available", at, instance);

    private static EventHistoryEntry Updated(TimeSpan at, string instance = "srv") =>
        Ev("instance_version_updated", at, instance);

    [Fact]
    public void TakesTheOldestNotice_NotTheNewest()
    {
        // The scheduler re-notices on every sweep. Taking the newest would report a gap that has been
        // open for two days as two minutes old.
        Dictionary<string, DateTimeOffset> since = Services.Availability.UpdateLagIndex.Select(
            [Notice(TimeSpan.FromHours(48)), Notice(TimeSpan.FromHours(49)), Notice(TimeSpan.FromHours(50))],
            []);

        Assert.Equal(T0 + TimeSpan.FromHours(48), since["srv"]);
    }

    [Fact]
    public void ANoticeClearedByAnUpdate_DoesNotStand()
    {
        Dictionary<string, DateTimeOffset> since = Services.Availability.UpdateLagIndex.Select(
            [Notice(TimeSpan.FromHours(1))],
            [Updated(TimeSpan.FromHours(2))]);

        Assert.Empty(since);
    }

    [Fact]
    public void ANoticeAfterTheLastUpdate_StandsAgain()
    {
        // Updated, then fell behind again: the age dates from the SECOND notice.
        Dictionary<string, DateTimeOffset> since = Services.Availability.UpdateLagIndex.Select(
            [Notice(TimeSpan.FromHours(1)), Notice(TimeSpan.FromHours(5))],
            [Updated(TimeSpan.FromHours(2))]);

        Assert.Equal(T0 + TimeSpan.FromHours(5), since["srv"]);
    }

    [Fact]
    public void InstancesAreIndependent()
    {
        Dictionary<string, DateTimeOffset> since = Services.Availability.UpdateLagIndex.Select(
            [Notice(TimeSpan.FromHours(1), "a"), Notice(TimeSpan.FromHours(3), "b")],
            [Updated(TimeSpan.FromHours(2), "a")]);

        Assert.False(since.ContainsKey("a"));
        Assert.Equal(T0 + TimeSpan.FromHours(3), since["b"]);
    }

    [Fact]
    public void NoNotices_IsAnEmptyIndex() =>
        Assert.Empty(Services.Availability.UpdateLagIndex.Select([], [Updated(TimeSpan.FromHours(1))]));
}
