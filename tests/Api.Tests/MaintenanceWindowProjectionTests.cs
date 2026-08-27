using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Scheduling;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The join behind a server's maintenance settings: what kgsm config says exists, and what the scheduler
/// leaf says about each window. Two authorities, and the rule for which answers what.
/// </summary>
public sealed class MaintenanceWindowProjectionTests
{
    private const string TwoWindows = "daily@05:00/backup;weekly.sun@04:00/backup,restart";

    [Fact]
    public void Config_is_the_authority_for_which_windows_exist()
    {
        // The leaf reads the same value on its own poll, so a window written a second ago is on this list
        // before the daemon has noticed it — with an honestly null next fire until it does.
        IReadOnlyList<MaintenanceWindowDto> windows = MaintenanceWindows.Project(TwoWindows, leaf: null);

        Assert.Equal(["daily@05:00", "weekly.sun@04:00"], windows.Select(w => w.Id));
        Assert.All(windows, w => Assert.Null(w.NextFireUtc));
        Assert.All(windows, w => Assert.Null(w.LastRun));
        Assert.All(windows, w => Assert.True(w.Valid));
    }

    [Fact]
    public void Nothing_configured_is_an_empty_list()
    {
        Assert.Empty(MaintenanceWindows.Project(null, leaf: null));
        Assert.Empty(MaintenanceWindows.Project("", leaf: null));
    }

    [Fact]
    public void The_leaf_supplies_the_next_fire_and_the_last_run()
    {
        var run = new SchedulerWindowRun(
            DateTimeOffset.Parse("2026-08-23T02:00:00Z"),
            DateTimeOffset.Parse("2026-08-23T02:07:41Z"),
            "failed",
            [
                new SchedulerTaskRun("backup", "ok", null),
                new SchedulerTaskRun("restart", "aborted", "a prior task in this window failed"),
            ]);

        SchedulerInstanceStatus leaf = Leaf(
            new SchedulerWindowStatus("weekly.sun@04:00", "appointment", ["backup", "restart"],
                true, null, DateTimeOffset.Parse("2026-08-30T02:00:00Z"), run));

        MaintenanceWindowDto window = Assert.Single(
            MaintenanceWindows.Project(TwoWindows, leaf), w => w.Id == "weekly.sun@04:00");

        Assert.Equal(DateTimeOffset.Parse("2026-08-30T02:00:00Z"), window.NextFireUtc);
        Assert.Equal("failed", window.LastRun!.Outcome);
        // All four outcomes travel as they were recorded: an aborted task never got its turn, and reading
        // it as a failure would say it was owed and did not happen.
        Assert.Equal(["ok", "aborted"], window.LastRun.Tasks.Select(t => t.Outcome));
        Assert.Equal("a prior task in this window failed", window.LastRun.Tasks[1].Message);
    }

    [Fact]
    public void A_window_the_leaf_refuses_is_reported_invalid_with_its_reason()
    {
        // The expression parses; this host still will not fire it. That refusal exists nowhere else, so
        // the leaf's verdict wins over the parser's where the leaf has one.
        SchedulerInstanceStatus leaf = Leaf(
            new SchedulerWindowStatus("daily@05:00", "appointment", ["backup"],
                false, "this host runs no 'backup' task", null, null));

        MaintenanceWindowDto window = MaintenanceWindows.Project(TwoWindows, leaf)[0];

        Assert.False(window.Valid);
        Assert.Equal("this host runs no 'backup' task", window.Error);
        Assert.Null(window.NextFireUtc);
    }

    [Fact]
    public void An_unreadable_window_disables_itself_and_leaves_the_rest_standing()
    {
        IReadOnlyList<MaintenanceWindowDto> windows =
            MaintenanceWindows.Project("daily@05:00/backup;weekly.funday@04:00/restart", leaf: null);

        Assert.True(windows[0].Valid);
        Assert.False(windows[1].Valid);
        Assert.Contains("funday", windows[1].Error);
        Assert.Null(windows[1].NextFireUtc);
    }

