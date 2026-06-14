namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// System information
/// </summary>
public class SystemInfo
{
    /// <summary>
    /// Total memory in MB
    /// </summary>
    public long TotalMemory { get; set; }

    /// <summary>
    /// Total disk space in GB
    /// </summary>
    public double TotalDisk { get; set; }

    /// <summary>
    /// Number of CPU cores
    /// </summary>
    public int CpuCores { get; set; }

    /// <summary>
    /// CPU model name
    /// </summary>
    public string CpuModel { get; set; } = string.Empty;
}
