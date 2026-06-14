namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// Network usage metrics
/// </summary>
public class NetworkMetrics
{
    /// <summary>
    /// Receive statistics
    /// </summary>
    public List<NetworkDataPoint> Rx { get; set; } = new();

    /// <summary>
    /// Transmit statistics
    /// </summary>
    public List<NetworkDataPoint> Tx { get; set; } = new();

    /// <summary>
    /// Total network statistics
    /// </summary>
    public NetworkTotal Total { get; set; } = new();
}
