namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// Total network statistics
/// </summary>
public class NetworkTotal
{
    /// <summary>
    /// Total received in MB
    /// </summary>
    public double Rx { get; set; }

    /// <summary>
    /// Total transmitted in MB
    /// </summary>
    public double Tx { get; set; }

    /// <summary>
    /// Current receive speed in KB/s
    /// </summary>
    public double RxSpeed { get; set; }

    /// <summary>
    /// Current transmit speed in KB/s
    /// </summary>
    public double TxSpeed { get; set; }
}
