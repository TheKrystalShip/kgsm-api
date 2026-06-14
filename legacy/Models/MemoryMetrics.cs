namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// Memory usage metrics
/// </summary>
public class MemoryMetrics
{
    /// <summary>
    /// Memory usage percentage
    /// </summary>
    public double Percent { get; set; }

    /// <summary>
    /// Total memory in MB
    /// </summary>
    public long Total { get; set; }

    /// <summary>
    /// Used memory in MB
    /// </summary>
    public long Used { get; set; }

    /// <summary>
    /// Free memory in MB
    /// </summary>
    public long Free { get; set; }
}
