using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>MetricsPump.BuildRoster</c> — the one frame a grid of server cards reads, as against the
/// per-server tick a chart subscribes to. Its contract is the union: a running instance contributes a
/// sample, an instance the monitor only measured a footprint for contributes that alone, and neither
/// case invents the other half.
/// </summary>
public sealed class MetricsRosterFrameTests
{
    [Fact]
    public void RunningInstanceCarriesItsSampleAndItsFootprint()
    {
        ServerMetricsRoster roster = MetricsPump.BuildRoster(
            SnapshotWith([Running("factorio-1", cpu: 42.5, mem: 2_147_483_648L)],
                         [new Snap.ServerDiskUsage("factorio-1", 8_589_934_592L)]));

        ServerMetricsRow row = Assert.Single(roster.Servers);
        Assert.Equal("factorio-1", row.Id);
        Assert.NotNull(row.Metrics);
        Assert.Equal(42.5, row.Metrics!.CpuPctCore);
        Assert.Equal(2_147_483_648L, row.Metrics.MemBytes);
        Assert.Equal(8_589_934_592L, row.DiskBytes);
    }

    [Fact]
    public void StoppedInstanceIsPresentWithAFootprintAndNoSample()
    {
        ServerMetricsRoster roster = MetricsPump.BuildRoster(
            SnapshotWith([], [new Snap.ServerDiskUsage("terraria-1", 1_073_741_824L)]));

        ServerMetricsRow row = Assert.Single(roster.Servers);
        Assert.Equal("terraria-1", row.Id);
        Assert.Null(row.Metrics);
        Assert.Equal(1_073_741_824L, row.DiskBytes);
    }

    // Two instances, one up and one down, is the ordinary state of a fleet — the frame must describe
    // both in one pass, each with what was actually measured for it.
    [Fact]
    public void MixedRosterCoversBothWithoutDuplicatingEither()
    {
        ServerMetricsRoster roster = MetricsPump.BuildRoster(
            SnapshotWith([Running("factorio-1", cpu: 10, mem: 1024)],
                         [new Snap.ServerDiskUsage("factorio-1", 2048),
                          new Snap.ServerDiskUsage("terraria-1", 4096)]));

        Assert.Equal(2, roster.Servers.Count);
        ServerMetricsRow up = roster.Servers.Single(r => r.Id == "factorio-1");
        ServerMetricsRow down = roster.Servers.Single(r => r.Id == "terraria-1");
        Assert.NotNull(up.Metrics);
        Assert.Equal(2048L, up.DiskBytes);
        Assert.Null(down.Metrics);
        Assert.Equal(4096L, down.DiskBytes);
    }

    // An unwalked instance is "not measured" on the disk half, exactly as an unsampled one is on the
    // metrics half — a row may be half-null, and never a fabricated 0.
    [Fact]
    public void RunningInstanceWithNoWalkYetReportsNullFootprint()
    {
        ServerMetricsRoster roster = MetricsPump.BuildRoster(
            SnapshotWith([Running("factorio-1", cpu: 5, mem: 512)], []));

        ServerMetricsRow row = Assert.Single(roster.Servers);
        Assert.NotNull(row.Metrics);
        Assert.Null(row.DiskBytes);
    }

    // A monitor too old to publish the array at all must read as "measured nothing", not as a fleet of
    // empty install directories.
    [Fact]
    public void SnapshotWithoutTheDiskArrayReportsNullFootprints()
    {
        Snap.Snapshot snap = SnapshotWith([Running("factorio-1", cpu: 5, mem: 512)], null);

        ServerMetricsRoster roster = MetricsPump.BuildRoster(snap);

        Assert.Null(Assert.Single(roster.Servers).DiskBytes);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static Snap.ServerMetrics Running(string id, double cpu, long mem) =>
        new(id, id, "native", cpu, mem, IoReadBps: null, IoWriteBps: null, Pids: 3,
            DiskBytes: null, RxBps: null, TxBps: null);

    private static Snap.Snapshot SnapshotWith(Snap.ServerMetrics[] servers, Snap.ServerDiskUsage[]? disks) =>
        new(Ts: 0, IntervalMs: 1000, Hostname: "test", UptimeSec: 1,
            Cpu: new Snap.CpuMetrics(0, [], new Snap.LoadAvg(0, 0, 0), Info: null),
            Mem: new Snap.MemoryMetrics(0, 0, 0, 0, 0, 0, 0, 0),
            Disk: new Snap.DiskMetrics([], new Snap.DiskIo(0, 0)),
            Net: new Snap.NetworkMetrics([]),
            Sensors: [], Servers: servers, Leaves: [], Conditions: [], ServerDisks: disks);
}
