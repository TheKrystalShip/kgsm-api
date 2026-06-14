namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// Disk usage metrics
/// </summary>
public class DiskMetrics
{
    /// <summary>
    /// Used disk percentage
    /// </summary>
    public double Used { get; set; }

    /// <summary>
    /// Free disk percentage
    /// </summary>
    public double Free { get; set; }

    /// <summary>
    /// Total disk space in GB
    /// </summary>
    public double TotalGB { get; set; }

    /// <summary>
    /// Used disk space in GB
    /// </summary>
    public double UsedGB { get; set; }

    /// <summary>
    /// Free disk space in GB
    /// </summary>
    public double FreeGB { get; set; }

    /// <summary>
    /// Total disk space (convenience property)
    /// </summary>
    public double Total { get; set; }
}
