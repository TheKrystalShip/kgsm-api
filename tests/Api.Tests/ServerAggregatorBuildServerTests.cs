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
    private static readonly Dictionary<string, UpdateReading> NoUpdateReadings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, BackupReading> NoBackupReadings = new(StringComparer.Ordinal);

    [Fact]
    public void MeasuredUp_NotLatched_IsRunning()
    {
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false);

        Assert.Equal(ServerStatus.Running, s.Status);
    }

    [Fact]
    public void MeasuredUp_Latched_IsStarting()
    {
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: id => id == "factorio-1");

        Assert.Equal(ServerStatus.Starting, s.Status);
    }

    [Fact]
    public void MeasuredDown_LatchIgnored_IsStopped_NeverStartingWhileDown()
    {
        // Belt-and-suspenders: even if the latch were somehow still open for a measured-down instance
        // (UpdateStatus already clears it on any stop/crash/fail — this proves BuildServer itself never
        // trusts a stale/inconsistent latch over an honest "down" reading).
        var statuses = Down("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => true);

        Assert.Equal(ServerStatus.Stopped, s.Status);
    }

    [Fact]
    public void NotMeasured_IsUnknown_RegardlessOfLatch()
    {
        var statuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Unavailable("requires regeneration"),
        };

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => true);

        Assert.Equal(ServerStatus.Unknown, s.Status);
    }

    [Fact]
    public void MissingFromStatuses_IsUnknown()
    {
        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance,
            new Dictionary<string, Reading<InstanceRuntimeStatus>>(), NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false);

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

        Server s1 = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: id => id == "factorio-1");
        Server s2 = ServerAggregator.BuildServer("factorio-2", i2, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: id => id == "factorio-1");

        Assert.Equal(ServerStatus.Starting, s1.Status);
        Assert.Equal(ServerStatus.Running, s2.Status); // NOT latched, even though the same call touched the map
    }

    // --- update-check field mapping (the slow-probe reading → DTO) ----------------------------------

    [Fact]
    public void NoUpdateReading_LightsUpNull_NeverFabricated()
    {
        // A cold update-check cache (the probe hasn't run yet, or the engine is unprovisioned) yields the
        // honest-null triple — never a fabricated false/"no update".
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false);

        Assert.Null(s.UpdateAvailable);
        Assert.Null(s.LatestVersion);
        Assert.Null(s.UpdateCheckedAt);
    }

    [Fact]
    public void UpdateAvailableTrue_PopulatesFields()
    {
        var statuses = Up("factorio-1");
        var now = DateTimeOffset.UtcNow;
        var updates = new Dictionary<string, UpdateReading>
        {
            ["factorio-1"] = new(true, "1.4.2", now),
        };

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, updates, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false);

        Assert.True(s.UpdateAvailable);
        Assert.Equal("1.4.2", s.LatestVersion);
        Assert.Equal(now, s.UpdateCheckedAt);
    }

    [Fact]
    public void UpdateAvailableFalse_LatestCheckedPresent_NoUpdate()
    {
        // A checked reading reporting "on the latest build" — false, not null. The SPA chip stays off with
        // an honest "on the latest build" reason (a real check ran, not the cold-cache unknown).
        var statuses = Up("factorio-1");
        var now = DateTimeOffset.UtcNow;
        var updates = new Dictionary<string, UpdateReading>
        {
            ["factorio-1"] = new(false, null, now),
        };

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, updates, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false);

        Assert.False(s.UpdateAvailable);
        Assert.Null(s.LatestVersion);
        Assert.Equal(now, s.UpdateCheckedAt);
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

        Server s = ServerAggregator.BuildServer("factorio-1", i, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false);

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

        Server s = ServerAggregator.BuildServer("factorio-1", i, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false);

        Assert.Equal(27015, s.ConnectPort);
    }

    [Fact]
    public void ConnectPort_NoPorts_IsNull_NeverFabricated()
    {
        // TestInstance declares no ports — honest null, never a 0 or a guessed game default.
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false);

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

        Server s = ServerAggregator.BuildServer("factorio-1", i, statuses, NoUpdateReadings, NoBackupReadings, NoMetrics, "host-1",
            isStarting: _ => false);

        Assert.Equal(34197, s.ConnectPort);
    }

    [Fact]
    public void NoBackupReading_BothFieldsNull_NotScannedIsNotZeroBackups()
    {
        // The cold-cache case. "We have not looked yet" must NOT render as "this server has no backups" —
        // a null count is what lets a surface say "unknown" instead of claiming a measured zero.
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings,
            NoBackupReadings, NoMetrics, "host-1", isStarting: _ => false);

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

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings,
            backups, NoMetrics, "host-1", isStarting: _ => false);

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

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings,
            backups, NoMetrics, "host-1", isStarting: _ => false);

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

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings,
            backups, NoMetrics, "host-1", isStarting: _ => false);

        Assert.Equal(ServerStatus.Stopped, s.Status);
        Assert.Null(s.Metrics);
        Assert.Equal(1, s.BackupCount);
        Assert.NotNull(s.LastBackup);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static Dictionary<string, Reading<InstanceRuntimeStatus>> Up(string id) => new()
    {
        [id] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus { InstanceName = id, Status = true }),
    };

    private static Dictionary<string, Reading<InstanceRuntimeStatus>> Down(string id) => new()
    {
        [id] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus { InstanceName = id, Status = false }),
    };
}