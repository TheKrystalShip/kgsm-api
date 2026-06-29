using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Metrics;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M9 Increment 1–2 tests: the frame→rows mapping (including null-metric→absent-row honesty), the
/// batched write path, rollup aggregation, and retention pruning. Each test uses its own temp DB.
/// </summary>
public sealed class MetricsHistoryTests
{
    // --- Frame → rows mapping (unit, no DB) ----------------------------------------------------------

    [Fact]
    public void MapServerMetrics_AllPresent_SixRows()
    {
        // RxBps/TxBps (Monitor.Contracts 1.3.0) are present here but the history sampler does NOT yet
        // persist per-server network rows — they ride the live wire DTO/WS tick only — so the row count
        // stays 6 even with both populated.
        var sm = new Snap.ServerMetrics("test-server", "Test", "native",
            CpuPctCore: 142.3, MemBytes: 512_000_000, IoReadBps: 1000, IoWriteBps: 2000,
            Pids: 5, DiskBytes: 10_000_000_000, RxBps: 3000, TxBps: 4000);
        var rows = new List<MetricSample>();
        MetricsSampler.MapServerMetrics(rows, sm, 1000);

        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Equal("server", r.EntityKind));
        Assert.All(rows, r => Assert.Equal("test-server", r.EntityId));
        Assert.All(rows, r => Assert.Equal(1000, r.Ts));
        Assert.Contains(rows, r => r.Metric == "cpuPctCore" && r.Value == 142.3);
        Assert.Contains(rows, r => r.Metric == "memBytes" && r.Value == 512_000_000);
        Assert.Contains(rows, r => r.Metric == "ioReadBps" && r.Value == 1000);
        Assert.Contains(rows, r => r.Metric == "ioWriteBps" && r.Value == 2000);
        Assert.Contains(rows, r => r.Metric == "pids" && r.Value == 5);
        Assert.Contains(rows, r => r.Metric == "diskBytes" && r.Value == 10_000_000_000);
    }

    [Fact]
    public void MapServerMetrics_NullIoAndDisk_AbsentRows()
    {
        var sm = new Snap.ServerMetrics("test-server", "Test", "native",
            CpuPctCore: 10.0, MemBytes: 100, IoReadBps: null, IoWriteBps: null,
            Pids: 1, DiskBytes: null, RxBps: null, TxBps: null);
        var rows = new List<MetricSample>();
        MetricsSampler.MapServerMetrics(rows, sm, 2000);

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Metric == "cpuPctCore");
        Assert.Contains(rows, r => r.Metric == "memBytes");
        Assert.Contains(rows, r => r.Metric == "pids");
        Assert.DoesNotContain(rows, r => r.Metric == "ioReadBps");
        Assert.DoesNotContain(rows, r => r.Metric == "ioWriteBps");
        Assert.DoesNotContain(rows, r => r.Metric == "diskBytes");
    }

    [Fact]
    public void MapHostMetrics_ProducesExpectedRows()
    {
        var snap = MakeSnapshot(1000);
        var rows = new List<MetricSample>();
        MetricsSampler.MapHostMetrics(rows, "test-host", snap, 1000);

        Assert.All(rows, r => Assert.Equal("host", r.EntityKind));
        Assert.All(rows, r => Assert.Equal("test-host", r.EntityId));
        Assert.Contains(rows, r => r.Metric == "cpuTotalPct");
        Assert.Contains(rows, r => r.Metric == "memUsedKb");
        Assert.Contains(rows, r => r.Metric == "memTotalKb");
        Assert.Contains(rows, r => r.Metric == "memAvailableKb");
        Assert.Contains(rows, r => r.Metric == "swapUsedKb");
        Assert.Contains(rows, r => r.Metric == "loadOne");
        Assert.Contains(rows, r => r.Metric == "loadFive");
        Assert.Contains(rows, r => r.Metric == "loadFifteen");
        Assert.Contains(rows, r => r.Metric == "diskReadBps");
        Assert.Contains(rows, r => r.Metric == "diskWriteBps");
    }

    // --- Write + read round-trip (integration, temp DB) ----------------------------------------------

    [Fact]
    public async Task WriteSamples_RoundTrips()
    {
        MetricsHistoryStore store = NewStore();

        var rows = new List<MetricSample>
        {
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = 1000, Value = 42.1 },
            new() { EntityKind = "server", EntityId = "s1", Metric = "memBytes", Ts = 1000, Value = 512 },
        };
        await store.WriteSamplesAsync(rows);

        List<MetricSample> read = await store.ReadRawAsync("server", "s1", null, 0, 2000);
        Assert.Equal(2, read.Count);
        Assert.Equal("cpuPctCore", read[0].Metric);
        Assert.Equal(42.1, read[0].Value);
    }

    [Fact]
    public async Task WriteSamples_DuplicateKey_Replaces()
    {
        MetricsHistoryStore store = NewStore();

        await store.WriteSamplesAsync([
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = 1000, Value = 10 }
        ]);
        await store.WriteSamplesAsync([
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = 1000, Value = 20 }
        ]);

        List<MetricSample> read = await store.ReadRawAsync("server", "s1", "cpuPctCore", 0, 2000);
        Assert.Single(read);
        Assert.Equal(20, read[0].Value);
    }

    // --- Rollup (Increment 2) ------------------------------------------------------------------------

    [Fact]
    public async Task Rollup_ProducesCorrectAggregates()
    {
        MetricsHistoryStore store = NewStore();
        long bucketStart = 300_000L; // 5 min = 300_000ms bucket
        await store.WriteSamplesAsync([
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = bucketStart, Value = 10 },
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = bucketStart + 15000, Value = 20 },
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = bucketStart + 30000, Value = 30 },
        ]);

        long nowMs = bucketStart + 300_000; // one full bucket later
        await store.RollupAsync(5, nowMs);

        List<MetricRollup> rollups = await store.ReadRollupAsync("server", "s1", "cpuPctCore", 0, nowMs);
        Assert.Single(rollups);
        Assert.Equal(bucketStart, rollups[0].BucketTs);
        Assert.Equal(20.0, rollups[0].Avg);
        Assert.Equal(10.0, rollups[0].Min);
        Assert.Equal(30.0, rollups[0].Max);
        Assert.Equal(3, rollups[0].N);
    }

    [Fact]
    public async Task Rollup_OpenBucket_NotRolledUp()
    {
        MetricsHistoryStore store = NewStore();
        long bucketStart = 300_000L;
        await store.WriteSamplesAsync([
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = bucketStart + 10000, Value = 50 },
        ]);

        // nowMs is within the same bucket → the bucket is still open
        long nowMs = bucketStart + 100_000;
        await store.RollupAsync(5, nowMs);

        List<MetricRollup> rollups = await store.ReadRollupAsync("server", "s1", "cpuPctCore", 0, nowMs + 300_000);
        Assert.Empty(rollups);
    }

    [Fact]
    public async Task Rollup_IsIdempotent()
    {
        MetricsHistoryStore store = NewStore();
        long bucketStart = 300_000L;
        await store.WriteSamplesAsync([
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = bucketStart, Value = 10 },
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = bucketStart + 15000, Value = 20 },
        ]);

        long nowMs = bucketStart + 300_000;
        await store.RollupAsync(5, nowMs);
        await store.RollupAsync(5, nowMs);

        List<MetricRollup> rollups = await store.ReadRollupAsync("server", "s1", "cpuPctCore", 0, nowMs);
        Assert.Single(rollups);
        Assert.Equal(15.0, rollups[0].Avg);
        Assert.Equal(2, rollups[0].N);
    }

    // --- Pruning (Increment 2) -----------------------------------------------------------------------

    [Fact]
    public async Task PruneRaw_DeletesOldKeepsRecent()
    {
        MetricsHistoryStore store = NewStore();
        await store.WriteSamplesAsync([
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = 1000, Value = 10 },
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = 5000, Value = 20 },
        ]);

        int deleted = await store.PruneRawAsync(3000);
        Assert.Equal(1, deleted);

        List<MetricSample> remaining = await store.ReadRawAsync("server", "s1", null, 0, 10000);
        Assert.Single(remaining);
        Assert.Equal(5000, remaining[0].Ts);
    }

    [Fact]
    public async Task PruneRollups_DeletesOldKeepsRecent()
    {
        MetricsHistoryStore store = NewStore();
        await store.WriteRollupsAsync([
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", BucketTs = 300_000, Avg = 10, Min = 5, Max = 15, N = 3 },
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", BucketTs = 600_000, Avg = 20, Min = 10, Max = 30, N = 4 },
        ]);

        int deleted = await store.PruneRollupsAsync(500_000);
        Assert.Equal(1, deleted);

        List<MetricRollup> remaining = await store.ReadRollupAsync("server", "s1", null, 0, 1_000_000);
        Assert.Single(remaining);
        Assert.Equal(600_000, remaining[0].BucketTs);
    }

    // --- Read with metric filter ---------------------------------------------------------------------

    [Fact]
    public async Task ReadRaw_FilterByMetric()
    {
        MetricsHistoryStore store = NewStore();
        await store.WriteSamplesAsync([
            new() { EntityKind = "server", EntityId = "s1", Metric = "cpuPctCore", Ts = 1000, Value = 10 },
            new() { EntityKind = "server", EntityId = "s1", Metric = "memBytes", Ts = 1000, Value = 512 },
        ]);

        List<MetricSample> cpu = await store.ReadRawAsync("server", "s1", "cpuPctCore", 0, 2000);
        Assert.Single(cpu);
        Assert.Equal("cpuPctCore", cpu[0].Metric);

        List<MetricSample> all = await store.ReadRawAsync("server", "s1", null, 0, 2000);
        Assert.Equal(2, all.Count);
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static MetricsHistoryStore NewStore()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"metricstest-{Guid.NewGuid():N}.db");
        ServiceProvider sp = new ServiceCollection()
            .AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .BuildServiceProvider();
        return new MetricsHistoryStore(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MetricsHistoryStore>.Instance);
    }

    private static Snap.Snapshot MakeSnapshot(long ts) =>
        new(ts, 1000, "test-host", 3600,
            new Snap.CpuMetrics(45.2, [22.1, 68.3], new Snap.LoadAvg(1.5, 2.0, 1.8), null),
            new Snap.MemoryMetrics(16_000_000, 8_000_000, 8_000_000, 50.0, 2_000_000, 500_000, 4_000_000, 1_000_000),
            new Snap.DiskMetrics([], new Snap.DiskIo(50_000, 30_000)),
            new Snap.NetworkMetrics([]),
            [],
            []);
}
