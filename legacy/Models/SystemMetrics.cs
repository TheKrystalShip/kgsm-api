namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// Represents system metrics data
/// </summary>
public class SystemMetrics
{
    /// <summary>
    /// CPU metrics
    /// </summary>
    public CpuMetrics Cpu { get; set; } = new();

    /// <summary>
    /// Memory metrics
    /// </summary>
    public MemoryMetrics Memory { get; set; } = new();

    /// <summary>
    /// Disk metrics
    /// </summary>
    public DiskMetrics Disk { get; set; } = new();

    /// <summary>
    /// Network metrics
    /// </summary>
    public NetworkMetrics Network { get; set; } = new();

    /// <summary>
    /// System information
    /// </summary>
    public SystemInfo SystemInfo { get; set; } = new();
}