    [Fact]
    public void Tasks_come_back_in_canonical_order_whatever_order_they_were_written()
    {
        // A backup taken after an update archives the new build instead of the rollback point, so the
        // order is a property of what the tasks are rather than of how somebody typed them.
        MaintenanceWindowDto window = MaintenanceWindows.Project("weekly.sun@04:00/restart,update,backup", null)[0];
        Assert.Equal(["backup", "update", "restart"], window.Tasks);
    }

    [Fact]
    public void An_outcome_this_build_does_not_know_is_unknown_rather_than_ok()
    {
        SchedulerInstanceStatus leaf = Leaf(
            new SchedulerWindowStatus("daily@05:00", "appointment", ["backup"], true, null, null,
                new SchedulerWindowRun(null, null, "sideways", [new SchedulerTaskRun("backup", "sideways", null)])));

        MaintenanceRunDto run = MaintenanceWindows.Project(TwoWindows, leaf)[0].LastRun!;
        Assert.Equal("unknown", run.Outcome);
        Assert.Equal("unknown", run.Tasks[0].Outcome);
    }

    [Fact]
    public void A_container_can_carry_a_backup_and_not_a_restart()
    {
        Assert.Null(Refusal("daily@05:00/backup"));
        Assert.NotNull(Refusal("weekly.sun@04:00/backup,restart"));
        Assert.NotNull(Refusal("daily@05:00/update"));
    }

    private static string? Refusal(string expression) =>
        MaintenanceWindows.Refusal(
            TheKrystalShip.KGSM.Core.Scheduling.MaintenanceWindowParser.ParseWindow(expression),
            isContainer: true);

    private static SchedulerInstanceStatus Leaf(params SchedulerWindowStatus[] windows) =>
        new("factorio-1", "Europe/Madrid", windows);
}

/// <summary>
/// A parked server is not a stopped one, and the roster says so.
/// </summary>
public sealed class ParkedServerStatusTests
{
    [Fact]
    public void A_parked_instance_reads_maintenance_rather_than_stopped()
    {
        // The watchdog drains the process for the span of a window while desired state stays running and
        // crash-restart stays suppressed, so the engine's boolean is an honest "no process" and the phase
        // beside it is what says why. Nobody asked for this server to be down and nothing went wrong.
        Server server = Build(measuredRunning: false, parked: true);
        Assert.Equal(ServerStatus.Maintenance, server.Status);
    }

    [Fact]
    public void An_ordinary_stopped_instance_still_reads_stopped() =>
        Assert.Equal(ServerStatus.Stopped, Build(measuredRunning: false, parked: false).Status);

    [Fact]
    public void A_live_process_wins_over_a_park_the_daemon_has_already_released() =>
        // The process is the stronger fact: the phase index is polled, so it can name a park that ended
        // between one poll and the next.
        Assert.Equal(ServerStatus.Running, Build(measuredRunning: true, parked: true).Status);

    [Fact]
    public void An_unreadable_instance_is_unknown_rather_than_parked() =>
        // Its library is not mounted, so every reading comes out of a directory that is not there. A park
        // is a precise measurement and this is the absence of one.
        Assert.Equal(ServerStatus.Unknown, Build(measuredRunning: null, parked: true).Status);

    private static Server Build(bool? measuredRunning, bool parked)
    {
        var instance = new Instance
        {
            Name = "factorio-1",
            BlueprintFile = "factorio.bp.yaml",
            Runtime = InstanceRuntime.Native,
        };

        var statuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "factorio-1", Status = measuredRunning }),
        };

        return ServerAggregator.BuildServer(
            "factorio-1", instance, statuses,
            new Dictionary<string, BackupReading>(),
            new Dictionary<string, TheKrystalShip.KGSM.Monitor.Contracts.ServerMetrics>(),
            hostId: "test-host",
            isStarting: _ => false,
            activeJob: _ => null,
            isParked: _ => parked);
    }
}
