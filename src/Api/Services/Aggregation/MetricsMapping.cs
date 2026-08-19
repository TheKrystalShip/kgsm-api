using TheKrystalShip.Api.Contracts;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// The single monitor-snapshot → honest-DTO mapper, shared by the REST aggregators (M1·a/M1·b) and the
/// M2 stream pumps. Centralizing it guarantees a WS metric tick is byte-identical to the REST element it
/// patches — same units, same rounding — so the two surfaces can never silently diverge. Units and the
/// honesty rules (native <c>cpuPctCore</c> can exceed 100; nullable <c>io*</c> passed through, never
/// coerced) are documented on <see cref="ServerMetricsDto"/> / <see cref="HostMetricsDto"/>.
/// </summary>
internal static class MetricsMapping
{
    /// <summary>One instance's resource sample, mapped 1:1 from the monitor's <c>ServerMetrics</c>.</summary>
    public static ServerMetricsDto ToServerMetrics(Snap.ServerMetrics m) =>
        new(Math.Round(m.CpuPctCore, 1), m.MemBytes, m.IoReadBps, m.IoWriteBps, m.Pids, m.DiskBytes,
            m.RxBps, m.TxBps);

    /// <summary>The host's capacity figures (the mutable, measured portion of the <c>Host</c> view). The M-diag
    /// enrichment surfaces the rest of the already-scraped snapshot — per-core, load, swap, fs, disk-IO,
    /// interfaces, hostname, uptime, and the sample <c>Ts</c> — all honest passthrough, no fabrication.</summary>
    public static HostMetricsDto ToHostMetrics(Snap.Snapshot s) =>
        new(Math.Round(s.Cpu.TotalPct, 1),
            new MemCapacity(
                KibToGib(s.Mem.UsedKb), KibToGib(s.Mem.TotalKb),
                Available: KibToGib(s.Mem.AvailableKb),
                SwapUsed: KibToGib(s.Mem.SwapUsedKb),
                SwapTotal: KibToGib(s.Mem.SwapTotalKb),
                // M-diag depth — kernel page cache + buffers, KiB→GiB to match the other mem figures.
                Cached: KibToGib(s.Mem.CachedKb),
                Buffers: KibToGib(s.Mem.BuffersKb)),
            MapDisks(s.Disk.Mounts),
            PerCore: RoundEach(s.Cpu.PerCore),
            Load: new LoadSample(s.Cpu.Load.One, s.Cpu.Load.Five, s.Cpu.Load.Fifteen),
            DiskIo: new DiskIoSample(s.Disk.Io.ReadBps, s.Disk.Io.WriteBps),
            Interfaces: MapIfaces(s.Net.Ifaces),
            Hostname: s.Hostname,
            UptimeSec: s.UptimeSec,
            SampleTs: s.Ts,
            // M-diag depth — the DYNAMIC sensor temperatures (mac/errors ride MapIfaces above; cached/buffers
            // ride MemCapacity above) so this tick mirrors the Host view exactly. Static cpu-info is NOT on the
            // tick (it rides the Host view via ToCpuInfo).
            Sensors: MapSensors(s.Sensors),
            Gpus: ToGpus(s.Gpu));

    public static IReadOnlyList<DiskCapacity> MapDisks(Snap.MountUsage[] mounts)
    {
        var disks = new List<DiskCapacity>(mounts.Length);
        foreach (Snap.MountUsage m in mounts)
            // Fs + Device both static-per-mount but ride this shared shape (so Device appears on the tick too,
            // exactly as Fs does — keeps Host.Disks byte-identical to tick.Disks). Device null when unresolvable.
            disks.Add(new DiskCapacity(m.Mount, BytesToGib(m.UsedBytes), BytesToGib(m.TotalBytes),
                Fs: m.Fs, Device: m.Device));
        return disks;
    }

    private static IReadOnlyList<InterfaceSample> MapIfaces(Snap.InterfaceRate[] ifaces)
    {
        var list = new List<InterfaceSample>(ifaces.Length);
        foreach (Snap.InterfaceRate i in ifaces)
            // mac null when unreadable; errors null ONLY when neither counter file reads — a genuine 0 stays 0,
            // never conflated with unknown (passed through 1:1, never coerced).
            list.Add(new InterfaceSample(i.Name, i.RxBps, i.TxBps, i.RxPps, i.TxPps, Mac: i.Mac, Errors: i.Errors));
        return list;
    }

    // hwmon temperatures — empty array when no chip reports (never an invented row). Null-tolerant: the
    // Snapshot's Sensors is non-nullable, but a hand-built/stub snapshot may leave it null → treat as empty
    // rather than NRE the read path.
    private static IReadOnlyList<SensorSample> MapSensors(Snap.SensorReading[]? sensors)
    {
        if (sensors is null || sensors.Length == 0) return [];
        var list = new List<SensorSample>(sensors.Length);
        foreach (Snap.SensorReading r in sensors)
            // Pass chip/label/valueC through 1:1 — the monitor already produced the °C value; don't re-round.
            list.Add(new SensorSample(r.Chip, r.Label, r.ValueC));
        return list;
    }

