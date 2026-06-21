using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// #9 — the host-diagnostics depth added in Monitor.Contracts 1.1.0, proven at the single source where
/// units + honesty live: <see cref="MetricsMapping.ToHostMetrics"/> (the shared REST/WS mapper) and the
/// static <see cref="MetricsMapping.ToCpuInfo"/> helper (the Host-only cpu identity).
/// <para>The load-bearing assertions are units (cached/buffers KiB→GiB; maxFreq already-GHz, only rounded,
/// never re-converted) and honesty: a field present ⇒ really sourced; absent ⇒ explicit null/empty, never
/// fabricated. The sharpest honesty case is iface <c>errors</c> — a genuine <c>0</c> stays <c>0</c>, while
/// "neither counter read" is <c>null</c>; the two are never conflated.</para>
/// </summary>
public sealed class HostDiagnosticsMappingTests
{
    // A fully-populated snapshot exercising every new 1.1.0 field with sourceable values.
    private static Snap.Snapshot FullSnapshot() => new(
        Ts: 1718400000000, IntervalMs: 1000, Hostname: "host-a", UptimeSec: 99,
        Cpu: new Snap.CpuMetrics(
            TotalPct: 12.34, PerCore: [10.0, 15.0],
            Load: new Snap.LoadAvg(0.4, 0.5, 0.6),
            Info: new Snap.CpuInfo("AMD Ryzen 7 3800X 8-Core Processor", 8, 16, 3.9)),
        Mem: new Snap.MemoryMetrics(
            TotalKb: 32768000, AvailableKb: 16384000, UsedKb: 16384000, UsedPct: 50.0,
            SwapTotalKb: 0, SwapUsedKb: 0, CachedKb: 4194304, BuffersKb: 1048576),
        Disk: new Snap.DiskMetrics(
            Mounts: [new Snap.MountUsage("/", "ext4", 500_000_000_000, 250_000_000_000, 50.0,
                Device: "Samsung SSD 990 EVO Plus 1TB")],
            Io: new Snap.DiskIo(1000, 2000)),
        Net: new Snap.NetworkMetrics(
            Ifaces: [new Snap.InterfaceRate("eth0", 100, 200, 1, 2, Mac: "aa:bb:cc:dd:ee:ff", Errors: 0)]),
        Sensors: [new Snap.SensorReading("k10temp", "Tctl", 42.5)],
        Servers: []);

    // --- dynamic depth: rides ToHostMetrics (so it mirrors onto both the REST element and the WS tick) ---

    [Fact]
    public void ToHostMetrics_MemCachedBuffers_AreGiB()
    {
        HostMetricsDto m = MetricsMapping.ToHostMetrics(FullSnapshot());
        // 4194304 KiB / 1048576 = 4 GiB; 1048576 KiB = 1 GiB (same KiB→GiB helper + rounding as used/total).
        Assert.Equal(4.0, m.Mem.Cached);
        Assert.Equal(1.0, m.Mem.Buffers);
    }

    [Fact]
    public void ToHostMetrics_Iface_MacAndErrors_PassThrough_GenuineZeroPreserved()
    {
        HostMetricsDto m = MetricsMapping.ToHostMetrics(FullSnapshot());
        InterfaceSample i = Assert.Single(m.Interfaces);
        Assert.Equal("aa:bb:cc:dd:ee:ff", i.Mac);
        Assert.Equal(0L, i.Errors);          // a GENUINE 0 stays 0 — never coerced to null
        Assert.NotNull(i.Errors);
    }

