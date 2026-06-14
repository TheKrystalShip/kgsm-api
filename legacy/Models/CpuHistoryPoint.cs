namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// CPU history point
/// </summary>
public class CpuHistoryPoint
{
    /// <summary>
    /// Timestamp
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Per-core usage
    /// </summary>
    public List<CpuCoreInfo> Cores { get; set; } = new();

    /// <summary>
    /// Average usage
    /// </summary>
    public double Average { get; set; }
}
