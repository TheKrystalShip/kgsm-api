using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.KGSM.Core.Models;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>ServerAggregator.BuildServer</c> — the single place the "starting" tri-state (an
/// <see cref="InstanceCache"/> latch, see <see cref="InstanceCacheStartingTests"/>) folds into the
/// <see cref="Server.Status"/> DTO field alongside the existing running/stopped/unknown derivation from
/// <c>Reading&lt;InstanceRuntimeStatus&gt;</c>, AND where the update-check cache (the slow, networked
/// probe's reading) folds into <see cref="Server.UpdateAvailable"/>/<see cref="Server.LatestVersion"/>/
/// <see cref="Server.UpdateCheckedAt"/>. This is item (f) of the tri-state test matrix, plus the
/// update-check field mapping.
/// </summary>
public sealed class ServerAggregatorBuildServerTests
{
    private static readonly Instance TestInstance = new() { Name = "factorio-1", BlueprintFile = "factorio.bp.yaml" };
    private static readonly Dictionary<string, Snap.ServerMetrics> NoMetrics = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, BackupReading> NoBackupReadings = new(StringComparer.Ordinal);
    // No long-running operation owns the instance — what JobRegistry.InFlightFor reports for an idle server.
    private static readonly Func<string, Job?> NoActiveJob = _ => null;

    [Fact]
    public void MeasuredUp_NotLatched_IsRunning()
    {
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Equal(ServerStatus.Running, s.Status);
    }

    [Fact]
    public void MeasuredUp_Latched_IsStarting()
    {
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: id => id == "factorio-1", activeJob: NoActiveJob);

        Assert.Equal(ServerStatus.Starting, s.Status);
    }

    [Fact]
    public void MeasuredDown_LatchIgnored_IsStopped_NeverStartingWhileDown()
    {
        // Belt-and-suspenders: even if the latch were somehow still open for a measured-down instance
        // (UpdateStatus already clears it on any stop/crash/fail — this proves BuildServer itself never
        // trusts a stale/inconsistent latch over an honest "down" reading).
        var statuses = Down("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => true, activeJob: NoActiveJob);

        Assert.Equal(ServerStatus.Stopped, s.Status);
    }

    [Fact]
    public void NotMeasured_IsUnknown_RegardlessOfLatch()
    {
        var statuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Unavailable("requires regeneration"),
        };

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => true, activeJob: NoActiveJob);

        Assert.Equal(ServerStatus.Unknown, s.Status);
    }

    [Fact]
    public void MissingFromStatuses_IsUnknown()
    {
        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance,
            new Dictionary<string, Reading<InstanceRuntimeStatus>>(), NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Equal(ServerStatus.Unknown, s.Status);
    }

    [Fact]
    public void Latch_IsCheckedPerInstanceId_NotGlobal()
    {
        var statuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "factorio-1", Status = true }),
            ["factorio-2"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "factorio-2", Status = true }),
        };
        var i2 = new Instance { Name = "factorio-2", BlueprintFile = "factorio.bp.yaml" };

        Server s1 = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: id => id == "factorio-1", activeJob: NoActiveJob);
        Server s2 = ServerAggregator.BuildServer("factorio-2", i2, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: id => id == "factorio-1", activeJob: NoActiveJob);

        Assert.Equal(ServerStatus.Starting, s1.Status);
        Assert.Equal(ServerStatus.Running, s2.Status); // NOT latched, even though the same call touched the map
    }

    // --- update-check field mapping (the engine's own record → DTO) --------------------------------
    //
    // All three fields come off the same status reading as the version. kgsm answers them from what it
    // recorded beside the instance, so this API runs no probe and holds no second opinion — these tests
    // pin that the DTO carries the engine's answer through unchanged, including its unknowns.

    [Fact]
    public void NoUpdateReading_LightsUpNull_NeverFabricated()
    {
        // Nothing has ever checked this instance (checked:false) — the honest-null triple, never a
        // fabricated false/"no update".
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Null(s.UpdateAvailable);
        Assert.Null(s.LatestVersion);
        Assert.Null(s.UpdateCheckedAt);
    }

    [Fact]
    public void UpdateAvailableTrue_PopulatesFields()
    {
        var now = DateTimeOffset.UtcNow;
        var statuses = UpWithVersion("factorio-1",
            new VersionInfo { Current = "1.4.1", Latest = "1.4.2", Checked = true, UpdatesAvailable = true, CheckedAt = now });

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false, activeJob: NoActiveJob);

        Assert.True(s.UpdateAvailable);
        Assert.Equal("1.4.2", s.LatestVersion);
        Assert.Equal(now, s.UpdateCheckedAt);
    }

    [Fact]
    public void UpdateAvailableFalse_LatestCheckedPresent_NoUpdate()
    {
        // A checked reading reporting "on the latest build" — false, not null. The SPA chip stays off with
        // an honest "on the latest build" reason (a real check ran, not the never-checked unknown).
        var now = DateTimeOffset.UtcNow;
        var statuses = UpWithVersion("factorio-1",
            new VersionInfo { Current = "1.4.2", Latest = "1.4.2", Checked = true, UpdatesAvailable = false, CheckedAt = now });

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false, activeJob: NoActiveJob);

        Assert.False(s.UpdateAvailable);
        Assert.Equal("1.4.2", s.LatestVersion);
        Assert.Equal(now, s.UpdateCheckedAt);
    }

    // A check that ran but could not reach upstream: the engine reports checked:false with no latest and
    // no time. The DTO must carry that through as unknown rather than settling on the last thing it saw —
    // there is no last thing to settle on any more.
    [Fact]
    public void AnUnreachableUpstream_ReadsAsUnknown_NotAsUpToDate()
    {
        var statuses = UpWithVersion("factorio-1",
            new VersionInfo { Current = "1.4.1", Latest = null, Checked = false, UpdatesAvailable = null, CheckedAt = null });

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Null(s.UpdateAvailable);
        Assert.Null(s.LatestVersion);
        Assert.Null(s.UpdateCheckedAt);
        Assert.Equal("1.4.1", s.Version);
    }

    // --- connect port (the list-visible player-facing port) -----------------------------------------

    [Fact]
    public void ConnectPort_IsTheFirstRequiredPort()
    {
        // The blueprint writes the game/connect port first, so the first mapping wins even when a later
        // one is numerically lower — order is the signal, not magnitude.
        var statuses = Up("factorio-1");
        var i = new Instance
        {
            Name = "factorio-1",
            BlueprintFile = "factorio.bp.yaml",
            Ports = [new PortMapping { Start = 34197, End = 34197, Protocol = "udp" },
                     new PortMapping { Start = 27015, End = 27015, Protocol = "tcp" }],
        };

        Server s = ServerAggregator.BuildServer("factorio-1", i, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Equal(34197, s.ConnectPort);
    }

    [Fact]
    public void ConnectPort_RangeYieldsItsStart()
    {
        var statuses = Up("factorio-1");
        var i = new Instance
        {
            Name = "factorio-1",
            BlueprintFile = "factorio.bp.yaml",
            Ports = [new PortMapping { Start = 27015, End = 27020, Protocol = "udp" }],
        };

        Server s = ServerAggregator.BuildServer("factorio-1", i, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Equal(27015, s.ConnectPort);
    }

    [Fact]
    public void ConnectPort_NoPorts_IsNull_NeverFabricated()
    {
        // TestInstance declares no ports — honest null, never a 0 or a guessed game default.
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Null(s.ConnectPort);
    }

    [Fact]
    public void ConnectPort_SkipsMalformedMappings()
    {
        // An inverted range is skipped exactly as NetworkAggregator's expansion skips it, so the two
        // surfaces can never disagree about which port is "first".
        var statuses = Up("factorio-1");
        var i = new Instance
        {
            Name = "factorio-1",
            BlueprintFile = "factorio.bp.yaml",
            Ports = [new PortMapping { Start = 27020, End = 27015, Protocol = "udp" },
                     new PortMapping { Start = 34197, End = 34197, Protocol = "udp" }],
        };

        Server s = ServerAggregator.BuildServer("factorio-1", i, statuses, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Equal(34197, s.ConnectPort);
    }

    [Fact]
    public void NoBackupReading_BothFieldsNull_NotScannedIsNotZeroBackups()
    {
        // The cold-cache case. "We have not looked yet" must NOT render as "this server has no backups" —
        // a null count is what lets a surface say "unknown" instead of claiming a measured zero.
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Null(s.LastBackup);
        Assert.Null(s.BackupCount);
    }

    [Fact]
    public void MeasuredZeroBackups_CountIsZero_NotNull()
    {
        // The engine was read and honestly reported no backups. That is a measured zero, and it must be
        // distinguishable from the cold-cache case above — this is the whole reason BackupCount exists.
        var statuses = Up("factorio-1");
        var backups = new Dictionary<string, BackupReading> { ["factorio-1"] = new(null, 0) };

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            backups, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Null(s.LastBackup);
        Assert.Equal(0, s.BackupCount);
    }

    [Fact]
    public void LatestBackup_CarriesTheWholeManifestRecord()
    {
        var statuses = Up("factorio-1");
        var taken = DateTimeOffset.UtcNow.AddHours(-3);
        var latest = new ServerBackup("factorio-1-20260802T200543Z-7f7e04", taken, "1.4.1",
            SizeBytes: 4096, FileCount: 12, Compressed: true, Consistency: "cold",
            Sources: ["install", "saves"], Sha256: "abc123");
        var backups = new Dictionary<string, BackupReading> { ["factorio-1"] = new(latest, 3) };

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            backups, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Equal(3, s.BackupCount);
        Assert.NotNull(s.LastBackup);
        Assert.Equal("factorio-1-20260802T200543Z-7f7e04", s.LastBackup!.Name);
        Assert.Equal(taken, s.LastBackup.CreatedAt);
        Assert.Equal("1.4.1", s.LastBackup.Version);
        Assert.Equal(4096, s.LastBackup.SizeBytes);
        Assert.Equal("cold", s.LastBackup.Consistency);
        Assert.Equal(["install", "saves"], s.LastBackup.Sources!);
    }

    [Fact]
    public void BackupsAreIndependentOfRunStateAndMetrics()
    {
        // A stopped server with no metrics row still holds whatever backups it holds. Backup standing is
        // its own axis — never inferred from, or suppressed by, run-state or metric presence.
        var statuses = Down("factorio-1");
        var latest = new ServerBackup("factorio-1-20260802T200543Z-7f7e04", DateTimeOffset.UtcNow.AddDays(-1));
        var backups = new Dictionary<string, BackupReading> { ["factorio-1"] = new(latest, 1) };

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            backups, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Equal(ServerStatus.Stopped, s.Status);
        Assert.Null(s.Metrics);
        Assert.Equal(1, s.BackupCount);
        Assert.NotNull(s.LastBackup);
    }

    [Fact]
    public void ActiveJobIsCarriedForTheMatchingInstanceOnly()
    {
        var statuses = Down("factorio-1");
        var update = new Job("job_abc12345", "factorio-1", CommandVerb.Update, JobState.Running,
            DateTimeOffset.UtcNow, SettledAt: null, Error: null);

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false,
            activeJob: id => id == "factorio-1" ? update : null);

        Assert.NotNull(s.ActiveJob);
        Assert.Equal(CommandVerb.Update, s.ActiveJob!.Verb);
        Assert.Equal(JobState.Running, s.ActiveJob.State);

        Server other = ServerAggregator.BuildServer("factorio-2", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false,
            activeJob: id => id == "factorio-1" ? update : null);

        Assert.Null(other.ActiveJob);
    }

    [Fact]
    public void ActiveJobDoesNotChangeStatus()
    {
        // An update in flight is what is being DONE to the instance; status stays what the instance IS.
        // A surface joins the two itself — folding the job into status here would put a word outside the
        // honest run-state vocabulary on the wire.
        var statuses = Down("factorio-1");
        var update = new Job("job_abc12345", "factorio-1", CommandVerb.Update, JobState.Running,
            DateTimeOffset.UtcNow, SettledAt: null, Error: null);

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false, activeJob: _ => update);

        Assert.Equal(ServerStatus.Stopped, s.Status);
    }

    // The whole point of carrying the footprint outside the metrics block: a stopped instance has no
    // sample and still occupies its disk, so a card can show what it takes up without the API implying
    // a reading nobody took.
    [Fact]
    public void StoppedServerReportsItsFootprintWithNoMetricsBlock()
    {
        var statuses = Down("factorio-1");
        var disks = new Dictionary<string, long>(StringComparer.Ordinal) { ["factorio-1"] = 4_294_967_296L };

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings,
            NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob, diskBytesById: disks);

        Assert.Equal(ServerStatus.Stopped, s.Status);
        Assert.Null(s.Metrics);
        Assert.Equal(4_294_967_296L, s.DiskBytes);
    }

    // Absent from the walk is "not measured", and it must not arrive as a 0 that reads like an empty
    // install directory.
    [Fact]
    public void UnmeasuredFootprintIsNullNeverZero()
    {
        var statuses = Up("factorio-1");

        Server withoutIndex = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob);
        Server notInIndex = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob,
            diskBytesById: new Dictionary<string, long>(StringComparer.Ordinal) { ["other"] = 1024 });

        Assert.Null(withoutIndex.DiskBytes);
        Assert.Null(notInIndex.DiskBytes);
    }

    // The count is whatever the roster answered for this id — a measured figure, carried through
    // untouched, including a measured zero for an observable server nobody is on.
    [Fact]
    public void OnlinePlayersCarriesTheMeasuredCount()
    {
        var statuses = Up("factorio-1");

        Server busy = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob,
            diskBytesById: null, onlinePlayers: _ => 3);
        Server empty = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob,
            diskBytesById: null, onlinePlayers: _ => 0);

        Assert.Equal(3, busy.OnlinePlayers);
        Assert.Equal(0, empty.OnlinePlayers);
    }

    // Unobservable presence is null, and a caller that asks for no count at all gets null too. Neither
    // may arrive as a 0 — a fleet total would silently absorb it as "nobody is on that server".
    [Fact]
    public void UnobservablePresenceIsNullNeverZero()
    {
        var statuses = Up("factorio-1");

        Server unobservable = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob,
            diskBytesById: null, onlinePlayers: _ => null);
        Server unasked = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Null(unobservable.OnlinePlayers);
        Assert.Null(unasked.OnlinePlayers);
    }

    // A stopped server is still asked, because the roster is what says whether anyone is on it — the
    // count must never be inferred from run-state (metric-presence is never a status, and neither is
    // its inverse).
    [Fact]
    public void CountComesFromTheRosterNotFromRunState()
    {
        var statuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "factorio-1", Status = false }),
        };

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob,
            diskBytesById: null, onlinePlayers: _ => 0);

        Assert.Equal(ServerStatus.Stopped, s.Status);
        Assert.Equal(0, s.OnlinePlayers);
    }

    // --- helpers -----------------------------------------------------------------------------------


    // ---- the run clock: which source dates a run, and when each half is reported ----------------

    private static readonly DateTimeOffset Spawned = new(2026, 8, 21, 9, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Exited = new(2026, 8, 18, 14, 44, 5, TimeSpan.Zero);

    private static Func<string, Services.Availability.RunTimes> Clock(
        DateTimeOffset? spawned = null, DateTimeOffset? exited = null) =>
        _ => new Services.Availability.RunTimes(spawned, exited);

    private static readonly Instance ContainerInstance = new()
    {
        Name = "factorio-1",
        BlueprintFile = "factorio.bp.yaml",
        Runtime = InstanceRuntime.Container,
    };

    [Fact]
    public void Native_IsDatedByTheWatchdog_NotTheEngine()
    {
        // The engine reports nothing for a watchdog-spawned native (no local pid file), which is the whole
        // reason the daemon is asked.
        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, Up("factorio-1"), NoBackupReadings,
            NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob,
            runTimes: Clock(spawned: Spawned));

        Assert.Equal(Spawned, s.StartedAt);
    }

    [Fact]
    public void Container_KeepsTheEnginesReading_AndIsNotDatedByTheWatchdog()
    {
        // Docker supplies a container's start time and the daemon does not supervise one, so the watchdog's
        // clock must not overwrite the engine's reading here.
        var engineStart = new DateTime(2026, 8, 20, 6, 0, 0, DateTimeKind.Utc);
        var statuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus
            {
                InstanceName = "factorio-1",
                Status = true,
                Process = new ProcessInfo { StartTime = engineStart },
            }),
        };

        Server s = ServerAggregator.BuildServer("factorio-1", ContainerInstance, statuses, NoBackupReadings,
            NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob,
            runTimes: Clock(spawned: Spawned, exited: Exited));

        Assert.Equal(new DateTimeOffset(engineStart), s.StartedAt);
        Assert.Null(s.StoppedAt);
    }

    [Fact]
    public void StoppedAt_IsReportedOnlyWhileStopped()
    {
        // The ledger always holds the last run's end. Reporting it beside a LIVE run would date a stop that
        // has already been superseded by the run currently going.
        Server running = ServerAggregator.BuildServer("factorio-1", TestInstance, Up("factorio-1"), NoBackupReadings,
            NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob,
            runTimes: Clock(spawned: Spawned, exited: Exited));

        Assert.Null(running.StoppedAt);

        Server stopped = ServerAggregator.BuildServer("factorio-1", TestInstance, Down("factorio-1"), NoBackupReadings,
            NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob,
            runTimes: Clock(exited: Exited));

        Assert.Equal(Exited, stopped.StoppedAt);
    }

    [Fact]
    public void NoRunClock_LeavesBothHalvesNull()
    {
        // A host with no watchdog: honestly undated, never a fabricated start.
        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, Down("factorio-1"), NoBackupReadings,
            NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob);

        Assert.Null(s.StartedAt);
        Assert.Null(s.StoppedAt);
    }

    [Fact]
    public void AnUnknownStatus_ReportsNoStopTime()
    {
        // Unknown is not stopped. Dating a stop for an instance whose state could not be read would state
        // something the host does not know.
        var statuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Unavailable("requires regeneration"),
        };

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoBackupReadings,
            NoMetrics, "host-1", isStarting: _ => false, activeJob: NoActiveJob,
            runTimes: Clock(exited: Exited));

        Assert.Equal(ServerStatus.Unknown, s.Status);
        Assert.Null(s.StoppedAt);
    }

    private static Dictionary<string, Reading<InstanceRuntimeStatus>> Up(string id) => new()
    {
        [id] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus { InstanceName = id, Status = true }),
    };

    private static Dictionary<string, Reading<InstanceRuntimeStatus>> UpWithVersion(string id, VersionInfo version) => new()
    {
        [id] = Reading<InstanceRuntimeStatus>.Measured(
            new InstanceRuntimeStatus { InstanceName = id, Status = true, Version = version }),
    };

    private static Dictionary<string, Reading<InstanceRuntimeStatus>> Down(string id) => new()
    {
        [id] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus { InstanceName = id, Status = false }),
    };
}