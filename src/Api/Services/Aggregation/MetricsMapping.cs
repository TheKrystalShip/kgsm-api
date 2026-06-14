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
        new(Math.Round(m.CpuPctCore, 1), m.MemBytes, m.IoReadBps, m.IoWriteBps, m.Pids);

    /// <summary>The host's capacity figures (the mutable, measured portion of the <c>Host</c> view).</summary>
    public static HostMetricsDto ToHostMetrics(Snap.Snapshot s) =>
        new(Math.Round(s.Cpu.TotalPct, 1),
            new MemCapacity(KibToGib(s.Mem.UsedKb), KibToGib(s.Mem.TotalKb)),
            MapDisks(s.Disk.Mounts));

    public static IReadOnlyList<DiskCapacity> MapDisks(Snap.MountUsage[] mounts)
    {
        var disks = new List<DiskCapacity>(mounts.Length);
        foreach (Snap.MountUsage m in mounts)
            disks.Add(new DiskCapacity(m.Mount, BytesToGib(m.UsedBytes), BytesToGib(m.TotalBytes)));
        return disks;
    }

    public static double KibToGib(long kib) => Math.Round(kib / 1048576.0, 2);
    public static double BytesToGib(long bytes) => Math.Round(bytes / 1073741824.0, 2);
}
