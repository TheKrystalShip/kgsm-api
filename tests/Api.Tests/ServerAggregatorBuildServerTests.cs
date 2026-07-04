using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.KGSM.Core.Models;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>ServerAggregator.BuildServer</c> — the single place the "starting" tri-state (an
/// <see cref="InstanceCache"/> latch, see <see cref="InstanceCacheStartingTests"/>) folds into the
/// <see cref="Server.Status"/> DTO field alongside the existing running/stopped/unknown derivation from
/// <c>Reading&lt;InstanceRuntimeStatus&gt;</c>. This is item (f) of the tri-state test matrix.
/// </summary>
public sealed class ServerAggregatorBuildServerTests
{
    private static readonly Instance TestInstance = new() { Name = "factorio-1", BlueprintFile = "factorio.bp.yaml" };
    private static readonly Dictionary<string, Snap.ServerMetrics> NoMetrics = new(StringComparer.Ordinal);

    [Fact]
    public void MeasuredUp_NotLatched_IsRunning()
    {
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoMetrics, "host-1",
            isStarting: _ => false);

        Assert.Equal(ServerStatus.Running, s.Status);
    }

    [Fact]
    public void MeasuredUp_Latched_IsStarting()
    {
        var statuses = Up("factorio-1");

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoMetrics, "host-1",
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

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoMetrics, "host-1",
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

        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoMetrics, "host-1",
            isStarting: _ => true);

        Assert.Equal(ServerStatus.Unknown, s.Status);
    }

    [Fact]
    public void MissingFromStatuses_IsUnknown()
    {
        Server s = ServerAggregator.BuildServer("factorio-1", TestInstance,
            new Dictionary<string, Reading<InstanceRuntimeStatus>>(), NoMetrics, "host-1",
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

        Server s1 = ServerAggregator.BuildServer("factorio-1", TestInstance, statuses, NoMetrics, "host-1",
            isStarting: id => id == "factorio-1");
        Server s2 = ServerAggregator.BuildServer("factorio-2", i2, statuses, NoMetrics, "host-1",
            isStarting: id => id == "factorio-1");

        Assert.Equal(ServerStatus.Starting, s1.Status);
        Assert.Equal(ServerStatus.Running, s2.Status); // NOT latched, even though the same call touched the map
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
