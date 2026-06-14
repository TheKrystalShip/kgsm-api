namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// Network data point
/// </summary>
public class NetworkDataPoint
{
    /// <summary>
    /// Timestamp
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Value in KB/s
    /// </summary>
    public double Value { get; set; }
}