    /// <summary>The STATIC CPU identity for the <see cref="Host"/> view (NOT the metrics tick — it's constant
    /// per frame). Null when the snapshot has no cpu-info; each field passes through 1:1 (MaxFreqGhz is already
    /// GHz from the monitor — only rounded, never re-converted).</summary>
    public static CpuInfoSample? ToCpuInfo(Snap.CpuInfo? info) =>
        info is null
            ? null
            : new CpuInfoSample(
                info.Model, info.Cores, info.Threads,
                info.MaxFreqGhz is { } ghz ? Math.Round(ghz, 2) : null);

    private static IReadOnlyList<double> RoundEach(double[] cores)
    {
        var rounded = new double[cores.Length];
        for (int i = 0; i < cores.Length; i++)
            rounded[i] = Math.Round(cores[i], 1);
        return rounded;
    }

    public static double KibToGib(long kib) => Math.Round(kib / 1048576.0, 2);
    public static double BytesToGib(long bytes) => Math.Round(bytes / 1073741824.0, 2);

    /// <summary>
    /// The host's GPUs, or null when it has none — no card, no driver, no NVML. Absence of a GPU is an
    /// ordinary host rather than a degraded one, so a client renders one less thing and reports nothing.
    /// </summary>
    public static IReadOnlyList<GpuSample>? ToGpus(Snap.GpuMetrics? gpu)
    {
        if (gpu is null || gpu.Devices.Length == 0) return null;

        var samples = new List<GpuSample>(gpu.Devices.Length);
        foreach (Snap.GpuDevice d in gpu.Devices)
            samples.Add(new GpuSample(
                d.Index, d.Name, d.Uuid,
                MemUsed: BytesToGib(d.MemUsedBytes),
                MemTotal: BytesToGib(d.MemTotalBytes),
                SmPct: d.SmPct is { } sm ? Math.Round(sm, 1) : null,
                TempC: d.TempC is { } t ? Math.Round(t, 1) : null,
                PowerW: d.PowerW is { } p ? Math.Round(p, 1) : null,
                PowerCapW: d.PowerCapW is { } c ? Math.Round(c, 1) : null));
        return samples;
    }

    /// <summary>Every compute context on the host's GPUs, unprojected. Gate before serving — see
    /// <see cref="GpuRedaction"/>.</summary>
    public static IReadOnlyList<GpuProcessSample>? ToGpuProcesses(Snap.GpuMetrics? gpu)
    {
        if (gpu is null) return null;

        var samples = new List<GpuProcessSample>(gpu.Processes.Length);
        foreach (Snap.GpuProcess p in gpu.Processes)
            samples.Add(new GpuProcessSample(
                p.DeviceIndex, p.Pid, p.ProcessName, p.Unit,
                MemUsed: BytesToGib(p.MemBytes),
                SmPct: p.SmPct is { } sm ? Math.Round(sm, 1) : null));
        return samples;
    }

    /// <summary>One leaf's attributed GPU, or null when it has no GPU context at all.</summary>
    public static LeafGpuSample? ToLeafGpu(Snap.LeafGpu? gpu) =>
        gpu is null ? null
            : new LeafGpuSample(
                gpu.Attribution,
                gpu.Units,
                MemUsed: BytesToGib(gpu.MemBytes),
                SmPct: gpu.SmPct is { } sm ? Math.Round(sm, 1) : null);
}

/// <summary>
/// Withholds the identity of GPU processes that belong to no unit this host knows about.
/// </summary>
/// <remarks>
/// <para>
/// The monitor measures every compute context on the card, and most hosts run things besides KGSM. A
/// viewer has no business learning that somebody is running a particular binary on the box, so below
/// operator the unresolved rows lose their pid and name.
/// </para>
/// <para>
/// ⚠ <b>They are aggregated, not dropped.</b> The withheld memory stays, as one unnamed row per device. A
/// reader can still see that the card is full and that something they cannot see is holding it — which is
/// the true state — instead of being shown per-process figures that mysteriously fail to add up to the
/// device's. Withholding a name and under-reporting a total are different acts, and only the first is honest.
/// </para>
/// </remarks>
public static class GpuRedaction
{
    public static IReadOnlyList<GpuProcessSample>? ForViewer(IReadOnlyList<GpuProcessSample>? processes)
    {
        if (processes is null) return null;

        var kept = new List<GpuProcessSample>(processes.Count);
        var withheldMem = new Dictionary<int, double>();
        var withheldSm = new Dictionary<int, double?>();

        foreach (GpuProcessSample p in processes)
        {
            if (p.Unit is not null)
            {
                kept.Add(p);
                continue;
            }

            withheldMem[p.DeviceIndex] = withheldMem.GetValueOrDefault(p.DeviceIndex) + p.MemUsed;
            if (p.SmPct is { } sm)
                withheldSm[p.DeviceIndex] = (withheldSm.GetValueOrDefault(p.DeviceIndex) ?? 0) + sm;
        }

        foreach ((int device, double mem) in withheldMem)
            kept.Add(new GpuProcessSample(
                DeviceIndex: device,
                Pid: null,
                Name: null,
                Unit: null,
                MemUsed: Math.Round(mem, 2),
                SmPct: withheldSm.TryGetValue(device, out double? sm) && sm is { } v ? Math.Round(v, 1) : null));

        return kept;
    }
}