    [Fact]
    public void ToHostMetrics_Iface_ErrorsNull_WhenUnreadable_NotConflatedWithZero()
    {
        // The honesty distinction the task stressed: errors null when neither counter file reads. The mapper
        // must pass null THROUGH (not coerce to 0) so "unknown" and a real zero stay distinct on the wire.
        Snap.Snapshot s = FullSnapshot() with
        {
            Net = new Snap.NetworkMetrics([new Snap.InterfaceRate("eth0", 0, 0, 0, 0, Mac: null, Errors: null)]),
        };
        InterfaceSample i = Assert.Single(MetricsMapping.ToHostMetrics(s).Interfaces);
        Assert.Null(i.Errors);               // honest-unknown, distinct from the genuine 0 above
        Assert.Null(i.Mac);                  // unreadable mac → null, never fabricated
    }

    [Fact]
    public void ToHostMetrics_Disk_DeviceModel_RidesSharedShape()
    {
        // device is static-per-mount but rides the shared DiskCapacity → present on the tick too, like Fs.
        DiskCapacity d = Assert.Single(MetricsMapping.ToHostMetrics(FullSnapshot()).Disks);
        Assert.Equal("Samsung SSD 990 EVO Plus 1TB", d.Device);
        Assert.Equal("ext4", d.Fs);
    }

    [Fact]
    public void ToHostMetrics_Sensors_PassThrough()
    {
        SensorSample sensor = Assert.Single(MetricsMapping.ToHostMetrics(FullSnapshot()).Sensors);
        Assert.Equal("k10temp", sensor.Chip);
        Assert.Equal("Tctl", sensor.Label);
        Assert.Equal(42.5, sensor.ValueC);    // passed through 1:1 (the monitor already produced the °C value)
    }

    [Fact]
    public void ToHostMetrics_Sensors_Empty_WhenNoHwmon_NeverInvented()
    {
        Snap.Snapshot s = FullSnapshot() with { Sensors = [] };
        Assert.Empty(MetricsMapping.ToHostMetrics(s).Sensors);   // empty array, never a fabricated row
    }

    [Fact]
    public void ToHostMetrics_Sensors_NullTolerant_NoNre()
    {
        // The contract's Sensors is non-nullable, but a hand-built/stub snapshot may leave it null. The mapper
        // treats null as empty rather than NRE-ing the read path (the GET /hosts/{id} + smoke-stub guard).
        Snap.Snapshot s = FullSnapshot() with { Sensors = null! };
        Assert.Empty(MetricsMapping.ToHostMetrics(s).Sensors);
    }

    // --- static depth: ToCpuInfo (Host view only — NOT on the tick) ---

    [Fact]
    public void ToCpuInfo_PassesThrough_MaxFreqAlreadyGhz_OnlyRounded()
    {
        // maxFreqGhz is ALREADY GHz from the monitor — round, never re-convert (a divide would emit a tiny wrong number).
        CpuInfoSample? cpu = MetricsMapping.ToCpuInfo(new Snap.CpuInfo("Ryzen", 8, 16, 3.901));
        Assert.NotNull(cpu);
        Assert.Equal("Ryzen", cpu!.Model);
        Assert.Equal(8, cpu.Cores);
        Assert.Equal(16, cpu.Threads);
        Assert.Equal(3.9, cpu.MaxFreqGhz);   // 3.901 rounded to 3.9 GHz, NOT divided
    }

    [Fact]
    public void ToCpuInfo_Null_WhenNoCpuInfo()
    {
        // No cpu-info on the snapshot → null (honest-unknown), never a fabricated identity.
        Assert.Null(MetricsMapping.ToCpuInfo(null));
    }

    [Fact]
    public void ToCpuInfo_InnerFieldsNull_WhenUnreadable_NeverGuessed()
    {
        // The source files are individually unreadable → each field null, including maxFreqGhz (no cpufreq).
        CpuInfoSample? cpu = MetricsMapping.ToCpuInfo(new Snap.CpuInfo(null, null, null, null));
        Assert.NotNull(cpu);                 // the blob is present (info object existed)...
        Assert.Null(cpu!.Model);             // ...but each field honestly null, never guessed
        Assert.Null(cpu.Cores);
        Assert.Null(cpu.Threads);
        Assert.Null(cpu.MaxFreqGhz);
    }
}
