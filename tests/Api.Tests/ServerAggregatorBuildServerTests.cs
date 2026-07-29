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

    [Fact]
    public void MeasuredUp_NotLatched_IsRunning()
    {
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoMetrics, "host-1",
            isStarting: _ => false);

        Assert.Equal(ServerStatus.Running, s.Status);
    }

    [Fact]
    public void MeasuredUp_Latched_IsStarting()
    {
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoMetrics, "host-1",
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

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoMetrics, "host-1",
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

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoMetrics, "host-1",
            isStarting: _ => true);

        Assert.Equal(ServerStatus.Unknown, s.Status);
    }

    [Fact]
    public void MissingFromStatuses_IsUnknown()
    {
        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance,
            new Dictionary<string, Reading<InstanceRuntimeStatus>>(), NoUpdateReadings, NoMetrics, "host-1",
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

        Server s1 = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoMetrics, "host-1",
            isStarting: id => id == "factorio-1");
        Server s2 = ServerAggregator.BuildServer("factorio-2", i2, statuses, NoUpdateReadings, NoMetrics, "host-1",
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

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoUpdateReadings, NoMetrics, "host-1",
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

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, updates, NoMetrics, "host-1",
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

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, updates, NoMetrics, "host-1",
            isStarting: _ => false);

        Assert.False(s.UpdateAvailable);
        Assert.Null(s.LatestVersion);
        Assert.Equal(now, s.UpdateCheckedAt);
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