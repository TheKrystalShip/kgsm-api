namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// CPU usage metrics
/// </summary>
public class CpuMetrics
{
    /// <summary>
    /// Overall CPU usage percentage
    /// </summary>
    public double Current { get; set; }

    /// <summary>
    /// Per-core CPU usage
    /// </summary>
    public List<CpuCoreInfo> Cores { get; set; } = new();

    /// <summary>
    /// CPU usage history
    /// </summary>
    public List<CpuHistoryPoint> History { get; set; } = new();

    /// <summary>
    /// CPU model name
    /// </summary>
    public string Model { get; set; } = string.Empty;
}
